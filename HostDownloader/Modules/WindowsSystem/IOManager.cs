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
using HostlistDownloader.Modules.HostlistManagement.Generation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.WindowsSystem
{
    internal class IOManager
    {
        public static readonly string HostfilesLocation = "hostfiles";
        public static readonly string BlockListFolderLocation = "hostfiles/blocklist";
        public static readonly string WhiteListFolderLocation = "hostfiles/whitelist";
        public static readonly string CombinedListFolderLocation = "hostfiles/combined";
        public static readonly string CombinedBlockListFileLocation = "hostfiles/combined/HLDcombined-blocklist.txt";
        public static readonly string CombinedWhiteListFileLocation = "hostfiles/combined/HLDcombined-whitelist.txt";
        public static readonly string CombinedListFileLocation = "hostfiles/combined/HLDcombined-list.txt";
        public static readonly string CombinedBlockListFileLocationTemp = "hostfiles/combined/HLDcombined-blocklist-TEMP.txt";
        public static readonly string CombinedWhiteListFileLocationTemp = "hostfiles/combined/HLDcombined-whitelist-TEMP.txt";
        public static readonly string CombinedListFileLocationTemp = "hostfiles/combined/HLDcombined-list-TEMP.txt";
        public static readonly string LogsLocation = "logs";
        public static readonly string UpdateStatsLocation = "logs/updatestats.txt";
        public static readonly string SettingJsonFileLocation = "settings.json";
        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

        public static void CreateNecessaryDirectoriesAndFiles()
        {
            string[] directories = [LogsLocation, HostfilesLocation, BlockListFolderLocation, WhiteListFolderLocation, CombinedListFolderLocation];

            bool ShowHelp = false;
            foreach (string dir in directories)
            {
                if (!Directory.Exists(dir))
                {
                    ShowHelp = true;
                    try
                    {
                        Directory.CreateDirectory(dir);
                        TraceLogger.Log($"Created directory: {dir} - First time setup will be started.", Enums.StatusSeverityType.Debug);
                    }
                    catch (Exception ex)
                    {
                        TraceLogger.Log($"Error creating directory {dir}: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.DirectoryCreationFailed);
                    }
                }
            }
            string[] files = [CombinedListFileLocationTemp, CombinedBlockListFileLocationTemp, CombinedWhiteListFileLocationTemp];
            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    ShowHelp = true;
                    try
                    {
                        var directory = Path.GetDirectoryName(file);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        File.Create(file).Dispose();
                        TraceLogger.Log($"Created file: {file}", Enums.StatusSeverityType.Debug);
                    }
                    catch (Exception ex)
                    {
                        TraceLogger.Log($"Error creating file {file}: {ex}", Enums.StatusSeverityType.Error);
                    }
                }
            }
            if (!File.Exists(IOManager.SettingJsonFileLocation))
            {
                ShowHelp = true;
                ConfigManager.CreateDefaultConfig(IOManager.SettingJsonFileLocation);
            }
            if (ShowHelp)
            {
                Console.WriteLine("[!] Configuration files and folders have been created in the directory where this program is stored. (settings.json)\nPlease refer to the documentation on the main GitHub page of HostlistDownloader to configure. Once configured, run HostlistDownloader again. HostlistDownloader will now exit.");
                Environment.Exit(ErrorCodes.GeneralError);
            }
        }

        public static void CheckForInvalidConfig()
        {
            try
            {
                string appDir = Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string currentDir = Path.GetFullPath(Environment.CurrentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(appDir, currentDir, StringComparison.OrdinalIgnoreCase))
                {
                    TraceLogger.Log($"HostlistDownloader must be run from the directory where it is stored.\nApplication Path: {appDir} - Path that was passed: {currentDir}. To fix this, you must CD to the path in your terminal where HostlistDownloader is stored '{appDir}' and try again.", Enums.StatusSeverityType.Fatal, ErrorCodes.WrongExecutionDirectory);
                }

                bool corruptionDetected = false;
                // Validates full URIs (http/https/ftp) OR bare domains with optional paths
                var urlOrDomainRegex = new Regex(@"^(https?:\/\/|ftp:\/\/)?[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?)+(?:\/[\w\-.*~=+@!$&'()*+,;:%]*)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                // Validates strictly RFC-standard DNS hostnames/tlds only (e.g., google.com, sub.domain.co.uk)
                var domainRegex = new Regex(@"^(?:(?:xn--)?[a-z0-9]+(?:-+[a-z0-9]+)*\.)+[a-z]{2,}$", RegexOptions.Compiled);

                // Stage 1: Validate public blocklist & whitelist sources
                var validBlocklistUrls = new List<string>();
                foreach (var url in ConfigManager.Instance.Blocklists.Select(u => u.Trim()))
                {
                    if (!string.IsNullOrEmpty(url) && (Uri.TryCreate(url, UriKind.Absolute, out _) || urlOrDomainRegex.IsMatch(url)))
                    {
                        validBlocklistUrls.Add(url);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid blocklist URL/Domain: {url.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                var validWhitelistUrls = new List<string>();
                foreach (var url in ConfigManager.Instance.Whitelist.Select(u => u.Trim()))
                {
                    if (!string.IsNullOrEmpty(url) && (Uri.TryCreate(url, UriKind.Absolute, out _) || urlOrDomainRegex.IsMatch(url)))
                    {
                        validWhitelistUrls.Add(url);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid whitelist URL/Domain: {url.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                // Stage 2: Validate user-defined website domains
                var validUserBlocklistDomains = new List<string>();
                foreach (var domain in ConfigManager.Instance.UserWebsiteBlocklist.Select(d => d.Trim()))
                {
                    if (!string.IsNullOrEmpty(domain) && domainRegex.IsMatch(domain))
                    {
                        validUserBlocklistDomains.Add(domain);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid user blocklist domain: {domain.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                var validUserWhitelistDomains = new List<string>();
                foreach (var domain in ConfigManager.Instance.UserWebsiteWhitelist.Select(d => d.Trim()))
                {
                    if (!string.IsNullOrEmpty(domain) && domainRegex.IsMatch(domain))
                    {
                        validUserWhitelistDomains.Add(domain);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid user whitelist domain: {domain.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                // Rebuild config file with valid entries if corruption was found
                if (corruptionDetected)
                {
                    var newConfig = new Settings
                    {
                        Blocklists = [.. validBlocklistUrls],
                        Whitelist = [.. validWhitelistUrls],
                        Formattype = ConfigManager.Instance.Formattype,
                        UserWebsiteBlocklist = [.. validUserBlocklistDomains],
                        UserWebsiteWhitelist = [.. validUserWhitelistDomains],
                        MaxDownloadThreads = ConfigManager.Instance.MaxDownloadThreads,
                        LogExpiryInDays = ConfigManager.Instance.LogExpiryInDays
                    };

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    string json = JsonSerializer.Serialize(newConfig, SettingsJsonSerializerContext.Default.Settings);
                    File.WriteAllText(SettingJsonFileLocation, json);
                    TraceLogger.Log("Configuration file has been updated with only valid entries.", Enums.StatusSeverityType.Information);
                }

                if (corruptionDetected)
                {
                    TraceLogger.Log("Corruption was detected during startup and was removed from the affected configuration entries. Please review the logs for details.", Enums.StatusSeverityType.Warning);
                }
                else
                {
                    TraceLogger.Log("Configuration corruption check completed. No issues found.", Enums.StatusSeverityType.Debug);
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Corruption Check failure! {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationCorrupted);
            }
        }

        //public static void AddToIniFile(string iniFilePath, string domain)
        //{
        //    var directory = Path.GetDirectoryName(iniFilePath);
        //    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        //        Directory.CreateDirectory(directory);
        //    File.AppendAllText(iniFilePath, $"{domain}{Environment.NewLine}");
        //}

        public static void MergeFiles(string sourceFolder, string outputFile)
        {
            var files = Directory.GetFiles(sourceFolder, "*.*")
                .Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals("_sources.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (files.Length == 0)
            {
                TraceLogger.Log($"No files found to merge in {sourceFolder}.", Enums.StatusSeverityType.Warning);
                return;
            }

            try
            {
                using var writer = new StreamWriter(outputFile);
                Stopwatch watch = Stopwatch.StartNew();
                ConsoleProgress.ShowOperationProgress(0, files.Length, "Merging files");

                int processedFiles = 0;
                foreach (var file in files)
                {
                    TraceLogger.Log($"Merging file: {file}");
                    using var reader = new StreamReader(file);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                        {
                            writer.WriteLine(line);
                        }
                    }
                    processedFiles++;
                    ConsoleProgress.ShowOperationProgress(processedFiles, files.Length, "Merging files");
                }
                writer.Flush();
                watch.Stop();
                TraceLogger.Log($"Merge files completed in {watch.Elapsed.TotalSeconds} seconds.");
            }
            catch (UnauthorizedAccessException ex1)
            {
                TraceLogger.Log($"Access denied when trying to merge files into {outputFile}: {ex1.Message}", Enums.StatusSeverityType.Error);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error merging files into {outputFile}: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Deletes the specified files (and their .etag companions) from the given folder.
        /// </summary>
        public static void DeleteFileAlongWithETag(string listFolderLocation, List<string> fileNames)
        {
            foreach (var fileName in fileNames)
            {
                var filePath = Path.Combine(listFolderLocation, fileName);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        TraceLogger.Log($"Deleted removed source file: {fileName}");
                    }

                    // Also remove the .etag file if present
                    var etagPath = filePath + ".etag";
                    if (File.Exists(etagPath))
                    {
                        File.Delete(etagPath);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to delete {fileName}: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }
        }

        public static IEnumerable<string> ReadLinesFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return [];

            return File.ReadLines(filePath)
                      .Select(line => line.Trim())
                      .Where(line => !string.IsNullOrEmpty(line));
        }

        public static HashSet<string> ReadLinesFromFileCached(string filePath)
        {
            lock (_cacheLock)
            {
                if (_fileLineCache.TryGetValue(filePath, out var cachedLines))
                {
                    return cachedLines;
                }

                var lines = new HashSet<string>(ReadLinesFromFile(filePath), StringComparer.OrdinalIgnoreCase);
                _fileLineCache[filePath] = lines;
                return lines;
            }
        }

        public static List<string> ReadUrlsFromFile(string filePath)
        {
            var urls = new List<string>();

            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || filePath.StartsWith('#'))
                    return null!;

                urls.Add(filePath.Trim());

                if (urls.Count == 0)
                {
                    TraceLogger.Log($"No URLs in {filePath}.", Enums.StatusSeverityType.Warning);
                }

                return urls;
            }
            catch (Exception ex)
            {
                HostListManager.ProblemDuringUpdate = true;
                TraceLogger.Log($"Error reading URLs from {filePath}: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.InvalidConfigEntry);
                return urls;
            }
        }

        public static void ClearTempFiles(string folder)
        {
            //HACK:
            //For some bizarre reason unbeknownst to me, IOManager.ClearFiles(IOManager.BlockListFolderLocation); in hostlistmanager.cs (44) causes the entire program to skip the majority
            //of the files in its directory when doing Directory.GetFiles if the Where(F check is present, even though there shouldn't be a computational difference.
            //It made sense in IOManager when I was trying to implement a ClearFiles deletion attempt system, which included Task.Wait - Since the thread would wait and cause havok.
            //I HAVE to duplicate the ClearFiles code from above plus the ONE change where it filters it based on combined. This fixes the problem.
            //I honestly don't know why and I don't even want to know. It's fixed, and I'm happy.
            var files = Directory.GetFiles(folder, "*.*").Where(f => !Path.GetFileName(f).StartsWith("HLDcombined-", StringComparison.OrdinalIgnoreCase)); /*.Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase));*/
            foreach (var file in files)
            {
                try
                {
                    TraceLogger.Log($"{file} deleted.");
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error deleting file {file}: {ex}", Enums.StatusSeverityType.Error);
                }
            }
            TraceLogger.Log($"Cleared all files in folder: {folder}");
        }

        public static string FormatBytes(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return string.Format("{0:0.##} {1}", len, sizes[order]);
        }

        /// <summary>
        /// Runs a duplicate check across all downloaded hostlists.
        /// </summary>
        public static void RunDuplicateCheck()
        {
            TraceLogger.Log("Starting Duplicate Analysis...", Enums.StatusSeverityType.Information);
            var blockListFolder = IOManager.BlockListFolderLocation;
            var whiteListFolder = IOManager.WhiteListFolderLocation;
            var allLists = new List<(string Name, HashSet<string> Lines)>();

            try
            {
                if (Directory.Exists(blockListFolder))
                {
                    foreach (var file in Directory.GetFiles(blockListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IsInternalFile(fileName)) continue;

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
                        if (IsInternalFile(fileName)) continue;

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

                TraceLogger.Log($"Analyzing {allLists.Count} hostlists for duplicates...", Enums.StatusSeverityType.Information);
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
                    TraceLogger.Log("No significant duplicates found.", Enums.StatusSeverityType.Information);
                    return;
                }

                // Sort results by total duplicate percentage (highest first)
                results.Sort((a, b) => b.TotalPercentage.CompareTo(a.TotalPercentage));

                foreach (var result in results)
                {
                    string statusIcon = result.TotalPercentage > 50 ? "!!" : result.TotalPercentage > 10 ? "! " : "  ";
                    TraceLogger.Log($"[{statusIcon}] {result.TargetName} is {result.TotalPercentage:F1}% duplicated", Enums.StatusSeverityType.Information);

                    foreach (var (SourceName, Count, Percentage) in result.Overlaps)
                    {
                        TraceLogger.Log($"{SourceName}: {Percentage:F1}% ({Count:N0} entries)", Enums.StatusSeverityType.Debug);
                    }
                }

                TraceLogger.Log("Duplicate analysis complete. Use /getsource \"<source_name>\" to retrieve source information, and /analysedup \"<source_name>\" to analyze duplicates for a specific source.", Enums.StatusSeverityType.Information);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error during duplicate check: {ex.Message}", Enums.StatusSeverityType.Error);
            }
        }

        private static bool IsInternalFile(string fileName)
        {
            return fileName.Equals("_sources.json", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".etag", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("combined", StringComparison.OrdinalIgnoreCase);
        }

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
        public static void AnalyseDuplicate(string targetFileName)
        {
            TraceLogger.Log($"Starting deep duplicate analysis for: {targetFileName}...", Enums.StatusSeverityType.Information);

            var blockListFolder = IOManager.BlockListFolderLocation;
            var whiteListFolder = IOManager.WhiteListFolderLocation;
            var allLists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(blockListFolder))
                {
                    foreach (var file in Directory.GetFiles(blockListFolder))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IsInternalFile(fileName)) continue;

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
                        if (IsInternalFile(fileName)) continue;

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
                    TraceLogger.Log($"No duplicates found for '{targetFileName}'. It is unique.", Enums.StatusSeverityType.Information);
                    return;
                }
                TraceLogger.Log($"Found duplicates in {duplicateMap.Count} other hostlists.", Enums.StatusSeverityType.Information);

                var sortedSources = duplicateMap.OrderBy(x => x.Value.Count, Comparer<int>.Default).Reverse();
                foreach (var (sourceName, dupLines) in sortedSources)
                {
                    int dupCount = dupLines.Count;
                    double targetOverlapPercentage = (dupCount / (double)targetLines.Count) * 100;

                    if (!allLists.TryGetValue(sourceName, out var sourceLines)) continue;

                    double sourceRedundancyPercentage = (dupCount / (double)sourceLines.Count) * 100;
                    int sourceUniqueEntries = sourceLines.Count - dupCount;

                    TraceLogger.Log($"--- Duplicate Source: {sourceName} ({dupCount} lines, {targetOverlapPercentage:F1}% overlap with Target) ---");

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
                        TraceLogger.Log($"   Consider removing '{sourceName}' as it is fully covered by '{targetFileName}'.");
                    }
                    else
                    {
                        // Optional: Inform user that while it overlaps, it still has unique content
                        TraceLogger.Log($"   Note: '{sourceName}' contains {sourceUniqueEntries} unique entries not found in '{targetFileName}'.");
                    }
                }

                TraceLogger.Log("Deep duplicate analysis complete. Use /getsource \"<source_name>\" to retrieve individual source files.", Enums.StatusSeverityType.Information);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error during deep duplicate analysis: {ex.Message}", Enums.StatusSeverityType.Error);
                TraceLogger.Log(ex.ToString(), Enums.StatusSeverityType.Error);
            }
        }
    }
}