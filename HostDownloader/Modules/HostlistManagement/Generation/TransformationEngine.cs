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
using HostlistDownloader.Modules.WindowsSystem.IO;
using iluvadev.ConsoleProgressBar;
using System.Diagnostics;
using System.Net;

namespace HostlistDownloader.Modules.HostlistManagement.Generation
{
    internal class TransformationEngine
    {
        /// <summary>
        /// Begins the transformation process on the specified combined hosts file by first removing duplicates and then formatting the entries according to the configured format type.
        /// </summary>
        /// <param name="combinedFileLocation"></param>
        internal static void BeginTransformation(string combinedFileLocation) //Cool name.
        {
            TraceLogger.Log("Starting Transformation...");
            RemoveDuplicates(combinedFileLocation);
            TraceLogger.Log("Formatting hosts...");
            FormatHosts(combinedFileLocation);
            TraceLogger.Log("Transformation complete.");
        }

        /// <summary>
        /// Formats the hosts file at the specified path according to the configured format type and writes the formatted entries back to the file.
        /// </summary>
        /// <remarks>Reads the format type from ConfigReader.Instance.Formattype (defaults to "domain").
        /// Supports formats such as hosts, domain, iponly, uBlock/AdGuard, dnsmasq, wildcard, and raw. Ignores blank
        /// lines and comments, handles malformed entries by logging warnings, and preserves or strips wildcard entries
        /// depending on the target format. Writes the resulting formatted lines back to the given file and logs errors
        /// if writing fails.</remarks>
        /// <param name="combinedFileLocation">Path to the combined hosts file to read, format, and overwrite.</param>
        private static void FormatHosts(string combinedFileLocation)
        {
            TraceLogger.Log($"Attempting to format Hostfile: {combinedFileLocation}");
            string formatTypePath = AppConfig.Instance.Formattype;
            string formatType = "domain"; // default format type

            try
            {
                formatType = formatTypePath.Trim().ToLowerInvariant();
                TraceLogger.Log($"Format Type: {formatType}", Enums.StatusSeverityType.Debug);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error reading format type from {formatTypePath}: {ex}. Reverting to domain format.", Enums.StatusSeverityType.Error);
            }

            if (!File.Exists(combinedFileLocation))
            {
                TraceLogger.Log($"Combined file not found: {combinedFileLocation}", Enums.StatusSeverityType.Warning);
                return;
            }

            var originalLines = File.ReadAllLines(combinedFileLocation);
            var formattedLines = new List<string>();

            int wildcardDropped = 0;
            int wildcardStripped = 0;
            int wildcardPreserved = 0;

            int max = originalLines.Length;

            using (var pb = new ProgressBar() { Maximum = max })
            {
                if (TraceLogger.QuietMode) pb.Text.Body.SetVisible(false);
                pb.Text.Description.Clear();
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Formatting line: {pb.ElementName}");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Lines remaining: {pb.Value}");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processing time: {pb.TimeProcessing.TotalSeconds}s.");
                pb.Text.Description.Processing.AddNew().SetValue(pb => $"Estimated remaining time: {pb.TimeRemaining?.TotalSeconds}s.");
                pb.Text.Description.Done.AddNew().SetValue(pb => $"{pb.Value} lines formatted in {pb.TimeProcessing.TotalSeconds}s.");

                foreach (var line in originalLines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                    var trimmedLine = line.Trim();
                    int commentIndex = trimmedLine.IndexOf('#');
                    if (commentIndex >= 0) trimmedLine = trimmedLine[..commentIndex].Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                    var parts = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    string? validIp = null;
                    string domainList = "";

                    if (parts.Length >= 2)
                    {
                        if (IPAddress.TryParse(parts[0], out var parsedIp))
                        {
                            validIp = parsedIp.ToString();
                            domainList = string.Join(" ", parts.Skip(1));
                        }
                        else
                        {
                            TraceLogger.Log($"Malformed hosts entry (invalid/missing IP): '{trimmedLine}'. Formatting as domain-only.", Enums.StatusSeverityType.Warning);
                            domainList = trimmedLine.Replace('\t', ' ').Trim();
                        }
                    }
                    else if (parts.Length == 1)
                    {
                        domainList = parts[0];
                    }

                    bool isWildcard = domainList.Contains("*.") || trimmedLine.Contains("*.");

                    switch (formatType)
                    {
                        case "hosts":
                        case "host":
                        case "pihole":
                        case "pi-hole":
                            if (isWildcard) { wildcardDropped++; continue; }
                            formattedLines.Add(validIp is not null ? $"{validIp} {domainList}" : $"0.0.0.0 {domainList}");
                            break;

                        case "domain":
                            if (isWildcard)
                            {
                                domainList = domainList.Replace("*.", "").Trim();
                                if (string.IsNullOrEmpty(domainList))
                                {
                                    wildcardDropped++;
                                    continue;
                                }
                                wildcardStripped++;
                            }
                            formattedLines.Add(domainList);
                            break;

                        case "iponly":
                            if (isWildcard) { wildcardDropped++; continue; }
                            if (validIp is not null && !string.Equals(validIp, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
                                formattedLines.Add(validIp);
                            break;

                        case "ublockorigin":
                        case "uBlock":
                        case "uBlock Origin":
                        case "ad-guard":
                        case "AdGuard":
                            if (isWildcard)
                            {
                                formattedLines.Add(domainList);
                                wildcardPreserved++;
                            }
                            else
                            {
                                formattedLines.Add($"||{domainList}^");
                            }
                            break;

                        case "dnsmasq":
                            if (isWildcard) { wildcardDropped++; continue; }
                            formattedLines.Add($"address=/{domainList}/0.0.0.0");
                            break;

                        case "wildcard":
                            formattedLines.Add(domainList.StartsWith("*.") ? domainList : $"*.{domainList}");
                            break;

                        case "raw":
                            formattedLines.Add(trimmedLine);
                            break;

                        default:
                            formattedLines.Add(domainList);
                            break;
                    }
                    pb.PerformStep(domainList);
                }
            }

            try
            {
                TraceLogger.Log($"Formatting Complete. Saving {formattedLines.Count:N0} lines to {combinedFileLocation}");
                File.WriteAllLines(combinedFileLocation, formattedLines);
                TraceLogger.Log($"Saved {formattedLines.Count:N0} lines to {combinedFileLocation}", Enums.StatusSeverityType.Notice);
                if (wildcardStripped > 0)
                    TraceLogger.Log($"Stripped prefix from {wildcardStripped:N0} wildcard entries (kept as base domain).");
                if (wildcardDropped > 0)
                    TraceLogger.Log($"Dropped {wildcardDropped:N0} wildcard entries (invalid for {formatType} format).");
                if (wildcardPreserved > 0)
                    TraceLogger.Log($"Preserved {wildcardPreserved:N0} wildcard entries (kept as-is for {formatType} format).");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error writing formatted lines to {combinedFileLocation}: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Removes duplicate and empty lines from the specified merged hosts file, preserving the first occurrence of each unique line. Logs the number of duplicates and empty lines removed, as well as the time taken for the operation. Writes the cleaned lines back to the same file.
        /// </summary>
        /// <param name="MergedFileLoc"></param>
        private static void RemoveDuplicates(string MergedFileLoc)
        {
            try
            {
                TraceLogger.Log($"Removing duplicates from {Path.GetFileName(MergedFileLoc)}...");

                if (!File.Exists(MergedFileLoc))
                {
                    TraceLogger.Log($"File not found: {MergedFileLoc}", Enums.StatusSeverityType.Warning);
                    return;
                }

                var originalLines = File.ReadAllLines(MergedFileLoc);
                int originalCount = originalLines.Length;

                Stopwatch watch = Stopwatch.StartNew();

                // Use List + HashSet to preserve first-occurrence order while deduping.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cleanedLines = new List<string>();
                int emptyRemoved = 0;

                int max = originalLines.Length;

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
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Removing duplicates: {pb.ElementName}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Line count: {pb.Value}");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Processing time: {pb.TimeProcessing.TotalSeconds}s.");
                    pb.Text.Description.Processing.AddNew().SetValue(pb => $"Estimated remaining time: {pb.TimeRemaining?.TotalSeconds}s.");

                    //Setting "Description Text" when "Done"
                    pb.Text.Description.Done.AddNew().SetValue(pb => $"{pb.Value} elements in {pb.TimeProcessing.TotalSeconds}s.");

                    foreach (var rawLine in originalLines)
                    {
                        // Trim leading/trailing whitespace (normalizes inconsistent source formatting)
                        var line = rawLine.Trim();

                        // Skip empty and whitespace-only lines
                        if (line.Length == 0)
                        {
                            emptyRemoved++;
                            continue;
                        }

                        // Case-insensitive dedup on the trimmed line
                        if (seen.Add(line))
                        {
                            cleanedLines.Add(line);
                        }
                        pb.PerformStep(line);
                    }
                }

                try
                {
                    File.WriteAllLines(MergedFileLoc, cleanedLines);
                    TraceLogger.Log($"Removed duplicates and empty lines. Saved {cleanedLines.Count:N0} unique lines to {MergedFileLoc}", Enums.StatusSeverityType.Notice);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error writing cleaned lines to {MergedFileLoc}: {ex}", Enums.StatusSeverityType.Error);
                }

                watch.Stop();

                int totalRemoved = originalCount - cleanedLines.Count;
                int dupRemoved = totalRemoved - emptyRemoved;

                // Size estimate (UTF-8 + newline per line)
                long originalSize = originalLines.Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 2);
                long newSize = cleanedLines.Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 2);
                long sizeDiff = originalSize - newSize;

                TraceLogger.Log($"Cleanup complete in {watch.Elapsed.TotalSeconds:F2}s.");
                TraceLogger.Log($"Removed {totalRemoved:N0} lines total ({IOManager.FormatBytes(sizeDiff)} saved).");
                if (dupRemoved > 0)
                    TraceLogger.Log($"  Duplicates removed: {dupRemoved:N0}");
                if (emptyRemoved > 0)
                    TraceLogger.Log($"  Empty/whitespace lines removed: {emptyRemoved:N0}");
                TraceLogger.Log($"Lines before: {originalCount:N0} → after: {cleanedLines.Count:N0}");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error removing duplicates from {MergedFileLoc}: {ex}", Enums.StatusSeverityType.Error);
            }
        }
    }
}