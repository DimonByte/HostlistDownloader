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
using HostlistDownloader.UI;
using System.Globalization;

namespace HostlistDownloader.IO;

/// <summary>--stats and --diff reports. Missing files are reported as missing instead of failing the whole report.</summary>
internal static class StatsReporter
{
    internal static int ShowStats(AppConfig config)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (string path in Paths.CombinedOutputFiles)
        {
            string name = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                rows.Add(new[] { name, "missing", "", "" });
                continue;
            }

            var info = new FileInfo(path);
            long entries = ListPublisher.ReadEntries(path).LongCount();
            rows.Add(new[] { name, entries.ToString("N0"), IOHelpers.FormatBytes(info.Length), info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) });
        }
        ConsoleUI.ShowTable("Output files", ["File", "Entries", "Size", "Last updated"], rows, rightAligned: new HashSet<int> { 1, 2 });

        ConsoleUI.ShowTable("Configuration", ["", "Count"],
        [
            new[] { "Blocklist sources (enabled)", $"{config.EnabledSources(ListKind.Block).Count:N0} of {config.Blocklists.Count:N0}" },
            new[] { "Whitelist sources (enabled)", $"{config.EnabledSources(ListKind.Allow).Count:N0} of {config.Whitelists.Count:N0}" },
            new[] { "User block rules", config.UserBlocklist.Count.ToString("N0") },
            new[] { "User allow rules", config.UserWhitelist.Count.ToString("N0") },
            new[] { "neverBlock rules", config.NeverBlock.Count.ToString("N0") },
            new[] { "Output format", config.Format.ToString() },
            new[] { "Data folder", Paths.Root }
        ], rightAligned: new HashSet<int> { 1 });

        TraceLogger.Log("Stats report shown.", LogSeverity.Debug);
        return ExitCodes.Success;
    }

    internal static int ShowDiff()
    {
        if (!File.Exists(Paths.UpdateStatsFile))
        {
            TraceLogger.Log("No previous run results found. The change summary is written after the lists are published.", LogSeverity.Warn);
            return ExitCodes.FileMissing;
        }

        string[] lines = File.ReadAllLines(Paths.UpdateStatsFile);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        if (values.TryGetValue("added", out string? added) && values.TryGetValue("removed", out string? removed))
        {
            string when = values.TryGetValue("generatedUtc", out string? generated)
                && DateTimeOffset.TryParse(generated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
                ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "the last publish";
            TraceLogger.Log($"Change at {when}: +{FormatCount(added)} entries added, -{FormatCount(removed)} entries removed.", LogSeverity.Notice);
        }
        else if (lines.Length > 0 && long.TryParse(lines[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long legacyDifference))
        {
            TraceLogger.Log($"Last run changed the combined list by {legacyDifference:N0} lines.", LogSeverity.Notice);
        }
        else
        {
            TraceLogger.Log("The previous run results could not be read.", LogSeverity.Warn);
            return ExitCodes.GeneralError;
        }
        return ExitCodes.Success;
    }

    private static string FormatCount(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number.ToString("N0", CultureInfo.InvariantCulture) : value;
}
