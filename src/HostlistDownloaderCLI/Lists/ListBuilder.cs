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
using HostlistDownloader.UI;
using System.Globalization;

namespace HostlistDownloader.Lists;

/// <summary>A cached source file to merge.</summary>
internal sealed record SourceFile(string Path, string DisplayName, ListKind Kind);

/// <summary>Final locations of the three outputs; the builder writes "&lt;path&gt;.tmp" next to each.</summary>
internal sealed record OutputTargets(string BlockList, string AllowList, string Combined)
{
    /// <summary>The files consumers read. While disabled (--disable) these are empty placeholders.</summary>
    internal static OutputTargets Published => new(Paths.CombinedBlockListFile, Paths.CombinedWhiteListFile, Paths.CombinedListFile);

    /// <summary>
    /// Where builds, publishing and --revert write. While the lists are disabled this is the ".disabled" copies, so
    /// updates keep running and --enable restores the latest lists.
    /// </summary>
    internal static OutputTargets Default => Paths.ListsDisabled
        ? new(Paths.CombinedBlockListFile + Paths.DisabledSuffix, Paths.CombinedWhiteListFile + Paths.DisabledSuffix, Paths.CombinedListFile + Paths.DisabledSuffix)
        : Published;

    internal IEnumerable<string> All => [BlockList, AllowList, Combined];
}

internal sealed class ListBuildResult
{
    public ParseStats Parse { get; } = new();
    public int SourceFiles { get; set; }
    public long UniqueBlockEntries { get; set; }
    public long UniqueAllowEntries { get; set; }
    public long Whitelisted { get; set; }
    public long NeverBlocked { get; set; }
    public long Compressed { get; set; }
    public long Exceptions { get; set; }
    public long WildcardsStripped { get; set; }
    public long WildcardsDropped { get; set; }
    public long NotApplicable { get; set; }
    public long BlockListCount { get; set; }
    public long AllowListCount { get; set; }
    public long CombinedCount { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Builds the three output files in one pass over the cached sources: parse every line into normalised entries,
/// de-duplicate on the parsed domain, apply neverBlock and the whitelist on parsed domains, optionally compress,
/// format, de-duplicate again on the formatted line (formatting can make distinct entries identical), sort, and
/// write to temp files. Nothing is published here; see <see cref="ListPublisher"/>.
/// </summary>
internal sealed class ListBuilder(AppConfig config, OutputTargets targets)
{
    private const int CancellationCheckInterval = 65_536;

    private readonly ParserOptions _parserOptions = new(config.Output.ValidateDomains, config.Output.RejectPublicSuffixes);
    private readonly OutputFormatter _formatter = new(config.Format, config.HostsBlockIp);

    internal ListBuildResult Build(IReadOnlyList<SourceFile> sources, CancellationToken cancellationToken)
    {
        var result = new ListBuildResult { SourceFiles = sources.Count };
        TraceLogger.Log($"Building {config.Format.CanonicalName()} output from {sources.Count} cached source file(s)...");

        return config.Format == OutputFormat.Raw
            ? BuildRaw(sources, result, cancellationToken)
            : BuildParsed(sources, result, cancellationToken);
    }

    private ListBuildResult BuildParsed(IReadOnlyList<SourceFile> sources, ListBuildResult result, CancellationToken cancellationToken)
    {
        var block = new Dictionary<(string, WildcardKind, EntryKind), HostEntry>();
        var allow = new Dictionary<(string, WildcardKind, EntryKind), HostEntry>();

        ReadSources(sources, result, cancellationToken, (in entry, kind) =>
        {
            if (kind == ListKind.Allow || entry.IsAllow)
            {
                allow.TryAdd(entry.Key, entry with { IsAllow = true });
            }
            else
            {
                block.TryAdd(entry.Key, entry);
            }
        });

        AddUserEntries(result, block, allow);

        DomainMatcher neverBlock = BuildNeverBlock(out List<HostEntry> neverBlockEntries, result);
        var allowMatcher = new DomainMatcher();
        foreach (HostEntry entry in allow.Values)
        {
            allowMatcher.Add(entry);
        }

        result.UniqueBlockEntries = block.Count;
        result.UniqueAllowEntries = allow.Count;

        var blockKept = new List<HostEntry>(block.Count);
        var combinedKept = new List<HostEntry>(block.Count);
        foreach (HostEntry entry in block.Values)
        {
            if (neverBlock.Matches(entry))
            {
                result.NeverBlocked++;
                continue;
            }
            blockKept.Add(entry);

            if (allowMatcher.Matches(entry))
            {
                result.Whitelisted++;
                continue;
            }
            combinedKept.Add(entry);
        }

        // Allowed subdomains of a blocked domain need explicit exceptions in formats where one entry blocks a whole tree.
        bool treeFormat = config.Format.MatchesSubdomains();
        var coveringTree = new DomainTree();
        foreach (HostEntry entry in combinedKept)
        {
            if (entry.Kind == EntryKind.Domain && (entry.Wildcard == WildcardKind.Subdomains || (treeFormat && entry.Wildcard == WildcardKind.None)))
            {
                coveringTree.Add(entry.Value);
            }
        }

        var exceptionLines = new List<string>();
        var exceptionTree = new DomainTree();
        var exceptionSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HostEntry entry in allow.Values.Concat(neverBlockEntries))
        {
            if (entry.Kind != EntryKind.Domain || entry.Wildcard is WildcardKind.AnyTld or WildcardKind.Pattern)
            {
                continue;
            }
            if (!coveringTree.HasParent(entry.Value))
            {
                continue;
            }
            string? line = _formatter.FormatException(entry);
            if (line is not null && exceptionSeen.Add(line))
            {
                exceptionLines.Add(line);
                exceptionTree.Add(entry.Value);
            }
        }
        result.Exceptions = exceptionLines.Count;

        if (config.Output.Compress && treeFormat)
        {
            blockKept = Compress(blockKept, exceptionTree, out _);
            combinedKept = Compress(combinedKept, exceptionTree, out long removed);
            result.Compressed = removed;
        }

        List<string> blockLines = FormatBlock(blockKept, countNotes: false, result);
        List<string> combinedLines = FormatBlock(combinedKept, countNotes: true, result);
        List<string> allowLines = FormatAllow(allow.Values);

        if (config.Output.Sort)
        {
            exceptionLines.Sort(StringComparer.Ordinal);
        }
        combinedLines.AddRange(exceptionLines);

        result.BlockListCount = blockLines.Count;
        result.AllowListCount = allowLines.Count;
        result.CombinedCount = combinedLines.Count;

        WriteOutputs(sources, blockLines, allowLines, combinedLines);
        return result;
    }

    private ListBuildResult BuildRaw(IReadOnlyList<SourceFile> sources, ListBuildResult result, CancellationToken cancellationToken)
    {
        // raw keeps each source line as written (comments removed), so lines are kept alongside their parsed entries.
        var blockLines = new List<(string Line, HostEntry[] Entries)>();
        var blockSeen = new HashSet<string>(StringComparer.Ordinal);
        var allowLines = new List<string>();
        var allowSeen = new HashSet<string>(StringComparer.Ordinal);
        var allowMatcher = new DomainMatcher();
        var buffer = new List<HostEntry>(8);

        void AddLine(string rawLine, ListKind kind)
        {
            buffer.Clear();
            if (HostEntryParser.ParseLine(rawLine, buffer, _parserOptions, result.Parse) != LineStatus.Parsed)
            {
                return;
            }

            string line = StripTrailingComment(rawLine.Trim());
            bool isAllow = kind == ListKind.Allow || buffer.TrueForAll(e => e.IsAllow);
            if (isAllow)
            {
                foreach (HostEntry entry in buffer)
                {
                    allowMatcher.Add(entry);
                }
                if (allowSeen.Add(line))
                {
                    allowLines.Add(line);
                }
            }
            else if (blockSeen.Add(line))
            {
                blockLines.Add((line, buffer.ToArray()));
            }
        }

        ConsoleUI.Progress("Reading source lists", sources.Count, "files", progress =>
        {
            foreach (SourceFile source in sources)
            {
                progress.SetCurrent(source.DisplayName);
                long count = 0;
                try
                {
                    foreach (string rawLine in File.ReadLines(source.Path))
                    {
                        if (++count % CancellationCheckInterval == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        AddLine(rawLine, source.Kind);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Warnings.Add($"Could not read {source.DisplayName}: {ex.Message}");
                }
                progress.Advance(source.DisplayName);
            }
        });

        foreach (string entry in config.UserBlocklist)
        {
            AddLine(entry, ListKind.Block);
        }
        foreach (string entry in config.UserWhitelist)
        {
            AddLine(entry, ListKind.Allow);
        }

        DomainMatcher neverBlock = BuildNeverBlock(out _, result);
        result.UniqueBlockEntries = blockLines.Count;
        result.UniqueAllowEntries = allowLines.Count;

        var blockOut = new List<string>(blockLines.Count);
        var combinedOut = new List<string>(blockLines.Count);
        foreach ((string line, HostEntry[] entries) in blockLines)
        {
            if (Array.TrueForAll(entries, e => neverBlock.Matches(e)))
            {
                result.NeverBlocked++;
                continue;
            }
            blockOut.Add(line);
            if (Array.TrueForAll(entries, e => allowMatcher.Matches(e)))
            {
                result.Whitelisted++;
                continue;
            }
            combinedOut.Add(line);
        }

        if (config.Output.Sort)
        {
            blockOut.Sort(StringComparer.Ordinal);
            combinedOut.Sort(StringComparer.Ordinal);
            allowLines.Sort(StringComparer.Ordinal);
        }

        result.BlockListCount = blockOut.Count;
        result.AllowListCount = allowLines.Count;
        result.CombinedCount = combinedOut.Count;
        WriteOutputs(sources, blockOut, allowLines, combinedOut);
        return result;
    }

    private delegate void EntrySink(in HostEntry entry, ListKind kind);

    private void ReadSources(IReadOnlyList<SourceFile> sources, ListBuildResult result, CancellationToken cancellationToken, EntrySink sink)
    {
        var buffer = new List<HostEntry>(8);
        ConsoleUI.Progress("Parsing source lists", sources.Count, "files", progress =>
        {
            foreach (SourceFile source in sources)
            {
                progress.SetCurrent(source.DisplayName);
                var fileStats = new ParseStats();
                try
                {
                    long count = 0;
                    foreach (string line in File.ReadLines(source.Path))
                    {
                        if (++count % CancellationCheckInterval == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        buffer.Clear();
                        if (HostEntryParser.ParseLine(line, buffer, _parserOptions, fileStats) != LineStatus.Parsed)
                        {
                            continue;
                        }
                        foreach (HostEntry entry in buffer)
                        {
                            sink(entry, source.Kind);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Warnings.Add($"Could not read {source.DisplayName}: {ex.Message}");
                }

                if (fileStats.ParsedLines == 0 && fileStats.Lines > fileStats.EmptyOrComment)
                {
                    result.Warnings.Add($"{source.DisplayName} contained no usable entries ({fileStats.Invalid:N0} invalid, {fileStats.Unsupported:N0} unsupported line(s)).");
                }
                TraceLogger.Log($"{source.DisplayName}: {fileStats.ParsedLines:N0} rule line(s), {fileStats.Invalid:N0} invalid, {fileStats.Unsupported:N0} unsupported, " +
                                $"{fileStats.InvalidDomains:N0} invalid domain(s), {fileStats.JunkEntries:N0} localhost-style entr(ies) skipped.", LogSeverity.Debug);
                result.Parse.Add(fileStats);
                progress.Advance(source.DisplayName);
            }
        });
    }

    private void AddUserEntries(ListBuildResult result,
                                Dictionary<(string, WildcardKind, EntryKind), HostEntry> block,
                                Dictionary<(string, WildcardKind, EntryKind), HostEntry> allow)
    {
        var buffer = new List<HostEntry>(4);
        foreach ((IReadOnlyList<string> entries, ListKind kind) in new[] { (config.UserBlocklist, ListKind.Block), (config.UserWhitelist, ListKind.Allow) })
        {
            foreach (string raw in entries)
            {
                buffer.Clear();
                LineStatus status = HostEntryParser.ParseLine(raw, buffer, _parserOptions, result.Parse);
                if (status == LineStatus.Empty)
                {
                    continue;
                }
                if (status != LineStatus.Parsed)
                {
                    result.Warnings.Add($"Ignored user {kind.DisplayName()} entry \"{raw}\": not a valid domain or rule.");
                    continue;
                }

                foreach (HostEntry entry in buffer)
                {
                    if (kind == ListKind.Allow || entry.IsAllow)
                    {
                        allow.TryAdd(entry.Key, entry with { IsAllow = true });
                    }
                    else
                    {
                        block.TryAdd(entry.Key, entry);
                    }
                }
            }
        }
    }

    private DomainMatcher BuildNeverBlock(out List<HostEntry> entries, ListBuildResult result)
    {
        var matcher = new DomainMatcher();
        entries = [];
        var buffer = new List<HostEntry>(4);
        var options = _parserOptions with { RejectPublicSuffixes = false };
        foreach (string raw in config.NeverBlock)
        {
            buffer.Clear();
            if (HostEntryParser.ParseLine(raw, buffer, options) != LineStatus.Parsed)
            {
                result.Warnings.Add($"Ignored neverBlock entry \"{raw}\": not a valid domain.");
                continue;
            }
            foreach (HostEntry entry in buffer)
            {
                matcher.Add(entry, protectSubdomains: true);
                entries.Add(entry with { IsAllow = true });
            }
        }
        return matcher;
    }

    private static List<HostEntry> Compress(List<HostEntry> entries, DomainTree exceptions, out long removed)
    {
        var tree = new DomainTree();
        foreach (HostEntry entry in entries)
        {
            if (entry.Kind == EntryKind.Domain && entry.Wildcard is WildcardKind.None or WildcardKind.Subdomains)
            {
                tree.Add(entry.Value);
            }
        }

        var kept = new List<HostEntry>(entries.Count);
        removed = 0;
        foreach (HostEntry entry in entries)
        {
            bool redundant = entry.Kind == EntryKind.Domain
                && entry.Wildcard is WildcardKind.None or WildcardKind.Subdomains
                && tree.HasParent(entry.Value)
                && !exceptions.HasParent(entry.Value); // keep it if an allowed domain sits between it and the blocked parent
            if (redundant)
            {
                removed++;
            }
            else
            {
                kept.Add(entry);
            }
        }
        return kept;
    }

    private List<string> FormatBlock(List<HostEntry> entries, bool countNotes, ListBuildResult result)
    {
        var seen = new HashSet<string>(entries.Count, StringComparer.Ordinal);
        var lines = new List<string>(entries.Count);
        foreach (HostEntry entry in entries)
        {
            string? line = _formatter.FormatBlock(entry, out FormatNote note);
            if (countNotes)
            {
                switch (note)
                {
                    case FormatNote.WildcardStripped:
                        result.WildcardsStripped++;
                        break;
                    case FormatNote.WildcardDropped:
                        result.WildcardsDropped++;
                        break;
                    case FormatNote.NotApplicable:
                        result.NotApplicable++;
                        break;
                }
            }
            if (line is not null && seen.Add(line))
            {
                lines.Add(line);
            }
        }

        if (config.Output.Sort)
        {
            lines.Sort(StringComparer.Ordinal);
        }
        return lines;
    }

    private List<string> FormatAllow(IEnumerable<HostEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (HostEntry entry in entries)
        {
            string? line = _formatter.FormatAllowList(entry);
            if (line is not null && seen.Add(line))
            {
                lines.Add(line);
            }
        }
        if (config.Output.Sort)
        {
            lines.Sort(StringComparer.Ordinal);
        }
        return lines;
    }

    private void WriteOutputs(IReadOnlyList<SourceFile> sources, List<string> blockLines, List<string> allowLines, List<string> combinedLines)
    {
        WriteFile(targets.BlockList, "combined blocklist", blockLines, sources, ListKind.Block);
        WriteFile(targets.AllowList, "combined whitelist", allowLines, sources, ListKind.Allow);
        WriteFile(targets.Combined, "blocklist with whitelist applied", combinedLines, sources, null);
    }

    private void WriteFile(string finalPath, string title, List<string> lines, IReadOnlyList<SourceFile> sources, ListKind? onlyKind)
    {
        string tempPath = AtomicFile.TempPathFor(finalPath);
        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = new StreamWriter(stream, AtomicFile.Utf8NoBom) { NewLine = config.NewLine };

        string p = config.Format.CommentPrefix();
        if (config.Output.Header)
        {
            writer.WriteLine($"{p} {AppInfo.Name} {AppInfo.ShortVersion} - {title}");
            writer.WriteLine($"{p} Generated: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
            writer.WriteLine($"{p} Format: {config.Format.CanonicalName()} | Entries: {lines.Count.ToString("N0", CultureInfo.InvariantCulture)}");
            List<SourceFile> relevant = [.. sources.Where(s => onlyKind is null || s.Kind == onlyKind)];
            writer.WriteLine($"{p} Sources: {relevant.Count} list(s), {config.UserBlocklist.Count} user block rule(s), {config.UserWhitelist.Count} user allow rule(s)");
            foreach (SourceFile source in relevant)
            {
                writer.WriteLine($"{p}   {source.Kind.DisplayName()}: {LogRedactor.Redact(source.DisplayName)}");
            }
            writer.WriteLine(p);
        }

        if (config.Format == OutputFormat.Rpz)
        {
            string serial = DateTime.UtcNow.ToString("yyMMddHHmm", CultureInfo.InvariantCulture);
            writer.WriteLine("$TTL 300");
            writer.WriteLine($"@ IN SOA localhost. root.localhost. ({serial} 3600 600 86400 300)");
            writer.WriteLine("  IN NS localhost.");
        }

        foreach (string line in lines)
        {
            writer.WriteLine(line);
        }
    }

    private static string StripTrailingComment(string line)
    {
        if (line.StartsWith("||", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal))
        {
            return line;
        }
        int hash = line.IndexOf('#');
        return hash > 0 ? line[..hash].TrimEnd() : line;
    }
}
