using HostlistDownloader.Modules.Helpers;
using HostlistDownloader.Modules.WindowsSystem.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.WindowsSystem.Config
{
    internal class AppConfigHealthChecker
    {
        internal static void CheckForInvalidConfig()
        {
            try
            {
                ValidateExecutionDirectory();
                bool corruptionDetected = false;
                var validBlocklists = ValidateAndFilterUrls(AppConfig.Instance.Blocklists, "blocklist", out bool blocklistCorruption);
                corruptionDetected |= blocklistCorruption;
                var validWhitelists = ValidateAndFilterUrls(AppConfig.Instance.Whitelist, "whitelist", out bool whitelistCorruption);
                corruptionDetected |= whitelistCorruption;
                var validUserBlockDomains = ValidateAndFilterDomains(AppConfig.Instance.UserWebsiteBlocklist, "user blocklist domain", out bool userBlockCorruption);
                corruptionDetected |= userBlockCorruption;
                var validUserWhiteDomains = ValidateAndFilterDomains(AppConfig.Instance.UserWebsiteWhitelist, "user whitelist domain", out bool userWhiteCorruption);
                corruptionDetected |= userWhiteCorruption;

                if (corruptionDetected)
                {
                    ApplyCorruptedConfigChanges(
                        validBlocklists,
                        validWhitelists,
                        validUserBlockDomains,
                        validUserWhiteDomains
                    );
                }

                TraceLogger.Log(
                    corruptionDetected
                        ? "Configuration corruption detected and cleaned. Please review logs."
                        : "Configuration validation passed. No issues found.",
                    corruptionDetected ? Enums.StatusSeverityType.Warning : Enums.StatusSeverityType.Debug
                );
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Critical failure during configuration check: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationCorrupted);
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
                    ErrorCodes.WrongExecutionDirectory
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
            var urlOrDomainRegex = new Regex(
                @"^(https?:\/\/|ftp:\/\/)?[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?)+(?:\/[\w\-.*~=+@!$&'()*+,;:%]*)*$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            );

            foreach (var url in rawUrls.Select(u => u.Trim()))
            {
                if (string.IsNullOrEmpty(url)) continue;

                bool isValid = Uri.TryCreate(url, UriKind.Absolute, out _) || urlOrDomainRegex.IsMatch(url);

                if (isValid)
                {
                    validUrls.Add(url);
                }
                else
                {
                    corruptionDetected = true;
                    TraceLogger.Log($"Removed invalid {contextName}: {url}", Enums.StatusSeverityType.Warning);
                }
            }

            return validUrls;
        }

        /// <summary>
        /// Validates a list of domains against strict RFC DNS standards.
        /// </summary>
        private static List<string> ValidateAndFilterDomains(IEnumerable<string> rawDomains, string contextName, out bool corruptionDetected)
        {
            corruptionDetected = false;
            var validDomains = new List<string>();
            var domainRegex = new Regex(
                @"^(?:(?:xn--)?[a-z0-9]+(?:-+[a-z0-9]+)*\.)+[a-z]{2,}$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase
            );

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
                    TraceLogger.Log($"Removed invalid {contextName}: {domain}", Enums.StatusSeverityType.Warning);
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

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = SettingsJsonSerializerContext.Default
            };

            string json = JsonSerializer.Serialize(newConfig, options);
            File.WriteAllText(Paths.SettingJsonFileLocation, json);

            TraceLogger.Log("Configuration file has been updated with only valid entries.", Enums.StatusSeverityType.Information);
        }
    }
}