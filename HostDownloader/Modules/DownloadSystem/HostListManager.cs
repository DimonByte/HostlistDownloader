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

using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;

namespace HostlistDownloader.Modules.DownloadSystem
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;

        public static void UpdateLists(bool forceMode)
        {
            TraceLogger.Log("Starting update...", Enums.StatusSeverityType.Information);
            var blockListIni = ReadConfigFile(IOManager.IniBlockListFileLocation);
            var whiteListIni = ReadConfigFile(IOManager.IniWhiteListFileLocation);
            var userblockListIni = ReadConfigFile(IOManager.IniUserWebsiteBlockListFileLocation);
            var userwhiteListIni = ReadConfigFile(IOManager.IniUserWebsiteWhiteListFileLocation);

            if (string.IsNullOrEmpty(blockListIni) && string.IsNullOrEmpty(whiteListIni))
            {
                TraceLogger.Log("Blocklist and Whitelist INI are not configured.", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationFileMissing);
                return;
            }

            bool hasUpdates = false;

            if (!string.IsNullOrEmpty(blockListIni))
            {
                TraceLogger.Log("Blocklist INI is configured. Updating blocklists...");
                //IOManager.ClearFiles(IOManager.BlockListFolderLocation);
                DownloadLists(IOManager.IniBlockListFileLocation,
                    IOManager.BlockListFolderLocation,
                    IOManager.CombinedBlockListFileLocation, forceMode).GetAwaiter().GetResult();
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("Blocklist INI not configured. Ignoring");
            }

            if (!string.IsNullOrEmpty(userblockListIni))
            {
                TraceLogger.Log("User blocklist INI is configured. Merging user config...");
                MergeUserConfig(IOManager.IniUserWebsiteBlockListFileLocation, IOManager.CombinedBlockListFileLocation);
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("User Blocklist INI not configured. Ignoring");
            }

            if (!string.IsNullOrEmpty(whiteListIni))
            {
                TraceLogger.Log("Whitelist INI is configured. Updating whitelists...");
                //IOManager.ClearFiles(IOManager.WhiteListFolderLocation);
                DownloadLists(IOManager.IniWhiteListFileLocation,
                    IOManager.WhiteListFolderLocation,
                    IOManager.CombinedWhiteListFileLocation, forceMode).GetAwaiter().GetResult();
                hasUpdates = true;
            }
            else
            {
                TraceLogger.Log("Whitelist INI not configured. Ignoring");
            }

            if (!string.IsNullOrEmpty(userwhiteListIni))
            {
                TraceLogger.Log("User Whitelist INI is configured. Merging user config...");
                MergeUserConfig(IOManager.IniUserWebsiteWhiteListFileLocation, IOManager.CombinedWhiteListFileLocation);
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

        private static string ReadConfigFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null!;

            try
            {
                return File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Error reading config file {filePath}: {ex}", Enums.StatusSeverityType.Error);
                return null!;
            }
        }

        private static void MergeUserConfig(string IniUserListLocation, string CombinedLocation)
        {
            TraceLogger.Log("Attempting to merge user defined website lists...");
            try
            {
                if (!File.Exists(IniUserListLocation))
                {
                    TraceLogger.Log($"User list configuration file not found: {IniUserListLocation}", Enums.StatusSeverityType.Warning);
                    return;
                }

                var existingLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(CombinedLocation))
                {
                    var existingContent = File.ReadAllLines(CombinedLocation);
                    foreach (var line in existingContent)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                        {
                            existingLines.Add(line.Trim());
                        }
                    }
                }
                var userLines = File.ReadAllLines(IniUserListLocation);
                var filteredLines = new List<string>();

                foreach (var line in userLines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#"))
                        continue;
                    if (!existingLines.Contains(trimmedLine))
                    {
                        filteredLines.Add(trimmedLine);
                        existingLines.Add(trimmedLine); // Add to existing set for subsequent checks in this method
                    }
                }
                if (filteredLines.Any())
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

        private static async Task DownloadLists(string IniLocation, string ListFolderLocation, string CombinedListLocation, bool forceMode)
        {
            TraceLogger.Log($"Starting download for INI {IniLocation} | ListFolderLocation: {ListFolderLocation} | CombinedListLocation: {CombinedListLocation}");
            if (!File.Exists(IniLocation))
            {
                TraceLogger.Log($"List configuration file not found: {IniLocation}", Enums.StatusSeverityType.Error);
                return;
            }
            var urls = ReadUrlsFromFile(IniLocation);
            if (urls.Count == 0)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log("No URLs found in the configuration file.", Enums.StatusSeverityType.Warning);
                return;
            }
            DateTime startTime = DateTime.Now;
            int completedCount = 0;
            Stopwatch watch = Stopwatch.StartNew();
            ConsoleProgress.ShowOperationProgress(0, urls.Count, "Downloading lists");
            foreach (var url in urls)
            {
                completedCount++;
                var fileName = $"{completedCount} - {Path.GetFileName(url)}";
                var filePath = Path.Combine(ListFolderLocation, fileName);

                try
                {
                    await DownloadController.DownloadFileAsync(url, filePath, forceMode);
                }
                catch (Exception ex)
                {
                    ProblemDuringUpdate = true;
                    TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                }
                ConsoleProgress.ShowOperationProgress(completedCount, urls.Count, "Downloading lists");
            }

            watch.Stop();
            TraceLogger.Log($"Downloads complete in {watch.Elapsed.TotalSeconds} seconds. Checking if all hostlists have been updated recently");
            if (!HasDownloadedUpdates)
            {
                TraceLogger.Log("No updates were applied.");
                CheckIntegrity(ListFolderLocation, urls.Count, CombinedListLocation, startTime);
                return;
            }
            IOManager.MergeFiles(ListFolderLocation, CombinedListLocation);
            IOManager.RemoveDuplicates(CombinedListLocation);
            IOManager.FormatHosts(CombinedListLocation);
            CheckIntegrity(ListFolderLocation, urls.Count, CombinedListLocation, startTime);
        }

        private static void CheckIntegrity(string ListFolderLocation, int urlCount, string CombinedListLocation, DateTime startTime)
        {
            TraceLogger.Log("Integrity check started. Checking if URL count and file count match...");
            var files = Directory.GetFiles(ListFolderLocation, "*.*").Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase)).Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase));
            if (files.Count() != urlCount)
            {
                TraceLogger.Log("URL and List file count mismatch! Clearing hostlist folder...", Enums.StatusSeverityType.Warning);
                TraceLogger.Log($"URL Count: {urlCount} | File Count: {files.Count()}", Enums.StatusSeverityType.Warning);
                try
                {
                    //foreach (var file in Directory.GetFiles(ListFolderLocation))
                    //{
                    //    File.Delete(file);
                    //    TraceLogger.Log($"Deleted {file} due to count mismatch...", Enums.StatusSeverityType.Warning);
                    //}
                    IOManager.ClearTempFiles(ListFolderLocation);
                    TraceLogger.Log("URL and list file count is different. Hostlist folder has been cleared. Please run HostlistDownloader again. If that doesn't work, run it with the /force argument.", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
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
                        TraceLogger.Log($"{CombinedListLocation} hasn't been written to but DownloadManager has reported it downloaded updates! {ListFolderLocation} deletion recommended.", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
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
                var whiteList = new HashSet<string>(ReadLinesFromFile(IOManager.CombinedWhiteListFileLocation), StringComparer.OrdinalIgnoreCase);
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

        private static List<string> ReadUrlsFromFile(string filePath)
        {
            var urls = new List<string>();

            if (!File.Exists(filePath))
                return urls;

            try
            {
                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                        continue;

                    urls.Add(line.Trim());
                }

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