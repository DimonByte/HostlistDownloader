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
using System.Net;

namespace HostlistDownloader.Modules.HostlistManagement.Generation
{
    internal class TransformationEngine
    {
        public static void BeginTransformation(string combinedFileLocation) //Cool name.
        {
            TraceLogger.Log("[Step 1] Starting Transformation... Removing duplicates...");
            RemoveDuplicates(combinedFileLocation);
            TraceLogger.Log("[Step 2] Formatting hosts...");
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
        public static void FormatHosts(string combinedFileLocation)
        {
            TraceLogger.Log($"Attempting to format Hostfile: {combinedFileLocation}");
            string formatTypePath = ConfigManager.Instance.Formattype;
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
            int wildcardPreserved = 0;
            int wildcardRemoved = 0;

            foreach (var line in originalLines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var trimmedLine = line.Trim();
                int commentIndex = trimmedLine.IndexOf('#');
                if (commentIndex >= 0)
                {
                    trimmedLine = trimmedLine[..commentIndex].Trim();
                }

                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;

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

                // Detect wildcard entries (e.g. "*.example.com" or "0.0.0.0 *.example.com")
                bool isWildcard = domainList.Contains("*.") || trimmedLine.Contains("*.");

                switch (formatType)
                {
                    case "hosts":
                    case "host":
                    case "pihole":
                    case "pi-hole":
                        if (isWildcard)
                        {
                            wildcardRemoved++;
                            continue; // Wildcards are not valid hostnames; skip for hosts/pihole
                        }
                        formattedLines.Add(validIp is not null ? $"{validIp} {domainList}" : $"0.0.0.0 {domainList}");
                        break;

                    case "domain":
                        if (isWildcard)
                        {
                            // Strip the "*." prefix so the base domain is still usable
                            domainList = domainList.Replace("*.", "").Trim();
                            if (string.IsNullOrEmpty(domainList))
                            {
                                wildcardRemoved++;
                                continue;
                            }
                            wildcardRemoved++;
                        }
                        formattedLines.Add(domainList);
                        break;

                    case "iponly":
                        if (isWildcard)
                        {
                            wildcardRemoved++;
                            continue; // No IP to extract from a wildcard entry
                        }
                        if (validIp is not null && !string.Equals(validIp, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
                        {
                            formattedLines.Add(validIp);
                        }
                        break;

                    case "ublock":
                    case "ublockorigin":
                    case "uBlock":
                    case "uBlock Origin":
                        if (isWildcard)
                        {
                            // Preserve wildcard entries — valid uBlock filter syntax
                            formattedLines.Add(domainList);
                            wildcardPreserved++;
                        }
                        else
                        {
                            // Standard uBlock filter rule
                            formattedLines.Add($"||{domainList}^");
                        }
                        break;

                    case "adguard":
                    case "ad-guard":
                    case "AdGuard":
                        if (isWildcard)
                        {
                            // Preserve wildcard entries — valid AdGuard filter syntax
                            formattedLines.Add(domainList);
                            wildcardPreserved++;
                        }
                        else
                        {
                            // Standard AdGuard filter rule
                            formattedLines.Add($"||{domainList}^");
                        }
                        break;

                    case "dnsmasq":
                        if (isWildcard)
                        {
                            wildcardRemoved++;
                            continue; // dnsmasq address= rules require a concrete domain
                        }
                        formattedLines.Add($"address=/{domainList}/0.0.0.0");
                        break;

                    case "wildcard":
                        // This format explicitly prepends "*." to every domain
                        if (!domainList.StartsWith("*."))
                        {
                            formattedLines.Add($"*.{domainList}");
                        }
                        else
                        {
                            formattedLines.Add(domainList);
                        }
                        break;
                    case "raw": //Ignores all formatting rules and preserves the original line as-is (after trimming comments and whitespace)
                        formattedLines.Add(trimmedLine);
                        break;
                    default:
                        formattedLines.Add(domainList);
                        break;
                }
            }

            try
            {
                TraceLogger.Log($"Formatting Complete. Saving {formattedLines.Count:N0} lines to {combinedFileLocation}");
                File.WriteAllLines(combinedFileLocation, formattedLines);

                if (wildcardPreserved > 0)
                    TraceLogger.Log($"Preserved {wildcardPreserved:N0} wildcard (*.) entries for {formatType} format.");
                if (wildcardRemoved > 0)
                    TraceLogger.Log($"Removed/stripped {wildcardRemoved:N0} wildcard (*.) entries (not valid for {formatType} format).");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error writing formatted lines to {combinedFileLocation}: {ex}", Enums.StatusSeverityType.Error);
            }
        }
        public static void RemoveDuplicates(string MergedFileLoc)
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
                }

                File.WriteAllLines(MergedFileLoc, cleanedLines);

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
            //catch (FileNotFoundException ex1)
            //{
            //    TraceLogger.Log($"{ex1.Message}. You can IGNORE this error if the file not found is for a list that you haven't configured. (e.g. if you left whitelist.ini blank and the file not found is the HLDcombined-whitelist.txt, you can ignore.).", Enums.StatusSeverityType.Error);
            //}
            catch (Exception ex)
            {
                TraceLogger.Log($"Error removing duplicates from {MergedFileLoc}: {ex}", Enums.StatusSeverityType.Error);
            }
        }
    }
}