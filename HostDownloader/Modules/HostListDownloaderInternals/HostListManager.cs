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
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Transactions;

namespace HostlistDownloader.Modules.HostListDownloaderInternals
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;
        public static List<string> UpdateStatistics = []; //Make this a array, this can be overwritten by whitelist and blocklist, so we need to store the statistics for both and then combine them into a single string for the final output.
        private static bool hasUpdates = false;
        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

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
                var (addedUrls, removedFileNames) = ReconcileSources(IOManager.BlockListFolderLocation, ConfigManager.Instance.Blocklists);

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
                var (wlAdded, wlRemoved) = ReconcileSources(IOManager.WhiteListFolderLocation, ConfigManager.Instance.Whitelist);

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
                GenerateCombinedList();
            }
            CommitToMasterLists();

            TraceLogger.Log("Host lists update completed!");
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
                TraceLogger.Log("Temporary combined lists cleared after committing to final locations.",Enums.StatusSeverityType.Debug);
                TraceLogger.Log($"Commit Complete: Difference between temporary and final combined lists: {tempCombinedList - finalCombinedList} lines");
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
                CompileList(IOManager.BlockListFolderLocation, IOManager.CombinedBlockListFileLocationTemp, blockListIni.Length, 0, DateTime.Now);
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }
            if (whiteListIni.Length != 0)
            {
                TraceLogger.Log("Whitelist is configured. Merging user config...");
                CompileList(IOManager.WhiteListFolderLocation, IOManager.CombinedWhiteListFileLocationTemp, whiteListIni.Length, 0, DateTime.Now);
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

            GenerateCombinedList();
            CommitToMasterLists();
        }

        /// <summary>
        /// Compares the previous run's _sources.json manifest against the current configuration
        /// to identify which URLs were added or removed since the last run.
        /// </summary>
        /// <param name="listFolderLocation">Folder containing the downloaded list files and _sources.json.</param>
        /// <param name="currentConfigUrls">The full set of URLs from the current configuration.</param>
        /// <returns>
        /// A tuple of:
        /// <list type="bullet">
        /// <item><description><b>addedUrls</b> – URLs present in config but absent from the previous manifest (new sources to download).</description></item>
        /// <item><description><b>removedFileNames</b> – File names from the previous manifest whose URLs are no longer in config (files to delete).</description></item>
        /// </list>
        /// </returns>
        private static (List<string> addedUrls, List<string> removedFileNames) ReconcileSources(
            string listFolderLocation,
            IReadOnlyList<string> currentConfigUrls)
        {
            string manifestPath = Path.Combine(listFolderLocation, "_sources.json");
            var addedUrls = new List<string>();
            var removedFileNames = new List<string>();

            // If there's no manifest yet (first run), everything is "new"
            if (!File.Exists(manifestPath))
            {
                TraceLogger.Log($"No _sources.json found in {listFolderLocation}. Treating all {currentConfigUrls.Count} URL(s) as new (first run).", Enums.StatusSeverityType.Debug);
                addedUrls.AddRange(currentConfigUrls);
                return (addedUrls, removedFileNames);
            }

            Dictionary<string, string> previousSources;
            try
            {
                var json = File.ReadAllText(manifestPath);
                previousSources = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    json, ManifestJsonSerializerContext.Default.DictionaryStringString) ?? [];
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to read _sources.json in {listFolderLocation} ({ex.Message}). Treating all URLs as new.", Enums.StatusSeverityType.Warning);
                addedUrls.AddRange(currentConfigUrls);
                return (addedUrls, removedFileNames);
            }

            // Build a lookup: URL → fileName from the previous manifest
            var previousUrlLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in previousSources)
            {
                previousUrlLookup[kvp.Value] = kvp.Key; // url → fileName
            }

            // Detect removed URLs: in previous manifest but NOT in current config
            foreach (var url in previousUrlLookup.Keys)
            {
                if (!currentConfigUrls.Any(c => c.Equals(url, StringComparison.OrdinalIgnoreCase)))
                {
                    removedFileNames.Add(previousUrlLookup[url]);
                }
            }

            // Detect added URLs: in current config but NOT in previous manifest
            foreach (var url in currentConfigUrls)
            {
                if (!previousUrlLookup.ContainsKey(url))
                {
                    addedUrls.Add(url);
                }
            }
            TraceLogger.Log($"Reconciliation complete for {listFolderLocation}: {addedUrls.Count} new URL(s), {removedFileNames.Count} removed URL(s).", Enums.StatusSeverityType.Debug);
            return (addedUrls, removedFileNames);
        }

        /// <summary>
        /// After ProcessDownloadLists writes the new _sources.json, removes any orphaned files
        /// in the folder that are no longer referenced by the manifest. This handles the case
        /// where file numbering shifts after a URL removal (e.g. "3-C.txt" becomes "2-C.txt").
        /// </summary>
        private static void CleanupOrphanedFiles(string listFolderLocation, Dictionary<string, string> validSources)
        {
            var validFileNames = new HashSet<string>(validSources.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(listFolderLocation))
            {
                var fileName = Path.GetFileName(file);

                // Skip non-list files
                if (fileName.EndsWith(".etag", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.Equals("_sources.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase)) continue;

                if (!validFileNames.Contains(fileName))
                {
                    try
                    {
                        File.Delete(file);
                        var etagPath = file + ".etag";
                        if (File.Exists(etagPath)) File.Delete(etagPath);
                        TraceLogger.Log($"Cleaned up orphaned file: {fileName}", Enums.StatusSeverityType.Debug);
                    }
                    catch (Exception ex)
                    {
                        TraceLogger.Log($"Failed to clean up orphaned file {fileName}: {ex.Message}", Enums.StatusSeverityType.Warning);
                    }
                }
            }
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
            SemaphoreSlim semaphore = new(ConfigManager.Instance.MaxDownloadThreads, ConfigManager.Instance.MaxDownloadThreads);

            List<Task> tasks = [];
            // Track per-URL outcome so we can (a) print a real summary and (b) tell CheckIntegrity which
            // URLs are known-permanently-dead (404) so it doesn't treat them as a file-count mismatch and
            // loop forever trying to "recover" a URL that will never succeed.
            var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, DownloadOutcome>();
            // fileName -> source URL, so SearchManager can attribute a matched line back to the real
            // source URL rather than just an internal "3 - hosts.txt" filename.
            var sourceManifest = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

            foreach (var url in allUrls)
            {
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
                        var outcome = await DownloadController.DownloadFileAsync(url, filePath, forceMode, threadCount, cancellationToken);
                        outcomes[url] = outcome;
                        ConsoleProgress.ShowOperationProgress(threadCount, allUrls.Count, $"Processing {Path.GetFileName(url)}");

                        switch (outcome)
                        {
                            case DownloadOutcome.Success:
                                TraceLogger.Log($"{fileName} downloaded successfully.");
                                break;
                            case DownloadOutcome.SkippedUpToDate:
                                TraceLogger.Log($"{fileName} already up to date, skipped.");
                                break;
                            case DownloadOutcome.PermanentFailure:
                                TraceLogger.Log($"{url} is permanently unreachable (e.g. 404) and will be skipped in the integrity check. Fix or remove this source from settings.json.", Enums.StatusSeverityType.Warning);
                                break;
                            case DownloadOutcome.TransientFailure:
                                ProblemDuringUpdate = true;
                                TraceLogger.Log($"Download of {url} failed after retries. This may succeed on a later run. Check logs for more details.", Enums.StatusSeverityType.Error);
                                break;
                            case DownloadOutcome.Cancelled:
                                TraceLogger.Log($"{fileName} download was cancelled.", Enums.StatusSeverityType.Warning);
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

            // NEW: Clean up any orphaned files that are no longer in the manifest
            // (handles renumbering after URL removals, e.g. "3-C.txt" → "2-C.txt")
            CleanupOrphanedFiles(ListFolderLocation, manifestForSerialization);

            int succeeded = outcomes.Values.Count(o => o == DownloadOutcome.Success);
            int upToDate = outcomes.Values.Count(o => o == DownloadOutcome.SkippedUpToDate);
            int permanentFailures = outcomes.Values.Count(o => o == DownloadOutcome.PermanentFailure);
            int transientFailures = outcomes.Values.Count(o => o == DownloadOutcome.TransientFailure);
            //Add string to UpdateStatistics array on the next available index.
            string InternalUpdateStats = $"Downloads took {watch.Elapsed.TotalSeconds:N1}s for {Path.GetFileName(CombinedListLocation)} file processing {ListFolderLocation}: " +
            $"{succeeded} downloaded, {upToDate} already up to date, {permanentFailures} permanently unreachable, {transientFailures} failed after retries.";
            UpdateStatistics.Add(InternalUpdateStats);
            TraceLogger.Log(InternalUpdateStats);

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
                integrityOk = CheckIntegrity(ListFolderLocation, allUrls.Count, permanentFailures, CombinedListLocation, startTime);
            }
            else
            {
                hasUpdates = true;
                integrityOk = CompileList(ListFolderLocation, CombinedListLocation, allUrls.Count, permanentFailures, startTime);
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

        public static bool CompileList(string listFolderLocation, string combinedListLocation, int urlCount, int knownPermanentFailures, DateTime startTime)
        {
            TraceLogger.Log($"Compiling {Path.GetFileName(combinedListLocation)} list...");
            IOManager.MergeFiles(listFolderLocation, combinedListLocation);
            TransformationEngine.BeginTransformation(combinedListLocation);
            return CheckIntegrity(listFolderLocation, urlCount, knownPermanentFailures, combinedListLocation, startTime);
        }

        /// <summary>
        /// Verifies that the number of downloaded files matches the number of configured URLs (minus any
        /// sources that failed permanently, e.g. a 404, this run - those are expected to be missing and
        /// re-downloading won't change that), and that the combined list was actually written when updates
        /// were reported. Returns false on mismatch instead of exiting immediately, so the caller can attempt
        /// one automatic recovery before treating it as fatal.
        /// </summary>
        private static bool CheckURLandFileCount(DirectoryInfo listFolder, int urlCount, int knownPermanentFailures)
        {
            TraceLogger.Log($"Checking URL and file count for {listFolder.FullName}...");
            IEnumerable<string> files = Directory.GetFiles(listFolder.FullName, "*")
                .Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals("_sources.json", StringComparison.OrdinalIgnoreCase));
            int fileCount = files.Count();
            int expectedCount = urlCount - knownPermanentFailures;
            if (fileCount != expectedCount)
            {
                TraceLogger.Log($"URL and List file count mismatch! URL Count: {urlCount} (Expected present: {expectedCount} after excluding {knownPermanentFailures} known-unreachable source(s)) | File Count: {fileCount}", Enums.StatusSeverityType.Error);
                return false;
            }
            TraceLogger.Log($"Url Count OK, no mismatch. (URL Count: {urlCount} | File Count: {fileCount})");
            return true;
        }

        private static bool CheckIntegrity(string ListFolderLocation, int urlCount, int knownPermanentFailures, string CombinedListLocation, DateTime startTime)
        {
            TraceLogger.Log("Checking integrity of host files...");
            if (CheckURLandFileCount(new DirectoryInfo(ListFolderLocation), urlCount, knownPermanentFailures) == false)
            {
                TraceLogger.Log($"Integrity check failed due to URL and file count mismatch. Please check the logs for details.", Enums.StatusSeverityType.Error);
                return false;
            }
            TraceLogger.Log("Checking if combined list has been written to during update...", Enums.StatusSeverityType.Debug);
            if (new FileInfo(CombinedListLocation).Length > 0)
            {
                TraceLogger.Log($"{CombinedListLocation} has valid file size.", Enums.StatusSeverityType.Debug);
                if (!ProblemDuringUpdate && HasDownloadedUpdates)
                {
                    DateTime lastWriteTime = File.GetLastWriteTime(CombinedListLocation);
                    if (lastWriteTime < startTime)
                    {
                        TraceLogger.Log($"Integrity check failure (Internal Status Check Mismatch): {CombinedListLocation} hasn't been written to during the update process but the DownloadManager has reported that it downloaded updates. Last write time: {lastWriteTime}, Update start time: {startTime}.", Enums.StatusSeverityType.Error);
                        return false;
                    }
                }
                else if (!ProblemDuringUpdate && !HasDownloadedUpdates)
                {
                    TraceLogger.Log($"Skipping date written check on combined list since no updates were downloaded.", Enums.StatusSeverityType.Debug);
                }
            }
            TraceLogger.Log("Integrity check complete. No issues detected.");
            return true;
        }

        public static void GenerateCombinedList()
        {
            TraceLogger.Log($"Generating {Path.GetFileName(IOManager.CombinedListFileLocationTemp)} list...");
            try
            {
                var whiteList = ReadLinesFromFileCached(IOManager.CombinedWhiteListFileLocationTemp);
                var blockListLines = ReadLinesFromFile(IOManager.CombinedBlockListFileLocationTemp);
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
                if (_fileLineCache.TryGetValue(filePath, out var cachedLines))
                {
                    return cachedLines;
                }

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
                        var lines = ReadLinesFromFileCached(file);
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
                        var lines = ReadLinesFromFileCached(file);
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

        public static string GetSourceNameForFile(string fileName, bool isBlockList)
        {
            string manifestPath = Path.Combine(isBlockList ? IOManager.BlockListFolderLocation : IOManager.WhiteListFolderLocation, "_sources.json");
            if (!File.Exists(manifestPath))
            {
                TraceLogger.Log($"Source manifest not found at {manifestPath}.", Enums.StatusSeverityType.Fatal, ErrorCodes.FileMissing);
            }

            var manifestContent = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(manifestContent);

            if (doc.RootElement.TryGetProperty(fileName, out var element))
            {
                return element.GetString() ?? fileName;
            }

            return fileName;
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

                        var lines = ReadLinesFromFileCached(file);
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

                        var lines = ReadLinesFromFileCached(file);
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