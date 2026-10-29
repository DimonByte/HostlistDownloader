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
using System.Text.RegularExpressions;

namespace HostlistDownloader.Lists;

/// <summary>--search: finds which sources and rules match a domain, using the same parser as the build.</summary>
internal static class SearchManager
{
    private sealed record Match(ListKind Kind, string Source, string Line, string How);

    internal static int Search(string query, AppConfig config)
    {
        if (!TryNormaliseQuery(query, out string domain, out bool wildcardQuery))
        {
            TraceLogger.Log($"\"{query}\" is not a domain. Usage: --search example.com", LogSeverity.Warn);
            return ExitCodes.MissingOrInvalidParameters;
        }

        TraceLogger.Log($"Searching cached lists for '{domain}'...");
        string filter = PrefilterToken(domain);
        var matches = new List<Match>();
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
                if (Paths.IsInternalFile(fileName))
                {
                    continue;
                }
                string source = manifest.TryGetValue(fileName, out string? url) ? SourceManager.DisplayName(new SourceEntry { Url = url }) : fileName;

                try
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        if (line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue; // cheap pre-filter before parsing
                        }
                        AddMatches(line, kind, source, domain, wildcardQuery, buffer, matches);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TraceLogger.Log($"Could not read {fileName}: {ex.Message}", LogSeverity.Warn);
                }
            }
        }

        foreach (string rule in config.UserBlocklist)
        {
            AddMatches(rule, ListKind.Block, "settings.json userWebsiteBlocklist", domain, wildcardQuery, buffer, matches);
        }
        foreach (string rule in config.UserWhitelist)
        {
            AddMatches(rule, ListKind.Allow, "settings.json userWebsiteWhitelist", domain, wildcardQuery, buffer, matches);
        }
        foreach (string rule in config.NeverBlock)
        {
            AddMatches(rule, ListKind.Allow, "settings.json neverBlock", domain, wildcardQuery, buffer, matches);
        }

        bool inFinalList = false;
        if (File.Exists(Paths.CombinedListFile))
        {
            var finalMatches = new List<Match>();
            foreach (string line in File.ReadLines(Paths.CombinedListFile))
            {
                if (line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    AddMatches(line, ListKind.Block, Paths.CombinedListFileName, domain, wildcardQuery, buffer, finalMatches);
                }
            }
            inFinalList = finalMatches.Exists(m => m.Kind == ListKind.Block && m.How != "parent domain (exact-match formats ignore this)");
        }

        if (matches.Count == 0)
        {
            ConsoleUI.Line($"'{domain}' is not in any cached source or user rule.", "green");
        }
        else
        {
            ConsoleUI.ShowTable($"Matches for '{domain}'",
                ["List", "Source", "Rule", "Match"],
                matches.OrderBy(m => m.Kind).ThenBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
                       .Select<Match, IReadOnlyList<string>>(m => [m.Kind.DisplayName(), m.Source, m.Line, m.How]));
        }

        bool blocked = matches.Exists(m => m.Kind == ListKind.Block);
        bool allowed = matches.Exists(m => m.Kind == ListKind.Allow);
        string verdict = (blocked, allowed) switch
        {
            (true, true) => "Blocked by at least one source but also allowed; the allow rule wins in the combined list.",
            (true, false) => "Blocked by at least one source.",
            (false, true) => "Only matched by allow rules, so it is not blocked.",
            _ => "Not affected by your lists."
        };
        ConsoleUI.Line(verdict, "bold");
        ConsoleUI.Line($"Present in {Paths.CombinedListFileName}: {(inFinalList ? "YES" : "NO")}", inFinalList ? "red" : "green");

        TraceLogger.Log($"Search for '{domain}': {matches.Count(m => m.Kind == ListKind.Block)} block match(es), " +
                        $"{matches.Count(m => m.Kind == ListKind.Allow)} allow match(es), in final list: {inFinalList}.");
        return ExitCodes.Success;
    }

    private static void AddMatches(string line, ListKind kind, string source, string domain, bool wildcardQuery, List<HostEntry> buffer, List<Match> matches)
    {
        buffer.Clear();
        if (HostEntryParser.ParseLine(line, buffer, ParserOptions.Lenient) != LineStatus.Parsed)
        {
            return;
        }

        foreach (HostEntry entry in buffer)
        {
            string? how = Describe(entry, domain, wildcardQuery);
            if (how is not null)
            {
                matches.Add(new Match(kind == ListKind.Allow || entry.IsAllow ? ListKind.Allow : ListKind.Block, source, line.Trim(), how));
                return; // one match per line is enough for attribution
            }
        }
    }

    /// <summary>How (if at all) <paramref name="entry"/> applies to the searched domain.</summary>
    internal static string? Describe(in HostEntry entry, string domain, bool wildcardQuery)
    {
        if (entry.Kind != EntryKind.Domain)
        {
            return null;
        }

        string value = entry.Value;
        if (wildcardQuery)
        {
            // "*.example.com": anything at or under example.com
            return value == domain || value.EndsWith("." + domain, StringComparison.Ordinal) ? "under searched domain" : null;
        }

        switch (entry.Wildcard)
        {
            case WildcardKind.None:
                if (value == domain)
                {
                    return "exact";
                }
                if (domain.EndsWith("." + value, StringComparison.Ordinal))
                {
                    return entry.IncludeSubdomains ? "parent rule (covers subdomains)" : "parent domain (exact-match formats ignore this)";
                }
                return null;

            case WildcardKind.Subdomains:
                return domain.EndsWith("." + value, StringComparison.Ordinal) ? "wildcard *." + value : null;

            case WildcardKind.AnyTld:
                return domain.StartsWith(value + ".", StringComparison.Ordinal) ? "wildcard " + value + ".*" : null;

            default:
                string pattern = "^" + Regex.Escape(value).Replace("\\*", "[a-z0-9._-]*") + "$";
                try
                {
                    return Regex.IsMatch(domain, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) ? "pattern " + value : null;
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }
        }
    }

    internal static bool TryNormaliseQuery(string query, out string domain, out bool wildcardQuery)
    {
        domain = query.Trim();
        wildcardQuery = false;

        if (domain.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(domain, UriKind.Absolute, out Uri? uri))
        {
            domain = uri.Host; // pasted a URL
        }

        if (domain.StartsWith("*.", StringComparison.Ordinal))
        {
            wildcardQuery = true;
            domain = domain[2..];
        }

        domain = domain.TrimEnd('.').ToLowerInvariant();
        if (!DomainRules.TryConvertToAscii(domain, out string ascii, out _))
        {
            return false;
        }
        domain = ascii;
        return domain.Length > 0 && (DomainRules.IsValidHostname(domain) || (!domain.Contains('.') && domain.All(char.IsAsciiLetterOrDigit)));
    }

    /// <summary>
    /// Cheap substring pre-filter applied before parsing a line: the label left of the TLD (ads.example.com → example),
    /// which also appears in example.* and *.example.com rules.
    /// </summary>
    private static string PrefilterToken(string domain)
    {
        string[] labels = domain.Split('.');
        return labels.Length >= 2 ? labels[^2] : domain;
    }
}
