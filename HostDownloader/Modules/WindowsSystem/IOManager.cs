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
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.WindowsSystem
{
    internal class IOManager
    {
        public static readonly string HostfilesLocation = "hostfiles";
        public static readonly string BlockListFolderLocation = "hostfiles/blocklist";
        public static readonly string WhiteListFolderLocation = "hostfiles/whitelist";
        public static readonly string CombinedListFolderLocation = "hostfiles/combined";
        //public static readonly string IniBlockListFileLocation = "hostfiles/blocklist.ini";
        //public static readonly string IniWhiteListFileLocation = "hostfiles/whitelist.ini";
        //public static readonly string IniUserWebsiteBlockListFileLocation = "hostfiles/userwebsiteblocklist.ini";
        //public static readonly string IniUserWebsiteWhiteListFileLocation = "hostfiles/userwebsitewhitelist.ini";
        public static readonly string CombinedBlockListFileLocation = "hostfiles/combined/HLDcombined-blocklist.txt";
        public static readonly string CombinedWhiteListFileLocation = "hostfiles/combined/HLDcombined-whitelist.txt";
        public static readonly string CombinedListFileLocation = "hostfiles/combined/HLDcombined-list.txt";
        //public static readonly string IniFormatTypeLocation = "hostfiles/formattype.ini";
        public static readonly string LogsLocation = "logs";
        public static readonly string SettingJsonFileLocation = "settings.json";

        public static void CreateNecessaryDirectoriesAndFiles()
        {
            string[] directories = [LogsLocation, HostfilesLocation, BlockListFolderLocation, WhiteListFolderLocation, CombinedListFolderLocation];

            bool ShowHelp = false;
            foreach (string dir in directories)
            {
                if (!Directory.Exists(dir))
                {
                    ShowHelp = true;
                    try
                    {
                        Directory.CreateDirectory(dir);
                        TraceLogger.Log($"Created directory: {dir} - First time setup will be started.");
                    }
                    catch (Exception ex)
                    {
                        TraceLogger.Log($"Error creating directory {dir}: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.DirectoryCreationFailed);
                    }
                }
            }
            string[] files = [CombinedListFileLocation, CombinedBlockListFileLocation, CombinedWhiteListFileLocation];
            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    ShowHelp = true;
                    try
                    {
                        var directory = Path.GetDirectoryName(file);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        File.Create(file).Dispose();
                        TraceLogger.Log($"Created file: {file}");
                    }
                    catch (Exception ex)
                    {
                        TraceLogger.Log($"Error creating file {file}: {ex}", Enums.StatusSeverityType.Error);
                    }
                }
            }
            if (!File.Exists(IOManager.SettingJsonFileLocation))
            {
                ShowHelp = true;
                ConfigReader.CreateDefaultConfig(IOManager.SettingJsonFileLocation);
            }
            if (ShowHelp)
            {
                Console.WriteLine("[!] Configuration files and folders have been created in the directory where this program is stored. (settings.json)\nPlease refer to the documentation on the main GitHub page of HostlistDownloader to configure. Once configured, run HostlistDownloader again. HostlistDownloader will now exit.");
                Environment.Exit(ErrorCodes.GeneralError);
            }
        }

        public static void CheckForInvalidConfig()
        {
            try
            {
                string appDir = Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string currentDir = Path.GetFullPath(Environment.CurrentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(appDir, currentDir, StringComparison.OrdinalIgnoreCase))
                {
                    TraceLogger.Log($"HostlistDownloader must be run from the directory where it is stored.\nApplication Path: {appDir} - Path that was passed: {currentDir}. To fix this, you must CD to the path in your terminal where HostlistDownloader is stored '{appDir}' and try again.", Enums.StatusSeverityType.Fatal, ErrorCodes.WrongExecutionDirectory);
                }

                bool corruptionDetected = false;
                // Validates full URIs (http/https/ftp) OR bare domains with optional paths
                var urlOrDomainRegex = new Regex(@"^(https?:\/\/|ftp:\/\/)?[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9])?)+(?:\/[\w\-.*~=+@!$&'()*+,;:%]*)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                // Validates strictly RFC-standard DNS hostnames/tlds only (e.g., google.com, sub.domain.co.uk)
                var domainRegex = new Regex(@"^(?:(?:xn--)?[a-z0-9]+(?:-+[a-z0-9]+)*\.)+[a-z]{2,}$", RegexOptions.Compiled);

                // Stage 1: Validate public blocklist & whitelist sources
                var validBlocklistUrls = new List<string>();
                foreach (var url in ConfigReader.Instance.Blocklists.Select(u => u.Trim()))
                {
                    if (!string.IsNullOrEmpty(url) && (Uri.TryCreate(url, UriKind.Absolute, out _) || urlOrDomainRegex.IsMatch(url)))
                    {
                        validBlocklistUrls.Add(url);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid blocklist URL/Domain: {url.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                var validWhitelistUrls = new List<string>();
                foreach (var url in ConfigReader.Instance.Whitelist.Select(u => u.Trim()))
                {
                    if (!string.IsNullOrEmpty(url) && (Uri.TryCreate(url, UriKind.Absolute, out _) || urlOrDomainRegex.IsMatch(url)))
                    {
                        validWhitelistUrls.Add(url);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid whitelist URL/Domain: {url.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                // Stage 2: Validate user-defined website domains
                var validUserBlocklistDomains = new List<string>();
                foreach (var domain in ConfigReader.Instance.UserWebsiteBlocklist.Select(d => d.Trim()))
                {
                    if (!string.IsNullOrEmpty(domain) && domainRegex.IsMatch(domain))
                    {
                        validUserBlocklistDomains.Add(domain);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid user blocklist domain: {domain.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                var validUserWhitelistDomains = new List<string>();
                foreach (var domain in ConfigReader.Instance.UserWebsiteWhitelist.Select(d => d.Trim()))
                {
                    if (!string.IsNullOrEmpty(domain) && domainRegex.IsMatch(domain))
                    {
                        validUserWhitelistDomains.Add(domain);
                    }
                    else
                    {
                        corruptionDetected = true;
                        TraceLogger.Log($"Corruption detected: Removed invalid user whitelist domain: {domain.Trim()}", Enums.StatusSeverityType.Warning);
                    }
                }

                // Rebuild config file with valid entries if corruption was found
                if (corruptionDetected)
                {
                    var newConfig = new Settings
                    {
                        Blocklists = [.. validBlocklistUrls],
                        Whitelist = [.. validWhitelistUrls],
                        Formattype = ConfigReader.Instance.Formattype,
                        UserWebsiteBlocklist = [.. validUserBlocklistDomains],
                        UserWebsiteWhitelist = [.. validUserWhitelistDomains],
                        MaxDownloadThreads = ConfigReader.Instance.MaxDownloadThreads,
                        LogExpiryInDays = ConfigReader.Instance.LogExpiryInDays
                    };

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    string json = JsonSerializer.Serialize(newConfig, SettingsJsonSerializerContext.Default.Settings);
                    File.WriteAllText(SettingJsonFileLocation, json);
                    TraceLogger.Log("Configuration file has been updated with only valid entries.", Enums.StatusSeverityType.Information);
                }

                if (corruptionDetected)
                {
                    TraceLogger.Log("Corruption was detected during startup and was removed from the affected configuration entries. Please review the logs for details.", Enums.StatusSeverityType.Warning);
                }
                else
                {
                    TraceLogger.Log("Configuration corruption check completed. No issues found.");
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Corruption Check failure! {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationCorrupted);
            }
        }

        //public static void AddToIniFile(string iniFilePath, string domain)
        //{
        //    var directory = Path.GetDirectoryName(iniFilePath);
        //    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        //        Directory.CreateDirectory(directory);
        //    File.AppendAllText(iniFilePath, $"{domain}{Environment.NewLine}");
        //}

        public static void MergeFiles(string sourceFolder, string outputFile)
        {
            var files = Directory.GetFiles(sourceFolder, "*.*")
                .Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFullPath(f).Contains("HLDcombined-", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals("_sources.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (files.Length == 0)
            {
                TraceLogger.Log($"No files found to merge in {sourceFolder}.", Enums.StatusSeverityType.Warning);
                return;
            }

            try
            {
                using var writer = new StreamWriter(outputFile);
                Stopwatch watch = Stopwatch.StartNew();
                ConsoleProgress.ShowOperationProgress(0, files.Length, "Merging files");

                int processedFiles = 0;
                foreach (var file in files)
                {
                    TraceLogger.Log($"Merging file: {file}");
                    using var reader = new StreamReader(file);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                        {
                            writer.WriteLine(line);
                        }
                    }
                    processedFiles++;
                    ConsoleProgress.ShowOperationProgress(processedFiles, files.Length, "Merging files");
                }
                writer.Flush();
                watch.Stop();
                TraceLogger.Log($"Merge files completed in {watch.Elapsed.TotalSeconds} seconds.");
            }
            catch (UnauthorizedAccessException ex1)
            {
                TraceLogger.Log($"Access denied when trying to merge files into {outputFile}: {ex1.Message}", Enums.StatusSeverityType.Error);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error merging files into {outputFile}: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        /// <summary>
        /// Deletes the specified files (and their .etag companions) from the given folder.
        /// </summary>
        public static void DeleteFileAlongWithETag(string listFolderLocation, List<string> fileNames)
        {
            foreach (var fileName in fileNames)
            {
                var filePath = Path.Combine(listFolderLocation, fileName);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        TraceLogger.Log($"Deleted removed source file: {fileName}");
                    }

                    // Also remove the .etag file if present
                    var etagPath = filePath + ".etag";
                    if (File.Exists(etagPath))
                    {
                        File.Delete(etagPath);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Failed to delete {fileName}: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }
        }

        //public static void ClearFiles(string folder)
        //{
        //    var files = Directory.GetFiles(folder, "*.*");
        //    foreach (var file in files)
        //    {
        //        try
        //        {
        //            TraceLogger.Log($"{file} deleted.");
        //            File.Delete(file);
        //        }
        //        catch (Exception ex)
        //        {
        //            TraceLogger.Log($"Error deleting file {file}: {ex}", Enums.StatusSeverityType.Error);
        //        }
        //    }
        //    TraceLogger.Log($"Cleared all files in folder: {folder}");
        //}

        public static void ClearTempFiles(string folder)
        {
            //HACK:
            //For some bizarre reason unbeknownst to me, IOManager.ClearFiles(IOManager.BlockListFolderLocation); in hostlistmanager.cs (44) causes the entire program to skip the majority
            //of the files in its directory when doing Directory.GetFiles if the Where(F check is present, even though there shouldn't be a computational difference.
            //It made sense in IOManager when I was trying to implement a ClearFiles deletion attempt system, which included Task.Wait - Since the thread would wait and cause havok.
            //I HAVE to duplicate the ClearFiles code from above plus the ONE change where it filters it based on combined. This fixes the problem.
            //I honestly don't know why and I don't even want to know. It's fixed, and I'm happy.
            var files = Directory.GetFiles(folder, "*.*").Where(f => !Path.GetFileName(f).StartsWith("HLDcombined-", StringComparison.OrdinalIgnoreCase)); /*.Where(f => !Path.GetFullPath(f).EndsWith(".etag", StringComparison.OrdinalIgnoreCase));*/
            foreach (var file in files)
            {
                try
                {
                    TraceLogger.Log($"{file} deleted.");
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error deleting file {file}: {ex}", Enums.StatusSeverityType.Error);
                }
            }
            TraceLogger.Log($"Cleared all files in folder: {folder}");
        }

        public static string FormatBytes(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return string.Format("{0:0.##} {1}", len, sizes[order]);
        }
    }
}