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
using HostlistDownloaderCLI.Modules.IO;
using HostlistDownloaderCLI.Modules.Network;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace HostlistDownloaderCLI.Modules.HostlistManagement.Generation
{

    public static class HostlistOrchestrator
    {
        internal static bool ProblemDuringUpdate;
        internal static bool HasDownloadedUpdates;
        internal static ConcurrentBag<(string Hostlist, string UpdateStatus, double DownloadTime)> UpdateStatistics = [];
        internal static bool hasUpdates = false;
        internal static int NumberOfWarningsDuringDownloadPhase = 0;
        internal static bool IsInDownloadingPhase = false;

        internal static void StartListProcessing(bool forceMode, CancellationToken cancellationToken = default)
        {
            TraceLogger.Log($"Starting list processing... Fresh Mode: {forceMode}", Enums.StatusSeverityType.Info);

            string[] blockListInstance = [.. AppConfig.Instance.Blocklists];
            string[] whiteListInstance = [.. AppConfig.Instance.Whitelist];
            string[] userblockListInstance = [.. AppConfig.Instance.UserWebsiteBlocklist];
            string[] userwhiteListInstance = [.. AppConfig.Instance.UserWebsiteWhitelist];

            if (blockListInstance.Length == 0 && whiteListInstance.Length == 0)
            {
                TraceLogger.Log("Blocklist and Whitelist config are not configured.", Enums.StatusSeverityType.Fatal, ExceptionHandler.FileMissing);
                return;
            }

            if (blockListInstance.Length != 0)
            {
                TraceLogger.Log("Blocklist is configured. Updating blocklists...");

                var (addedUrls, removedFileNames) = SourceManager.ReconcileSources(Paths.BlockListFolderLocation, AppConfig.Instance.Blocklists);

                if (removedFileNames.Count > 0)
                {
                    TraceLogger.Log($"Blocklist: {removedFileNames.Count} URL(s) removed from config. Deleting only the affected file(s)...", Enums.StatusSeverityType.Warn);
                    IOManager.DeleteFileAlongWithETag(Paths.BlockListFolderLocation, removedFileNames);
                }
                if (addedUrls.Count > 0)
                {
                    TraceLogger.Log($"Blocklist: {addedUrls.Count} new URL(s) detected in config. Will download only those...");
                }
                if (addedUrls.Count == 0 && removedFileNames.Count == 0)
                {
                    TraceLogger.Log("Blocklist: No URL changes detected. Will verify existing files are up to date.");
                }

                ProcessDownloadLists(blockListInstance,
                    Paths.BlockListFolderLocation,
                    Paths.CombinedBlockListFileLocationTemp, forceMode, false, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userblockListInstance.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                IOManager.MergeUserDefinedDomains(Paths.CombinedBlockListFileLocationTemp, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (whiteListInstance.Length != 0)
            {
                var (wlAdded, wlRemoved) = SourceManager.ReconcileSources(Paths.WhiteListFolderLocation, AppConfig.Instance.Whitelist);

                if (wlRemoved.Count > 0)
                {
                    TraceLogger.Log($"Whitelist: {wlRemoved.Count} URL(s) removed from config. Deleting only the affected file(s)...", Enums.StatusSeverityType.Warn);
                    IOManager.DeleteFileAlongWithETag(Paths.WhiteListFolderLocation, wlRemoved);
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
                ProcessDownloadLists(whiteListInstance,
                    Paths.WhiteListFolderLocation,
                    Paths.CombinedWhiteListFileLocationTemp, forceMode, false, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userwhiteListInstance.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
                IOManager.MergeUserDefinedDomains(Paths.CombinedWhiteListFileLocationTemp, isBlocklist: false);
            }
            else
            {
                TraceLogger.Log("User Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (hasUpdates)
            {
                IOManager.GenerateTemporaryCombinedList();
            }
            IOManager.CommitTemporaryToMaster();

            TraceLogger.Log("Host lists update completed from processing function", Enums.StatusSeverityType.Debug);
        }

        /// <summary>
        /// Processes the blocklist and whitelist files in offline mode, merging them into combined lists without downloading from the internet. /merge does this. 
        /// </summary>
        internal static void StartOfflineListProcessing()
        {
            string[] blockListInstance = [.. AppConfig.Instance.Blocklists];
            string[] whiteListInstance = [.. AppConfig.Instance.Whitelist];
            string[] userblockListInstance = [.. AppConfig.Instance.UserWebsiteBlocklist];
            string[] userwhiteListInstance = [.. AppConfig.Instance.UserWebsiteWhitelist];
            TraceLogger.Log("Starting offline list processing...", Enums.StatusSeverityType.Info);

            if (blockListInstance.Length == 0 && whiteListInstance.Length == 0)
            {
                TraceLogger.Log("Blocklist and Whitelist config are not configured.", Enums.StatusSeverityType.Fatal, ExceptionHandler.FileMissing);
                return;
            }
            if (blockListInstance.Length != 0)
            {
                TraceLogger.Log("Blocklist is configured. Merging...");
                CompileSpecificList(Paths.BlockListFolderLocation, Paths.CombinedBlockListFileLocationTemp, blockListInstance.Length, 0, DateTime.Now);
            }
            else
            {
                TraceLogger.Log("Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }
            if (whiteListInstance.Length != 0)
            {
                TraceLogger.Log("Whitelist is configured. Merging user config...");
                CompileSpecificList(Paths.WhiteListFolderLocation, Paths.CombinedWhiteListFileLocationTemp, whiteListInstance.Length, 0, DateTime.Now);
            }
            else
            {
                TraceLogger.Log("Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            if (userwhiteListInstance.Length != 0)
            {
                TraceLogger.Log("User Whitelist is configured. Merging user config...");
                IOManager.MergeUserDefinedDomains(Paths.CombinedWhiteListFileLocationTemp, isBlocklist: false);
            }
            else
            {
                TraceLogger.Log("User Whitelist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }
            if (userblockListInstance.Length != 0)
            {
                TraceLogger.Log("User blocklist is configured. Merging user config...");
                IOManager.MergeUserDefinedDomains(Paths.CombinedBlockListFileLocationTemp, isBlocklist: true);
            }
            else
            {
                TraceLogger.Log("User Blocklist not configured. Ignoring", Enums.StatusSeverityType.Debug);
            }

            IOManager.GenerateTemporaryCombinedList();
            IOManager.CommitTemporaryToMaster();
            TraceLogger.Log("Offline list processing completed!", Enums.StatusSeverityType.Notice);
            Environment.Exit(0);
        }

        private static async Task ProcessDownloadLists(string[] listConfigInstance, string ListFolderLocation, string CombinedListLocation, bool forceMode, bool isRetryAttempt = false, CancellationToken cancellationToken = default)
        {
            TraceLogger.Log($"Starting download for INI files. ListFolderLocation: {ListFolderLocation} | CombinedListLocation: {CombinedListLocation}", Enums.StatusSeverityType.Debug);

            var allUrls = new List<string>();

            foreach (var urlInstance in listConfigInstance)
            {
                var isValid = URLSecurityValidator.ValidateURLFromConfigInstance(urlInstance);
                if (isValid)
                {
                    allUrls.Add(urlInstance);
                }
                else
                {
                    TraceLogger.Log($"Invalid URL skipped: {urlInstance}. Please check your settings.json configuration.", Enums.StatusSeverityType.Warn);
                }
            }

            if (allUrls.Count == 0)
            {
                TraceLogger.Log("No URLs found in the configuration files.", Enums.StatusSeverityType.Warn);
                return;
            }

            DateTime startTime = DateTime.Now;
            Stopwatch watch = Stopwatch.StartNew();

            // Track per-URL outcome so we can (a) print a real summary and (b) tell CheckIntegrity which
            // URLs are known-permanently-dead (404) so it doesn't treat them as a file-count mismatch and
            // loop forever trying to "recover" a URL that will never succeed.
            var outcomes = new ConcurrentDictionary<string, DownloadOutcome>();
            // fileName -> source URL, so SearchManager can attribute a matched line back to the real
            // source URL rather than just an internal "3 - hosts.txt" filename.
            var sourceManifest = new ConcurrentDictionary<string, string>();
            IsInDownloadingPhase = true;
            await ConsoleUI.ProgressAsync("Downloading", allUrls.Count, "URLs", async progress =>
            {
                await Parallel.ForEachAsync(
                    allUrls.Select((url, index) => (url, index)),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = AppConfig.Instance.MaxDownloadThreads,
                        CancellationToken = cancellationToken
                    },
                    async (item, ct) =>
                    {
                        Stopwatch downloadTime = Stopwatch.StartNew();
                        var (url, zeroBasedIndex) = item;
                        var threadCount = zeroBasedIndex + 1;

                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            TraceLogger.Log($"Invalid or non-HTTP(S) URL skipped: {url}", Enums.StatusSeverityType.Warn);
                            progress.Advance(url);
                            return;
                        }

                        var safeFileName = string.Join("_", uri.LocalPath.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                        var fileName = $"{threadCount} - {safeFileName}";
                        var filePath = Path.Combine(ListFolderLocation, fileName);

                        string fullFilePath = Path.GetFullPath(filePath);
                        string fullBaseDir = Path.GetFullPath(ListFolderLocation) + Path.DirectorySeparatorChar;
                        if (!fullFilePath.StartsWith(fullBaseDir, StringComparison.OrdinalIgnoreCase))
                        {
                            TraceLogger.Log($"Path traversal attempt blocked: {url}", Enums.StatusSeverityType.Error);
                            progress.Advance(url);
                            return;
                        }
                        if (safeFileName.Contains(".."))
                        {
                            TraceLogger.Log($"Filename contains '..' segment, blocked: {url}", Enums.StatusSeverityType.Error);
                            progress.Advance(url);
                            return;
                        }

                        sourceManifest[fileName] = url;
                        progress.SetCurrent(url);

                        try
                        {
                            TraceLogger.Log($"Added {fileName} to queue.", Enums.StatusSeverityType.Debug);
                            var outcome = DownloadOutcome.NotStarted;
                            outcome = await DownloadController.DownloadFileAsync(url, filePath, forceMode, threadCount, ct);
                            outcomes[url] = outcome;
                            downloadTime.Stop();
                            switch (outcome)
                            {
                                case DownloadOutcome.Success:
                                    UpdateStatistics.Add(($"{fileName}", "Downloaded", downloadTime.ElapsedMilliseconds));
                                    TraceLogger.Log($"{fileName} downloaded successfully.");
                                    break;
                                case DownloadOutcome.SuccessWithNoETag:
                                    UpdateStatistics.Add(($"{fileName}", "Downloaded (no ETag)", downloadTime.ElapsedMilliseconds));
                                    TraceLogger.Log($"{fileName} downloaded successfully (no ETag detected, list will be re-downloaded on every sync regardless of any changes).");
                                    break;
                                case DownloadOutcome.SkippedUpToDate:
                                    //UpdateStatistics.Add($"{fileName} already up to date, skipped.");
                                    TraceLogger.Log($"{fileName} already up to date, skipped.");
                                    break;
                                case DownloadOutcome.PermanentFailure:
                                    UpdateStatistics.Add(($"{fileName}", "Permanently unreachable", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"{url} is permanently unreachable (e.g. 404) and will be skipped in the integrity check. Fix or remove this source from settings.json.", Enums.StatusSeverityType.Warn);
                                    break;
                                case DownloadOutcome.TransientFailure:
                                    UpdateStatistics.Add(($"{fileName}", "Failed after retries", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"Download of {url} failed after retries. This may succeed on a later run. Check logs for more details.", Enums.StatusSeverityType.Error);
                                    break;
                                case DownloadOutcome.Cancelled:
                                    UpdateStatistics.Add(($"{fileName}", "Cancelled", 0));
                                    TraceLogger.Log($"{fileName} download was cancelled.", Enums.StatusSeverityType.Warn);
                                    break;
                                case DownloadOutcome.DownloadBlockedByConfig:
                                    UpdateStatistics.Add(($"{fileName}", "Blocked by config", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"{fileName} download was blocked by settings.json configuration (e.g. allowInsecureSources was false and HLD attempted to download from HTTP.).", Enums.StatusSeverityType.Warn);
                                    break;
                                case DownloadOutcome.NotStarted:
                                    UpdateStatistics.Add(($"{fileName}", "Not started", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"Download of {url} was not started. This is unexpected and may indicate a bug.", Enums.StatusSeverityType.Error);
                                    break;
                                case DownloadOutcome.ContentRejectedNonText:
                                    UpdateStatistics.Add(($"{fileName}", "Content rejected", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"Download of {url} was rejected because the content was not a text file. This may indicate a misconfigured source or a change in the source's content type.", Enums.StatusSeverityType.Warn);
                                    break;
                                case DownloadOutcome.SecurityViolationDetected:
                                    UpdateStatistics.Add(($"{fileName}", "Security violation", 0));
                                    ProblemDuringUpdate = true;
                                    TraceLogger.Log($"Download of {url} was blocked due to a security violation (e.g. SSRF protection fault).", Enums.StatusSeverityType.Warn);
                                    break;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            UpdateStatistics.Add(($"{fileName}", "Cancelled", 0));
                            outcomes[url] = DownloadOutcome.Cancelled;
                            TraceLogger.Log($"{fileName} download was cancelled.", Enums.StatusSeverityType.Warn);
                        }
                        catch (Exception ex)
                        {
                            UpdateStatistics.Add(($"{fileName}", "Failed after retries", 0));
                            outcomes[url] = DownloadOutcome.TransientFailure;
                            ProblemDuringUpdate = true;
                            TraceLogger.Log($"Failed to download {url}: {ex}", Enums.StatusSeverityType.Error);
                        }
                        finally
                        {
                            progress.Advance(url);
                        }
                    });
            });

            IsInDownloadingPhase = false;

            watch.Stop();

            if (cancellationToken.IsCancellationRequested)
            {
                TraceLogger.Log("Download cancelled by user before completion.", Enums.StatusSeverityType.Warn);
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

            int succeeded = 0;
            int succeededWithNoETag = 0;
            int upToDate = 0;
            int permanentFailures = 0;
            int transientFailures = 0;
            int cancelled = 0;
            int blockedByConfig = 0;
            int contentRejected = 0;
            int securityViolation = 0;
            foreach (var outcome in outcomes.Values)
            {
                switch (outcome)
                {
                    case DownloadOutcome.Success:
                        succeeded++;
                        break;
                    case DownloadOutcome.SuccessWithNoETag:
                        succeededWithNoETag++;
                        break;
                    case DownloadOutcome.SkippedUpToDate:
                        upToDate++;
                        break;
                    case DownloadOutcome.PermanentFailure:
                        permanentFailures++;
                        break;
                    case DownloadOutcome.TransientFailure:
                        transientFailures++;
                        break;
                    case DownloadOutcome.Cancelled:
                        cancelled++;
                        break;
                    case DownloadOutcome.DownloadBlockedByConfig:
                        blockedByConfig++;
                        break;
                    case DownloadOutcome.ContentRejectedNonText:
                        contentRejected++;
                        break;
                    case DownloadOutcome.SecurityViolationDetected:
                        securityViolation++;
                        break;
                    default:
                        TraceLogger.Log($"Unexpected download outcome: {outcome}. This may indicate a bug.", Enums.StatusSeverityType.Error);
                        break;
                }
            }

            //Add string to UpdateStatistics array on the next available index.
            string InternalUpdateStats = $"Downloads took {watch.Elapsed.TotalSeconds:N1}s for {Path.GetFileName(CombinedListLocation)} file processing {ListFolderLocation}: " +
                $"{succeeded} downloaded, {succeededWithNoETag} downloaded (no ETag), {upToDate} already up to date, {permanentFailures} permanently unreachable, {transientFailures} failed after retries.";

            TraceLogger.Log(InternalUpdateStats, Enums.StatusSeverityType.Info);
            ConsoleUI.ShowDownloadSummary(watch.Elapsed, succeeded, succeededWithNoETag, upToDate, permanentFailures, transientFailures, cancelled, blockedByConfig, contentRejected, securityViolation);

            if (transientFailures > 0)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Some downloads failed after retries. This may succeed on a later run. Check logs for more details.", Enums.StatusSeverityType.Warn);
            }
            if (permanentFailures > 0)
            {
                ProblemDuringUpdate = true;
                TraceLogger.Log($"Some downloads failed permanently (e.g. 404). These will be skipped in the integrity check. Please review and fix the affected URL(s) in settings.json or remove permanently offline entries.", Enums.StatusSeverityType.Warn);
            }

            bool integrityOk;
            if (!HasDownloadedUpdates)
            {
                TraceLogger.Log("No need to compile lists since no available updates were downloaded. Checking integrity of existing lists...");
                integrityOk = HostlistIntegrityValidator.CheckIntegrity(ListFolderLocation, allUrls.Count, permanentFailures, CombinedListLocation, startTime, ProblemDuringUpdate, HasDownloadedUpdates);
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
                TraceLogger.Log($"Integrity check failed again after an automatic retry. Giving up on {CombinedListLocation}. Please run HostlistDownloader again, or with the /fresh argument if the problem persists.", Enums.StatusSeverityType.Fatal, ExceptionHandler.IntegrityCheckFailure);
                return;
            }

            if (permanentFailures > 0 && transientFailures == 0)
            {
                // Every failure was a permanent one (404 etc). Re-downloading will hit the exact same
                // 404s again, so the "clear and retry" recovery path can't fix anything here - it would
                // just loop forever. Treat this as a config problem instead of a transient integrity fault.
                TraceLogger.Log($"Integrity check failed because {permanentFailures} source(s) are permanently unreachable, not due to a transient issue. Automatic recovery would repeat the same failure, so it's being skipped. Please review and fix the affected URL(s) in settings.json.", Enums.StatusSeverityType.Fatal, ExceptionHandler.IntegrityCheckFailure);
                return;
            }

            TraceLogger.Log("Attempting automatic recovery: clearing this list's folder and re-downloading everything once...", Enums.StatusSeverityType.Warn);
            IOManager.ClearTempFiles(ListFolderLocation);
            await ProcessDownloadLists(listConfigInstance, ListFolderLocation, CombinedListLocation, forceMode: true, isRetryAttempt: true, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Used to compile specific lists, e.g. blocklist files or whitelist files, into a single combined list.
        /// </summary>
        /// <param name="listFolderLocation"></param>
        /// <param name="combinedListLocation"></param>
        /// <param name="urlCount"></param>
        /// <param name="knownPermanentFailures"></param>
        /// <param name="startTime"></param>
        /// <returns></returns>
        private static bool CompileSpecificList(string listFolderLocation, string combinedListLocation, int urlCount, int knownPermanentFailures, DateTime startTime)
        {
            if (HostlistIntegrityValidator.CheckIntegrity(listFolderLocation, urlCount, knownPermanentFailures, combinedListLocation, startTime, ProblemDuringUpdate, HasDownloadedUpdates))
            {
                TraceLogger.Log($"Compiling {Path.GetFileName(combinedListLocation)} list...");
                IOManager.MergeFilesInDirectory(listFolderLocation, combinedListLocation);
                TransformationEngine.BeginTransformation(combinedListLocation);
                return true;
            }
            else
            {
                TraceLogger.Log($"Integrity check failed for {Path.GetFileName(combinedListLocation)}. Attempting automatic recovery.", Enums.StatusSeverityType.Warn);
                return false;
            }
        }
    }
}