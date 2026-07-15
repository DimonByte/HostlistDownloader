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
using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.DownloadSystem
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;
        private static bool hasUpdates = false;

        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

        public static void StartListProcessing(bool forceMode)
        {
            TraceLogger.Log("Starting list processing...", Enums.StatusSeverityType.Information);

            // Use ConfigReader to get configuration values instead of IOManager
            string[] blockListIni = [.. ConfigReader.Instance.Blocklists];
            string[] whiteListIni = [.. ConfigReader.Instance.Whitelist];
            string[] userblockListIni = [.. ConfigReader.Instance.UserWebsiteBlocklist];
            string[] userwhiteListIni = [.. ConfigReader.Instance.UserWebsiteWhitelist];

            if (blockListIni.Length == 0 && whiteListIni.Length == 0)
            {
                TraceLogger.Log("Blocklist and Whitelist config are not configured.", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationFileMissing);
                return;
            }

            if (blockListIni.Length != 0)
            {
                TraceLogger.Log("Blocklist is configured. Updating blocklists...");
                // Since we're using the ConfigReader now, we need to adapt how we handle blocklist files
                ProcessDownloadLists(blockListIni,
                    IOManager.BlockListFolderLocation,
                    IOManager.CombinedBlockListFileLocation, forceMode).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring");
            }

            if (userblockListIni.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                // Process multiple user-blocklist files
                MergeUserDefinedDomains(IOManager.CombinedBlockListFileLocation, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring");
            }

            if (whiteListIni.Length != 0)
            {
                TraceLogger.Log("Whitelist is configured. Updating whitelists...");
                // Process multiple whitelist files
                ProcessDownloadLists(whiteListIni,
                    IOManager.WhiteListFolderLocation,
                    IOManager.CombinedWhiteListFileLocation, forceMode).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring");
            }

            if (userwhiteListIni.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
                // Process multiple user-whitelist files
                MergeUserDefinedDomains(IOManager.CombinedWhiteListFileLocation, isBlocklist: false);
            }
            else
            {
                TraceLogger.Log("User Whitelist not configured. Ignoring");
            }

            if (hasUpdates)
            {
                GenerateCombinedList();
            }

            TraceLogger.Log("Host lists update completed!");
        }

        private static void MergeUserDefinedDomains(string CombinedLocation, bool isBlocklist)
        {
            TraceLogger.Log($"Attempting to merge user defined website lists for {CombinedLocation}...");
            try
            {
                // Get existing lines from combined list for uniqueness check
                //var existingLines = ReadLinesFromFileCached(CombinedLocation);
                IReadOnlyList<string> existingLines;

                if (isBlocklist)
                {
                    existingLines = ConfigReader.Instance.UserWebsiteBlocklist;
                }
                else
                {
                    existingLines = ConfigReader.Instance.UserWebsiteWhitelist;
                }
                TraceLogger.Log($"Existing lines count in user defined list: {existingLines.Count:N0}");

                var filteredLines = new List<string>();
                string[] trimmedURLLines = new string[existingLines.Count];

                for (int i = 0; i < existingLines.Count; i++)
                {
                    trimmedURLLines[i] = existingLines.ElementAt(i).Trim();
                    if (string.IsNullOrWhiteSpace(trimmedURLLines[i]) || trimmedURLLines[i].StartsWith('#'))
                    {
                        TraceLogger.Log($"Null User URL. {CombinedLocation}. Ignoring.", Enums.StatusSeverityType.Warning);
                        return;
                    }
                    //Check if the trimmed line starts or ends with a * to allow for wildcard entries, if so, we will not check for uniqueness since it is a wildcard entry
                    if (trimmedURLLines[i].StartsWith('*') || trimmedURLLines[i].EndsWith('*'))
                    {
                        if (!existingLines.Contains(trimmedURLLines[i]))
                        {
                            filteredLines.Add(trimmedURLLines[i]);
                        }
                    }
                    else
                    {
                        filteredLines.Add(trimmedURLLines[i]);
                    }
                    //TraceLogger.Log($"User defined list entry: {trimmedURLLines[i]}");
                }
                //If not check for exact match on the line. So contain wont work here.

                if (filteredLines.Count != 0)
                {
                    File.WriteAllLines(CombinedLocation, filteredLines);
                    TraceLogger.Log($"Merged user defined lists on {CombinedLocation} (added {filteredLines.Count} unique entries)");
                    hasUpdates = true;
                }
                else
                {
                    TraceLogger.Log("No new unique entries to add to the combined list.");
                }
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Fault during update of lists! {ex}", Enums.StatusSeverityType.Error);
            }
        }

        private static async Task ProcessDownloadLists(string[] iniLocations, string ListFolderLocation, string CombinedListLocation, bool forceMode)
        {
            TraceLogger.Log($"Starting download for INI files. ListFolderLocation: {ListFolderLocation} | CombinedListLocation: {CombinedListLocation}");

            var allUrls = new List<string>();

            foreach (var iniLocation in iniLocations)
            {
                var urls = ReadUrlsFromFile(iniLocation);
                if (urls != null)
                {
                    allUrls.AddRange(urls);
                }
                else
                {
                    TraceLogger.Log($"Null URL value in {ListFolderLocation} config. Ignoring value.", Enums.StatusSeverityType.Warning);
                }
            }

            if (allUrls.Count == 0)
            {
                TraceLogger.Log("No URLs found in the configuration files.", Enums.StatusSeverityType.Warning);
                return;
            }

            DateTime startTime = DateTime.Now;
            Stopwatch watch = Stopwatch.StartNew();
            int completedCount = 0;
            //Semaphore with a maximum of 3 concurrent downloads
            SemaphoreSlim semaphore = new(ConfigReader.Instance.MaxDownloadThreads, ConfigReader.Instance.MaxDownloadThreads);

            List<Task> tasks = [];

            foreach (var url in allUrls)
            {
                var threadCount = ++completedCount;
                var fileName = $"{threadCount} - {Path.GetFileName(url)}";
                var filePath = Path.Combine(ListFolderLocation, fileName);

                //TraceLogger.Log($"Adding {fileName} download to task queue...");
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await semaphore.WaitAsync(); // Wait for available slot
                        TraceLogger.Log($"Added {fileName} to queue.");
                        ConsoleProgress.ShowOperationProgress(threadCount, allUrls.Count, $"Downloading {Path.GetFileName(url)}");
                        var downloadSuccess = await DownloadController.DownloadFileAsync(url, filePath, forceMode);
                        //TraceLogger.Log($"{fileName} task complete.");
                        //Check if DownloadFileAsync returned false, if so set ProblemDuringUpdate to true and log a warning
                        if (!downloadSuccess)
                        {
                            ProblemDuringUpdate = true;
                            TraceLogger.Log($"Download operation has thrown an exception. Something has gone wrong with {url}. Check logs for more details.", Enums.StatusSeverityType.Error);
                        }
                        else
                        {
                            TraceLogger.Log($"{fileName} downloaded successfully.");
                        }
                    }
                    catch (Exception ex)
                    {
                        ProblemDuringUpdate = true;
                        TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                    }
                    finally
                    {
                        semaphore.Release();
                        TraceLogger.Log($"{fileName} download task completed and released.");
                    }
                }));
            }
            await Task.WhenAll(tasks);

            watch.Stop();
            TraceLogger.Log($"Downloads complete in {watch.Elapsed.TotalSeconds} seconds. Checking if all hostlists have been updated recently");
            if (!HasDownloadedUpdates)
            {
                TraceLogger.Log("No need to compile lists since no available updates were downloaded. Checking integrity of existing lists...");
                CheckIntegrity(ListFolderLocation, allUrls.Count, CombinedListLocation, startTime);
                return;
            }
            else
            {
                hasUpdates = true; // Set the flag to indicate that updates were downloaded, this will tell the GenerateCombinedList method to run later
            }

            // Use IOManager methods but with the right folder path
            CompileList(ListFolderLocation, CombinedListLocation, allUrls.Count, startTime);
        }

        private static void CompileList(string listFolderLocation, string combinedListLocation, int urlCount, DateTime startTime)
        {
            TraceLogger.Log($"Compiling {Path.GetFileName(combinedListLocation)} list...");
            IOManager.MergeFiles(listFolderLocation, combinedListLocation);
            IOManager.RemoveDuplicates(combinedListLocation);
            IOManager.FormatHosts(combinedListLocation);
            CheckIntegrity(listFolderLocation, urlCount, combinedListLocation, startTime);
        }

        private static void CheckIntegrity(string ListFolderLocation, int urlCount, string CombinedListLocation, DateTime startTime)
        {
            TraceLogger.Log("Integrity check started. Checking if URL count and file count match...");
            var files = Directory.GetFiles(ListFolderLocation, ".").Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase)).Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase));
            if (files.Count() != urlCount)
            {
                TraceLogger.Log("URL and List file count mismatch! Clearing hostlist folder...", Enums.StatusSeverityType.Warning);
                TraceLogger.Log($"URL Count: {urlCount} | File Count: {files.Count()}", Enums.StatusSeverityType.Warning);
                try
                {
                    IOManager.ClearTempFiles(ListFolderLocation);
                    TraceLogger.Log("URL and list file count is different. Hostlist folder has been cleared. Please run HostlistDownloader again. If that doesn't work, run it with the /fresh argument.", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Integrity check failure (URL and File Count Mismatch): Mismatch check failure. {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
                }
            }
            else
            {
                TraceLogger.Log("URL and file count OK.");
            }
            TraceLogger.Log("Checking if combined list has been written to during update...");
            if (new FileInfo(CombinedListLocation).Length > 0)
            {
                TraceLogger.Log($"{CombinedListLocation} has valid file size.");
                if (!ProblemDuringUpdate && HasDownloadedUpdates)
                {
                    DateTime lastWriteTime = File.GetLastWriteTime(CombinedListLocation);
                    if (lastWriteTime < startTime)
                    {
                        TraceLogger.Log($"Integrity check failure (Internal Status Check Mismatch): {CombinedListLocation} hasn't been written to during the update process but the DownloadManager has reported that it downloaded updates. Last write time: {lastWriteTime}, Update start time: {startTime}.\n{ListFolderLocation} cleanup recommended (run HostlistDownloader with the /fresh argument)", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
                    }
                }
                else if (!ProblemDuringUpdate && !HasDownloadedUpdates)
                {
                    TraceLogger.Log($"Skipping date written check on combined list since no updates were downloaded.");
                }
            }
            TraceLogger.Log("Integrity check complete. No issues detected.");
        }

        public static void GenerateCombinedList()
        {
            TraceLogger.Log($"Generating {Path.GetFileName(IOManager.CombinedListFileLocation)} list...");
            try
            {
                // Use cached version for the white list to avoid repeated file reads
                //TraceLogger.Log($"Reading white list from: {IOManager.CombinedWhiteListFileLocation}");
                var whiteList = ReadLinesFromFileCached(IOManager.CombinedWhiteListFileLocation);
                //TraceLogger.Log($"White list count: {whiteList.Count:N0}");
                //TraceLogger.Log($"Reading block list from: {IOManager.CombinedBlockListFileLocation}");
                var blockListLines = ReadLinesFromFile(IOManager.CombinedBlockListFileLocation);
                //TraceLogger.Log($"Block list count: {blockListLines.Count():N0}");
                var filteredLines = blockListLines.Where(line =>
                    !whiteList.Any(whiteItem =>
                    {
                        // 1. If it contains a wildcard, use Regex
                        if (whiteItem.Contains('*'))
                        {
                            // Convert * to .* and escape other regex characters (like dots)
                            string pattern = "^" + Regex.Escape(whiteItem).Replace("\\*", ".*") + "$";
                            return Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase);
                        }
                        // 2. Otherwise, perform an exact line match
                        return line.Equals(whiteItem, StringComparison.OrdinalIgnoreCase);
                    })
                ).ToList();
                File.WriteAllLines(IOManager.CombinedListFileLocation, filteredLines);
                TraceLogger.Log($"Generated combined list to: {IOManager.CombinedListFileLocation} | Line count: {filteredLines.Count:N0}");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Combined List Generation Failure: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        private static IEnumerable<string> ReadLinesFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return [];

            return File.ReadLines(filePath)
                      .Select(line => line.Trim())
                      .Where(line => !string.IsNullOrEmpty(line));
        }

        private static HashSet<string> ReadLinesFromFileCached(string filePath)
        {
            lock (_cacheLock)
            {
                // Check if we already have this file in cache
                if (_fileLineCache.TryGetValue(filePath, out var cachedLines))
                {
                    return cachedLines;
                }

                // Load the lines and add to cache
                var lines = new HashSet<string>(ReadLinesFromFile(filePath), StringComparer.OrdinalIgnoreCase);
                _fileLineCache[filePath] = lines;
                return lines;
            }
        }

        private static List<string> ReadUrlsFromFile(string filePath)
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
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Error reading URLs from {filePath}: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.InvalidConfigEntry);
                return urls;
            }
        }
    }
}