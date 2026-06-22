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

using HostlistDownloader.Modules.DownloadSystem;

namespace HostlistDownloader.Modules.WindowsSystem
{
    internal class IOManager
    {
        public static readonly string HostfilesLocation = "hostfiles";
        public static readonly string BlockListFolderLocation = "hostfiles\\blocklist";
        public static readonly string WhiteListFolderLocation = "hostfiles\\whitelist";
        public static readonly string IniBlockListFileLocation = "hostfiles\\blocklist.ini";
        public static readonly string IniWhiteListFileLocation = "hostfiles\\whitelist.ini";
        public static readonly string IniUserWebsiteBlockListFileLocation = "hostfiles\\userwebsiteblocklist.ini";
        public static readonly string IniUserWebsiteWhiteListFileLocation = "hostfiles\\userwebsitewhitelist.ini";
        public static readonly string CombinedBlockListFileLocation = "hostfiles\\blocklist\\combined-blocklist.txt";
        public static readonly string CombinedWhiteListFileLocation = "hostfiles\\whitelist\\combined-whitelist.txt";
        public static readonly string IniFormatTypeLocation = "hostfiles\\formattype.ini";
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
            string[] files = [IniFormatTypeLocation, IniUserWebsiteBlockListFileLocation, IniUserWebsiteWhiteListFileLocation, CombinedBlockListFileLocation, CombinedWhiteListFileLocation, IniBlockListFileLocation, IniWhiteListFileLocation];
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
                //Is the running environment path and the application directory path different??
                string appDir = Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string currentDir = Path.GetFullPath(Environment.CurrentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(appDir, currentDir, StringComparison.OrdinalIgnoreCase))
                {
                    TraceLogger.Log($"HostfileDownloader must be run from the directory where it is stored. Application Path: {appDir} - Path that was passed: {currentDir}. To fix this, you must CD to the path in your terminal where HostfileDownloader is stored '{appDir}' and try again.", Enums.StatusSeverityType.Fatal);
                }
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

        public static void FormatHosts(string combinedFileLocation)
        {
            TraceLogger.Log($"Attempting to format Hostfile: {combinedFileLocation}");
            string formatTypePath = IniFormatTypeLocation;
            string formatType = "domain"; // default format type
            if (File.Exists(formatTypePath))
            {
                try
                {
                    var lines = File.ReadAllLines(formatTypePath);
                    if (lines.Length > 0)
                    {
                        formatType = lines[0].Trim().ToLowerInvariant();
                        TraceLogger.Log($"Format Type: {formatType}");
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error reading format type from {formatTypePath}: {ex}", Enums.StatusSeverityType.Error);
                }
            }

            // Read all lines from the combined file
            if (!File.Exists(combinedFileLocation))
            {
                TraceLogger.Log($"Combined file not found: {combinedFileLocation}", Enums.StatusSeverityType.Warning);
                return;
            }

            var originalLines = File.ReadAllLines(combinedFileLocation);
            var formattedLines = new List<string>();

            foreach (var line in originalLines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var trimmedLine = line.Trim();

                // Check if it's in host format: "IP domain"
                if (Uri.CheckHostName(trimmedLine) == UriHostNameType.Unknown)
                {
                    // Try to split the line and see what we get
                    var parts = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2)
                    {
                        // Assume first part is IP address, rest is domain
                        var ipAddress = parts[0];
                        var hostName = string.Join(" ", parts.Skip(1)); // Join the remaining parts in case the domain has spaces
                        // Apply format according to type
                        switch (formatType)
                        {
                            case "hosts":
                            case "host":
                                formattedLines.Add($"{ipAddress} {hostName}");
                                break;
                            case "domain":
                                formattedLines.Add(hostName);
                                break;
                            case "iponly":
                                if (!string.Equals(ipAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
                                {
                                    formattedLines.Add(ipAddress);
                                }
                                break;
                            default:
                                formattedLines.Add(hostName);
                                break;
                        }
                    }
                    else if (parts.Length == 1)
                    {
                        // Just a single domain, no IP address
                        var domain = parts[0];
                        switch (formatType)
                        {
                            case "hosts":
                            case "host":
                                // Default to host format, so prepend with 0.0.0.0
                                formattedLines.Add($"0.0.0.0 {domain}");
                                break;
                            case "iponly":
                                // For iponly format, there's no IP so we skip this line
                                break;
                            default:
                                formattedLines.Add(domain);
                                break;
                        }
                    }
                }
                else
                {
                    // This is a domain itself (no IP address provided)
                    var domain = trimmedLine;
                    switch (formatType)
                    {
                        case "hosts":
                        case "host":
                            // Prepend with 0.0.0.0 for host format
                            formattedLines.Add($"0.0.0.0 {domain}");
                            break;
                        case "iponly":
                            // For iponly, we don't have IP to work with
                            break;
                        default:
                            // Default to domain-only format
                            formattedLines.Add(domain);
                            break;
                    }
                }
            }

            try
            {
                TraceLogger.Log($"Formatting Complete. Saving formattedLines to {combinedFileLocation}");
                File.WriteAllLines(combinedFileLocation, formattedLines);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error writing formatted lines to {combinedFileLocation}: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        public static void MergeFiles(string sourceFolder, string outputFile)
        {
            var files = Directory.GetFiles(sourceFolder, "*.*"); // Fixed: was ".txt"
            if (files.Length == 0)
            {
                TraceLogger.Log($"No files found to merge in {sourceFolder}.", Enums.StatusSeverityType.Warning);
                return;
            }

            try
            {
                using var writer = new StreamWriter(outputFile);
                foreach (var file in files)
                {
                    if (file.Contains("combined-"))
                    {
                        TraceLogger.Log($"Combined file {file} ignored.");
                        return;
                    }
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

        public static void ClearFiles(string folder)
        {
            var files = Directory.GetFiles(folder, "*.*");
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

        public static void ClearTempFiles(string folder)
        {
            //HACK:
            //For some bizarre reason unbeknownst to me, IOManager.ClearFiles(IOManager.BlockListFolderLocation); in hostlistmanager.cs (44) causes the entire program to skip the majority
            //of the files in its directory when doing Directory.GetFiles if the Where(F check is present, even though there shouldn't be a computational difference.
            //It made sense in IOManager when I was trying to implement a ClearFiles deletion attempt system, which included Task.Wait - Since the thread would wait and cause havok.
            //I HAVE to duplicate the ClearFiles code from above plus the ONE change where it filters it based on combined. This fixes the problem.
            //I honestly don't know why and I don't even want to know. It's fixed, and I'm happy.
            var files = Directory.GetFiles(folder, "*.*").Where(f => !Path.GetFileName(f).StartsWith("combined-", StringComparison.OrdinalIgnoreCase));
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
                var originalLines = File.ReadAllLines(MergedFileLoc);

                if (!File.Exists(MergedFileLoc))
                {
                    TraceLogger.Log($"File not found: {MergedFileLoc}", Enums.StatusSeverityType.Warning);
                    return;
                }
                var uniqueLines = new HashSet<string>(originalLines, StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(MergedFileLoc, uniqueLines);
                int removedLines = originalLines.Length - uniqueLines.Count;
                var fileInfo = new FileInfo(MergedFileLoc);
                long originalFileSize = originalLines.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line) + 2); // +2 for newline characters
                long newSize = uniqueLines.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line) + 2);
                long sizeDifference = originalFileSize - newSize;
                TraceLogger.Log($"Total lines in {MergedFileLoc} before removing duplicates: {originalLines.Length:N0}");
                TraceLogger.Log($"Total lines in {MergedFileLoc} after removing duplicates: {uniqueLines.Count:N0}");
                TraceLogger.Log($"Removed {removedLines:N0} lines ({FormatBytes(sizeDifference)} of space saved)");
            }
            catch (FileNotFoundException ex1)
            {
                TraceLogger.Log($"{ex1.Message}. You can IGNORE this error if the file not found is for a list that you haven't configured. (e.g. if you left whitelist.ini blank and the file not found is the combined-whitelist.txt, you can ignore.).", Enums.StatusSeverityType.Error);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error removing duplicates from {MergedFileLoc}: {ex}", Enums.StatusSeverityType.Error);
            }
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