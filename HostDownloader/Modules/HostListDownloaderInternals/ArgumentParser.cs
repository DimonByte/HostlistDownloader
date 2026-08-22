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

namespace HostlistDownloader.Modules.HostListDownloaderInternals
{
    /// <summary>
    /// Parses command-line arguments and returns structured results.
    /// </summary>
    public static class ArgumentParser
    {
        private const string HelpText = @"HostlistDownloader Help:
--HostlistDownloader arguments--
/quiet or /q: Suppresses console output.
/fresh or /fr: Clears block and white list folders before updating. Useful for troubleshooting.
/search <domain> or /s <domain>: Searches for a specific domain in the hostlists.
/purge or /p: Deletes all log files.
/help or /h or /?: Displays this help message.
/debug: Enables debug mode for detailed logging.
/duplicatescan or /dupscan: Checks each hostlist for duplicate entries, and outputs a percentage of duplicates found. Does not modify any files.
/dupanalyse or /analysedup: Analyses duplicate entries in the hostlists.
/getsource <source_name> or /gs <source_name>: Retrieves the source name for a given hostlist file name.
--Hostlist rules-- (For blocklist/whitelist management)
/addblocklist <url> or /ab <url>: Add a blocklist source.
/removeblocklist <url> or /rb <url>: Remove a blocklist source.
/addwhitelist <url> or /aw <url>: Add a whitelist source.
/removewhitelist <url> or /rw <url>: Remove a whitelist source.
--User-defined rules-- (For single-domain management)
/adduserblock <domain> or /aub <domain>: Add a user-defined website block.
/removeuserblock <domain> or /rub <domain>: Remove a user-defined website block.
/adduserwhitelist <domain> or /auw <domain>: Add a user-defined website allow rule.
/removeuserwhitelist <domain> or /ruw <domain>: Remove a user-defined website allow rule.";

        /// <summary>
        /// Parses the command-line arguments.
        /// </summary>
        /// <param name="args">The raw command-line arguments.</param>
        /// <returns>An ArgumentResult containing parsed flags and values.</returns>
        public static ArgumentResult Parse(string[] args)
        {
            bool isQuiet = false;
            bool isFresh = false;
            string? searchDomain = null;
            bool shouldPurgeLogs = false;
            bool showHelp = false;
            string? addBlocklistUrl = null;
            string? removeBlocklistUrl = null;
            string? addWhitelistUrl = null;
            string? removeWhitelistUrl = null;
            string? addUserBlockDomain = null;
            string? removeUserBlockDomain = null;
            string? addUserAllowDomain = null;
            string? removeUserAllowDomain = null;
            bool debugMode = false;
            bool checkDuplicates = false;
            string? getSourceName = null;
            string? analyseDuplicateSource = null;

            List<string> remainingArgs = [];

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                // Helper to check if next arg is valid value (not a flag)
                bool HasNextArg() => i + 1 < args.Length && !args[i + 1].StartsWith('/');
                string GetNextArg() => HasNextArg() ? args[i + 1] : throw new ArgumentException($"Missing argument for {arg}");

                if (arg.Equals("/analysedup", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/dupanalyse", StringComparison.OrdinalIgnoreCase))
                {
                    analyseDuplicateSource = GetNextArg();
                    i++;
                }

                if (arg.Equals("/getsource", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/gs", StringComparison.OrdinalIgnoreCase))
                {
                    getSourceName = GetNextArg();
                    i++;
                }

                if (arg.Equals("/duplicate", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/dup", StringComparison.OrdinalIgnoreCase))
                {
                    checkDuplicates = true;
                }

                if (arg.Equals("/debug", StringComparison.OrdinalIgnoreCase))
                {
                    debugMode = true;
                }

                // Handle /quiet or /q
                if (arg.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/q", StringComparison.OrdinalIgnoreCase))
                {
                    isQuiet = true;
                }
                // Handle /fresh or /fr
                else if (arg.Equals("/fresh", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/fr", StringComparison.OrdinalIgnoreCase))
                {
                    isFresh = true;
                }
                // Handle /search or /s
                else if (arg.Equals("/search", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/s", StringComparison.OrdinalIgnoreCase))
                {
                    searchDomain = GetNextArg();
                    i++;
                }
                // Handle /purge or /p
                else if (arg.Equals("/purge", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/p", StringComparison.OrdinalIgnoreCase))
                {
                    shouldPurgeLogs = true;
                }
                // Handle /help variants
                else if (arg.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/h", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
                {
                    showHelp = true;
                }

                // Add Blocklist
                else if (arg.Equals("/addblocklist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/ab", StringComparison.OrdinalIgnoreCase))
                {
                    addBlocklistUrl = GetNextArg();
                    i++;
                }
                // Remove Blocklist
                else if (arg.Equals("/removeblocklist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rb", StringComparison.OrdinalIgnoreCase))
                {
                    removeBlocklistUrl = GetNextArg();
                    i++;
                }
                // Add Whitelist
                else if (arg.Equals("/addwhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/aw", StringComparison.OrdinalIgnoreCase))
                {
                    addWhitelistUrl = GetNextArg();
                    i++;
                }
                // Remove Whitelist
                else if (arg.Equals("/removewhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rw", StringComparison.OrdinalIgnoreCase))
                {
                    removeWhitelistUrl = GetNextArg();
                    i++;
                }
                // Add User Block
                else if (arg.Equals("/adduserblock", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/aub", StringComparison.OrdinalIgnoreCase))
                {
                    addUserBlockDomain = GetNextArg();
                    i++;
                }
                // Remove User Block
                else if (arg.Equals("/removeuserblock", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rub", StringComparison.OrdinalIgnoreCase))
                {
                    removeUserBlockDomain = GetNextArg();
                    i++;
                }
                // Add User Whitelist
                else if (arg.Equals("/adduserwhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/auw", StringComparison.OrdinalIgnoreCase))
                {
                    addUserAllowDomain = GetNextArg();
                    i++;
                }
                // Remove User Whitelist
                else if (arg.Equals("/removeuserwhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/ruw", StringComparison.OrdinalIgnoreCase))
                {
                    removeUserAllowDomain = GetNextArg();
                    i++;
                }

                else
                {
                    remainingArgs.Add(arg);
                }
            }

            return new ArgumentResult(
                IsQuiet: isQuiet,
                IsFresh: isFresh,
                SearchDomain: searchDomain,
                ShouldPurgeLogs: shouldPurgeLogs,
                RemainingArgs: remainingArgs,
                ShowHelp: showHelp,
                // Pass the new values to the result object
                AddBlocklistUrl: addBlocklistUrl,
                RemoveBlocklistUrl: removeBlocklistUrl,
                AddWhitelistUrl: addWhitelistUrl,
                RemoveWhitelistUrl: removeWhitelistUrl,
                AddUserBlockDomain: addUserBlockDomain,
                RemoveUserBlockDomain: removeUserBlockDomain,
                AddUserAllowDomain: addUserAllowDomain,
                RemoveUserAllowDomain: removeUserAllowDomain,
                DebugMode: debugMode,
                CheckDuplicate: checkDuplicates,
                GetSourceName: getSourceName,
                AnalyseDuplicateSource: analyseDuplicateSource
            );
        }
        public static void PrintHelp()
        {
            Console.WriteLine(HelpText);
        }

        /// <summary>
        /// Handles the side effects of parsing (e.g., setting quiet mode, purging logs, updating config).
        /// </summary>
        /// <param name="result">The parsed result.</param>
        public static void ApplySideEffects(ArgumentResult result)
        {
            if (result.IsQuiet)
            {
                TraceLogger.QuietMode = true;
                TraceLogger.Log("/quiet enabled. Console output will be suppressed.", Enums.StatusSeverityType.Debug);
            }

            if (result.ShouldPurgeLogs)
            {
                TraceLogger.Log("/purge enabled. Deleting all logs...", Enums.StatusSeverityType.Debug);
                TraceLogger.PurgeAllLogs();
            }

            if (result.ShowHelp)
            {
                PrintHelp();
                Environment.Exit(0);
            }

            if (result.CheckDuplicate)
            {
                TraceLogger.Log("/dup command detected. Running duplicate analysis...", Enums.StatusSeverityType.Information);
                try
                {
                    HostListManager.RunDuplicateCheck();
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to run duplicate check: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }

            if (result.AnalyseDuplicateSource != null)
            {
                TraceLogger.Log("/analysedup command detected. Analysing duplicates for source: " + result.AnalyseDuplicateSource, Enums.StatusSeverityType.Information);
                try
                {
                    HostListManager.AnalyseDuplicate(result.AnalyseDuplicateSource);
                    TraceLogger.Log($"Analysed duplicates for source: {result.AnalyseDuplicateSource}", Enums.StatusSeverityType.Information);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to analyse duplicates: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }

            if (result.GetSourceName != null)
            {
                TraceLogger.Log($"/getsource command detected. Retrieving source: {result.GetSourceName}", Enums.StatusSeverityType.Information);
                try
                {
                    //Check if it exists in blocklist or whitelist folders
                    if (!File.Exists(Path.Combine(IOManager.BlockListFolderLocation, result.GetSourceName)) &&
                        !File.Exists(Path.Combine(IOManager.WhiteListFolderLocation, result.GetSourceName)))
                    {
                        TraceLogger.Log($"Source file '{result.GetSourceName}' not found in blocklist or whitelist folders.", Enums.StatusSeverityType.Warning);
                        Environment.Exit(1);
                    }
                    else if (File.Exists(Path.Combine(IOManager.BlockListFolderLocation, result.GetSourceName)))
                    {
                        TraceLogger.Log($"Source file '{result.GetSourceName}' found in blocklist folder.", Enums.StatusSeverityType.Information);
                        string sourcePath = Path.Combine(IOManager.BlockListFolderLocation, result.GetSourceName);
                        string sourceName = HostListManager.GetSourceNameForFile(result.GetSourceName, true);
                        TraceLogger.Log($"Retrieved source: {sourceName}", Enums.StatusSeverityType.Information);
                    }
                    else if (File.Exists(Path.Combine(IOManager.WhiteListFolderLocation, result.GetSourceName)))
                    {
                        TraceLogger.Log($"Source file '{result.GetSourceName}' found in whitelist folder.", Enums.StatusSeverityType.Information);
                        string sourcePath = Path.Combine(IOManager.WhiteListFolderLocation, result.GetSourceName);
                        string sourceName = HostListManager.GetSourceNameForFile(result.GetSourceName, false);
                        TraceLogger.Log($"Retrieved source: {sourceName}", Enums.StatusSeverityType.Information);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to retrieve source: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }

            bool hasConfigCommands = !string.IsNullOrEmpty(result.AddBlocklistUrl) ||
                         !string.IsNullOrEmpty(result.RemoveBlocklistUrl) ||
                         !string.IsNullOrEmpty(result.AddWhitelistUrl) ||
                         !string.IsNullOrEmpty(result.RemoveWhitelistUrl) ||
                         !string.IsNullOrEmpty(result.AddUserBlockDomain) ||
                         !string.IsNullOrEmpty(result.RemoveUserBlockDomain) ||
                         !string.IsNullOrEmpty(result.AddUserAllowDomain) ||
                         !string.IsNullOrEmpty(result.RemoveUserAllowDomain);

            if (hasConfigCommands)
            {
                TraceLogger.Log("Configuration update commands detected. Processing...", Enums.StatusSeverityType.Debug);
                HandleConfigUpdates(result);
                Environment.Exit(0);
            }
        }

        private static void HandleConfigUpdates(ArgumentResult result)
        {
            var config = ConfigManager.Instance;
            var settingsPath = IOManager.SettingJsonFileLocation;

            if (string.IsNullOrEmpty(settingsPath))
            {
                TraceLogger.Log("Settings file path is not specified. Unable to save configuration.", Enums.StatusSeverityType.Fatal, ErrorCodes.MissingOrInvalidParameters);
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(result.AddBlocklistUrl))
                {
                    config = config.AddEntry("blocklists", result.AddBlocklistUrl);
                    TraceLogger.Log($"Added blocklist: {result.AddBlocklistUrl}");
                }

                if (!string.IsNullOrEmpty(result.RemoveBlocklistUrl))
                {
                    config = config.RemoveEntry("blocklists", result.RemoveBlocklistUrl);
                    TraceLogger.Log($"Removed blocklist: {result.RemoveBlocklistUrl}");
                }

                if (!string.IsNullOrEmpty(result.AddWhitelistUrl))
                {
                    config = config.AddEntry("whitelist", result.AddWhitelistUrl);
                    TraceLogger.Log($"Added whitelist: {result.AddWhitelistUrl}");
                }

                if (!string.IsNullOrEmpty(result.RemoveWhitelistUrl))
                {
                    config = config.RemoveEntry("whitelist", result.RemoveWhitelistUrl);
                    TraceLogger.Log($"Removed whitelist: {result.RemoveWhitelistUrl}");
                }

                if (!string.IsNullOrEmpty(result.AddUserBlockDomain))
                {
                    config = config.AddEntry("userWebsiteBlocklist", result.AddUserBlockDomain);
                    TraceLogger.Log($"Added user block: {result.AddUserBlockDomain}");
                }

                if (!string.IsNullOrEmpty(result.RemoveUserBlockDomain))
                {
                    config = config.RemoveEntry("userWebsiteBlocklist", result.RemoveUserBlockDomain);
                    TraceLogger.Log($"Removed user block: {result.RemoveUserBlockDomain}");
                }

                if (!string.IsNullOrEmpty(result.AddUserAllowDomain))
                {
                    config = config.AddEntry("userWebsiteWhitelist", result.AddUserAllowDomain);
                    TraceLogger.Log($"Added user allow: {result.AddUserAllowDomain}");
                }

                if (!string.IsNullOrEmpty(result.RemoveUserAllowDomain))
                {
                    config = config.RemoveEntry("userWebsiteWhitelist", result.RemoveUserAllowDomain);
                    TraceLogger.Log($"Removed user allow: {result.RemoveUserAllowDomain}");
                }

                bool wasModified = result.AddBlocklistUrl != null || result.RemoveBlocklistUrl != null ||
                                   result.AddWhitelistUrl != null || result.RemoveWhitelistUrl != null ||
                                   result.AddUserBlockDomain != null || result.RemoveUserBlockDomain != null ||
                                   result.AddUserAllowDomain != null || result.RemoveUserAllowDomain != null;

                if (wasModified)
                {
                    config.SaveToDisk(settingsPath);
                    TraceLogger.Log("Configuration saved to disk.");
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to update configuration: {ex.Message}", Enums.StatusSeverityType.Error, ErrorCodes.InvalidConfigEntry);
            }
        }
    }
}