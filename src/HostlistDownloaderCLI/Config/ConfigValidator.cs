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

using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using HostlistDownloader.IO;
using HostlistDownloader.Lists;
using HostlistDownloader.Network;
using HostlistDownloader.UI;

namespace HostlistDownloader.Config;

internal enum ValidationMode
{
    ReportOnly,
    Fix
}

internal sealed record ValidationIssue(ConfigIssueSeverity Severity, string Setting, string Value, string Problem, string? Fix);

/// <summary>
/// Checks settings.json with the same validators the runtime uses (so --validate can never pass something the
/// update then skips) and, in Fix mode, writes a corrected file after backing up the original.
/// </summary>
internal static class ConfigValidator
{
    internal static List<ValidationIssue> Validate(AppConfig config)
    {
        var issues = new List<ValidationIssue>();

        foreach (ConfigIssue issue in config.Issues)
        {
            if (issue.Message.Contains("Duplicate source", StringComparison.Ordinal))
            {
                continue; // reported per entry below, with a fix
            }
            bool fixable = issue.Severity == ConfigIssueSeverity.Warning && issue.Message.Contains(" Using ", StringComparison.Ordinal);
            issues.Add(new ValidationIssue(issue.Severity, issue.Setting, string.Empty, issue.Message, fixable ? "Save the value shown" : null));
        }

        ValidateSources(config, ListKind.Block, "blocklists", issues);
        ValidateSources(config, ListKind.Allow, "whitelists", issues);
        ValidateRules(config.UserBlocklist, "userWebsiteBlocklist", ParserOptions.Default, issues);
        ValidateRules(config.UserWhitelist, "userWebsiteWhitelist", ParserOptions.Default, issues);
        ValidateRules(config.NeverBlock, "neverBlock", ParserOptions.Default with { RejectPublicSuffixes = false }, issues);
        return issues;
    }

    /// <summary>
    /// Logs problems at startup without stopping. <paramref name="verbose"/> shows each problem on the console
    /// (update runs); otherwise the details only go to the log file so reports like --search stay readable.
    /// </summary>
    internal static void ReportAtStartup(AppConfig config, bool verbose)
    {
        List<ValidationIssue> issues = Validate(config);
        foreach (ValidationIssue issue in issues)
        {
            string value = issue.Value.Length > 0 ? $" \"{issue.Value}\"" : string.Empty;
            LogSeverity severity = !verbose ? LogSeverity.Debug : issue.Severity == ConfigIssueSeverity.Error ? LogSeverity.Error : LogSeverity.Warn;
            TraceLogger.Log($"settings.json {issue.Setting}{value}: {issue.Problem}", severity);
        }
        if (!verbose)
        {
            return;
        }
        if (issues.Count > 0)
        {
            TraceLogger.Log($"{issues.Count} problem(s) found in settings.json. Run with --validate for a report, or --validate --fix to correct them.", LogSeverity.Notice);
        }
    }

    /// <summary>--validate [--fix]. Returns 0 when the settings are (or have been made) valid.</summary>
    internal static int Run(AppConfig config, ValidationMode mode)
    {
        List<ValidationIssue> issues = Validate(config);
        if (issues.Count == 0)
        {
            TraceLogger.Log("Configuration validation passed. No issues found.", LogSeverity.Notice);
            return ExitCodes.Success;
        }

        bool fixing = mode == ValidationMode.Fix;
        ConsoleUI.ShowTable(fixing ? "settings.json problems (fixing)" : "settings.json problems",
            ["Severity", "Setting", "Value", "Problem", fixing ? "Fix applied" : "Fix (with --fix)"],
            issues.Select<ValidationIssue, IReadOnlyList<string>>(i =>
            [
                i.Severity.ToString(),
                i.Setting,
                i.Value,
                i.Problem,
                i.Fix ?? "Manual edit required"
            ]));

        foreach (ValidationIssue issue in issues)
        {
            TraceLogger.Log($"[validate] {issue.Setting} \"{issue.Value}\": {issue.Problem}", LogSeverity.Debug);
        }

        if (!fixing)
        {
            TraceLogger.Log($"{issues.Count} problem(s) found. Nothing was changed. Run with --validate --fix to apply the fixes shown.", LogSeverity.Warn);
            return ExitCodes.InvalidConfigEntry;
        }

        SettingsFile fixedSettings = ApplyFixes(config);
        string backup = Paths.SettingsFile + ".bak";
        try
        {
            File.Copy(Paths.SettingsFile, backup, overwrite: true);
            new AppConfig(fixedSettings).Save(Paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TraceLogger.Log($"Could not save the fixed settings: {ex.Message}", LogSeverity.Error);
            return ExitCodes.GeneralError;
        }

        int manual = issues.Count(i => i.Fix is null);
        TraceLogger.Log($"Fixed settings saved. The previous file was backed up to {backup}." +
                        (manual > 0 ? $" {manual} problem(s) need a manual edit." : string.Empty), LogSeverity.Notice);
        return manual > 0 ? ExitCodes.InvalidConfigEntry : ExitCodes.Success;
    }

    private static void ValidateSources(AppConfig config, ListKind kind, string setting, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceEntry source in config.Sources(kind))
        {
            if (!seen.Add(AppConfig.SourceKey(source.Url)))
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Warning, setting, source.Url, "Listed more than once.", "Remove the duplicate"));
                continue;
            }

            if (!UrlValidator.TryValidateSourceUrl(source.Url, out Uri? uri, out string? reason))
            {
                if (source.Enabled)
                {
                    issues.Add(new ValidationIssue(ConfigIssueSeverity.Error, setting, source.Url, reason ?? "Invalid URL.", "Set \"enabled\": false"));
                }
                continue;
            }

            if (UrlValidator.IsInsecure(uri!) && !config.AllowInsecureSources && source.Enabled)
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Warning, setting, source.Url,
                    "Uses http://, which is blocked while allowInsecureSources is false.", null));
            }

            if (!string.IsNullOrWhiteSpace(source.Sha256) && (source.Sha256.Trim().Length != 64 || !source.Sha256.Trim().All(Uri.IsHexDigit)))
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Error, setting, source.Url, $"sha256 \"{source.Sha256}\" is not a 64-character hex SHA-256.", "Remove sha256"));
            }

            if (source.MaxSizeMB is < 1 or > 1000)
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Warning, setting, source.Url, $"maxSizeMB {source.MaxSizeMB} must be between 1 and 1000.", "Remove maxSizeMB"));
            }
        }
    }

    private static void ValidateRules(IReadOnlyList<string> rules, string setting, ParserOptions options, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new List<HostEntry>(4);
        foreach (string rule in rules)
        {
            if (!seen.Add(rule))
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Warning, setting, rule, "Listed more than once.", "Remove the duplicate"));
                continue;
            }

            buffer.Clear();
            LineStatus status = HostEntryParser.ParseLine(rule, buffer, options);
            if (status is not (LineStatus.Parsed or LineStatus.Empty)) // Empty: a "# comment" entry, harmless
            {
                issues.Add(new ValidationIssue(ConfigIssueSeverity.Error, setting, rule,
                    "Not a valid domain or rule (expected example.com, *.example.com, example.* or ||example.com^).", "Remove the entry"));
            }
        }
    }

    private static SettingsFile ApplyFixes(AppConfig config)
    {
        SettingsFile s = config.Settings;
        string lineEnding = (s.Output.LineEnding ?? "lf").Trim().ToLowerInvariant();
        return s with
        {
            Blocklists = FixSources(s.Blocklists),
            Whitelists = FixSources(s.Whitelists),
            UserWebsiteBlocklist = FixRules(s.UserWebsiteBlocklist, ParserOptions.Default),
            UserWebsiteWhitelist = FixRules(s.UserWebsiteWhitelist, ParserOptions.Default),
            NeverBlock = FixRules(s.NeverBlock, ParserOptions.Default with { RejectPublicSuffixes = false }),
            // Out-of-range numbers are saved as the value actually used.
            MaxConcurrentDownloads = config.MaxConcurrentDownloads,
            LogExpiryInDays = config.LogExpiryInDays,
            MaxListSizeInMB = config.MaxListSizeInMB,
            UpdateCheckIntervalHours = config.UpdateCheckIntervalHours,
            Network = config.Network,
            SourceSanityCheck = config.Sanity,
            Output = s.Output with
            {
                HostsBlockIp = config.HostsBlockIp.ToString(),
                LineEnding = lineEnding is "lf" or "crlf" or "native" ? lineEnding : "lf"
            }
        };
    }

    private static SourceEntry[] FixSources(SourceEntry[] sources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SourceEntry>();
        foreach (SourceEntry source in sources)
        {
            if (!seen.Add(AppConfig.SourceKey(source.Url)))
            {
                continue;
            }

            SourceEntry fixedSource = source;
            if (!UrlValidator.TryValidateSourceUrl(source.Url, out _, out _))
            {
                fixedSource = fixedSource with { Enabled = false };
            }
            if (!string.IsNullOrWhiteSpace(source.Sha256) && (source.Sha256.Trim().Length != 64 || !source.Sha256.Trim().All(Uri.IsHexDigit)))
            {
                fixedSource = fixedSource with { Sha256 = null };
            }
            if (source.MaxSizeMB is < 1 or > 1000)
            {
                fixedSource = fixedSource with { MaxSizeMB = null };
            }
            result.Add(fixedSource);
        }
        return [.. result];
    }

    private static string[] FixRules(string[] rules, ParserOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new List<HostEntry>(4);
        var result = new List<string>();
        foreach (string rule in rules)
        {
            if (!seen.Add(rule))
            {
                continue;
            }
            buffer.Clear();
            if (HostEntryParser.ParseLine(rule, buffer, options) is LineStatus.Parsed or LineStatus.Empty)
            {
                result.Add(rule);
            }
        }
        return [.. result];
    }
}
