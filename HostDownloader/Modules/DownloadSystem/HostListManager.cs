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

namespace HostlistDownloader.Modules.DownloadSystem
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate = false;
        public static void UpdateLists()
        {
            TraceLogger.Log("Starting update...", Enums.StatusSeverityType.Information);
            var blockListIni = ReadConfigFile(IOManager.IniBlockListFileLocation);
            var whiteListIni = ReadConfigFile(IOManager.IniWhiteListFileLocation);

            if (string.IsNullOrEmpty(blockListIni) && string.IsNullOrEmpty(whiteListIni))
            {
                TraceLogger.Log("Blocklist and Whitelist INI are not configured. Please configure HostlistDownloader.", Enums.StatusSeverityType.Fatal);
                return;
            }

            if (!string.IsNullOrEmpty(blockListIni))
            {
                TraceLogger.Log("Blocklist INI is configured. Updating blocklists...");
                IOManager.ClearFiles(IOManager.BlockListFolderLocation);
                DownloadLists(IOManager.IniBlockListFileLocation,
                    IOManager.BlockListFolderLocation,
                    IOManager.CombinedBlockListFileLocation).GetAwaiter().GetResult();
                MergeUserConfig(IOManager.IniUserWebsiteBlockListFileLocation,
                    IOManager.CombinedBlockListFileLocation);
            }
            else
            {
                TraceLogger.Log("Blocklist INI not configured. Ignoring");
            }

            if (!string.IsNullOrEmpty(whiteListIni))
            {
                TraceLogger.Log("Whitelist INI is configured. Updating whitelists...");
                IOManager.ClearFiles(IOManager.WhiteListFolderLocation);
                DownloadLists(IOManager.IniWhiteListFileLocation,
                    IOManager.WhiteListFolderLocation,
                    IOManager.CombinedWhiteListFileLocation).GetAwaiter().GetResult();
                MergeUserConfig(IOManager.IniUserWebsiteWhiteListFileLocation,
                    IOManager.CombinedWhiteListFileLocation);
            }
            else
            {
                TraceLogger.Log("Whitelist INI not configured. Ignoring");
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

                var lines = File.ReadAllLinesAsync(IniUserListLocation).GetAwaiter().GetResult();
                File.AppendAllLinesAsync(CombinedLocation, lines).GetAwaiter().GetResult();
                TraceLogger.Log($"Merged user defined lists on {CombinedLocation}");
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Fault during update of lists! {ex}", Enums.StatusSeverityType.Error);
            }
        }

        private static async Task DownloadLists(string IniLocation, string ListFolderLocation, string CombinedListLocation)
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

            var startTime = DateTime.Now;
            int completedCount = 0;
            foreach (var url in urls)
            {
                completedCount++;
                double progressPercentage = (double)completedCount / urls.Count * 100;
                UpdateProgressBar(progressPercentage, $"{completedCount} out of {urls.Count}");
                TraceLogger.Log($"Progress: [{completedCount} out of {urls.Count}] - Downloading list from: {url}");
                var fileName = Path.GetFileName(url);
                var filePath = Path.Combine(ListFolderLocation, fileName);
                try
                {
                    DownloadController.DownloadFileAsync(url, filePath).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    ProblemDuringUpdate = true;
                    TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                    // Continue with other downloads rather than failing entire process
                }
            }
            TraceLogger.Log("Downloads complete. Checking if all hostlists have been updated recently");
            CheckForOldHostLists(ListFolderLocation, startTime);
            IOManager.MergeFiles(ListFolderLocation, CombinedListLocation);
            IOManager.RemoveDuplicates(CombinedListLocation);
            IOManager.FormatHosts(CombinedListLocation);
        }

        private static void CheckForOldHostLists(string ListFolderLocation, DateTime StartOfBlockList)
        {
            try
            {
                var files = Directory.GetFiles(ListFolderLocation);
                foreach (var file in files)
                {
                    if (file.Contains("combined-"))
                    {
                        TraceLogger.Log("Combined list ignored from check.");
                        return;
                    }
                    DateTime lastWriteTime = File.GetLastWriteTime(file);
                    if (lastWriteTime < StartOfBlockList)
                    {
                        TraceLogger.Log($"Deleting {file} since it was not written to during download. (LastWriteTime is less than StartOfBlockListTime)");
                        File.Delete(file);
                    }
                    else
                    {
                        TraceLogger.Log($"List file {file} was updated successfully. Last write time: {lastWriteTime}");
                    }
                }
                TraceLogger.Log("Check complete.");
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Error checking old host lists: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        private static void UpdateProgressBar(double percentage, string statusText)
        {
            int barLength = 70;
            int progress = (int)percentage;

            Console.CursorLeft = 0;
            Console.Write("[");

            for (int i = 0; i < barLength; i++)
            {
                if (i < barLength * percentage / 100)
                    Console.Write("█");
                else
                    Console.Write("░");
            }

            Console.Write($"] {progress}% - {statusText}");
            Console.Write(new string(' ', Console.WindowWidth - Console.CursorLeft - 1));
            Console.WriteLine();
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
                TraceLogger.Log($"Error reading URLs from {filePath}: {ex}", Enums.StatusSeverityType.Error);
                return urls;
            }
        }
    }
}