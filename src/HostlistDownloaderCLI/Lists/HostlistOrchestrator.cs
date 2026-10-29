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
using HostlistDownloader.Network;
using HostlistDownloader.UI;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace HostlistDownloader.Lists;

internal sealed record SourceReport(ListKind Kind, string DisplayName, string FilePath, DownloadOutcome Outcome, string Message, TimeSpan Elapsed, bool HasCachedCopy);

internal enum RunStatus
{
    Updated,
    UpdatedWithIssues,
    UpToDate,
    UpToDateWithIssues,
    Failed
}

/// <summary>Everything that happened in one run. Returned to the caller instead of being kept in global flags.</summary>
internal sealed class RunResult
{
    public List<SourceReport> Sources { get; } = [];
    public ListBuildResult? Build { get; set; }
    public PublishResult? Publish { get; set; }
    public bool NetworkUnavailable { get; set; }
    public int DownloadWarnings { get; set; }
    public string? FailureReason { get; set; }
    public int FailureExitCode { get; set; } = ExitCodes.UpdateProcessError;

    public bool Published => Publish?.Success == true;

    public bool HasSourceProblems => Sources.Exists(s => s.Outcome.IsProblem());

    public bool AnySourceUsable => Sources.Exists(s => s.Outcome.IsSuccess() || s.Outcome == DownloadOutcome.SkippedUpToDate || s.HasCachedCopy);

    public RunStatus Status
    {
        get
        {
            if (FailureReason is not null)
            {
                return RunStatus.Failed;
            }
            bool issues = HasSourceProblems || NetworkUnavailable || (Build?.Warnings.Count ?? 0) > 0;
            if (Published)
            {
                return issues ? RunStatus.UpdatedWithIssues : RunStatus.Updated;
            }
            if (issues && Sources.Count > 0 && !AnySourceUsable)
            {
                return RunStatus.Failed;
            }
            return issues ? RunStatus.UpToDateWithIssues : RunStatus.UpToDate;
        }
    }

    public int ExitCode => Status switch
    {
        RunStatus.Updated or RunStatus.UpToDate => ExitCodes.Success,
        RunStatus.Failed => FailureReason is not null ? FailureExitCode : (NetworkUnavailable ? ExitCodes.NetworkConnectionFailed : ExitCodes.UpdateProcessError),
        _ => NetworkUnavailable ? ExitCodes.NetworkConnectionFailed : ExitCodes.PartialUpdateWithIssues
    };
}

/// <summary>
/// Runs an update: download every enabled source, then rebuild and publish the outputs if anything changed.
/// Each source is cached under a stable name and a cached copy is only ever replaced by a validated download,
/// so a source that fails this run keeps contributing its last good copy.
/// </summary>
internal sealed class HostlistOrchestrator(AppConfig config)
{
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(5);

    internal async Task<RunResult> RunAsync(bool fresh, CancellationToken cancellationToken)
    {
        EnsureSomethingConfigured();
        var result = new RunResult();
        List<SourcePlan> plans = PrepareSources();

        List<SourcePlan> downloadable = plans.Where(p => p.IsValid).ToList();
        AddStaticReports(plans, result);

        if (downloadable.Count > 0)
        {
            bool online = await NetworkUpChecker.IsNetworkAvailableAsync(
                downloadable.Select(p => p.Uri!.Host), config.ProxyUri is not null, cancellationToken).ConfigureAwait(false);

            if (!online)
            {
                result.NetworkUnavailable = true;
                TraceLogger.Log("No network connection. Sources were not downloaded; cached copies will be used if the output needs rebuilding.", LogSeverity.Error);
                foreach (SourcePlan plan in downloadable)
                {
                    result.Sources.Add(new SourceReport(plan.Kind, plan.DisplayName, plan.FilePath, DownloadOutcome.TransientFailure, "No network connection.", TimeSpan.Zero, File.Exists(plan.FilePath)));
                }
            }
            else
            {
                int warningsBefore = TraceLogger.WarningCount;
                List<SourceReport> reports = await DownloadAsync(downloadable, fresh, "Downloading", cancellationToken).ConfigureAwait(false);

                // Recovery: retry only sources that failed transiently AND have no cached copy. Nothing is deleted first.
                List<SourcePlan> retry = downloadable
                    .Where(p => reports.Exists(r => r.FilePath == p.FilePath && r.Outcome == DownloadOutcome.TransientFailure && !r.HasCachedCopy))
                    .ToList();
                if (retry.Count > 0)
                {
                    TraceLogger.Log($"{retry.Count} source(s) failed and have no cached copy. Retrying them once in {RecoveryDelay.TotalSeconds:N0}s...", LogSeverity.Warn);
                    await Task.Delay(RecoveryDelay, cancellationToken).ConfigureAwait(false);
                    List<SourceReport> retried = await DownloadAsync(retry, true, "Retrying", cancellationToken).ConfigureAwait(false);
                    foreach (SourceReport report in retried)
                    {
                        reports.RemoveAll(r => r.FilePath == report.FilePath);
                        reports.Add(report);
                    }
                }

                result.Sources.AddRange(reports);
                result.DownloadWarnings = TraceLogger.WarningCount - warningsBefore;
            }
        }

        ShowDownloadReport(result);

        bool anyDownloaded = result.Sources.Exists(s => s.Outcome.IsSuccess());
        BuildAndPublish(plans, result, force: fresh || anyDownloaded, cancellationToken);
        return result;
    }

    /// <summary>--merge: rebuild the outputs from cached copies without touching the network.</summary>
    internal RunResult RunOffline(CancellationToken cancellationToken)
    {
        EnsureSomethingConfigured();
        var result = new RunResult();
        List<SourcePlan> plans = PrepareSources();
        AddStaticReports(plans, result);
        foreach (SourcePlan plan in plans.Where(p => p.IsValid))
        {
            bool cached = File.Exists(plan.FilePath);
            result.Sources.Add(new SourceReport(plan.Kind, plan.DisplayName, plan.FilePath,
                cached ? DownloadOutcome.SkippedUpToDate : DownloadOutcome.TransientFailure,
                cached ? "Using cached copy." : "No cached copy yet; run an online update first.", TimeSpan.Zero, cached));
        }

        BuildAndPublish(plans, result, force: true, cancellationToken);
        return result;
    }

    private void EnsureSomethingConfigured()
    {
        if (config.HasErrors)
        {
            foreach (ConfigIssue issue in config.Issues.Where(i => i.Severity == ConfigIssueSeverity.Error))
            {
                TraceLogger.Log($"{issue.Setting}: {issue.Message}", LogSeverity.Error);
            }
            throw TraceLogger.Fatal("settings.json has errors that must be fixed first (see above, or run --validate).", ExitCodes.InvalidConfigEntry);
        }

        bool anyBlockInput = config.EnabledSources(ListKind.Block).Count > 0 || config.UserBlocklist.Count > 0;
        if (!anyBlockInput)
        {
            throw TraceLogger.Fatal($"No blocklists are configured. Add at least one enabled blocklist source or user block rule to {Paths.SettingsFile} and run again.", ExitCodes.FileMissing);
        }
    }

    private List<SourcePlan> PrepareSources()
    {
        var plans = new List<SourcePlan>();
        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            SourceManager.MigrateLegacyFiles(config, kind);
            SourceManager.RemoveUnconfiguredFiles(config, kind);
            SourceManager.WriteManifest(config, kind);
            plans.AddRange(SourceManager.Plan(config, kind));
        }
        return plans;
    }

    private void AddStaticReports(List<SourcePlan> plans, RunResult result)
    {
        foreach (SourcePlan plan in plans.Where(p => !p.IsValid))
        {
            TraceLogger.Log($"Invalid {plan.Kind.DisplayName()} URL skipped: {plan.Source.Url} ({plan.InvalidReason})", LogSeverity.Warn);
            result.Sources.Add(new SourceReport(plan.Kind, plan.DisplayName, string.Empty, DownloadOutcome.InvalidUrl, plan.InvalidReason ?? "Invalid URL.", TimeSpan.Zero, false));
        }

        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            foreach (SourceEntry source in config.Sources(kind).Where(s => !s.Enabled))
            {
                result.Sources.Add(new SourceReport(kind, SourceManager.DisplayName(source), string.Empty, DownloadOutcome.Disabled, "\"enabled\": false in settings.json.", TimeSpan.Zero, false));
            }
        }
    }

    private async Task<List<SourceReport>> DownloadAsync(List<SourcePlan> plans, bool force, string verb, CancellationToken cancellationToken)
    {
        var reports = new ConcurrentBag<SourceReport>();
        using var downloader = new ListDownloader(config);

        await ConsoleUI.ProgressAsync(verb, plans.Count, "sources", async progress =>
        {
            await Parallel.ForEachAsync(plans,
                new ParallelOptions { MaxDegreeOfParallelism = config.MaxConcurrentDownloads, CancellationToken = cancellationToken },
                async (plan, ct) =>
                {
                    // Advance first in a finally block so the bar always reaches 100%, whatever happens below.
                    var stopwatch = Stopwatch.StartNew();
                    progress.SetCurrent(plan.DisplayName);
                    try
                    {
                        DownloadResult download = await downloader.DownloadAsync(new DownloadRequest(plan.Source, plan.Uri!, plan.FilePath, force), ct).ConfigureAwait(false);
                        bool cached = File.Exists(plan.FilePath);
                        reports.Add(new SourceReport(plan.Kind, plan.DisplayName, plan.FilePath, download.Outcome, download.Message, stopwatch.Elapsed, cached));
                        LogOutcome(plan, download);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        reports.Add(new SourceReport(plan.Kind, plan.DisplayName, plan.FilePath, DownloadOutcome.Cancelled, "Cancelled.", stopwatch.Elapsed, File.Exists(plan.FilePath)));
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        reports.Add(new SourceReport(plan.Kind, plan.DisplayName, plan.FilePath, DownloadOutcome.TransientFailure, ex.Message, stopwatch.Elapsed, File.Exists(plan.FilePath)));
                        TraceLogger.Log($"{plan.DisplayName}: unexpected error: {ex}", LogSeverity.Error);
                    }
                    finally
                    {
                        progress.Advance(plan.DisplayName);
                    }
                }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return [.. reports];
    }

    private static void LogOutcome(SourcePlan plan, DownloadResult download)
    {
        LogSeverity severity = download.Outcome switch
        {
            DownloadOutcome.Success or DownloadOutcome.SuccessWithoutValidator or DownloadOutcome.SkippedUpToDate => LogSeverity.Info,
            DownloadOutcome.TransientFailure or DownloadOutcome.SecurityViolation => LogSeverity.Error,
            _ => LogSeverity.Warn
        };
        TraceLogger.Log($"{plan.Kind.DisplayName()} {plan.DisplayName}: {download.Outcome.Describe()}. {download.Message}", severity);
    }

    private void BuildAndPublish(List<SourcePlan> plans, RunResult result, bool force, CancellationToken cancellationToken)
    {
        var available = new List<SourceFile>();
        foreach (SourcePlan plan in plans.Where(p => p.IsValid))
        {
            if (File.Exists(plan.FilePath) && new FileInfo(plan.FilePath).Length > 0)
            {
                available.Add(new SourceFile(plan.FilePath, plan.DisplayName, plan.Kind));
            }
            else
            {
                TraceLogger.Log($"{plan.Kind.DisplayName()} {plan.DisplayName} has no cached copy yet, so it is not included.", LogSeverity.Warn);
            }
        }

        bool hasBlockInput = available.Exists(s => s.Kind == ListKind.Block) || config.UserBlocklist.Count > 0;
        if (!hasBlockInput)
        {
            result.FailureReason = "No blocklist could be downloaded and no cached copies exist, so there is nothing to build.";
            result.FailureExitCode = result.NetworkUnavailable ? ExitCodes.NetworkConnectionFailed : ExitCodes.UpdateProcessError;
            TraceLogger.Log(result.FailureReason, LogSeverity.Error);
            return;
        }

        OutputTargets targets = OutputTargets.Default;
        string stamp = BuildStamp.Compute(config, available);
        bool outputsExist = targets.All.All(File.Exists);
        if (!force && outputsExist && string.Equals(stamp, BuildStamp.Read(), StringComparison.Ordinal))
        {
            TraceLogger.Log("No sources or settings changed since the last build. Outputs are already current.");
            return;
        }

        ListBuildResult build;
        try
        {
            build = new ListBuilder(config, targets).Build(available, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ListPublisher.DiscardTemps(targets);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ListPublisher.DiscardTemps(targets);
            result.FailureReason = $"Building the output failed: {ex.Message}. The published lists were not changed.";
            result.FailureExitCode = ExitCodes.UpdateProcessError;
            TraceLogger.Log(result.FailureReason, LogSeverity.Error);
            return;
        }
        result.Build = build;
        foreach (string warning in build.Warnings)
        {
            TraceLogger.Log(warning, LogSeverity.Warn);
        }

        string? integrityProblem = ValidateBuild(build);
        if (integrityProblem is not null)
        {
            ListPublisher.DiscardTemps(targets);
            result.FailureReason = integrityProblem + " The published lists were not changed.";
            result.FailureExitCode = ExitCodes.IntegrityCheckFailure;
            TraceLogger.Log(result.FailureReason, LogSeverity.Error);
            ShowBuildReport(build, null);
            return;
        }

        PublishResult publish = ListPublisher.Publish(targets, config.AllowRevert);
        result.Publish = publish;
        if (!publish.Success)
        {
            result.FailureReason = $"Publishing the new lists failed: {publish.Error}";
            result.FailureExitCode = ExitCodes.UpdateProcessError;
            TraceLogger.Log(result.FailureReason, LogSeverity.Error);
        }
        else
        {
            BuildStamp.Write(stamp);
            TraceLogger.Log($"Published {build.CombinedCount:N0} entries to {Paths.CombinedListFileName} (+{publish.Added:N0} / -{publish.Removed:N0} since the previous version).", LogSeverity.Notice);
        }
        ShowBuildReport(build, publish);
    }

    /// <summary>Output checks run before anything is published. Returns a reason when the build must not replace the current lists.</summary>
    private string? ValidateBuild(ListBuildResult build)
    {
        if (build.UniqueBlockEntries == 0)
        {
            return "No block entries could be read from any source.";
        }

        long removedByWhitelist = build.Whitelisted + build.NeverBlocked;
        if (build.CombinedCount == 0 && removedByWhitelist < build.UniqueBlockEntries)
        {
            return $"The {config.Format.CanonicalName()} format produced an empty list from {build.UniqueBlockEntries:N0} entries " +
                   $"({build.WildcardsDropped:N0} wildcard(s) and {build.NotApplicable:N0} entr(ies) cannot be written in this format). Check formattype.";
        }
        return null;
    }

    private static void ShowDownloadReport(RunResult result)
    {
        if (result.Sources.Count == 0)
        {
            return;
        }

        var ordered = result.Sources.OrderBy(s => s.Kind).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        ConsoleUI.ShowTable("Sources",
            ["List", "Source", "Result", "Time", "Details"],
            ordered.Select<SourceReport, IReadOnlyList<string>>(s => new[]
            {
                s.Kind.DisplayName(),
                s.DisplayName,
                ColourOutcome(s.Outcome),
                s.Elapsed == TimeSpan.Zero ? "" : $"{s.Elapsed.TotalSeconds:N1}s",
                s.Outcome.IsProblem() && s.HasCachedCopy ? s.Message + " Using the cached copy." : s.Message
            }),
            rightAligned: new HashSet<int> { 3 },
            markupColumns: new HashSet<int> { 2 });

        var counts = result.Sources.GroupBy(s => s.Outcome).OrderBy(g => g.Key).ToList();
        List<IReadOnlyList<string>> rows = counts.Select<IGrouping<DownloadOutcome, SourceReport>, IReadOnlyList<string>>(g => new[] { ColourOutcome(g.Key), g.Count().ToString("N0") }).ToList();
        rows.Add([ConsoleUI.Colour("Total configured", "bold"), result.Sources.Count.ToString("N0")]);
        string? caption = result.DownloadWarnings > 0 ? $"{result.DownloadWarnings:N0} warning(s) were logged while downloading. See the log for details." : null;
        ConsoleUI.ShowTable("Download summary", ["Result", "Count"], rows, caption, rightAligned: new HashSet<int> { 1 }, markupColumns: new HashSet<int> { 0 });

        TraceLogger.Log("Download summary: " + string.Join(", ", counts.Select(g => $"{g.Count()} {g.Key.Describe().ToLowerInvariant()}")) +
                        $" ({result.Sources.Count} configured).", LogSeverity.Info);
    }

    private static void ShowBuildReport(ListBuildResult build, PublishResult? publish)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Source files merged", build.SourceFiles.ToString("N0") },
            new[] { "Lines read", build.Parse.Lines.ToString("N0") },
            new[] { "Unique block entries", build.UniqueBlockEntries.ToString("N0") },
            new[] { "Unique allow entries", build.UniqueAllowEntries.ToString("N0") },
            new[] { "Removed by whitelist", build.Whitelisted.ToString("N0") },
            new[] { "Removed by neverBlock", build.NeverBlocked.ToString("N0") }
        };
        if (build.Compressed > 0)
        {
            rows.Add(new[] { "Removed by compression (parent already blocked)", build.Compressed.ToString("N0") });
        }
        if (build.Exceptions > 0)
        {
            rows.Add(new[] { "Exception rules added (allowed subdomain of a blocked domain)", build.Exceptions.ToString("N0") });
        }
        if (build.WildcardsStripped > 0)
        {
            rows.Add(new[] { "Wildcards written as base domain", build.WildcardsStripped.ToString("N0") });
        }
        if (build.WildcardsDropped > 0)
        {
            rows.Add(new[] { "Wildcards dropped (not supported by format)", build.WildcardsDropped.ToString("N0") });
        }
        if (build.NotApplicable > 0)
        {
            rows.Add(new[] { "Entries not applicable to format", build.NotApplicable.ToString("N0") });
        }
        rows.Add(new[] { "Invalid lines skipped", build.Parse.Invalid.ToString("N0") });
        rows.Add(new[] { "Unsupported rules skipped (cosmetic, URL path, redirect)", build.Parse.Unsupported.ToString("N0") });
        rows.Add(new[] { "Invalid domains skipped", build.Parse.InvalidDomains.ToString("N0") });
        rows.Add(new[] { "Public suffixes skipped", build.Parse.PublicSuffixesRejected.ToString("N0") });
        rows.Add(new[] { "localhost-style entries skipped", build.Parse.JunkEntries.ToString("N0") });
        rows.Add(new[] { Paths.CombinedBlockListFileName, build.BlockListCount.ToString("N0") });
        rows.Add(new[] { Paths.CombinedWhiteListFileName, build.AllowListCount.ToString("N0") });
        rows.Add(new[] { Paths.CombinedListFileName, build.CombinedCount.ToString("N0") });
        if (publish?.Success == true)
        {
            rows.Add(new[] { "Change since previous version", $"+{publish.Added:N0} / -{publish.Removed:N0}" });
        }

        ConsoleUI.ShowTable("Build", ["", "Count"], rows, rightAligned: new HashSet<int> { 1 });
        TraceLogger.Log($"Build: {build.UniqueBlockEntries:N0} unique block entries, {build.Whitelisted:N0} whitelisted, {build.NeverBlocked:N0} neverBlock, " +
                        $"{build.CombinedCount:N0} written to {Paths.CombinedListFileName}; {build.Parse.Invalid:N0} invalid and {build.Parse.Unsupported:N0} unsupported line(s) skipped.", LogSeverity.Info);
    }

    private static string ColourOutcome(DownloadOutcome outcome)
    {
        string style = outcome switch
        {
            DownloadOutcome.Success or DownloadOutcome.SuccessWithoutValidator => "green",
            DownloadOutcome.SkippedUpToDate => "blue",
            DownloadOutcome.Disabled or DownloadOutcome.Cancelled => "grey",
            DownloadOutcome.TransientFailure or DownloadOutcome.SecurityViolation => "red",
            _ => "yellow"
        };
        return ConsoleUI.Colour(outcome.Describe(), style);
    }
}
