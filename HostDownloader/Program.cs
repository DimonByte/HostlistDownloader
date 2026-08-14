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

using HostlistDownloader.Modules;
using HostlistDownloader.Modules.Helpers;
using HostlistDownloader.Modules.HostListDownloaderInternals;
using HostlistDownloader.Modules.Network;
using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;
using System.Reflection;

Console.WriteLine($"--HostlistDownloader-- [MIT License] ver:{Assembly.GetExecutingAssembly().GetName().Version} starting...");
Stopwatch watch = Stopwatch.StartNew();
Directory.SetCurrentDirectory(AppContext.BaseDirectory); //Fixes issue where if the user runs the program from a different directory path in their terminal it will attempt to run with an invalid location.
IOManager.CreateNecessaryDirectoriesAndFiles();
ConfigReader.Init(IOManager.SettingJsonFileLocation);
IOManager.CheckForInvalidConfig();
if (!NetworkChecker.IsNetworkAvailable())
{
    TraceLogger.Log("Unable to get a network connection!", Enums.StatusSeverityType.Fatal, ErrorCodes.NetworkConnectionFailed);
}

bool fresh = false;

List<string> remainingArgs = [];
string? searchDomain = null;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];

    if (arg == "/quiet" || arg == "/q")
    {
        TraceLogger.QuietMode = true;
        TraceLogger.Log("/quiet enabled. Console output will be suppressed.");
    }
    else if (arg == "/fresh" || arg == "/fr")
    {
        TraceLogger.Log("/fresh enabled. Clearing block and white list folders...");
        IOManager.ClearTempFiles(IOManager.BlockListFolderLocation);
        IOManager.ClearTempFiles(IOManager.WhiteListFolderLocation);
        IOManager.ClearTempFiles(IOManager.CombinedListFolderLocation);
        fresh = true;
    }
    else if (arg == "/search" || arg == "/s")
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith('/'))
        {
            TraceLogger.Log("/search requires a domain argument, e.g. /search example.com", Enums.StatusSeverityType.Error);
            Environment.Exit(ErrorCodes.InvalidConfigEntry);
        }
        searchDomain = args[i + 1];
        i++; // consume the domain token so it isn't also treated as a separate arg
    }
    else if (arg == "/purge" || arg == "/p")
    {
        TraceLogger.Log("/purge enabled. Deleting all logs...");
        TraceLogger.PurgeAllLogs();
    }
    else if (arg == "/help" || arg == "/h" || arg == "/?")
    {
        Console.WriteLine("HostlistDownloader Help:");
        Console.WriteLine("/quiet or /q: Suppresses console output.");
        Console.WriteLine("/fresh or /fr: Clears block and white list folders before updating. Useful for troubleshooting.");
        Console.WriteLine("/search <domain> or /s <domain>: Searches for a specific domain in the hostlists.");
        Console.WriteLine("/purge or /p: Deletes all log files.");
        Console.WriteLine("/help or /h: Displays this help message.");
        Environment.Exit(0);
    }
    else
    {
        remainingArgs.Add(arg);
    }
}
if (searchDomain != null)
{
    SearchManager.Search(searchDomain);
    return;
}
TraceLogger.ClearExpiredLogs();

HostListManager.StartListProcessing(fresh); //Main Update Loop

watch.Stop();
if (!HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"[UPDATED] Hostfiles updated successfully in {watch.Elapsed.TotalSeconds} seconds.");
}
else if (HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"[UPDATED WITH ISSUES] Some hostfiles have updated successfully in {watch.Elapsed.TotalSeconds} seconds. But issues were detected. Please look through the logs for more information.");
    Environment.ExitCode = ErrorCodes.PartialUpdateWithIssues;
}
else if (!HostListManager.ProblemDuringUpdate && !HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"[UP TO DATE] Hostfiles are already up to date! (time taken: {watch.Elapsed.TotalSeconds} seconds.)");
}
else //Problem and no downloads
{
    TraceLogger.Log($"[PROBLEM] A problem was ran into when updating your hostlists. Please check the console output or log files for more information.", Enums.StatusSeverityType.Warning);
    Environment.ExitCode = ErrorCodes.UpdateProcessError;
}
Console.BackgroundColor = ConsoleColor.Black;
Console.ForegroundColor = ConsoleColor.White;