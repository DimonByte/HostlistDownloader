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
using HostlistDownloader.Modules.WindowsSystem.IO;

namespace HostlistDownloader.Modules.HostlistManagement
{
    internal class HostlistDuplicateAnalyser
    {
        /// <summary>
        /// Helper class to hold analysis results for sorting and formatting
        /// </summary>
        private class DuplicateResult
        {
            public string TargetName { get; set; } = "";
            public double TotalPercentage { get; set; }
            public List<(string SourceName, int Count, double Percentage)> Overlaps { get; set; } = [];
        }

        /// <summary>
        /// Performs an in-depth analysis of duplicate entries for a specific hostlist.
        /// Identifies exactly which lines are duplicated and which source files contain them.
        /// </summary>
        /// <param name="targetFileName">The filename to analyze for duplicates.</param>
        internal static void CheckDuplicationOnSpecificHostlist(string targetFileName)
        {
            TraceLogger.Log($"Starting deep duplicate analysis for: {targetFileName}...", Enums.StatusSeverityType.Notice);

            var blockListFolder = Paths.BlockListFolderLocation;
            var whiteListFolder = Paths.WhiteListFolderLocation;
            var allLists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(blockListFolder))
                {
                    foreach (var file in Directory.GetFiles(blockListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IOManager.IsInternalFile(fileName)) continue;

                        var lines = IOManager.ReadLinesFromFileCached(file);
                        if (lines != null && lines.Count > 0)
                            allLists[fileName] = lines;
                    }
                }

                if (Directory.Exists(whiteListFolder))
                {
                    foreach (var file in Directory.GetFiles(whiteListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IOManager.IsInternalFile(fileName)) continue;

                        var lines = IOManager.ReadLinesFromFileCached(file);
                        if (lines != null && lines.Count > 0)
                            allLists[fileName] = lines;
                    }
                }

                if (!allLists.ContainsKey(targetFileName))
                {
                    TraceLogger.Log($"Target file '{targetFileName}' not found in loaded lists.", Enums.StatusSeverityType.Warning);
                    return;
                }
                if (!allLists.TryGetValue(targetFileName, out var targetLines))
                {
                    TraceLogger.Log($"Failed to retrieve lines for '{targetFileName}'.", Enums.StatusSeverityType.Error);
                    return;
                }
                var duplicateMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in allLists)
                {
                    var sourceName = kvp.Key;
                    var sourceLines = kvp.Value;

                    if (string.Equals(targetFileName, sourceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var line in targetLines)
                    {
                        if (sourceLines.Contains(line))
                        {
                            if (!duplicateMap.TryGetValue(sourceName, out var list))
                            {
                                list = [];
                                duplicateMap[sourceName] = list;
                            }
                            list.Add(line);
                        }
                    }
                }

                if (duplicateMap.Count == 0)
                {
                    TraceLogger.Log($"No duplicates found for '{targetFileName}'. It is unique.", Enums.StatusSeverityType.Notice);
                    return;
                }
                TraceLogger.Log($"Found duplicates in {duplicateMap.Count} other hostlists.", Enums.StatusSeverityType.Notice);

                var sortedSources = duplicateMap.OrderBy(x => x.Value.Count, Comparer<int>.Default).Reverse();
                foreach (var (sourceName, dupLines) in sortedSources)
                {
                    int dupCount = dupLines.Count;
                    double targetOverlapPercentage = (dupCount / (double)targetLines.Count) * 100;

                    if (!allLists.TryGetValue(sourceName, out var sourceLines)) continue;

                    double sourceRedundancyPercentage = (dupCount / (double)sourceLines.Count) * 100;
                    int sourceUniqueEntries = sourceLines.Count - dupCount;

                    TraceLogger.Log($"--- Duplicate Source: {sourceName} ({dupCount} lines, {targetOverlapPercentage:F1}% overlap with Target) ---", Enums.StatusSeverityType.Notice);

                    int displayLimit = Math.Min(20, dupLines.Count);
                    for (int i = 0; i < displayLimit; i++)
                    {
                        TraceLogger.Log($"  - {dupLines[i]}", Enums.StatusSeverityType.Debug);
                    }

                    if (dupCount > displayLimit)
                    {
                        TraceLogger.Log($"  ... and {dupCount - displayLimit} more duplicate entries.", Enums.StatusSeverityType.Debug);
                    }

                    if (sourceRedundancyPercentage >= 100.0)
                    {
                        TraceLogger.Log($"Redundant: '{sourceName}' is 100% redundant (contains no unique entries).", Enums.StatusSeverityType.Warning);
                        TraceLogger.Log($"   Consider removing '{sourceName}' as it is fully covered by '{targetFileName}'.", Enums.StatusSeverityType.Notice);
                    }
                    else
                    {
                        // Optional: Inform user that while it overlaps, it still has unique content
                        TraceLogger.Log($"   Note: '{sourceName}' contains {sourceUniqueEntries} unique entries not found in '{targetFileName}'.", Enums.StatusSeverityType.Notice);
                    }
                }

                TraceLogger.Log("Deep duplicate analysis complete. Use /getsource \"<source_name>\" to retrieve individual source files.", Enums.StatusSeverityType.Notice);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error during deep duplicate analysis: {ex.Message}", Enums.StatusSeverityType.Error);
                TraceLogger.Log(ex.ToString(), Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Runs a duplicate check across all downloaded hostlists.
        /// </summary>
        internal static void CheckDuplicationAcrossHostlists()
        {
            TraceLogger.Log("Starting Duplicate Analysis...", Enums.StatusSeverityType.Notice);
            var blockListFolder = Paths.BlockListFolderLocation;
            var whiteListFolder = Paths.WhiteListFolderLocation;
            var allLists = new List<(string Name, HashSet<string> Lines)>();

            try
            {
                if (Directory.Exists(blockListFolder))
                {
                    foreach (var file in Directory.GetFiles(blockListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IOManager.IsInternalFile(fileName)) continue;

                        TraceLogger.Log($"Loading {fileName} for analysis...", Enums.StatusSeverityType.Debug);
                        var lines = IOManager.ReadLinesFromFileCached(file);
                        allLists.Add((fileName, lines));
                    }
                }

                if (Directory.Exists(whiteListFolder))
                {
                    foreach (var file in Directory.GetFiles(whiteListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IOManager.IsInternalFile(fileName)) continue;

                        TraceLogger.Log($"Loading {fileName} for analysis...", Enums.StatusSeverityType.Debug);
                        var lines = IOManager.ReadLinesFromFileCached(file);
                        allLists.Add((fileName, lines));
                    }
                }

                if (allLists.Count < 2)
                {
                    TraceLogger.Log("Not enough hostlists found to perform duplicate comparison.", Enums.StatusSeverityType.Warning);
                    return;
                }

                TraceLogger.Log($"Analyzing {allLists.Count} hostlists for duplicates...", Enums.StatusSeverityType.Notice);
                var results = new List<DuplicateResult>();
                foreach (var currentList in allLists)
                {
                    var (Name, Lines) = currentList;
                    if (Lines == null || Lines.Count == 0) continue;

                    var overlaps = new List<(string SourceName, int Count, double Percentage)>();
                    var uniqueDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var source in allLists)
                    {
                        if (string.Equals(Name, source.Name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var (SourceName, SourceLines) = source;

                        int sharedCount = 0;
                        foreach (var line in Lines)
                        {
                            if (SourceLines.Contains(line))
                            {
                                sharedCount++;
                                uniqueDuplicates.Add(line);
                            }
                        }

                        if (sharedCount > 0)
                        {
                            double percentage = (sharedCount / (double)Lines.Count) * 100;
                            overlaps.Add((SourceName, sharedCount, percentage));
                        }
                    }
                    if (uniqueDuplicates.Count > 0)
                    {
                        double totalDupPercentage = (uniqueDuplicates.Count / (double)Lines.Count) * 100;

                        results.Add(new DuplicateResult
                        {
                            TargetName = Name,
                            TotalPercentage = totalDupPercentage,
                            Overlaps = [.. overlaps.OrderByDescending(o => o.Percentage)]
                        });
                    }
                }

                if (results.Count == 0)
                {
                    TraceLogger.Log("No significant duplicates found.", Enums.StatusSeverityType.Notice);
                    return;
                }

                // Sort results by total duplicate percentage (highest first)
                results.Sort((a, b) => b.TotalPercentage.CompareTo(a.TotalPercentage));

                foreach (var result in results)
                {
                    string statusIcon = result.TotalPercentage > 50 ? "!!" : result.TotalPercentage > 10 ? "! " : "  ";
                    TraceLogger.Log($"[{statusIcon}] {result.TargetName} is {result.TotalPercentage:F1}% duplicated", Enums.StatusSeverityType.Notice);

                    foreach (var (SourceName, Count, Percentage) in result.Overlaps)
                    {
                        TraceLogger.Log($"{SourceName}: {Percentage:F1}% ({Count:N0} entries)", Enums.StatusSeverityType.Debug);
                    }
                }

                TraceLogger.Log("Duplicate analysis complete. Use /getsource \"<source_name>\" to retrieve source information, and /analysedup \"<source_name>\" to analyze duplicates for a specific source.", Enums.StatusSeverityType.Notice);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error during duplicate check: {ex.Message}", Enums.StatusSeverityType.Error);
            }
        }
    }
}