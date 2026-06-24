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
using HostlistDownloader.Modules.DownloadSystem;
using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;
using System.Reflection;

Console.WriteLine($"--HostlistDownloader-- ver:{Assembly.GetExecutingAssembly().GetName().Version} starting...");
Console.WriteLine("Arguments: /force < ignores etags - forces a redownload of all hostlists.");
Stopwatch watch = Stopwatch.StartNew();
Directory.SetCurrentDirectory(AppContext.BaseDirectory); //Fixes issue where if the user runs the program from a different directory path in their terminal it will attempt to run with an invalid location.
IOManager.CreateNecessaryDirectoriesAndFiles();
if (!NetworkChecker.IsNetworkAvailable())
{
    TraceLogger.Log("Unable to get a network connection!", Enums.StatusSeverityType.Fatal, ErrorCodes.NetworkConnectionFailed);
}

bool force = false;
List<string> remainingArgs = [];

foreach (string arg in args)
{
    if (arg == "/force")
    {
        TraceLogger.Log("/force enabled. Will ignore Etags.");
        force = true;
    }
    else
    {
        remainingArgs.Add(arg);
    }
}

TraceLogger.ClearExpiredLogs();

HostListManager.UpdateLists(force); //Main Update Loop

//IOManager.ClearTempFiles(IOManager.BlockListFolderLocation); //Cant clear out hostfiles... used for etags.
//IOManager.ClearTempFiles(IOManager.WhiteListFolderLocation);

watch.Stop();
if (!HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"(UPDATED) Hostfiles updated successfully in {watch.Elapsed.TotalSeconds} seconds.");
}
else if (HostListManager.ProblemDuringUpdate && HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"(UPDATED WITH ISSUES) Some hostfiles have updated successfully in {watch.Elapsed.TotalSeconds} seconds. But issues were detected. Please look through the logs for more information.");
    Environment.ExitCode = ErrorCodes.PartialUpdateWithIssues;
}
else if (!HostListManager.ProblemDuringUpdate && !HostListManager.HasDownloadedUpdates)
{
    TraceLogger.Log($"(UP TO DATE) Hostfiles are already up to date! (time taken: {watch.Elapsed.TotalSeconds} seconds.)");
}
else //Problem and no downloads
{
    TraceLogger.Log("(PROBLEM) A problem was ran into when updating your hostlists. Please check the console output or log files for more information.", Enums.StatusSeverityType.Warning);
    Environment.ExitCode = ErrorCodes.UpdateProcessError;
}