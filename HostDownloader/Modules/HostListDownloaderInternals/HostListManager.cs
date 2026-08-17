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
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.HostListDownloaderInternals
{
    public static class HostListManager
    {
        public static bool ProblemDuringUpdate;
        public static bool HasDownloadedUpdates;
        private static bool hasUpdates = false;
        private static readonly Dictionary<string, HashSet<string>> _fileLineCache = [];
        private static readonly Lock _cacheLock = new();

        public static void StartListProcessing(bool forceMode, CancellationToken cancellationToken = default)
        {
            TraceLogger.Log("Starting list processing...", Enums.StatusSeverityType.Information);

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

                // Reconcile sources: detect added/removed URLs individually instead of clearing everything
                var (addedUrls, removedFileNames) = ReconcileSources(IOManager.BlockListFolderLocation, ConfigReader.Instance.Blocklists);

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
                    IOManager.CombinedBlockListFileLocation, forceMode, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring");
            }

            if (userblockListIni.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                MergeUserDefinedDomains(IOManager.CombinedBlockListFileLocation, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring");
            }

            if (whiteListIni.Length != 0)
            {
                // Reconcile sources for whitelist
                var (wlAdded, wlRemoved) = ReconcileSources(IOManager.WhiteListFolderLocation, ConfigReader.Instance.Whitelist);

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
                    IOManager.CombinedWhiteListFileLocation, forceMode, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring");
            }

            if (userwhiteListIni.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
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
                TraceLogger.Log($"No _sources.json found in {listFolderLocation}. Treating all {currentConfigUrls.Count} URL(s) as new (first run).");
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
            TraceLogger.Log($"Reconciliation complete for {listFolderLocation}: {addedUrls.Count} new URL(s), {removedFileNames.Count} removed URL(s).");
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
                        TraceLogger.Log($"Cleaned up orphaned file: {fileName}");
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
                    ? ConfigReader.Instance.UserWebsiteBlocklist
                    : ConfigReader.Instance.UserWebsiteWhitelist;
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
            SemaphoreSlim semaphore = new(ConfigReader.Instance.MaxDownloadThreads, ConfigReader.Instance.MaxDownloadThreads);

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
                var fileName = $"{threadCount} - {Path.GetFileName(url)}";
                var filePath = Path.Combine(ListFolderLocation, fileName);
                sourceManifest[fileName] = url;

                tasks.Add(Task.Run(async () =>
                {
                    bool acquired = false;
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        acquired = true;
                        TraceLogger.Log($"Added {fileName} to queue.");
                        var outcome = await DownloadController.DownloadFileAsync(url, filePath, forceMode, cancellationToken);
                        outcomes[url] = outcome;
                        ConsoleProgress.ShowOperationProgress(threadCount, allUrls.Count, $"Downloaded {Path.GetFileName(url)}");

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
                            TraceLogger.Log($"{fileName} download task completed and released.");
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
            TraceLogger.Log($"Downloads complete in {watch.Elapsed.TotalSeconds:N1}s for {Path.GetFileName(CombinedListLocation)}: " +
                $"{succeeded} downloaded, {upToDate} already up to date, {permanentFailures} permanently unreachable, {transientFailures} failed after retries.");

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

        private static bool CompileList(string listFolderLocation, string combinedListLocation, int urlCount, int knownPermanentFailures, DateTime startTime)
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
            TraceLogger.Log($"Url Count OK, no mismatch. (URL COUNT: {urlCount} | ACCEPTABLE FILE COUNT: {fileCount})");
            return true;
        }

        private static bool CheckIntegrity(string ListFolderLocation, int urlCount, int knownPermanentFailures, string CombinedListLocation, DateTime startTime)
        {
            TraceLogger.Log("Integrity check started. Checking if URL count and file count match...");
            if (CheckURLandFileCount(new DirectoryInfo(ListFolderLocation), urlCount, knownPermanentFailures) == false)
            {
                TraceLogger.Log($"Integrity check failed due to URL and file count mismatch. Please check the logs for details.", Enums.StatusSeverityType.Error);
                return false;
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
                        TraceLogger.Log($"Integrity check failure (Internal Status Check Mismatch): {CombinedListLocation} hasn't been written to during the update process but the DownloadManager has reported that it downloaded updates. Last write time: {lastWriteTime}, Update start time: {startTime}.", Enums.StatusSeverityType.Error);
                        return false;
                    }
                }
                else if (!ProblemDuringUpdate && !HasDownloadedUpdates)
                {
                    TraceLogger.Log($"Skipping date written check on combined list since no updates were downloaded.");
                }
            }
            TraceLogger.Log("Integrity check complete. No issues detected.");
            return true;
        }

        public static void GenerateCombinedList()
        {
            TraceLogger.Log($"Generating {Path.GetFileName(IOManager.CombinedListFileLocation)} list...");
            try
            {
                var whiteList = ReadLinesFromFileCached(IOManager.CombinedWhiteListFileLocation);
                var blockListLines = ReadLinesFromFile(IOManager.CombinedBlockListFileLocation);
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
    }
}