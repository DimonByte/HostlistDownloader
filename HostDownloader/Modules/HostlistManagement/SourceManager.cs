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
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace HostlistDownloader.Modules.HostlistManagement
{
    internal class SourceManager
    {
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
        public static (List<string> addedUrls, List<string> removedFileNames) ReconcileSources(
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
                previousSources = JsonSerializer.Deserialize<Dictionary<string, string>>(
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
        public static void CleanupOrphanedFiles(string listFolderLocation, Dictionary<string, string> validSources)
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
    }
}
