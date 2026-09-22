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
using HostlistDownloaderCLI.Modules.Helpers;
using HostlistDownloaderCLI.Modules.HostlistManagement.Generation;
using iluvadev.ConsoleProgressBar;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HostlistDownloaderCLI.Modules.WindowsSystem.IO
{
    internal class IOManager
    {

        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

        internal static void CreateNecessaryDirectoriesAndFiles()
        {
            string[] directories = [Paths.LogsLocation, Paths.HostfilesLocation, Paths.BlockListFolderLocation, Paths.WhiteListFolderLocation, Paths.CombinedListFolderLocation];

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
            string[] files = [Paths.CombinedListFileLocationTemp, Paths.CombinedBlockListFileLocationTemp, Paths.CombinedWhiteListFileLocationTemp];
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
            if (!File.Exists(Paths.SettingJsonFileLocation))
            {
                ShowHelp = true;
                AppConfig.CreateDefaultConfig(Paths.SettingJsonFileLocation);
            }
            if (ShowHelp)
            {
                Console.WriteLine("[!] Configuration files and folders have been created in the directory where this program is stored. (settings.json)\nPlease refer to the documentation on the main GitHub page of HostlistDownloader to configure. Once configured, run HostlistDownloader again. HostlistDownloader will now exit.");
                Environment.Exit(ErrorCodes.GeneralError);
            }
        }

        internal static void MergeFilesInDirectory(string sourceFolder, string outputFile)
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
                //ConsoleProgress.ShowOperationProgress(0, files.Length, "Merging files");

                int max = files.Length;
                //Create the ProgressBar
                using (var pb = new ProgressBar() { Maximum = max })
                {
                    if (TraceLogger.QuietMode)
                    {
                        pb.Text.Body.SetVisible(false);
                    }
                    //Clear "Description Text"
                    pb.Text.Description.Clear();

                    //Setting "Description Text" when "Processing"
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Merging file: {pb.ElementName}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Merged file count: {pb.Value}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processing time: {pb.TimeProcessing.TotalSeconds}s.");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Estimated remaining time: {pb.TimeRemaining?.TotalSeconds}s.");

                    //Setting "Description Text" when "Done"
                    pb.Text.Description.Done.AddNew().SetValue(pb => $"{pb.Value} files merged in {pb.TimeProcessing.TotalSeconds}s.");

                    foreach (var file in files)
                    {
                        TraceLogger.Log($"Merging file: {file}", Enums.StatusSeverityType.Debug);
                        using var reader = new StreamReader(file);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                            {
                                writer.WriteLine(line);
                            }
                        }
                        //Thread.Sleep(1);
                        pb.PerformStep(file);
                        //processedFiles++;
                        //ConsoleProgress.ShowOperationProgress(processedFiles, files.Length, "Merging files");
                    }
                }
                writer.Flush();
                watch.Stop();
                TraceLogger.Log($"Merge files completed in {watch.Elapsed.TotalSeconds} seconds.", Enums.StatusSeverityType.Information);
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
        internal static void DeleteFileAlongWithETag(string listFolderLocation, List<string> fileNames)
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

        internal static IEnumerable<string> ReadLinesFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return [];

            return File.ReadLines(filePath)
                      .Select(line => line.Trim())
                      .Where(line => !string.IsNullOrEmpty(line));
        }

        internal static HashSet<string> ReadLinesFromFileCached(string filePath)
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

        internal static void ClearTempFiles(string folder)
        {
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

        internal static string FormatBytes(long bytes)
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

        internal static void GenerateStatsReport()
        {
            //Get last write time of combined list, blocklist, and whitelist
            //Then get size of each file and number of lines in each file
            //Then get number of sources in blocklist and whitelist
            try
            {
                var combinedListInfo = new FileInfo(Paths.CombinedListFileLocation);
                var combinedBlockListInfo = new FileInfo(Paths.CombinedBlockListFileLocation);
                var combinedWhiteListInfo = new FileInfo(Paths.CombinedWhiteListFileLocation);
                //Example output:
                //[Stats Report]
                //Combined List: 1,234,567 lines, 12.34 MB, Last Updated: 2024-06-01 12:34:56
                //Combined Blocklist: 1,234,567 lines, 12.34 MB, Last Updated: 2024-06-01 12:34:56
                //Combined Whitelist: 1,234,567 lines, 12.34 MB, Last Updated: 2024-06-01 12:34:56
                //Number of Blocklist Sources: 123
                //Number of Whitelist Sources: 123
                TraceLogger.Log("[Stats Report]", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Combined List: {File.ReadLines(Paths.CombinedListFileLocation).Count():N0} lines, {FormatBytes(combinedListInfo.Length)}, Last Updated: {combinedListInfo.LastWriteTime}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Combined Blocklist: {File.ReadLines(Paths.CombinedBlockListFileLocation).Count():N0} lines, {FormatBytes(combinedBlockListInfo.Length)}, Last Updated: {combinedBlockListInfo.LastWriteTime}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Combined Whitelist: {File.ReadLines(Paths.CombinedWhiteListFileLocation).Count():N0} lines, {FormatBytes(combinedWhiteListInfo.Length)}, Last Updated: {combinedWhiteListInfo.LastWriteTime}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Number of Blocklist Sources: {AppConfig.Instance.Blocklists.Count:N0}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Number of Whitelist Sources: {AppConfig.Instance.Whitelist.Count:N0}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Number of User Blocklist Domains: {AppConfig.Instance.UserWebsiteBlocklist.Count:N0}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log($"Number of User Whitelist Domains: {AppConfig.Instance.UserWebsiteWhitelist.Count:N0}", Enums.StatusSeverityType.Notice);
                TraceLogger.Log("[Stats Report Complete]");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error generating stats report: {ex.Message}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Generates a temporary combined list by merging the temporary whitelist and blocklist files, ensuring that any entries in the whitelist are excluded from the final combined list. This method reads the temporary whitelist and blocklist files, filters out any entries from the blocklist that are present in the whitelist (including handling wildcard patterns), and writes the resulting filtered list to a temporary combined list file.
        /// </summary>
        internal static void GenerateTemporaryCombinedList()
        {
            TraceLogger.Log($"Generating temporary {Path.GetFileName(Paths.CombinedListFileLocationTemp)} list...");
            try
            {
                var whiteList = ReadLinesFromFileCached(Paths.CombinedWhiteListFileLocationTemp);
                var blockListLines = ReadLinesFromFile(Paths.CombinedBlockListFileLocationTemp);

                // Partition whitelist into exact-match set and wildcard patterns
                var exactWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var wildcardPatterns = new List<Regex>();

                foreach (var item in whiteList)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    if (item.Contains('*'))
                    {
                        string pattern = "^" + Regex.Escape(item).Replace("\\*", ".*") + "$";
                        wildcardPatterns.Add(new Regex(pattern, RegexOptions.IgnoreCase));
                    }
                    else
                    {
                        exactWhitelist.Add(item.Trim());
                    }
                }

                var filteredLines = new List<string>(blockListLines.Count());
                foreach (var line in blockListLines)
                {
                    if (exactWhitelist.Contains(line)) continue;

                    bool wildcardMatch = false;
                    for (int i = 0; i < wildcardPatterns.Count && !wildcardMatch; i++)
                    {
                        wildcardMatch = wildcardPatterns[i].IsMatch(line);
                    }
                    if (!wildcardMatch)
                    {
                        filteredLines.Add(line);
                    }
                }

                File.WriteAllLines(Paths.CombinedListFileLocationTemp, filteredLines);
                TraceLogger.Log($"Generated combined list to: {Paths.CombinedListFileLocationTemp} | Line count: {filteredLines.Count:N0}");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Combined List Generation Failure: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Commits the temporary generated hostfile files to the final master version.
        /// This is done to allow a fallback in the event that something goes wrong, so if the generation fails spectacularly, it wont overwrite the existing master lists with a broken version.
        /// </summary>
        internal static void CommitTemporaryToMaster()
        {
            var tempCombinedList = File.Exists(Paths.CombinedListFileLocationTemp) ? File.ReadAllLines(Paths.CombinedListFileLocationTemp).Length : 0;
            var finalCombinedList = File.Exists(Paths.CombinedListFileLocation) ? File.ReadAllLines(Paths.CombinedListFileLocation).Length : 0;
            string[] FilesToCreate =
            [
                Paths.CombinedBlockListFileLocationTemp,
                    Paths.CombinedWhiteListFileLocationTemp,
                    Paths.CombinedListFileLocationTemp
            ];
            string[] FilesToBackup =
            [
                Paths.CombinedBlockListFileLocation,
                    Paths.CombinedWhiteListFileLocation,
                    Paths.CombinedListFileLocation
            ];
            TraceLogger.Log("Committing temporary combined lists to final locations... Allow revert is set to " + AppConfig.Instance.AllowRevert);
            try
            {
                if (AppConfig.Instance.AllowRevert && File.Exists(Paths.CombinedBlockListFileLocation)) //backup
                {
                    TraceLogger.Log("Backing up existing combined lists before committing new ones...", Enums.StatusSeverityType.Debug);
                    foreach (string file in FilesToBackup)
                    {
                        string backupPath = file + ".bak";
                        if (File.Exists(file))
                        {
                            File.Copy(file, backupPath, overwrite: true);
                            TraceLogger.Log($"Backup of existing combined list created at {backupPath}", Enums.StatusSeverityType.Debug);
                        }
                    }
                }
                TraceLogger.Log("Committing temporary combined lists to final locations...", Enums.StatusSeverityType.Debug);
                if (File.Exists(Paths.CombinedBlockListFileLocationTemp))
                {
                    File.Move(Paths.CombinedBlockListFileLocationTemp, Paths.CombinedBlockListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {Paths.CombinedBlockListFileLocationTemp} to {Paths.CombinedBlockListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                if (File.Exists(Paths.CombinedWhiteListFileLocationTemp))
                {
                    File.Move(Paths.CombinedWhiteListFileLocationTemp, Paths.CombinedWhiteListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {Paths.CombinedWhiteListFileLocationTemp} to {Paths.CombinedWhiteListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                if (File.Exists(Paths.CombinedListFileLocationTemp))
                {
                    File.Move(Paths.CombinedListFileLocationTemp, Paths.CombinedListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {Paths.CombinedListFileLocationTemp} to {Paths.CombinedListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                foreach (string file in FilesToCreate)
                {
                    File.Create(file).Dispose();
                }
                TraceLogger.Log("Temporary combined lists cleared after committing to final locations.", Enums.StatusSeverityType.Debug);
                TraceLogger.Log($"Commit Complete: Difference between temporary and final combined lists: {tempCombinedList - finalCombinedList} lines", Enums.StatusSeverityType.Notice);
                File.WriteAllLines(Paths.UpdateStatsLocation, [$"{tempCombinedList - finalCombinedList}"]);
            }
            catch (Exception ex)
            {
                HostlistOrchestrator.ProblemDuringUpdate = true;
                TraceLogger.Log($"Failed to commit combined lists to final locations: {ex}", Enums.StatusSeverityType.Error);
                TraceLogger.Log("[!] No changes were made to the final combined lists. Please check the logs for details.", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Replace the Final combined lists with the backup versions if they exist.
        /// </summary>
        internal static void RevertCombinedListsToPreviousVersion()
        {
            string[] FilesToRevert =
            [
                Paths.CombinedBlockListFileLocation,
                    Paths.CombinedWhiteListFileLocation,
                    Paths.CombinedListFileLocation
            ];
            string[] BackupFiles =
            [
                Paths.CombinedBlockListFileLocation + ".bak",
                    Paths.CombinedWhiteListFileLocation + ".bak",
                    Paths.CombinedListFileLocation + ".bak"
            ];
            TraceLogger.Log("Reverting to previous version of combined lists...");
            try
            {
                for (int i = 0; i < FilesToRevert.Length; i++)
                {
                    if (File.Exists(BackupFiles[i]))
                    {
                        long size = new FileInfo(BackupFiles[i]).Length;
                        if (size == 0)
                        {
                            TraceLogger.Log($"Backup for {FilesToRevert[i]} is empty. Skipping revert.", Enums.StatusSeverityType.Warning);
                            continue;
                        }
                        string firstLine = File.ReadLines(BackupFiles[i]).First();
                        if (firstLine.TrimStart().StartsWith("<", StringComparison.OrdinalIgnoreCase))
                        {
                            TraceLogger.Log($"Backup for {FilesToRevert[i]} appears to be corrupted (HTML). Skipping revert.", Enums.StatusSeverityType.Error);
                            continue;
                        }
                        File.Move(BackupFiles[i], FilesToRevert[i], overwrite: true);
                        TraceLogger.Log($"Reverted {FilesToRevert[i]} to previous version from {BackupFiles[i]}", Enums.StatusSeverityType.Warning);
                    }
                    else
                    {
                        TraceLogger.Log($"No backup found for {FilesToRevert[i]}. Cannot revert.", Enums.StatusSeverityType.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to revert combined lists to previous versions: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Merges user-defined domains from the configuration into the combined list, ensuring no duplicates are added. This method reads the existing combined list and appends unique entries from the user-defined list, handling both blocklist and whitelist scenarios.
        /// </summary>
        /// <param name="CombinedLocation"></param>
        /// <param name="isBlocklist"></param>
        internal static void MergeUserDefinedDomains(string CombinedLocation, bool isBlocklist)
        {
            TraceLogger.Log($"Attempting to merge user defined website lists for {CombinedLocation}...");
            try
            {
                IReadOnlyList<string> userDefinedLines = isBlocklist
                    ? AppConfig.Instance.UserWebsiteBlocklist
                    : AppConfig.Instance.UserWebsiteWhitelist;
                TraceLogger.Log($"User defined list entry count: {userDefinedLines.Count:N0}");

                var existingCombinedLines = new HashSet<string>(
                    File.Exists(CombinedLocation) ? File.ReadAllLines(CombinedLocation) : [],
                    StringComparer.OrdinalIgnoreCase);

                var newEntries = new List<string>();

                int max = userDefinedLines.Count;

                //Create the ProgressBar
                using (var pb = new ProgressBar() { Maximum = max })
                {
                    if (TraceLogger.QuietMode)
                    {
                        pb.Text.Body.SetVisible(false);
                    }
                    //Clear "Description Text"
                    pb.Text.Description.Clear();
                    string blockorwhite = isBlocklist ? "blocklist" : "whitelist";
                    //Setting "Description Text" when "Processing"
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Merging user defined {blockorwhite}: {pb.ElementName}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processed: {pb.Value}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processing time: {pb.TimeProcessing.TotalSeconds}s.");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Estimated remaining time: {pb.TimeRemaining?.TotalSeconds}s.");

                    //Setting "Description Text" when "Done"
                    pb.Text.Description.Done.AddNew().SetValue(pb => $"{pb.Value} elements in {pb.TimeProcessing.TotalSeconds}s.");

                    foreach (var rawLine in userDefinedLines)
                    {
                        string trimmed = rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                        {
                            continue;
                        }

                        bool isWildcard = trimmed.StartsWith('*') || trimmed.EndsWith('*');

                        // Wildcard entries (e.g. "*.example.com") aren't deduplicated against the combined list
                        // since they aren't a literal line match, but duplicate wildcard entries within the
                        // user's own list are still skipped.
                        if (isWildcard)
                        {
                            if (existingCombinedLines.Add(trimmed))
                            {
                                newEntries.Add(trimmed);
                            }
                        }
                        else if (existingCombinedLines.Add(trimmed))
                        {
                            newEntries.Add(trimmed);
                        }
                        pb.PerformStep(trimmed);
                    }
                }

                if (newEntries.Count != 0)
                {
                    File.AppendAllLines(CombinedLocation, newEntries);
                    TraceLogger.Log($"Merged user defined list into {CombinedLocation} (added {newEntries.Count:N0} unique entries)");
                    HostlistOrchestrator.hasUpdates = true;
                }
                else
                {
                    TraceLogger.Log("No new unique user-defined entries to add to the combined list.");
                }
            }
            catch (Exception ex)
            {
                HostlistOrchestrator.ProblemDuringUpdate = true;
                TraceLogger.Log($"Fault during update of lists! {ex}", Enums.StatusSeverityType.Error);
            }
        }
        internal static bool IsInternalFile(string fileName)
        {
            return fileName.Equals("_sources.json", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".etag", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("combined", StringComparison.OrdinalIgnoreCase);
        }
    }
}