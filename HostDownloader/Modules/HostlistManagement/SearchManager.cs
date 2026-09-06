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

using HostlistDownloader.Modules.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.WindowsSystem
{
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class ManifestJsonSerializerContext : JsonSerializerContext
    {
    }

    public static class SearchManager
    {
        public enum MatchListType
        {
            Blocklist,
            Whitelist
        }

        public readonly record struct SearchMatch(
            MatchListType ListType,
            string SourceFile,
            string? SourceUrl,
            string MatchedLine,
            bool IsWildcardMatch);

        public static void Search(string domain)
        {
            domain = NormalizeDomain(domain);
            if (string.IsNullOrWhiteSpace(domain))
            {
                TraceLogger.Log("No domain supplied to /search. Usage: HostlistDownloader /search example.com", Enums.StatusSeverityType.Warning);
                return;
            }

            TraceLogger.Log($"Searching configured lists for '{domain}'...");

            var blockMatches = SearchFolder(IOManager.BlockListFolderLocation, domain, MatchListType.Blocklist);
            var whiteMatches = SearchFolder(IOManager.WhiteListFolderLocation, domain, MatchListType.Whitelist);

            if (ConfigManager.Instance != null)
            {
                foreach (var entry in ConfigManager.Instance.UserWebsiteBlocklist ?? [])
                    CheckUserEntry(entry, domain, MatchListType.Blocklist, blockMatches);

                foreach (var entry in ConfigManager.Instance.UserWebsiteWhitelist ?? [])
                    CheckUserEntry(entry, domain, MatchListType.Whitelist, whiteMatches);
            }
            else
            {
                TraceLogger.Log("ConfigReader not initialized. Skipping user-defined lists.", Enums.StatusSeverityType.Warning);
            }

            bool inFinalCombinedList = IsDomainInCompiledList(IOManager.CombinedListFileLocation, domain);

            Console.WriteLine();
            Console.WriteLine($"=== Search results for '{domain}' ===");

            if (blockMatches.Count == 0 && whiteMatches.Count == 0)
            {
                Console.WriteLine("No matching entries found in any configured blocklist or whitelist source.");
                Console.WriteLine("This domain is not currently affected by any of your configured lists.");
                TraceLogger.Log($"Search for '{domain}' found no matches in any source.", Enums.StatusSeverityType.Warning);
                return;
            }

            if (blockMatches.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Found in {blockMatches.Count} blocklist source(s):");
                foreach (var match in blockMatches)
                {
                    string origin = match.SourceUrl ?? match.SourceFile;
                    string kind = match.IsWildcardMatch ? "wildcard" : "exact";
                    Console.WriteLine($"  [{kind}] {origin}");
                    Console.WriteLine($"      matched line: \"{match.MatchedLine}\"");
                }
            }

            if (whiteMatches.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Found in {whiteMatches.Count} whitelist source(s) (these override the blocklist):");
                foreach (var match in whiteMatches)
                {
                    string origin = match.SourceUrl ?? match.SourceFile;
                    string kind = match.IsWildcardMatch ? "wildcard" : "exact";
                    Console.WriteLine($"  [{kind}] {origin}");
                    Console.WriteLine($"      matched line: \"{match.MatchedLine}\"");
                }
            }

            Console.WriteLine();
            if (blockMatches.Count > 0 && whiteMatches.Count > 0)
            {
                Console.WriteLine("Verdict: this domain is blocked by at least one source, but also whitelisted -");
                Console.WriteLine("the whitelist entry takes precedence, so it should NOT appear in the final combined list.");
            }
            else if (blockMatches.Count > 0)
            {
                Console.WriteLine("Verdict: this domain should appear in the final combined blocklist.");
            }
            else if (whiteMatches.Count > 0)
            {
                Console.WriteLine("Verdict: this domain is only present in a whitelist source, so it is not blocked.");
            }

            Console.WriteLine($"Currently present in {Path.GetFileName(IOManager.CombinedListFileLocation)}: {(inFinalCombinedList ? "YES" : "NO")}");
            Console.WriteLine();

            TraceLogger.Log($"Search for '{domain}' complete: {blockMatches.Count} blocklist match(es), {whiteMatches.Count} whitelist match(es), present in final combined list: {inFinalCombinedList}.");
        }

        private static List<SearchMatch> SearchFolder(string folder, string domain, MatchListType listType)
        {
            var results = new List<SearchMatch>();
            if (!Directory.Exists(folder))
                return results;

            var manifest = LoadSourceManifest(folder);
            var files = Directory.GetFiles(folder, "*.*")
                .Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals("_sources.json", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                manifest.TryGetValue(fileName, out var sourceUrl);

                IEnumerable<string> lines;
                try
                {
                    lines = File.ReadLines(file);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Could not read {file} during search: {ex.Message}", Enums.StatusSeverityType.Warning);
                    continue;
                }

                foreach (var rawLine in lines)
                {
                    string line = StripComment(rawLine).Trim();
                    if (string.IsNullOrEmpty(line))
                        continue;

                    foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryMatchToken(token, domain, out bool isWildcard))
                        {
                            results.Add(new SearchMatch(listType, fileName, sourceUrl, line, isWildcard));
                            break; // One match per line is sufficient for attribution
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Checks a single domain/token against the search query. Handles exact matches, 
        /// parent/subdomain matches, and wildcards in both directions.
        /// </summary>
        private static bool TryMatchToken(string token, string domain, out bool isWildcard)
        {
            isWildcard = false;

            // 1. Handle wildcard in the FILE entry (e.g., "*.example.com")
            if (token.Contains('*'))
            {
                isWildcard = true;
                string pattern = "^" + Regex.Escape(token).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(domain, pattern, RegexOptions.IgnoreCase);
            }

            // 2. Handle wildcard in the SEARCH domain (e.g., "*.example.com)")
            if (domain.Contains('*'))
            {
                isWildcard = true;
                string pattern = "^" + Regex.Escape(domain).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(token, pattern, RegexOptions.IgnoreCase);
            }

            // 3. Exact match
            if (string.Equals(token, domain, StringComparison.OrdinalIgnoreCase))
                return true;

            // 4. Parent-domain match: entry "example.com" matches domain "ads.example.com"
            // This is standard hosts-file behavior for blocklists.
            if (domain.EndsWith("." + token, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static void CheckUserEntry(string entry, string domain, MatchListType type, List<SearchMatch> results)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;

            string clean = StripComment(entry).Trim();
            if (string.IsNullOrEmpty(clean)) return;

            // User entries may contain multiple domains/rule patterns per line
            foreach (var token in clean.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryMatchToken(token, domain, out bool isWildcard))
                {
                    results.Add(new SearchMatch(type, "User Config", null, entry.Trim(), isWildcard));
                    break; // One match per user line is enough
                }
            }
        }

        private static bool IsDomainInCompiledList(string combinedListFile, string domain)
        {
            if (!File.Exists(combinedListFile))
                return false;

            foreach (var rawLine in File.ReadLines(combinedListFile))
            {
                string line = StripComment(rawLine).Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryMatchToken(token, domain, out _))
                        return true;
                }
            }

            return false;
        }

        private static Dictionary<string, string> LoadSourceManifest(string folder)
        {
            string manifestPath = Path.Combine(folder, "_sources.json");
            if (!File.Exists(manifestPath))
                return [];

            try
            {
                var json = File.ReadAllText(manifestPath);
                return JsonSerializer.Deserialize(json, ManifestJsonSerializerContext.Default.DictionaryStringString) ?? [];
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Could not read source manifest {manifestPath}: {ex.Message}", Enums.StatusSeverityType.Warning);
                return [];
            }
        }

        private static string StripComment(string line)
        {
            int commentIndex = line.IndexOf('#');
            return commentIndex >= 0 ? line[..commentIndex] : line;
        }

        private static string NormalizeDomain(string domain)
        {
            domain = domain.Trim();

            // Preserve leading wildcard if the user intentionally searches for one
            bool hasLeadingWildcard = domain.StartsWith('*');

            // Strip URL scheme/paths if accidentally pasted
            if (Uri.TryCreate(domain, UriKind.Absolute, out var uri))
                domain = uri.Host;

            return hasLeadingWildcard ? "*" + domain.TrimEnd('.').ToLowerInvariant()
                                      : domain.TrimEnd('.').ToLowerInvariant();
        }
    }
}