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
using System.Reflection;

Console.WriteLine($"--HostlistDownloader-- ver:{Assembly.GetExecutingAssembly().GetName().Version} starting...");

Directory.SetCurrentDirectory(AppContext.BaseDirectory); //Fixes issue where if the user runs the program from a different directory path in their terminal it will attempt to run with an invalid location.
IOManager.CreateNecessaryDirectoriesAndFiles();
TraceLogger.ClearExpiredLogs();

HostListManager.UpdateLists(); //Main Update Loop

IOManager.ClearTempFiles(IOManager.BlockListFolderLocation);
IOManager.ClearTempFiles(IOManager.WhiteListFolderLocation);

if (!HostListManager.ProblemDuringUpdate)
{
    TraceLogger.Log("Hostfiles updated successfully!");
}
else
{
    TraceLogger.Log("A problem was ran into when updating your hostlists. Please check the console output or log files for more information.", Enums.StatusSeverityType.Warning);
}

Environment.Exit(0);