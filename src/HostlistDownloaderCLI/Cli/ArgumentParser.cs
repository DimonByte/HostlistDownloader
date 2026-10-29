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
using System.Text;

namespace HostlistDownloader.Cli;

/// <summary>Invalid command line. Carries the exit code to use.</summary>
internal sealed class CliException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Parses the command line into a <see cref="CliRequest"/> without side effects (no logging, no file access), so
/// flags like --quiet and --data-dir are known before anything else runs. Options use --long names. The old
/// /slash spellings are still accepted for existing scheduled tasks, with a deprecation notice.
/// </summary>
internal static class ArgumentParser
{
    private const int MaxArgumentLength = 4096;

    private const string SectionCommands = "Commands (run one at a time; with no command, the lists are updated)";
    private const string SectionUpdate = "Update options";
    private const string SectionGlobal = "Global options";
    private const string SectionSources = "Edit sources in settings.json";
    private const string SectionRules = "Edit user rules in settings.json";

    private sealed class Builder
    {
        public CommandKind? Command;
        public string? CommandName;
        public string? Argument;
        public bool Quiet;
        public bool Debug;
        public bool NoBanner;
        public bool NoUpdateCheck;
        public bool PurgeLogs;
        public bool Fresh;
        public bool Fix;
        public string? DataDirectory;
        public readonly List<ConfigEdit> Edits = [];
        public readonly List<string> Deprecated = [];

        public void SetCommand(CommandKind kind, string name, string? argument = null)
        {
            bool repeatEdit = Command == CommandKind.EditConfig && kind == CommandKind.EditConfig;
            if (Command is not null && !repeatEdit)
            {
                throw new CliException($"Only one command can be run at a time, but both {CommandName} and {name} were given.", ExitCodes.InvalidArguments);
            }
            Command = kind;
            CommandName = name;
            Argument = argument;
        }

        public void AddEdit(string name, EditAction action, ConfigTarget target, string value)
        {
            SetCommand(CommandKind.EditConfig, name);
            Edits.Add(new ConfigEdit(action, target, value));
        }
    }

    private sealed record OptionSpec(
        string Name,
        string[] Aliases,
        string[] LegacyAliases,
        string? ValueName,
        string Section,
        string Help,
        Action<Builder, string, string?> Apply);

    private static readonly OptionSpec[] Options =
    [
        // Commands
        new("--help", ["-h", "-?"], ["/help", "/h", "/?"], null, SectionCommands, "Show this help.", (b, n, _) => b.SetCommand(CommandKind.Help, n)),
        new("--version", ["-v"], [], null, SectionCommands, "Show the version.", (b, n, _) => b.SetCommand(CommandKind.Version, n)),
        new("--merge", ["--regenerate"], ["/merge", "/compile", "/regenerate", "/re"], null, SectionCommands,
            "Rebuild the combined lists from cached sources without downloading (e.g. after changing formattype or a user rule).",
            (b, n, _) => b.SetCommand(CommandKind.Merge, n)),
        new("--search", ["-s"], ["/search", "/s"], "domain", SectionCommands, "Show which sources and rules match a domain.",
            (b, n, v) => b.SetCommand(CommandKind.Search, n, v)),
        new("--stats", [], ["/stats"], null, SectionCommands, "Show output file and configuration statistics.", (b, n, _) => b.SetCommand(CommandKind.Stats, n)),
        new("--diff", [], ["/diff"], null, SectionCommands, "Show how much the combined list changed in the last publish.", (b, n, _) => b.SetCommand(CommandKind.Diff, n)),
        new("--revert", [], ["/revert"], null, SectionCommands, "Swap the combined lists with their backups (needs \"allowRevert\": true).", (b, n, _) => b.SetCommand(CommandKind.Revert, n)),
        new("--update", [], ["/update"], null, SectionCommands, "Check for a new HostlistDownloader version and install it (Windows).", (b, n, _) => b.SetCommand(CommandKind.SelfUpdate, n)),
        new("--validate", [], ["/validate"], null, SectionCommands, "Check settings.json and report problems. Add --fix to correct them.", (b, n, _) => b.SetCommand(CommandKind.Validate, n)),
        new("--dupscan", ["--duplicate-scan"], ["/duplicatescan", "/dupscan"], null, SectionCommands, "Report how much each cached list overlaps with the others.",
            (b, n, _) => b.SetCommand(CommandKind.DuplicateScan, n)),
        new("--dupanalyse", ["--dupanalyze"], ["/dupanalyse", "/analysedup"], "file", SectionCommands, "Detailed overlap report for one cached list (file name or source name).",
            (b, n, v) => b.SetCommand(CommandKind.DuplicateAnalyse, n, v)),
        new("--getsource", [], ["/getsource", "/gs"], "file", SectionCommands, "Show the URL a cached file was downloaded from.",
            (b, n, v) => b.SetCommand(CommandKind.GetSource, n, v)),

        // Update options
        new("--fresh", [], ["/fresh", "/fr"], null, SectionUpdate, "Re-download every source, ignoring ETags and size sanity checks, then rebuild.", (b, _, _) => b.Fresh = true),
        new("--fix", [], [], null, SectionUpdate, "With --validate: apply the fixes (settings.json is backed up first).", (b, _, _) => b.Fix = true),

        // Global options
        new("--quiet", ["-q"], ["/quiet", "/q"], null, SectionGlobal, "No console output (the log file is still written).", (b, _, _) => b.Quiet = true),
        new("--debug", [], ["/debug"], null, SectionGlobal, "Show detailed output and write debug lines to the log.", (b, _, _) => b.Debug = true),
        new("--no-banner", [], [], null, SectionGlobal, "Do not print the version banner.", (b, _, _) => b.NoBanner = true),
        new("--no-update-check", [], [], null, SectionGlobal, "Skip the automatic check for a new version this run.", (b, _, _) => b.NoUpdateCheck = true),
        new("--purge-logs", [], ["/purge", "/p"], null, SectionGlobal, "Delete all log files before running.", (b, _, _) => b.PurgeLogs = true),
        new("--data-dir", [], [], "path", SectionGlobal, "Folder for settings.json, logs and lists (default: next to the program if settings.json is there, else the user data folder).",
            (b, _, v) => b.DataDirectory = v),

        // Sources
        new("--add-blocklist", [], ["/addblocklist", "/ab"], "url", SectionSources, "Add a blocklist source.", (b, n, v) => b.AddEdit(n, EditAction.Add, ConfigTarget.Blocklist, v!)),
        new("--remove-blocklist", [], ["/removeblocklist", "/rb"], "url", SectionSources, "Remove a blocklist source.", (b, n, v) => b.AddEdit(n, EditAction.Remove, ConfigTarget.Blocklist, v!)),
        new("--add-whitelist", [], ["/addwhitelist", "/aw"], "url", SectionSources, "Add a whitelist source.", (b, n, v) => b.AddEdit(n, EditAction.Add, ConfigTarget.Whitelist, v!)),
        new("--remove-whitelist", [], ["/removewhitelist", "/rw"], "url", SectionSources, "Remove a whitelist source.", (b, n, v) => b.AddEdit(n, EditAction.Remove, ConfigTarget.Whitelist, v!)),

        // User rules
        new("--add-user-block", [], ["/adduserblock", "/aub"], "rule", SectionRules, "Block a domain (example.com, *.example.com, ||example.com^).",
            (b, n, v) => b.AddEdit(n, EditAction.Add, ConfigTarget.UserBlock, v!)),
        new("--remove-user-block", [], ["/removeuserblock", "/rub"], "rule", SectionRules, "Remove a user block rule.",
            (b, n, v) => b.AddEdit(n, EditAction.Remove, ConfigTarget.UserBlock, v!)),
        new("--add-user-allow", [], ["/adduserwhitelist", "/auw"], "rule", SectionRules, "Allow a domain even if a blocklist contains it.",
            (b, n, v) => b.AddEdit(n, EditAction.Add, ConfigTarget.UserAllow, v!)),
        new("--remove-user-allow", [], ["/removeuserwhitelist", "/ruw"], "rule", SectionRules, "Remove a user allow rule.",
            (b, n, v) => b.AddEdit(n, EditAction.Remove, ConfigTarget.UserAllow, v!)),
    ];

    internal static CliRequest Parse(IReadOnlyList<string> args)
    {
        var builder = new Builder();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Length > MaxArgumentLength)
            {
                throw new CliException($"An argument is longer than {MaxArgumentLength} characters.", ExitCodes.InvalidArguments);
            }

            string name = arg;
            string? inlineValue = null;
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            OptionSpec spec = Find(name, out bool legacy)
                ?? throw new CliException($"Unknown argument '{arg}'. Run --help to see the available options.", ExitCodes.InvalidArguments);

            if (legacy)
            {
                builder.Deprecated.Add($"{arg} (use {spec.Name})");
            }

            string? value = null;
            if (spec.ValueName is not null)
            {
                value = inlineValue;
                if (value is null && i + 1 < args.Count && Find(args[i + 1], out _) is null)
                {
                    value = args[++i];
                }
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new CliException($"{spec.Name} needs a value: {spec.Name} <{spec.ValueName}>", ExitCodes.MissingOrInvalidParameters);
                }
                if (value.Length > MaxArgumentLength)
                {
                    throw new CliException($"The value for {spec.Name} is longer than {MaxArgumentLength} characters.", ExitCodes.InvalidArguments);
                }
            }
            else if (inlineValue is not null)
            {
                throw new CliException($"{spec.Name} does not take a value.", ExitCodes.InvalidArguments);
            }

            spec.Apply(builder, spec.Name, value);
        }

        if (builder.Fix && builder.Command != CommandKind.Validate)
        {
            throw new CliException("--fix can only be used together with --validate.", ExitCodes.InvalidArguments);
        }
        if (builder.Fresh && builder.Command is not null)
        {
            throw new CliException($"--fresh only applies to an update run, not to {builder.CommandName}.", ExitCodes.InvalidArguments);
        }

        return new CliRequest(
            builder.Command ?? CommandKind.Update,
            new GlobalOptions(builder.Quiet, builder.Debug, builder.NoBanner, builder.DataDirectory, builder.NoUpdateCheck, builder.PurgeLogs),
            new RunOptions(builder.Fresh),
            builder.Argument,
            builder.Fix ? ValidationMode.Fix : ValidationMode.ReportOnly,
            builder.Edits,
            builder.Deprecated);
    }

    internal static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.Name} {AppInfo.ShortVersion}");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  HostlistDownloader [command] [options]");

        foreach (IGrouping<string, OptionSpec> section in Options.GroupBy(o => o.Section))
        {
            sb.AppendLine();
            sb.AppendLine(section.Key.ToUpperInvariant() + ":");
            foreach (OptionSpec option in section)
            {
                string names = string.Join(", ", new[] { option.Name }.Concat(option.Aliases));
                if (option.ValueName is not null)
                {
                    names += $" <{option.ValueName}>";
                }
                sb.AppendLine($"  {names,-34} {option.Help}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("The old /slash spellings (e.g. /q, /fresh, /search) still work but are deprecated.");
        sb.AppendLine("Exit codes and every settings.json option are described in the README on GitHub.");
        return sb.ToString();
    }

    private static OptionSpec? Find(string name, out bool legacy)
    {
        legacy = false;
        foreach (OptionSpec option in Options)
        {
            if (option.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || option.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return option;
            }
        }
        foreach (OptionSpec option in Options)
        {
            if (option.LegacyAliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                legacy = true;
                return option;
            }
        }
        return null;
    }
}
