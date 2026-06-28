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

namespace HostlistDownloader.Modules.DownloadSystem
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;

        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

        public static void UpdateLists(bool forceMode)
        {
            TraceLogger.Log("Starting update...", Enums.StatusSeverityType.Information);

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

            bool hasUpdates = false;

            if (blockListIni.Length != 0)
            {
                TraceLogger.Log("Blocklist INI is configured. Updating blocklists...");
                // Since we're using the ConfigReader now, we need to adapt how we handle blocklist files
                ProcessDownloadLists(blockListIni,
                    IOManager.BlockListFolderLocation,
                    IOManager.CombinedBlockListFileLocation, forceMode).GetAwaiter().GetResult();
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("Blocklist INI not configured. Ignoring");
            }

            if (userblockListIni.Length != 0)
            {
                TraceLogger.Log("User blocklist INI is configured. Merging user config...");
                // Process multiple user-blocklist files
                foreach (string urlEntry in userblockListIni)
                {
                    MergeUserConfig(urlEntry, IOManager.CombinedBlockListFileLocation);
                }
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("User Blocklist INI not configured. Ignoring");
            }

            if (whiteListIni.Length != 0)
            {
                TraceLogger.Log("Whitelist INI is configured. Updating whitelists...");
                // Process multiple whitelist files
                ProcessDownloadLists(whiteListIni,
                    IOManager.WhiteListFolderLocation,
                    IOManager.CombinedWhiteListFileLocation, forceMode).GetAwaiter().GetResult();
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("Whitelist INI not configured. Ignoring");
            }

            if (userwhiteListIni.Length != 0)
            {
                TraceLogger.Log("User Whitelist INI is configured. Merging user config...");
                // Process multiple user-whitelist files
                foreach (string urlEntry in userwhiteListIni)
                {
                    MergeUserConfig(urlEntry, IOManager.CombinedBlockListFileLocation);
                }
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("User Whitelist INI not configured. Ignoring");
            }

            if (hasUpdates)
            {
                GenerateCombinedList();
            }

            TraceLogger.Log("Host lists update completed!");
        }

        private static void MergeUserConfig(string IniUserListLocation, string CombinedLocation)
        {
            TraceLogger.Log("Attempting to merge user defined website lists...");
            try
            {

                // Get existing lines from combined list for uniqueness check
                var existingLines = ReadLinesFromFileCached(CombinedLocation);
                var filteredLines = new List<string>();

                var trimmedLine = IniUserListLocation.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                {
                    TraceLogger.Log($"Null User URL. {CombinedLocation}. Ignoring.", Enums.StatusSeverityType.Warning);
                }
                if (!existingLines.Contains(trimmedLine))
                {
                    filteredLines.Add(trimmedLine);
                    existingLines.Add(trimmedLine); // Add to existing set for subsequent checks in this method
                }

                if (filteredLines.Count != 0)
                {
                    File.AppendAllLines(CombinedLocation, filteredLines);
                    TraceLogger.Log($"Merged user defined lists on {CombinedLocation} (added {filteredLines.Count} unique entries)");
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
                        await DownloadController.DownloadFileAsync(url, filePath, forceMode);
                        TraceLogger.Log($"{fileName} task complete.");
                    }
                    catch (Exception ex)
                    {
                        ProblemDuringUpdate = true;
                        TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                    }
                    finally
                    {
                        TraceLogger.Log($"{fileName} task released.");
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks);

            watch.Stop();
            TraceLogger.Log($"Downloads complete in {watch.Elapsed.TotalSeconds} seconds. Checking if all hostlists have been updated recently");
            if (!HasDownloadedUpdates)
            {
                TraceLogger.Log("No updates were applied.");
                CheckIntegrity(ListFolderLocation, allUrls.Count, CombinedListLocation, startTime);
                return;
            }

            // Use IOManager methods but with the right folder path
            IOManager.MergeFiles(ListFolderLocation, CombinedListLocation);
            IOManager.RemoveDuplicates(CombinedListLocation);
            IOManager.FormatHosts(CombinedListLocation);
            CheckIntegrity(ListFolderLocation, allUrls.Count, CombinedListLocation, startTime);
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
                    TraceLogger.Log($"Mismatch check failure. {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
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
                        TraceLogger.Log($"{CombinedListLocation} hasn't been written to but DownloadManager has reported it downloaded updates! {ListFolderLocation} cleanup recommended (run HostlistDownloader with the /fresh argument).", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
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
            TraceLogger.Log("Generating combined list...");
            try
            {
                // Use cached version for the white list to avoid repeated file reads
                var whiteList = ReadLinesFromFileCached(IOManager.CombinedWhiteListFileLocation);
                var blockListLines = ReadLinesFromFile(IOManager.CombinedBlockListFileLocation);
                var filteredLines = blockListLines.Where(line =>
                !whiteList.Any(whiteItem => line.Contains(whiteItem, StringComparison.OrdinalIgnoreCase))).ToList();
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
                    return null;

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