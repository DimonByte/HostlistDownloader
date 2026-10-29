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

using HostlistDownloader.Cli;
using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using HostlistDownloader.UI;

namespace HostlistDownloader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cancellation = new CancellationTokenSource();
        void onCancel(object? _, ConsoleCancelEventArgs e)
        {
            if (cancellation.IsCancellationRequested)
            {
                return; // second Ctrl+C: let the process terminate immediately
            }
            e.Cancel = true;
            cancellation.Cancel();
            TraceLogger.Log("Cancellation requested. Finishing the current step; published lists are left unchanged. Press Ctrl+C again to force quit.", LogSeverity.Warn);
        }
        Console.CancelKeyPress += onCancel;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            TraceLogger.Log($"Unhandled exception: {e.ExceptionObject}", LogSeverity.Fatal);
            TraceLogger.Shutdown();
        };

        int exitCode;
        try
        {
            CliRequest request;
            try
            {
                request = ArgumentParser.Parse(args);
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ex.ExitCode;
            }

            TraceLogger.QuietMode = request.Global.Quiet;
#if DEBUG
            TraceLogger.DebugMode = true; // development builds always log debug detail
#else
            TraceLogger.DebugMode = request.Global.Debug;
#endif

            if (!request.Global.NoBanner && request.Command is not (CommandKind.Help or CommandKind.Version))
            {
                ConsoleUI.ShowBanner();
            }

            exitCode = await CommandDispatcher.ExecuteAsync(request, cancellation.Token).ConfigureAwait(false);
        }
        catch (FatalException ex)
        {
            exitCode = ex.ExitCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TraceLogger.Log("Cancelled. The published lists were not changed.", LogSeverity.Warn);
            exitCode = ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            TraceLogger.Log($"Unexpected error: {ex}", LogSeverity.Fatal);
            exitCode = ExitCodes.GeneralError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Console.ResetColor();
        }

        TraceLogger.Log($"[PROGRAM CLOSED WITH CODE: {exitCode}] {ExitCodes.GetDescription(exitCode)}", LogSeverity.Debug);
        TraceLogger.Shutdown();
        return exitCode;
    }
}
