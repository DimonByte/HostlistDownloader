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

using HostlistDownloaderCLI.Modules;
using HostlistDownloaderCLI.Modules.Helpers;
using HostlistDownloaderCLI.Modules.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.WindowsSystem.Config
{
    internal partial class AppConfigHealthChecker
    {
        internal static void CheckForInvalidConfig()
        {
            ValidateConfig(fixMode: false, reportIssuesOnly: true);
        }

        /// <summary>
        /// Validates configuration and optionally fixes issues found.
        /// </summary>
        /// <param name="fixMode">If true, automatically fix invalid entries.</param>
        /// <param name="reportIssuesOnly">If true, report findings but don't exit on errors (for validation-only mode).</param>
        internal static void ValidateConfig(bool fixMode, bool reportIssuesOnly = false)
        {
            try
            {
                // Always validate execution directory first
                ValidateExecutionDirectory();

                var issuesFound = new List<string>();
                bool corruptionDetected = false;
                
                // Validate blocklists
                var validBlocklists = ValidateAndFilterUrls(AppConfig.Instance.Blocklists, "blocklist", out bool blocklistCorruption);
                corruptionDetected |= blocklistCorruption;
                if (validBlocklists.Count != AppConfig.Instance.Blocklists!.Count || blocklistCorruption)
                {
                    issuesFound.Add($"Invalid blocklist entries found: {(AppConfig.Instance.Blocklists?.Count ?? 0) - validBlocklists.Count} invalid entry/entries removed.");
                }

                // Validate whitelists
                var validWhitelists = ValidateAndFilterUrls(AppConfig.Instance.Whitelist, "whitelist", out bool whitelistCorruption);
                corruptionDetected |= whitelistCorruption;
                if (validWhitelists.Count != AppConfig.Instance.Whitelist!.Count || whitelistCorruption)
                {
                    issuesFound.Add($"Invalid whitelist entries found: {(AppConfig.Instance.Whitelist?.Count ?? 0) - validWhitelists.Count} invalid entry/entries removed.");
                }

                // Validate user blocklist domains
                var validUserBlockDomains = ValidateAndFilterDomains(AppConfig.Instance.UserWebsiteBlocklist, "user blocklist domain", out bool userBlockCorruption);
                corruptionDetected |= userBlockCorruption;
                if (validUserBlockDomains.Count != AppConfig.Instance.UserWebsiteBlocklist!.Count || userBlockCorruption)
                {
                    issuesFound.Add($"Invalid user blocklist domains found: {(AppConfig.Instance.UserWebsiteBlocklist?.Count ?? 0) - validUserBlockDomains.Count} invalid entry/entries removed.");
                }

                // Validate user whitelist domains
                var validUserWhiteDomains = ValidateAndFilterDomains(AppConfig.Instance.UserWebsiteWhitelist, "user whitelist domain", out bool userWhiteCorruption);
                corruptionDetected |= userWhiteCorruption;
                if (validUserWhiteDomains.Count != AppConfig.Instance.UserWebsiteWhitelist!.Count || userWhiteCorruption)
                {
                    issuesFound.Add($"Invalid user whitelist domains found: {(AppConfig.Instance.UserWebsiteWhitelist?.Count ?? 0) - validUserWhiteDomains.Count} invalid entry/entries removed.");
                }

                // Report findings
                if (issuesFound.Count == 0)
                {
                    TraceLogger.Log("Configuration validation passed. No issues found.", Enums.StatusSeverityType.Notice);
                }
                else
                {
                    bool willFix = fixMode || !reportIssuesOnly;
                    
                    if (willFix)
                    {
                        // Apply fixes by saving the cleaned configuration
                        ApplyCorruptedConfigChanges(
                            validBlocklists,
                            validWhitelists,
                            validUserBlockDomains,
                            validUserWhiteDomains
                        );
                        TraceLogger.Log("Issues have been automatically fixed. Please review the logs for details.", Enums.StatusSeverityType.Notice);
                    }
                    else
                    {
                        // Only report, don't fix or exit
                        foreach (var issue in issuesFound)
                        {
                            TraceLogger.Log(issue, Enums.StatusSeverityType.Warn);
                        }
                        TraceLogger.Log("Use /validate --fix to automatically fix these issues.", Enums.StatusSeverityType.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Critical failure during configuration check: {ex}", Enums.StatusSeverityType.Fatal, ExceptionHandler.ConfigurationCorrupted);
                if (!reportIssuesOnly) Environment.Exit(ExceptionHandler.ConfigurationCorrupted);
            }
        }

        /// <summary>
        /// Validates that the application is running from its installation directory.
        /// </summary>
        private static void ValidateExecutionDirectory()
        {
            string appDir = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string currentDir = Path.GetFullPath(Environment.CurrentDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(appDir, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                TraceLogger.Log(
                    $"HostlistDownloader must be run from the directory where it is stored.\n" +
                    $"Application Path: {appDir} - Current Path: {currentDir}. " +
                    $"To fix this, CD to '{appDir}' in your terminal.",
                    Enums.StatusSeverityType.Fatal,
                    ExceptionHandler.WrongExecutionDirectory
                );
            }
        }

        /// <summary>
        /// Validates a list of URLs/Domain strings against URI standards or Domain Regex.
        /// </summary>
        private static List<string> ValidateAndFilterUrls(IEnumerable<string> rawUrls, string contextName, out bool corruptionDetected)
        {
            corruptionDetected = false;
            var validUrls = new List<string>();
            var domainRegex = URLValidationRegex();

            foreach (var url in rawUrls.Select(u => u.Trim()))
            {
                if (string.IsNullOrEmpty(url)) continue;

                bool isValid = Uri.TryCreate(url, UriKind.Absolute, out _);

                if (!isValid && url.Contains('*'))
                {
                    isValid = IsValidWildcardPattern(url, domainRegex);
                }

                if (!isValid && domainRegex.IsMatch(url))
                {
                    isValid = true;
                }

                if (isValid)
                {
                    validUrls.Add(url);
                }
                else
                {
                    corruptionDetected = true;
                    TraceLogger.Log($"Removed invalid {contextName}: {url}", Enums.StatusSeverityType.Warn);
                }
            }

            return validUrls;
        }

        /// <summary>
        /// Validates wildcard domain patterns (e.g., *.example.com or example.*) 
        /// by stripping the wildcard and validating the remaining base structure.
        /// </summary>
        private static bool IsValidWildcardPattern(string input, Regex domainRegex)
        {
            if (input.Count(c => c == '*') != 1) return false;
            bool startsWithStar = input.StartsWith('*');
            bool endsWithStar = input.EndsWith('*');
            if (!startsWithStar && !endsWithStar) return false;

            string basePart = input.TrimStart('*', '.').TrimEnd('*', '.');
            if (string.IsNullOrEmpty(basePart)) return false;

            // Validate the remaining part as a standard domain/URL
            return domainRegex.IsMatch(basePart);
        }

        /// <summary>
        /// Validates a list of domains against strict RFC DNS standards.
        /// </summary>
        private static List<string> ValidateAndFilterDomains(IEnumerable<string> rawDomains, string contextName, out bool corruptionDetected)
        {
            corruptionDetected = false;
            var validDomains = new List<string>();
            var domainRegex = DomainRegex();

            foreach (var domain in rawDomains.Select(d => d.Trim()))
            {
                if (string.IsNullOrEmpty(domain)) continue;

                if (domainRegex.IsMatch(domain))
                {
                    validDomains.Add(domain);
                }
                else
                {
                    corruptionDetected = true;
                    TraceLogger.Log($"Removed invalid {contextName}: {domain}", Enums.StatusSeverityType.Warn);
                }
            }

            return validDomains;
        }

        /// <summary>
        /// Rebuilds the settings.json file with only the validated entries.
        /// </summary>
        private static void ApplyCorruptedConfigChanges(
            List<string> validBlocklists,
            List<string> validWhitelists,
            List<string> validUserBlockDomains,
            List<string> validUserWhiteDomains)
        {
            var newConfig = new Settings
            {
                Blocklists = [.. validBlocklists],
                Whitelist = [.. validWhitelists],
                Formattype = AppConfig.Instance.Formattype,
                UserWebsiteBlocklist = [.. validUserBlockDomains],
                UserWebsiteWhitelist = [.. validUserWhiteDomains],
                MaxDownloadThreads = AppConfig.Instance.MaxDownloadThreads,
                LogExpiryInDays = AppConfig.Instance.LogExpiryInDays,
                MaxListSizeInMB = AppConfig.Instance.MaxListSizeInMB,
                AllowInsecureSources = AppConfig.Instance.AllowInsecureSources,
                AllowRevert = AppConfig.Instance.AllowRevert
            };

            JsonSerializerOptions options = new()
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = SettingsJsonSerializerContext.Default
            };

            string json = JsonSerializer.Serialize(newConfig, options);
            File.WriteAllText(Paths.SettingJsonFileLocation, json);

            TraceLogger.Log("Configuration file has been updated with only valid entries.", Enums.StatusSeverityType.Info);
        }

        [GeneratedRegex(@"^(?:(?:xn--)?[a-z0-9]+(?:-+[a-z0-9]+)*\.)+[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
        private static partial Regex DomainRegex();
        [GeneratedRegex(@"^(https?:\/\/|ftp:\/\/)?[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?)*(?:\/[\w\-.*~=+@!$&'()*+,;:%]*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
        private static partial Regex URLValidationRegex();
    }
}