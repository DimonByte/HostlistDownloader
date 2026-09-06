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
using HostlistDownloader.Modules.Network;
using HostlistDownloader.Modules.WindowsSystem;
using iluvadev.ConsoleProgressBar;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.HostlistManagement.Generation
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;
        public static List<string> UpdateStatistics = []; //Make this a array, this can be overwritten by whitelist and blocklist, so we need to store the statistics for both and then combine them into a single string for the final output.
        private static bool hasUpdates = false;

        public static void StartListProcessing(bool forceMode, CancellationToken cancellationToken = default)
        {
            TraceLogger.Log($"Starting list processing... Fresh Mode: {forceMode}", Enums.StatusSeverityType.Information);

            string[] blockListIni = [.. ConfigManager.Instance.Blocklists];
            string[] whiteListIni = [.. ConfigManager.Instance.Whitelist];
            string[] userblockListIni = [.. ConfigManager.Instance.UserWebsiteBlocklist];
            string[] userwhiteListIni = [.. ConfigManager.Instance.UserWebsiteWhitelist];

            if (blockListIni.Length == 0 && whiteListIni.Length == 0)
            {
                TraceLogger.Log("Blocklist and Whitelist config are not configured.", Enums.StatusSeverityType.Fatal, ErrorCodes.FileMissing);
                return;
            }

            if (blockListIni.Length != 0)
            {
                TraceLogger.Log("Blocklist is configured. Updating blocklists...");

                // Reconcile sources: detect added/removed URLs individually instead of clearing everything
                var (addedUrls, removedFileNames) = SourceManager.ReconcileSources(IOManager.BlockListFolderLocation, ConfigManager.Instance.Blocklists);

                if (removedFileNames.Count > 0)
                {
                    TraceLogger.Log($"Blocklist: {removedFileNames.Count} URL(s) removed from config. Deleting only the affected file(s)...", Enums.StatusSeverityType.Warning);
                    IOManager.DeleteFileAlongWithETag(IOManager.BlockListFolderLocation, removedFileNames);
                }
                if (addedUrls.Count > 0)
                {
                    TraceLogger.Log($"Blocklist: {addedUrls.Count} new URL(s) detected in config. Will download only those...");
                }
                if (addedUrls.Count == 0 && removedFileNames.Count == 0)
                {
                    TraceLogger.Log("Blocklist: No URL changes detected. Will verify existing files are up to date.");
                }

                ProcessDownloadLists(blockListIni,
                    IOManager.BlockListFolderLocation,
                    IOManager.CombinedBlockListFileLocationTemp, forceMode, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userblockListIni.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                MergeUserDefinedDomains(IOManager.CombinedBlockListFileLocationTemp, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (whiteListIni.Length != 0)
            {
                // Reconcile sources for whitelist
                var (wlAdded, wlRemoved) = SourceManager.ReconcileSources(IOManager.WhiteListFolderLocation, ConfigManager.Instance.Whitelist);

                if (wlRemoved.Count > 0)
                {
                    TraceLogger.Log($"Whitelist: {wlRemoved.Count} URL(s) removed from config. Deleting only the affected file(s)...", Enums.StatusSeverityType.Warning);
                    IOManager.DeleteFileAlongWithETag(IOManager.WhiteListFolderLocation, wlRemoved);
                }
                if (wlAdded.Count > 0)
                {
                    TraceLogger.Log($"Whitelist: {wlAdded.Count} new URL(s) detected in config. Will download only those...");
                }
                if (wlAdded.Count == 0 && wlRemoved.Count == 0)
                {
                    TraceLogger.Log("Whitelist: No URL changes detected. Will verify existing files are up to date.");
                }

                TraceLogger.Log("Whitelist is configured. Updating whitelists...");
                ProcessDownloadLists(whiteListIni,
                    IOManager.WhiteListFolderLocation,
                    IOManager.CombinedWhiteListFileLocationTemp, forceMode, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userwhiteListIni.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
                MergeUserDefinedDomains(IOManager.CombinedWhiteListFileLocationTemp, isBlocklist: false);
            }
            else
            {
                TraceLogger.Log("User Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (hasUpdates)
            {
                GenerateTemporaryCombinedList();
            }
            CommitToMasterLists();

            TraceLogger.Log("Host lists update completed!", Enums.StatusSeverityType.Important);
        }
        /// <summary>
        /// Commits the temporary generated hostfile files to the final master version.
        /// This is done to allow a fallback in the event that something goes wrong, so if the generation fails spectacularly, it wont overwrite the existing master lists with a broken version.
        /// </summary>
        private static void CommitToMasterLists()
        {
            //Move the temp combined lists to their final locations
            //e.g. HLDcombined-blocklist-TEMP.txt and HLDcombined-whitelist-TEMP.txt will be moved to HLDcombined-blocklist.txt and HLDcombined-whitelist.txt respectively.
            //This is done to ensure that the combined lists are only updated if the entire process completes successfully. And also to prevent OS file locks from preventing the combined lists from being updated.
            //This also allows partial completion, since the ones that failed usually have a old version in the hostfiles folder, so the user can still use the old version of the combined lists if something goes wrong.
            //First get lines of temp and final and print the difference in the logs for debugging purposes.

            var tempCombinedList = File.Exists(IOManager.CombinedListFileLocationTemp) ? File.ReadAllLines(IOManager.CombinedListFileLocationTemp).Length : 0;
            var finalCombinedList = File.Exists(IOManager.CombinedListFileLocation) ? File.ReadAllLines(IOManager.CombinedListFileLocation).Length : 0;
            string[] FilesToCreate =
            [
                IOManager.CombinedBlockListFileLocationTemp,
                    IOManager.CombinedWhiteListFileLocationTemp,
                    IOManager.CombinedListFileLocationTemp
            ];
            string[] FilesToBackup =
            [
                IOManager.CombinedBlockListFileLocation,
                    IOManager.CombinedWhiteListFileLocation,
                    IOManager.CombinedListFileLocation
            ];
            TraceLogger.Log("Committing temporary combined lists to final locations... Allow revert is set to " + ConfigManager.Instance.AllowRevert);
            try
            {
                if (ConfigManager.Instance.AllowRevert && File.Exists(IOManager.CombinedBlockListFileLocation)) //backup
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
                if (File.Exists(IOManager.CombinedBlockListFileLocationTemp))
                {
                    File.Move(IOManager.CombinedBlockListFileLocationTemp, IOManager.CombinedBlockListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {IOManager.CombinedBlockListFileLocationTemp} to {IOManager.CombinedBlockListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                if (File.Exists(IOManager.CombinedWhiteListFileLocationTemp))
                {
                    File.Move(IOManager.CombinedWhiteListFileLocationTemp, IOManager.CombinedWhiteListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {IOManager.CombinedWhiteListFileLocationTemp} to {IOManager.CombinedWhiteListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                if (File.Exists(IOManager.CombinedListFileLocationTemp))
                {
                    File.Move(IOManager.CombinedListFileLocationTemp, IOManager.CombinedListFileLocation, overwrite: true);
                    TraceLogger.Log($"Committed {IOManager.CombinedListFileLocationTemp} to {IOManager.CombinedListFileLocation}", Enums.StatusSeverityType.Debug);
                }
                foreach (string file in FilesToCreate)
                {
                    File.Create(file).Dispose();
                }
                TraceLogger.Log("Temporary combined lists cleared after committing to final locations.", Enums.StatusSeverityType.Debug);
                TraceLogger.Log($"Commit Complete: Difference between temporary and final combined lists: {tempCombinedList - finalCombinedList} lines", Enums.StatusSeverityType.Important);
                //Save diff to UpdateStatistics.txt
                File.WriteAllLines(IOManager.UpdateStatsLocation, [$"{tempCombinedList - finalCombinedList}"]);
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Failed to commit combined lists to final locations: {ex}", Enums.StatusSeverityType.Error);
                TraceLogger.Log("[!] No changes were made to the final combined lists. Please check the logs for details.", Enums.StatusSeverityType.Error);
            }
        }

        public static void RevertToPreviousVersion()
        {
            //Replace the Final combined lists with the backup versions if they exist.
            string[] FilesToRevert =
            [
                IOManager.CombinedBlockListFileLocation,
                    IOManager.CombinedWhiteListFileLocation,
                    IOManager.CombinedListFileLocation
            ];
            string[] BackupFiles =
            [
                IOManager.CombinedBlockListFileLocation + ".bak",
                    IOManager.CombinedWhiteListFileLocation + ".bak",
                    IOManager.CombinedListFileLocation + ".bak"
            ];
            TraceLogger.Log("Reverting to previous version of combined lists...");
            try
            {
                for (int i = 0; i < FilesToRevert.Length; i++)
                {
                    if (File.Exists(BackupFiles[i]))
                    {
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

        public static void StartOfflineListProcessing()
        {
            //Used when the user specifies the /offline command argument.
            string[] blockListIni = [.. ConfigManager.Instance.Blocklists];
            string[] whiteListIni = [.. ConfigManager.Instance.Whitelist];
            string[] userblockListIni = [.. ConfigManager.Instance.UserWebsiteBlocklist];
            string[] userwhiteListIni = [.. ConfigManager.Instance.UserWebsiteWhitelist];
            TraceLogger.Log("Starting offline list processing...", Enums.StatusSeverityType.Information);

            if (blockListIni.Length == 0 && whiteListIni.Length == 0)
            {
                TraceLogger.Log("Blocklist and Whitelist config are not configured.", Enums.StatusSeverityType.Fatal, ErrorCodes.FileMissing);
                return;
            }
            if (blockListIni.Length != 0)
            {
                TraceLogger.Log("Blocklist is configured. Merging...");
                CompileSpecificList(IOManager.BlockListFolderLocation, IOManager.CombinedBlockListFileLocationTemp, blockListIni.Length, 0, DateTime.Now);
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }
            if (whiteListIni.Length != 0)
            {
                TraceLogger.Log("Whitelist is configured. Merging user config...");
                CompileSpecificList(IOManager.WhiteListFolderLocation, IOManager.CombinedWhiteListFileLocationTemp, whiteListIni.Length, 0, DateTime.Now);
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userwhiteListIni.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
                MergeUserDefinedDomains(IOManager.CombinedWhiteListFileLocationTemp, isBlocklist: false);
            }
            else
            {
                TraceLogger.Log("User Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }
            if (userblockListIni.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                MergeUserDefinedDomains(IOManager.CombinedBlockListFileLocationTemp, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            GenerateTemporaryCombinedList();
            CommitToMasterLists();
            Environment.Exit(0);
        }

        private static void MergeUserDefinedDomains(string CombinedLocation, bool isBlocklist)
        {
            TraceLogger.Log($"Attempting to merge user defined website lists for {CombinedLocation}...");
            try
            {
                IReadOnlyList<string> userDefinedLines = isBlocklist
                    ? ConfigManager.Instance.UserWebsiteBlocklist
                    : ConfigManager.Instance.UserWebsiteWhitelist;
                TraceLogger.Log($"User defined list entry count: {userDefinedLines.Count:N0}");

                // Read what's already in the compiled/downloaded combined list so we APPEND unique
                // entries to it instead of replacing it. Previously this used File.WriteAllLines with
                // only the user-defined entries, which discarded every downloaded entry on each run.
                var existingCombinedLines = new HashSet<string>(
                    File.Exists(CombinedLocation) ? File.ReadAllLines(CombinedLocation) : [],
                    StringComparer.OrdinalIgnoreCase);

                var newEntries = new List<string>();

                int max = userDefinedLines.Count;

                //Create the ProgressBar
                using (var pb = new ProgressBar() { Maximum = max })
                {
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
                    hasUpdates = true;
                }
                else
                {
                    TraceLogger.Log("No new unique user-defined entries to add to the combined list.");
                }
            }
            catch (Exception ex)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Fault during update of lists! {ex}", Enums.StatusSeverityType.Error);
            }
        }

        private static async Task ProcessDownloadLists(string[] iniLocations, string ListFolderLocation, string CombinedListLocation, bool forceMode, CancellationToken cancellationToken = default, bool isRetryAttempt = false)
        {
            TraceLogger.Log($"Starting download for INI files. ListFolderLocation: {ListFolderLocation} | CombinedListLocation: {CombinedListLocation}", Enums.StatusSeverityType.Debug);

            var allUrls = new List<string>();

            foreach (var iniLocation in iniLocations)
            {
                var urls = IOManager.ReadUrlsFromFile(iniLocation);
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
            SemaphoreSlim semaphore = new(ConfigManager.Instance.MaxDownloadThreads, ConfigManager.Instance.MaxDownloadThreads);

            List<Task> tasks = [];
            // Track per-URL outcome so we can (a) print a real summary and (b) tell CheckIntegrity which
            // URLs are known-permanently-dead (404) so it doesn't treat them as a file-count mismatch and
            // loop forever trying to "recover" a URL that will never succeed.
            var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, DownloadOutcome>();
            // fileName -> source URL, so SearchManager can attribute a matched line back to the real
            // source URL rather than just an internal "3 - hosts.txt" filename.
            var sourceManifest = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

            int max = allUrls.Count;

            //Create the ProgressBar
            using (var pb = new ProgressBar() { Maximum = max })
            {
                //Clear "Description Text"
                pb.Text.Description.Clear();

                //Setting "Description Text" when "Processing"
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Downloading: {pb.ElementName}");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Number of URLs processed: {pb.Value}");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processing time: {pb.TimeProcessing.TotalSeconds}s.");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Estimated remaining time: {pb.TimeRemaining?.TotalSeconds}s.");

                //Setting "Description Text" when "Done"
                pb.Text.Description.Done.AddNew().SetValue(pb => $"{pb.Value} URLs downloaded in {pb.TimeProcessing.TotalSeconds}s.");


                foreach (var url in allUrls)
                {
                    Thread.Sleep(1);
                    pb.PerformStep(url);
                    var threadCount = ++completedCount;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        TraceLogger.Log($"Invalid or non-HTTP(S) URL skipped: {url}", Enums.StatusSeverityType.Warning);
                        continue;
                    }

                    var safeFileName = string.Join("_", uri.LocalPath.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                    var fileName = $"{threadCount} - {safeFileName}";
                    var filePath = Path.Combine(ListFolderLocation, fileName);

                    if (!filePath.StartsWith(ListFolderLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        TraceLogger.Log($"Path traversal attempt blocked: {url}", Enums.StatusSeverityType.Error);
                        continue;
                    }

                    sourceManifest[fileName] = url;

                    tasks.Add(Task.Run(async () =>
                    {
                        bool acquired = false;
                        try
                        {
                            await semaphore.WaitAsync(cancellationToken);
                            acquired = true;
                            TraceLogger.Log($"Added {fileName} to queue.", Enums.StatusSeverityType.Debug);
                            var outcome = DownloadOutcome.NotStarted;
                            //ConsoleProgress.ShowOperationProgress(threadCount, allUrls.Count, $"Processing {Path.GetFileName(url)}");
                            outcome = await DownloadController.DownloadFileAsync(url, filePath, forceMode, threadCount, cancellationToken);
                            outcomes[url] = outcome;
                            switch (outcome)
                            {
                                case DownloadOutcome.Success:
                                    TraceLogger.Log($"{fileName} downloaded successfully.");
                                    break;
                                case DownloadOutcome.SkippedUpToDate:
                                    TraceLogger.Log($"{fileName} already up to date, skipped.");
                                    break;
                                case DownloadOutcome.PermanentFailure:
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"{url} is permanently unreachable (e.g. 404) and will be skipped in the integrity check. Fix or remove this source from settings.json.", Enums.StatusSeverityType.Warning);
                                    break;
                                case DownloadOutcome.TransientFailure:
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"Download of {url} failed after retries. This may succeed on a later run. Check logs for more details.", Enums.StatusSeverityType.Error);
                                    break;
                                case DownloadOutcome.Cancelled:
                                    TraceLogger.Log($"{fileName} download was cancelled.", Enums.StatusSeverityType.Warning);
                                    break;
                                case DownloadOutcome.DownloadBlockedByConfig:
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"{fileName} download was blocked by settings.json configuration (e.g. allowInsecureSources was false and HLD attempted to download from HTTP.).", Enums.StatusSeverityType.Warning);
                                    break;
                                case DownloadOutcome.NotStarted:
                                    TraceLogger.Log($"Download of {url} was not started. This is unexpected and may indicate a bug.", Enums.StatusSeverityType.Error);
                                    break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            outcomes[url] = DownloadOutcome.Cancelled;
                            TraceLogger.Log($"{fileName} download was cancelled.", Enums.StatusSeverityType.Warning);
                        }
                        catch (Exception ex)
                        {
                            outcomes[url] = DownloadOutcome.TransientFailure;
                            ProblemDuringUpdate = true;
                            TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                        }
                        finally
                        {
                            if (acquired)
                            {
                                semaphore.Release();
                                TraceLogger.Log($"Semaphore released for {fileName} download task.", Enums.StatusSeverityType.Debug);
                            }
                        }
                    }));
                }

                //for (int i = 0; i < max; i++)
                //{
                //    string elementName = Guid.NewGuid().ToString();

                //    Task.Delay(10).Wait(); //Do something
                //    pb.PerformStep(elementName); //Step in ProgressBar. Setting current ElementName
                //}
            }
            await Task.WhenAll(tasks);

            watch.Stop();
            if (cancellationToken.IsCancellationRequested)
            {
                TraceLogger.Log("Download cancelled by user before completion.", Enums.StatusSeverityType.Warning);
                return;
            }

            // Write the updated _sources.json manifest
            string manifestPath = Path.Combine(ListFolderLocation, "_sources.json");
            var manifestForSerialization = new Dictionary<string, string>(sourceManifest);
            try
            {
                File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(
                    manifestForSerialization, ManifestJsonSerializerContext.Default.DictionaryStringString));
            }
            catch (Exception ex)
            {
                // Non-fatal but may cause issues the next time HLD runs - SearchManager falls back to raw filenames if this is missing or unreadable.
                TraceLogger.Log($"Failed to write source manifest for {ListFolderLocation}: {ex.Message}", Enums.StatusSeverityType.Error);
            }

            // Clean up any orphaned files that are no longer in the manifest
            // (handles renumbering after URL removals, e.g. "3-C.txt" > "2-C.txt")
            SourceManager.CleanupOrphanedFiles(ListFolderLocation, manifestForSerialization);

            int succeeded = outcomes.Values.Count(o => o == DownloadOutcome.Success);
            int upToDate = outcomes.Values.Count(o => o == DownloadOutcome.SkippedUpToDate);
            int permanentFailures = outcomes.Values.Count(o => o == DownloadOutcome.PermanentFailure);
            int transientFailures = outcomes.Values.Count(o => o == DownloadOutcome.TransientFailure);
            //Add string to UpdateStatistics array on the next available index.
            string InternalUpdateStats = $"Downloads took {watch.Elapsed.TotalSeconds:N1}s for {Path.GetFileName(CombinedListLocation)} file processing {ListFolderLocation}: " +
            $"{succeeded} downloaded, {upToDate} already up to date, {permanentFailures} permanently unreachable, {transientFailures} failed after retries.";
            UpdateStatistics.Add(InternalUpdateStats);
            TraceLogger.Log(InternalUpdateStats, Enums.StatusSeverityType.Important);

            if (transientFailures > 0)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Some downloads failed after retries. This may succeed on a later run. Check logs for more details.", Enums.StatusSeverityType.Warning);
            }
            if (permanentFailures > 0)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Some downloads failed permanently (e.g. 404). These will be skipped in the integrity check. Please review and fix the affected URL(s) in settings.json or remove permanently offline entries.", Enums.StatusSeverityType.Warning);
            }

            bool integrityOk;
            if (!HasDownloadedUpdates)
            {
                TraceLogger.Log("No need to compile lists since no available updates were downloaded. Checking integrity of existing lists...");
                integrityOk = IntegrityChecker.CheckIntegrity(ListFolderLocation, allUrls.Count, permanentFailures, CombinedListLocation, startTime, ProblemDuringUpdate, HasDownloadedUpdates);
            }
            else
            {
                hasUpdates = true;
                integrityOk = CompileSpecificList(ListFolderLocation, CombinedListLocation, allUrls.Count, permanentFailures, startTime);
            }

            if (integrityOk)
                return;

            if (isRetryAttempt)
            {
                TraceLogger.Log($"Integrity check failed again after an automatic retry. Giving up on {CombinedListLocation}. Please run HostlistDownloader again, or with the /fresh argument if the problem persists.", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
                return;
            }

            if (permanentFailures > 0 && transientFailures == 0)
            {
                // Every failure was a permanent one (404 etc). Re-downloading will hit the exact same
                // 404s again, so the "clear and retry" recovery path can't fix anything here - it would
                // just loop forever. Treat this as a config problem instead of a transient integrity fault.
                TraceLogger.Log($"Integrity check failed because {permanentFailures} source(s) are permanently unreachable, not due to a transient issue. Automatic recovery would repeat the same failure, so it's being skipped. Please review and fix the affected URL(s) in settings.json.", Enums.StatusSeverityType.Fatal, ErrorCodes.IntegrityCheckFailure);
                return;
            }

            TraceLogger.Log("Attempting automatic recovery: clearing this list's folder and re-downloading everything once...", Enums.StatusSeverityType.Warning);
            IOManager.ClearTempFiles(ListFolderLocation);
            await ProcessDownloadLists(iniLocations, ListFolderLocation, CombinedListLocation, forceMode: true, cancellationToken, isRetryAttempt: true);
        }

        public static bool CompileSpecificList(string listFolderLocation, string combinedListLocation, int urlCount, int knownPermanentFailures, DateTime startTime)
        {
            //Used to compile specific lists, e.g. blocklist files or whitelist files, into a single combined list.
            TraceLogger.Log($"Compiling {Path.GetFileName(combinedListLocation)} list...");
            IOManager.MergeFiles(listFolderLocation, combinedListLocation);
            TransformationEngine.BeginTransformation(combinedListLocation);
            return IntegrityChecker.CheckIntegrity(listFolderLocation, urlCount, knownPermanentFailures, combinedListLocation, startTime, ProblemDuringUpdate, HasDownloadedUpdates);
        }


        public static void GenerateTemporaryCombinedList()
        {
            //Used to combine the compiled blocklist and whitelist into a single combined list file, which is then used for the final output to the user.
            TraceLogger.Log($"Generating temporary {Path.GetFileName(IOManager.CombinedListFileLocationTemp)} list...");
            try
            {
                var whiteList = IOManager.ReadLinesFromFileCached(IOManager.CombinedWhiteListFileLocationTemp);
                var blockListLines = IOManager.ReadLinesFromFile(IOManager.CombinedBlockListFileLocationTemp);
                var filteredLines = blockListLines.Where(line =>
                    !whiteList.Any(whiteItem =>
                    {
                        if (whiteItem.Contains('*'))
                        {
                            string pattern = "^" + Regex.Escape(whiteItem).Replace("\\*", ".") + "$";
                            return Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase);
                        }
                        return line.Equals(whiteItem, StringComparison.OrdinalIgnoreCase);
                    })
                ).ToList();
                File.WriteAllLines(IOManager.CombinedListFileLocationTemp, filteredLines);
                TraceLogger.Log($"Generated combined list to: {IOManager.CombinedListFileLocationTemp} | Line count: {filteredLines.Count:N0}");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Combined List Generation Failure: {ex}", Enums.StatusSeverityType.Error);
            }
        }
    }
}