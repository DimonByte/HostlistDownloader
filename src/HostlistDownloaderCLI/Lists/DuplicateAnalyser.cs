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
using HostlistDownloader.UI;

namespace HostlistDownloader.Lists;

/// <summary>
/// Reports overlap between cached sources. Blocklists are compared with blocklists and whitelists with whitelists;
/// a domain that is in both a blocklist and a whitelist is an override, not a redundancy, and is reported
/// separately. Lines are compared after parsing, so "0.0.0.0 x.com", "||x.com^" and "x.com" are the same entry.
/// </summary>
internal static class DuplicateAnalyser
{
    private const int SampleSize = 15;

    private sealed record LoadedList(string FileName, string DisplayName, ListKind Kind, HashSet<string> Entries);

    internal static int ScanAll()
    {
        List<LoadedList> lists = LoadAll();
        if (lists.Count == 0)
        {
            TraceLogger.Log("No cached lists found. Run an update first.", LogSeverity.Warn);
            return ExitCodes.FileMissing;
        }

        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            List<LoadedList> group = [.. lists.Where(l => l.Kind == kind)];
            if (group.Count < 2)
            {
                continue;
            }

            // How many lists of this kind contain each entry.
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LoadedList list in group)
            {
                foreach (string entry in list.Entries)
                {
                    occurrences[entry] = occurrences.TryGetValue(entry, out int n) ? n + 1 : 1;
                }
            }

            var rows = new List<(LoadedList List, int Shared, double Percent)>();
            foreach (LoadedList list in group)
            {
                int shared = list.Entries.Count(e => occurrences[e] > 1);
                double percent = list.Entries.Count == 0 ? 0 : shared * 100.0 / list.Entries.Count;
                rows.Add((list, shared, percent));
            }

            ConsoleUI.ShowTable($"{Capitalise(kind.DisplayName())} overlap",
                ["Source", "File", "Entries", "Also in another " + kind.DisplayName(), "Unique", "Note"],
                rows.OrderByDescending(r => r.Percent).Select<(LoadedList List, int Shared, double Percent), IReadOnlyList<string>>(r =>
                [
                    r.List.DisplayName,
                    r.List.FileName,
                    r.List.Entries.Count.ToString("N0"),
                    $"{r.Shared:N0} ({r.Percent:F1}%)",
                    (r.List.Entries.Count - r.Shared).ToString("N0"),
                    r.List.Entries.Count > 0 && r.Shared == r.List.Entries.Count
                        ? "Fully covered by the other " + kind.DisplayName() + "s"
                        : string.Empty
                ]),
                caption: "\"Fully covered\" lists add nothing on their own; if two lists cover each other, keep one. Use --dupanalyse <file> for details.",
                rightAligned: new HashSet<int> { 2, 3, 4 });
        }

        ShowConflicts(lists, target: null);
        return ExitCodes.Success;
    }

    internal static int AnalyseOne(string target)
    {
        List<LoadedList> lists = LoadAll();
        LoadedList? subject = lists.FirstOrDefault(l => l.FileName.Equals(target, StringComparison.OrdinalIgnoreCase))
            ?? lists.FirstOrDefault(l => l.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase))
            ?? lists.FirstOrDefault(l => l.DisplayName.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (subject is null)
        {
            TraceLogger.Log($"No cached list matches \"{target}\". Use --dupscan to see file names.", LogSeverity.Warn);
            return ExitCodes.MissingOrInvalidParameters;
        }

        ConsoleUI.ShowRule($"{subject.DisplayName} ({subject.FileName}): {subject.Entries.Count:N0} entries");

        var rows = new List<IReadOnlyList<string>>();
        foreach (LoadedList other in lists.Where(l => l.Kind == subject.Kind && !ReferenceEquals(l, subject)))
        {
            int shared = subject.Entries.Count(other.Entries.Contains);
            if (shared == 0)
            {
                continue;
            }

            string note = other.Entries.Count > 0 && shared == other.Entries.Count
                ? $"Fully covered by {subject.DisplayName}"
                : shared == subject.Entries.Count
                    ? $"Covers all of {subject.DisplayName}"
                    : $"{other.Entries.Count - shared:N0} entries not in {subject.DisplayName}";

            rows.Add(
            [
                other.DisplayName,
                shared.ToString("N0"),
                $"{shared * 100.0 / Math.Max(1, subject.Entries.Count):F1}%",
                $"{shared * 100.0 / Math.Max(1, other.Entries.Count):F1}%",
                note,
                string.Join(", ", subject.Entries.Where(other.Entries.Contains).Take(3))
            ]);
        }

        if (rows.Count == 0)
        {
            ConsoleUI.Line($"No entries of {subject.DisplayName} appear in any other {subject.Kind.DisplayName()}.", "green");
        }
        else
        {
            ConsoleUI.ShowTable($"Overlap with other {subject.Kind.DisplayName()}s",
                ["Other source", "Shared", "% of this list", "% of other list", "Note", "Examples"],
                rows.OrderByDescending(r => long.Parse(r[1], System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture)),
                rightAligned: new HashSet<int> { 1, 2, 3 });
        }

        ShowConflicts(lists, subject);
        return ExitCodes.Success;
    }

    /// <summary>Entries that a blocklist blocks and a whitelist allows: the whitelist wins, which is usually intended.</summary>
    private static void ShowConflicts(List<LoadedList> lists, LoadedList? target)
    {
        List<LoadedList> blocks = [.. lists.Where(l => l.Kind == ListKind.Block && (target is null || ReferenceEquals(l, target) || target.Kind == ListKind.Allow))];
        List<LoadedList> allows = [.. lists.Where(l => l.Kind == ListKind.Allow && (target is null || ReferenceEquals(l, target) || target.Kind == ListKind.Block))];

        var rows = new List<IReadOnlyList<string>>();
        foreach (LoadedList block in blocks)
        {
            foreach (LoadedList allow in allows)
            {
                List<string> overridden = [.. block.Entries.Where(allow.Entries.Contains).Take(SampleSize + 1)];
                if (overridden.Count == 0)
                {
                    continue;
                }
                int total = overridden.Count > SampleSize ? block.Entries.Count(allow.Entries.Contains) : overridden.Count;
                rows.Add([block.DisplayName, allow.DisplayName, total.ToString("N0"), string.Join(", ", overridden.Take(3))]);
            }
        }

        if (rows.Count > 0)
        {
            ConsoleUI.ShowTable("Blocklist entries overridden by a whitelist",
                ["Blocklist", "Whitelist", "Entries", "Examples"], rows,
                caption: "These are not duplicates: the whitelist entry removes them from the combined list.",
                rightAligned: new HashSet<int> { 2 });
        }
    }

    private static List<LoadedList> LoadAll()
    {
        var lists = new List<LoadedList>();
        var buffer = new List<HostEntry>(8);
        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            string folder = Paths.ListFolder(kind);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            Dictionary<string, string> manifest = SourceManager.ReadManifest(kind);
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string fileName = Path.GetFileName(path);
                if (Paths.IsInternalFile(fileName) || !SourceManager.IsManagedFileName(fileName))
                {
                    continue;
                }

                var entries = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(path))
                {
                    buffer.Clear();
                    if (HostEntryParser.ParseLine(line, buffer, ParserOptions.Lenient) != LineStatus.Parsed)
                    {
                        continue;
                    }
                    foreach (HostEntry entry in buffer)
                    {
                        if (entry.Kind == EntryKind.Domain && (kind == ListKind.Allow || !entry.IsAllow))
                        {
                            entries.Add(entry.DisplayText);
                        }
                    }
                }

                string display = manifest.TryGetValue(fileName, out string? url) ? SourceManager.DisplayName(new Config.SourceEntry { Url = url }) : fileName;
                lists.Add(new LoadedList(fileName, display, kind, entries));
            }
        }
        return lists;
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
