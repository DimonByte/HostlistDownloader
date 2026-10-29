//MIT License

//Copyright (c) 2026 Dimon

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

using HostlistDownloader.Config;
using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using HostlistDownloader.IO;
using HostlistDownloader.Lists;
using HostlistDownloader.Network;
using HostlistDownloader.UI;
using System.Diagnostics;

namespace HostlistDownloader.Cli;

/// <summary>Runs the command chosen by <see cref="ArgumentParser"/>. Every path returns an exit code; nothing calls Environment.Exit.</summary>
internal static class CommandDispatcher
{
    internal static async Task<int> ExecuteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case CommandKind.Help:
                Console.WriteLine(ArgumentParser.HelpText());
                return ExitCodes.Success;
            case CommandKind.Version:
                Console.WriteLine($"{AppInfo.Name} {AppInfo.ShortVersion}");
                return ExitCodes.Success;
        }

        Paths.Initialize(request.Global.DataDirectory);
        TraceLogger.Initialize(Paths.LogsFolder);
        TraceLogger.Log($"[START] {AppInfo.Name} {AppInfo.ShortVersion}, command {request.Command}, data folder {Paths.Root}");
        foreach (string deprecated in request.DeprecatedArguments)
        {
            TraceLogger.Log($"Deprecated argument {deprecated}.", LogSeverity.Notice);
        }

        if (Workspace.EnsureCreated())
        {
            ConsoleUI.ShowFirstRun(Paths.SettingsFile);
            return ExitCodes.FirstRunSetupCompleted;
        }

        AppConfig config = AppConfig.Load(Paths.SettingsFile);
        AppConfig.SetCurrent(config);
        if (request.Command is CommandKind.Update or CommandKind.Merge)
        {
            ConfigValidator.ReportAtStartup(config, verbose: true);
        }
        else if (request.Command != CommandKind.Validate)
        {
            ConfigValidator.ReportAtStartup(config, verbose: false);
        }

        if (request.Global.PurgeLogs)
        {
            int deleted = TraceLogger.PurgeAllLogs();
            TraceLogger.Log($"Deleted {deleted} log file(s).", LogSeverity.Notice);
        }
        TraceLogger.ClearExpiredLogs(config.LogExpiryInDays);

        switch (request.Command)
        {
            case CommandKind.Update:
            {
                using RunLock runLock = RunLock.Acquire();
                Workspace.RemoveLeftovers();
                var stopwatch = Stopwatch.StartNew();
                RunResult result = await new HostlistOrchestrator(config).RunAsync(request.Run.Fresh, cancellationToken).ConfigureAwait(false);
                ReportRun(result, stopwatch.Elapsed);
                if (!request.Global.NoUpdateCheck)
                {
                    await UpdateChecker.CheckIfDueAsync(config, cancellationToken).ConfigureAwait(false);
                }
                return result.ExitCode;
            }

            case CommandKind.Merge:
            {
                using RunLock runLock = RunLock.Acquire();
                Workspace.RemoveLeftovers();
                var stopwatch = Stopwatch.StartNew();
                RunResult result = new HostlistOrchestrator(config).RunOffline(cancellationToken);
                ReportRun(result, stopwatch.Elapsed);
                return result.ExitCode;
            }

            case CommandKind.Search:
                return SearchManager.Search(request.Argument!, config);

            case CommandKind.Stats:
                return StatsReporter.ShowStats(config);

            case CommandKind.Diff:
                return StatsReporter.ShowDiff();

            case CommandKind.Revert:
            {
                using RunLock runLock = RunLock.Acquire();
                int reverted = ListPublisher.Revert(OutputTargets.Default);
                return reverted > 0 ? ExitCodes.Success : ExitCodes.FileMissing;
            }

            case CommandKind.SelfUpdate:
                return await UpdateChecker.RunSelfUpdateAsync(cancellationToken).ConfigureAwait(false);

            case CommandKind.Validate:
            {
                if (request.ValidationMode == ValidationMode.Fix)
                {
                    using RunLock runLock = RunLock.Acquire();
                    return ConfigValidator.Run(config, ValidationMode.Fix);
                }
                return ConfigValidator.Run(config, ValidationMode.ReportOnly);
            }

            case CommandKind.DuplicateScan:
                return DuplicateAnalyser.ScanAll();

            case CommandKind.DuplicateAnalyse:
                return DuplicateAnalyser.AnalyseOne(request.Argument!);

            case CommandKind.GetSource:
                return GetSource(request.Argument!);

            case CommandKind.Disable:
            {
                using RunLock runLock = RunLock.Acquire();
                return ListSwitch.Disable();
            }

            case CommandKind.Enable:
            {
                using RunLock runLock = RunLock.Acquire();
                return ListSwitch.Enable();
            }

            case CommandKind.EditConfig:
            {
                using RunLock runLock = RunLock.Acquire();
                return EditConfig(config, request.Edits);
            }

            default:
                throw new InvalidOperationException($"Unhandled command {request.Command}.");
        }
    }

    private static int GetSource(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName)
        {
            TraceLogger.Log($"'{fileName}' must be a file name, not a path.", LogSeverity.Warn);
            return ExitCodes.MissingOrInvalidParameters;
        }

        (string Url, ListKind Kind)? source = SourceManager.FindSourceForFile(fileName);
        if (source is null)
        {
            TraceLogger.Log($"'{fileName}' is not a known cached list. Use --dupscan to list cached files.", LogSeverity.Warn);
            return ExitCodes.FileMissing;
        }

        TraceLogger.Log($"{fileName} is the {source.Value.Kind.DisplayName()} source {LogRedactor.Redact(source.Value.Url)}", LogSeverity.Notice);
        return ExitCodes.Success;
    }

    private static int EditConfig(AppConfig config, IReadOnlyList<ConfigEdit> edits)
    {
        SettingsFile settings = config.Settings;
        bool anyFailed = false;
        bool changed = false;
        foreach (ConfigEdit edit in edits)
        {
            SettingsFile updated = ConfigEditor.Apply(settings, edit, out EditOutcome outcome);
            changed |= !ReferenceEquals(updated, settings) && updated != settings;
            settings = updated;
            anyFailed |= !outcome.Success;
            TraceLogger.Log(outcome.Message, outcome.Success ? LogSeverity.Notice : LogSeverity.Warn);
        }

        if (changed)
        {
            var updatedConfig = new AppConfig(settings);
            updatedConfig.Save(Paths.SettingsFile);
            AppConfig.SetCurrent(updatedConfig);
            TraceLogger.Log("settings.json saved. Run HostlistDownloader (or --merge for user rules) to apply the change.", LogSeverity.Notice);
        }

        return anyFailed ? ExitCodes.InvalidConfigEntry : ExitCodes.Success;
    }

    private static void ReportRun(RunResult result, TimeSpan elapsed)
    {
        string took = $"Completed in {elapsed.TotalSeconds:N1}s.";
        switch (result.Status)
        {
            case RunStatus.Updated:
                TraceLogger.Log($"[UPDATED] Hostlists updated successfully. {took}", LogSeverity.Notice);
                break;
            case RunStatus.UpdatedWithIssues:
                TraceLogger.Log($"[UPDATED WITH ISSUES] Hostlists were updated, but some sources had problems (see the Sources table and the log). {took}", LogSeverity.Warn);
                break;
            case RunStatus.UpToDate:
                TraceLogger.Log($"[UP TO DATE] Hostlists are already up to date. {took}", LogSeverity.Notice);
                break;
            case RunStatus.UpToDateWithIssues:
                TraceLogger.Log($"[UP TO DATE WITH ISSUES] The lists did not need changing, but some sources had problems (see the Sources table and the log). {took}", LogSeverity.Warn);
                break;
            default:
                TraceLogger.Log($"[PROBLEM] {result.FailureReason ?? "The update failed. Check the log for details."} {took}", LogSeverity.Error);
                break;
        }
    }
}
