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
using HostlistDownloader.Modules.HostlistManagement;
using HostlistDownloader.Modules.HostlistManagement.Generation;
using HostlistDownloader.Modules.Network;
using HostlistDownloader.Modules.WindowsSystem;

namespace HostlistDownloader.Modules.ArgumentParsing
{
    /// <summary>
    /// Parses command-line arguments and returns structured results.
    /// </summary>
    public static class ArgumentParser
    {
        private const string HelpText = @"HostlistDownloader Help

USAGE:
  HostlistDownloader [arguments]

GLOBAL ARGUMENTS:
  /quiet, /q                Suppress console output.
  /fresh, /fr               Clear block/white list folders before updating.
  /search <domain>, /s      Search for a specific domain in hostlists.
  /purge, /p                Delete all log files.
  /help, /h, /?             Display this help message.
  /debug                     Enable detailed debug logging.
  /duplicatescan, /dupscan  Scan hostlists for duplicates (reports percentage).
  /dupanalyse <source>, /analysedup <source>  Analyze duplicate entries.
  /getsource <source>, /gs <source>            Retrieve source name from filename.
  /merge, /regenerate, /re  Consolidate hostlists and user rules locally (offline).
  /update                    Check for application updates.
  /diff                      Show differences from the previous hostlist version.
  /revert                    Revert to the previous hostlist version.
  /stats                    Generate a statistics report of the last run.

HOSTLIST MANAGEMENT:
  /addblocklist <url>, /ab  Add a new blocklist source.
  /removeblocklist <url>, /rb Remove a blocklist source.
  /addwhitelist <url>, /aw   Add a new whitelist source.
  /removewhitelist <url>, /rw Remove a whitelist source.

USER-DEFINED RULES:
  /adduserblock <domain>, /aub  Add a custom domain block.
  /removeuserblock <*>, /rub    Remove a custom domain block.
  /adduserwhitelist <domain>, /auw  Add a custom domain allow rule.
  /removeuserwhitelist <domain>, /ruw  Remove a custom domain allow rule.";

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
            bool mergeMode = false;
            bool updateCheck = false;
            bool diffMode = false;
            bool revertLists = false;
            bool statsReport = false;

            List<string> remainingArgs = [];

            TraceLogger.Log("Starting argument parsing...", Enums.StatusSeverityType.Debug);

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                // Helper to check if next arg is valid value (not a flag)
                bool HasNextArg() => i + 1 < args.Length && !args[i + 1].StartsWith('/');
                string GetNextArg() => HasNextArg() ? args[i + 1] : throw new ArgumentException($"Missing argument for {arg}");

                TraceLogger.Log($"Processing argument: {arg}", Enums.StatusSeverityType.Debug);

                if (arg.Equals("/stats", StringComparison.OrdinalIgnoreCase))
                {
                    statsReport = true;
                }

                if (arg.Equals("/diff", StringComparison.OrdinalIgnoreCase))
                {
                    diffMode = true;
                }

                if (arg.Equals("/revert", StringComparison.OrdinalIgnoreCase))
                {
                    revertLists = true;
                }

                if (arg.Equals("/update", StringComparison.OrdinalIgnoreCase))
                {
                    updateCheck = true;
                }

                if (arg.Equals("/compile", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/regenerate", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/re", StringComparison.OrdinalIgnoreCase))
                {
                    mergeMode = true;
                }

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
                if (arg.Equals("/fresh", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/fr", StringComparison.OrdinalIgnoreCase))
                {
                    isFresh = true;
                }
                // Handle /search or /s
                if (arg.Equals("/search", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/s", StringComparison.OrdinalIgnoreCase))
                {
                    searchDomain = GetNextArg();
                    i++;
                }
                // Handle /purge or /p
                if (arg.Equals("/purge", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/p", StringComparison.OrdinalIgnoreCase))
                {
                    shouldPurgeLogs = true;
                }
                // Handle /help variants
                if (arg.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/h", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
                {
                    showHelp = true;
                }

                // Add Blocklist
                if (arg.Equals("/addblocklist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/ab", StringComparison.OrdinalIgnoreCase))
                {
                    addBlocklistUrl = GetNextArg();
                    i++;
                }
                // Remove Blocklist
                if (arg.Equals("/removeblocklist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rb", StringComparison.OrdinalIgnoreCase))
                {
                    removeBlocklistUrl = GetNextArg();
                    i++;
                }
                // Add Whitelist
                if (arg.Equals("/addwhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/aw", StringComparison.OrdinalIgnoreCase))
                {
                    addWhitelistUrl = GetNextArg();
                    i++;
                }
                // Remove Whitelist
                if (arg.Equals("/removewhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rw", StringComparison.OrdinalIgnoreCase))
                {
                    removeWhitelistUrl = GetNextArg();
                    i++;
                }
                // Add User Block
                if (arg.Equals("/adduserblock", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/aub", StringComparison.OrdinalIgnoreCase))
                {
                    addUserBlockDomain = GetNextArg();
                    i++;
                }
                // Remove User Block
                if (arg.Equals("/removeuserblock", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/rub", StringComparison.OrdinalIgnoreCase))
                {
                    removeUserBlockDomain = GetNextArg();
                    i++;
                }
                // Add User Whitelist
                if (arg.Equals("/adduserwhitelist", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/auw", StringComparison.OrdinalIgnoreCase))
                {
                    addUserAllowDomain = GetNextArg();
                    i++;
                }
                // Remove User Whitelist
                if (arg.Equals("/removeuserwhitelist", StringComparison.OrdinalIgnoreCase) ||
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
            TraceLogger.Log("Argument parsing completed.", Enums.StatusSeverityType.Debug);
            return new ArgumentResult(
                IsQuiet: isQuiet,
                IsFresh: isFresh,
                SearchDomain: searchDomain,
                ShouldPurgeLogs: shouldPurgeLogs,
                RemainingArgs: remainingArgs,
                ShowHelp: showHelp,
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
                AnalyseDuplicateSource: analyseDuplicateSource,
                MergeMode: mergeMode,
                UpdateCheck: updateCheck,
                DiffMode: diffMode,
                RevertLists: revertLists,
                StatsReport: statsReport
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
            TraceLogger.Log($"Applying side effects based on parsed arguments... Argument Results: {result}", Enums.StatusSeverityType.Debug);
            if (result.StatsReport)
            {
                TraceLogger.Log("/stats command detected. Generating stats report...", Enums.StatusSeverityType.Information);
                try
                {
                    IOManager.GenerateStatsReport();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to generate stats report: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }

            if (result.RevertLists)
            {
                TraceLogger.Log("/revert command detected. Attempting to revert to previous hostlist version...", Enums.StatusSeverityType.Information);
                try
                {
                    HostListManager.RevertToPreviousVersion();
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to revert hostlist: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }
            if (result.DiffMode)
            {
                TraceLogger.Log("/diff command detected. Running diff analysis...", Enums.StatusSeverityType.Information);
                try
                {
                    var diffResult = File.ReadAllLines(IOManager.UpdateStatsLocation);
                    if (diffResult.Length == 0)
                    {
                        TraceLogger.Log("No previous run diff analysis results found.", Enums.StatusSeverityType.Warning);
                    }
                    //Check if diffResult is a number only
                    else if (diffResult.Length >= 1 && int.TryParse(diffResult[0], out int diffCount))
                    {
                        TraceLogger.Log($"Last run diff analysis results: {diffCount} differences found.", Enums.StatusSeverityType.Important);
                    }
                    else
                    {
                        TraceLogger.Log("Warning: TryParse failed for diff analysis results or returned a non-numeric value. Printing raw results.", Enums.StatusSeverityType.Debug);
                        TraceLogger.Log("Last run diff analysis results: " + string.Join(Environment.NewLine, diffResult), Enums.StatusSeverityType.Important);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to run diff analysis: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }
            if (result.MergeMode)
            {
                HostListManager.StartOfflineListProcessing();
            }

            if (result.UpdateCheck)
            {
                TraceLogger.Log("/update command detected. Checking for updates...", Enums.StatusSeverityType.Information);
                try
                {
                    if (UpdateChecker.IsUpdateAvailable())
                    {
                        UpdateChecker.BeginUpdateReplacement().Wait();
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to check for updates: {ex.Message}", Enums.StatusSeverityType.Error);
                }
                Environment.Exit(0);
            }

            if (result.DebugMode)
            {
                TraceLogger.DebugMode = true;
                TraceLogger.Log("/debug enabled. Debug mode is active.", Enums.StatusSeverityType.Debug);
            }

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
                    IOManager.RunDuplicateCheck();
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
                    IOManager.AnalyseDuplicate(result.AnalyseDuplicateSource);
                    TraceLogger.Log($"Analysed duplicates for source: {result.AnalyseDuplicateSource}", Enums.StatusSeverityType.Important);
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
                        TraceLogger.Log($"Source file '{result.GetSourceName}' found in blocklist folder.", Enums.StatusSeverityType.Important);
                        string sourcePath = Path.Combine(IOManager.BlockListFolderLocation, result.GetSourceName);
                        string sourceName = SourceManager.GetSourceNameForFile(result.GetSourceName, true);
                        TraceLogger.Log($"Retrieved source: {sourceName}", Enums.StatusSeverityType.Important);
                    }
                    else if (File.Exists(Path.Combine(IOManager.WhiteListFolderLocation, result.GetSourceName)))
                    {
                        TraceLogger.Log($"Source file '{result.GetSourceName}' found in whitelist folder.", Enums.StatusSeverityType.Important);
                        string sourcePath = Path.Combine(IOManager.WhiteListFolderLocation, result.GetSourceName);
                        string sourceName = SourceManager.GetSourceNameForFile(result.GetSourceName, false);
                        TraceLogger.Log($"Retrieved source: {sourceName}", Enums.StatusSeverityType.Important);
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
            TraceLogger.Log("Updating configuration based on command-line arguments...", Enums.StatusSeverityType.Debug);
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
                TraceLogger.Log("Configuration update completed successfully.", Enums.StatusSeverityType.Important);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to update configuration: {ex.Message}", Enums.StatusSeverityType.Error, ErrorCodes.InvalidConfigEntry);
            }
        }
    }
}