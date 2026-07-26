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

        public static void StartListProcessing(bool forceMode, CancellationToken cancellationToken = default)
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
                    IOManager.CombinedBlockListFileLocation, forceMode, cancellationToken).GetAwaiter().GetResult();
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
                    IOManager.CombinedWhiteListFileLocation, forceMode, cancellationToken).GetAwaiter().GetResult();
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
                        // Skip just this entry - a single blank/comment line shouldn't abort the whole merge.
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
            //Semaphore with a maximum of 3 concurrent downloads
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

                //TraceLogger.Log($"Adding {fileName} download to task queue...");
                tasks.Add(Task.Run(async () =>
                {
                    bool acquired = false;
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken); // Wait for available slot
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
                                // Don't set ProblemDuringUpdate for a permanent failure (e.g. 404) - that's an
                                // expected/known-bad source that CheckIntegrity accounts for separately, not a
                                // fault in the app or this run.
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

            try
            {
                string manifestPath = Path.Combine(ListFolderLocation, "_sources.json");
                var manifestForSerialization = new Dictionary<string, string>(sourceManifest);
                File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(
                    manifestForSerialization, ManifestJsonSerializerContext.Default.DictionaryStringString));
            }
            catch (Exception ex)
            {
                // Non-fatal - SearchManager falls back to raw filenames if this is missing or unreadable.
                TraceLogger.Log($"Failed to write source manifest for {ListFolderLocation}: {ex.Message}", Enums.StatusSeverityType.Warning);
            }

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
                hasUpdates = true; // Set the flag to indicate that updates were downloaded, this will tell the GenerateCombinedList method to run later
                // Use IOManager methods but with the right folder path
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
            IOManager.RemoveDuplicates(combinedListLocation);
            IOManager.FormatHosts(combinedListLocation);
            return CheckIntegrity(listFolderLocation, urlCount, knownPermanentFailures, combinedListLocation, startTime);
        }

        /// <summary>
        /// Verifies that the number of downloaded files matches the number of configured URLs (minus any
        /// sources that failed permanently, e.g. a 404, this run - those are expected to be missing and
        /// re-downloading won't change that), and that the combined list was actually written when updates
        /// were reported. Returns false on mismatch instead of exiting immediately, so the caller can attempt
        /// one automatic recovery before treating it as fatal.
        /// </summary>
        private static bool CheckIntegrity(string ListFolderLocation, int urlCount, int knownPermanentFailures, string CombinedListLocation, DateTime startTime)
        {
            TraceLogger.Log("Integrity check started. Checking if URL count and file count match...");
            var files = Directory.GetFiles(ListFolderLocation, "*.*")
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
            TraceLogger.Log("URL and file count OK.");

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