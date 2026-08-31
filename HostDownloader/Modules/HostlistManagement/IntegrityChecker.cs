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

namespace HostlistDownloader.Modules.HostlistManagement
{
    internal class IntegrityChecker
    {
        /// <summary>
        /// Verifies that the number of downloaded files matches the number of configured URLs (minus any
        /// sources that failed permanently, e.g. a 404, this run - those are expected to be missing and
        /// re-downloading won't change that), and that the combined list was actually written when updates
        /// were reported. Returns false on mismatch instead of exiting immediately, so the caller can attempt
        /// one automatic recovery before treating it as fatal.
        /// </summary>
        public static bool CheckURLandFileCount(DirectoryInfo listFolder, int urlCount, int knownPermanentFailures)
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

        public static bool CheckIntegrity(string ListFolderLocation, int urlCount, int knownPermanentFailures, string CombinedListLocation, DateTime startTime, bool ProblemDuringUpdate, bool HasDownloadedUpdates)
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
    }
}
