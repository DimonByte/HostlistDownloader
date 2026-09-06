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
using HostlistDownloader.Modules.ArgumentParsing;
using HostlistDownloader.Modules.Helpers;
using HostlistDownloader.Modules.HostlistManagement.Generation;
using HostlistDownloader.Modules.Network;
using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;
using System.Reflection;

Console.WriteLine($"HostlistDownloader version {Assembly.GetExecutingAssembly().GetName().Version}\nCopyright (c) {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright} under MIT License. Uses other libraries which are subject to their own licenses.");
Stopwatch watch = Stopwatch.StartNew();

// Set current directory to application base directory
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Initialize necessary files/directories and config
IOManager.CreateNecessaryDirectoriesAndFiles();
TraceLogger.Log("[START] Initializing HostlistDownloader...");
ConfigManager.Init(IOManager.SettingJsonFileLocation);
IOManager.CheckForInvalidConfig();

// --- PARSE ARGUMENTS ---
ArgumentResult argsResult;
try
{
    argsResult = ArgumentParser.Parse(args);
    ArgumentParser.ApplySideEffects(argsResult);
    TraceLogger.DebugMode = argsResult.DebugMode;
}
catch (ArgumentException ex)
{
    TraceLogger.Log(ex.Message, Enums.StatusSeverityType.Error, ErrorCodes.InvalidConfigEntry);
    Environment.Exit(ErrorCodes.InvalidConfigEntry);
    return; //Although the environment exits, the return here prevents "Use of unassigned local variable 'argsResult'" error in solution error check.
}

if (argsResult.SearchDomain != null)
{
    SearchManager.Search(argsResult.SearchDomain);
    return;
}

if (!NetworkChecker.IsNetworkAvailable())
{
    TraceLogger.Log("Unable to get a network connection!", Enums.StatusSeverityType.Fatal, ErrorCodes.NetworkConnectionFailed);
}
TraceLogger.ClearExpiredLogs();
bool freshMode = argsResult.IsFresh;

HostListManager.StartListProcessing(freshMode);

watch.Stop();

// Handle Post-Update Status
if (!HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    Console.ForegroundColor = ConsoleColor.Green;
    ListUpdateStats();
    TraceLogger.Log($"[UPDATED] Hostfiles updated successfully. Compile time: {watch.Elapsed.TotalSeconds} seconds total.", Enums.StatusSeverityType.Important);
}
else if (HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    ListUpdateStats();
    TraceLogger.Log($"[UPDATED WITH ISSUES] Some hostfiles have updated successfully and compiled in {watch.Elapsed.TotalSeconds} seconds. But issues were detected. Please look through the logs for more information.", Enums.StatusSeverityType.Warning);
    Environment.ExitCode = ErrorCodes.PartialUpdateWithIssues;
}
else if (!HostListManager.ProblemDuringUpdate && !HostListManager.HasDownloadedUpdates)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    ListUpdateStats();
    TraceLogger.Log($"[UP TO DATE] Hostfiles are already up to date! (Time taken: {watch.Elapsed.TotalSeconds} seconds.)", Enums.StatusSeverityType.Important);
}
else // Problem and no downloads
{
    Console.ForegroundColor = ConsoleColor.Red;
    ListUpdateStats();
    TraceLogger.Log($"[PROBLEM] A problem was ran into when updating your hostlists. Please check the console output or log files for more information.", Enums.StatusSeverityType.Warning);
    Environment.ExitCode = ErrorCodes.UpdateProcessError;
}

Console.BackgroundColor = ConsoleColor.Black;
Console.ForegroundColor = ConsoleColor.White;

UpdateChecker.IsUpdateAvailable();

static void ListUpdateStats()
{
    TraceLogger.Log($"[STATS] Total hostlists processed: {HostListManager.UpdateStatistics.Count}", Enums.StatusSeverityType.Important);
    foreach (var stat in HostListManager.UpdateStatistics)
    {
        TraceLogger.Log($"[STATS] {stat}", Enums.StatusSeverityType.Important);
    }
}
TraceLogger.Log($"[END OF LOG]");