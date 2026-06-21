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

using HostDownloader.Modules.DownloadSystem;

namespace HostDownloader.Modules.WindowsSystem
{
    internal class IOManager
    {
        public static readonly string HostfilesLocation = "hostfiles";
        public static readonly string BlockListFolderLocation = "hostfiles/blocklist";
        public static readonly string WhiteListFolderLocation = "hostfiles/whitelist";
        public static readonly string IniBlockListFileLocation = "hostfiles/blocklist.ini";
        public static readonly string IniWhiteListFileLocation = "hostfiles/whitelist.ini";
        public static readonly string UserWebsiteBlockListFileLocation = "hostfiles/userwebsiteblocklist.ini";
        public static readonly string UserWebsiteWhiteListFileLocation = "hostfiles/userwebsitewhitelist.ini";
        public static readonly string CombinedBlockListFileLocation = "hostfiles/blocklist/combined-blocklist.txt";
        public static readonly string CombinedWhiteListFileLocation = "hostfiles/whitelist/combined-whitelist.txt";
        public static readonly string LogsLocation = "logs";
        public static void CreateNecessaryDirectoriesAndFiles()
        {
            string[] directories = [LogsLocation, HostfilesLocation, BlockListFolderLocation, WhiteListFolderLocation];
            bool checkForCorruption = false;
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
                        TraceLogger.Log($"Error creating directory {dir}: {ex}", Enums.StatusSeverityType.Fatal);
                    }
                }
            }
            string[] files = [UserWebsiteBlockListFileLocation, UserWebsiteWhiteListFileLocation, CombinedBlockListFileLocation, CombinedWhiteListFileLocation, IniBlockListFileLocation, IniWhiteListFileLocation];
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
                else
                {
                    checkForCorruption = true;
                }
            }
            if (ShowHelp)
            {
                Console.WriteLine("[!] Configuration files and folders have been created in the directory where this program is stored.\nPlease refer to the documentation on the main GitHub page of HostlistDownloader to configure. Once configured, run HostlistDownloader again. HostlistDownloader will now exit.");
                Environment.Exit(0);
            }
            if (checkForCorruption)
            {
                CheckForInvalidConfig();
            }
        }

        public static void CheckForInvalidConfig()
        {
            try
            {
                bool corruptionDetected = false;
                //Stage 1: Check blocklist and whitelist INI files for corruption (invalid entries)
                string[] configFiles = ["hostfiles/blocklist.ini", "hostfiles/whitelist.ini"];
                foreach (string configFile in configFiles)
                {
                    if (File.Exists(configFile))
                    {
                        var lines = File.ReadAllLines(configFile);
                        TraceLogger.Log($"Checking {configFile} for corruption. Total lines: {lines.Length}");
                        var validLines = new List<string>();
                        foreach (var line in lines)
                        {
                            if (Uri.IsWellFormedUriString(line, UriKind.Absolute))
                            {
                                validLines.Add(line);
                            }
                            else
                            {
                                corruptionDetected = true;
                                TraceLogger.Log($"Corruption detected: Removed invalid line from {configFile}: {line}", Enums.StatusSeverityType.Warning);
                            }
                        }
                        File.WriteAllLines(configFile, validLines);
                        TraceLogger.Log($"Checked {configFile} for corruption. Valid lines retained: {validLines.Count}");
                    }
                    else
                    {
                        TraceLogger.Log($"Critical configuration file missing: {configFile}. HostDirectory cannot continue without this file. Please ensure the file exists and is accessible.", Enums.StatusSeverityType.Fatal);
                    }
                }
                //Stage 2: Check user blocklist and whitelist INI files for corruption (invalid entries), they should only contain domain names, not full URLs. So google.com is valid, but http://google.com is not.
                string[] userConfigFiles = ["hostfiles/userwebsiteblocklist.ini", "hostfiles/userwebsitewhitelist.ini"];
                foreach (string userConfigFile in userConfigFiles)
                {
                    if (File.Exists(userConfigFile))
                    {
                        var lines = File.ReadAllLines(userConfigFile);
                        TraceLogger.Log($"Checking {userConfigFile} for corruption. Total lines: {lines.Length}");
                        var validLines = new List<string>();
                        foreach (var line in lines)
                        {
                            if (Uri.CheckHostName(line) != UriHostNameType.Unknown)
                            {
                                validLines.Add(line);
                            }
                            else
                            {
                                corruptionDetected |= true;
                                TraceLogger.Log($"Corruption detected: Removed corrupt line from {userConfigFile}: {line}", Enums.StatusSeverityType.Warning);
                            }
                        }
                        File.WriteAllLines(userConfigFile, validLines);
                        TraceLogger.Log($"Checked {userConfigFile} for corruption. Valid lines retained: {validLines.Count}");
                    }
                    else
                    {
                        TraceLogger.Log($"Critical configuration file missing: {userConfigFile}. HostlistDownloader cannot continue without this file. Please ensure the file exists and is accessible.", Enums.StatusSeverityType.Fatal);
                    }
                }
                TraceLogger.Log("Configuration corruption check completed.");
                if (corruptionDetected)
                {
                    TraceLogger.Log("Corruption was detected during startup and was removed from the affected configuration files. Please review the logs for details.", Enums.StatusSeverityType.Warning);
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Corruption Check failure! {ex}", Enums.StatusSeverityType.Fatal);
            }
        }

        public static void AddToIniFile(string iniFilePath, string domain)
        {
            var directory = Path.GetDirectoryName(iniFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(iniFilePath, $"{domain}{Environment.NewLine}");
        }

        public static void MergeFiles(string sourceFolder, string outputFile)
        {
            var files = Directory.GetFiles(sourceFolder, "*.txt");
            if (files.Length == 0)
            {
                TraceLogger.Log($"No files found to merge in {sourceFolder}", Enums.StatusSeverityType.Warning);
                return;
            }

            try
            {
                using var writer = new StreamWriter(outputFile);
                foreach (var file in files)
                {
                    TraceLogger.Log($"Merging file: {file}");
                    var lines = File.ReadAllLines(file);
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                        {
                            writer.WriteLine(line);
                        }
                    }
                }
                writer.Flush();
                writer.Dispose();
            }
            catch (UnauthorizedAccessException ex1)
            {
                TraceLogger.Log($"Access denied when trying to merge files into {outputFile}: {ex1.Message}", Enums.StatusSeverityType.Error);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error merging files into {outputFile}: {ex}", Enums.StatusSeverityType.Error);
            }
            //TraceLogger.Log($"Total entries in {outputFile}: {File.ReadAllLines(outputFile).Length}"); Removing since it will be in notification and that will be logged anyway.
        }

        public static void ClearFiles(string folder)
        {
            var files = Directory.GetFiles(folder, "*.txt");
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

        public static void RemoveDuplicates(string MergedFileLoc)
        {
            try
            {
                TraceLogger.Log("Removing duplicates from merge...");
                TraceLogger.Log($"Total lines in {MergedFileLoc} before removing duplicates: {File.ReadAllLines(MergedFileLoc).Length}");
                if (!File.Exists(MergedFileLoc))
                {
                    TraceLogger.Log($"File not found: {MergedFileLoc}", Enums.StatusSeverityType.Warning);
                    return;
                }
                var lines = File.ReadAllLines(MergedFileLoc);
                var uniqueLines = new HashSet<string>(lines);
                File.WriteAllLines(MergedFileLoc, uniqueLines);
                TraceLogger.Log($"Removed {lines.Length - uniqueLines.Count} duplicate entries.");
                TraceLogger.Log($"Total lines in {MergedFileLoc} after removing duplicates: {File.ReadAllLines(MergedFileLoc).Length}");
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error removing duplicates from {MergedFileLoc}: {ex}", Enums.StatusSeverityType.Error);
            }
        }
    }
}
