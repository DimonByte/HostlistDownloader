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

using HostlistDownloader.Modules.WindowsSystem;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using static HostlistDownloader.Modules.Enums;

namespace HostlistDownloader.Modules.Helpers
{
    public static class TraceLogger
    {
        /// <summary>
        /// When true, suppresses Console output (log file writing is unaffected). Set via the /quiet argument.
        /// </summary>
        public static bool QuietMode = false;
        public static bool DebugMode = false;
        //public static bool ProgressBarOnScreen = false; //Used to prevent tracelogger from writing to console while a progress bar is on screen, which would break the progress bar display.

        private static readonly Lock _lock = new();
        private static readonly string _logDirectory = IOManager.LogsLocation;
        private static string _currentDate = DateTime.Now.ToString("dd-MM-yyyy");
        private static DateTime _lastDateCheck = DateTime.MinValue;
        //private static List<string> _pendingLogs = [];
        //private static List<Enums.StatusSeverityType> _pendingLogSeverities = [];

        public static void PurgeAllLogs()
        {
            foreach (string file in Directory.GetFiles(_logDirectory))
            {
                Console.WriteLine($"Deleting all logs. Currently deleting: {file}");
                File.Delete(file);
            }
        }
        public static void ClearExpiredLogs()
        {
            lock (_lock)
            {
                _lastDateCheck = DateTime.MinValue;
                try
                {
                    if (!Directory.Exists(_logDirectory))
                        return;
                    var logFiles = Directory.GetFiles(_logDirectory, "*.log");
                    int expiryDays = 7;
                    try
                    {
                        expiryDays = ConfigManager.Instance.LogExpiryInDays;
                    }
                    catch (InvalidOperationException)
                    {
                        // ConfigReader not initialised yet (e.g. very first run before settings.json exists) - use the 7 day default.
                    }
                    DateTime expiryDate = DateTime.Now.AddDays(-expiryDays);
                    foreach (var logFile in logFiles)
                    {
                        var fileInfo = new FileInfo(logFile);
                        if (fileInfo.CreationTime < expiryDate)
                        {
                            fileInfo.Delete();
                            Log($"Deleted expired log file: {fileInfo.Name}");
                            Debug.WriteLine($"Deleted expired log file: {fileInfo.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to clear expired logs: {ex}", StatusSeverityType.Error);
                    Debug.WriteLine($"Failed to clear expired logs: {ex}");
                }
            }
        }

        public static void Log(string message, StatusSeverityType severity = StatusSeverityType.Information, int PassedErrorCode = 1,
                              [CallerMemberName] string memberName = "",
                              [CallerLineNumber] int lineNumber = 0)
        {
            if (string.IsNullOrEmpty(message))
            {
                Log($"Class Malfunction Warning: The function {memberName} has called the TraceLogger.Log at line {lineNumber} but hasn't defined any of the log variables!", StatusSeverityType.Warning);
            }

            string logEntry = string.Empty;
            string filePathLog = Path.Combine(_logDirectory, $"{_currentDate}.log");
            try
            {
                DateTime now = DateTime.Now;
                var currentDate = now.ToString("dd-MM-yyyy");
                if (now.Subtract(_lastDateCheck).TotalSeconds > 10)
                {
                    _currentDate = currentDate;
                    _lastDateCheck = now;
                }
                string timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string severityText = severity.ToString().ToUpper();
                string processID = Environment.ProcessId.ToString();

                if (DebugMode)
                {
                    logEntry = $"[{timestamp}] [PID: {processID}] [{severityText}] [{memberName}]({lineNumber}): {message}";
                }
                else
                {
                    logEntry = $"[{timestamp}] [{severityText}] {message}";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to prepare log entry: {ex}");
            }

            // 1. Determine console output behavior explicitly
            bool suppressedByQuietMode = QuietMode && (severity is StatusSeverityType.Information or StatusSeverityType.Debug);
            bool suppressedByDebugMode = !DebugMode && severity == StatusSeverityType.Debug || severity == StatusSeverityType.Information;
            bool shouldPrintToConsole = !suppressedByQuietMode && !suppressedByDebugMode;

            // 2. Handle console output (if allowed)
            if (shouldPrintToConsole)
            {
                ConsoleColor originalForeground = Console.ForegroundColor;
                ConsoleColor originalBackground = Console.BackgroundColor;
                try
                {
                    switch (severity)
                    {
                        case StatusSeverityType.Information: Console.ForegroundColor = ConsoleColor.White; break;
                        case StatusSeverityType.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
                        case StatusSeverityType.Error: Console.ForegroundColor = ConsoleColor.Red; break;
                        case StatusSeverityType.Fatal: Console.ForegroundColor = ConsoleColor.White; Console.BackgroundColor = ConsoleColor.Red; break;
                    }
                    Console.WriteLine(logEntry);
                }
                finally
                {
                    Console.ForegroundColor = originalForeground;
                    Console.BackgroundColor = originalBackground;
                }
            }

            //if (ProgressBarOnScreen)
            //{
            //    WriteToPendingLog(message, severity);
            //    _pendingLogSeverities.Add(severity);
            //}

            // 3. Write to file (unaffected by DebugMode or QuietMode)
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(filePathLog, $"{logEntry}{Environment.NewLine}", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to write to log: {ex}");
                }
            }

            if (severity == StatusSeverityType.Fatal)
            {
                Log($"[FAULT STOP] Error Code: {PassedErrorCode} ({ErrorCodes.GetDescription(PassedErrorCode)}) - HostDirectory must exit. Trace Message: {logEntry}", StatusSeverityType.Error);
                Environment.Exit(PassedErrorCode);
            }
        }

        //private static void WriteToPendingLog(string message, Enums.StatusSeverityType severityType)
        //{
        //    _pendingLogs.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{severityType}] {message}");
        //}

        //public static void PrintPendingLog()
        //{
        //    ProgressBarOnScreen = false;
        //    try
        //    {
        //        for (int i = 0; i < _pendingLogs.Count; i++)
        //        {
        //            string logEntry = _pendingLogs[i];
        //            Enums.StatusSeverityType severityType = _pendingLogSeverities[i];
        //            ConsoleColor originalForeground = Console.ForegroundColor;
        //            ConsoleColor originalBackground = Console.BackgroundColor;
        //            try
        //            {
        //                switch (severityType)
        //                {
        //                    case Enums.StatusSeverityType.Information: Console.ForegroundColor = ConsoleColor.White; break;
        //                    case Enums.StatusSeverityType.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
        //                    case Enums.StatusSeverityType.Error: Console.ForegroundColor = ConsoleColor.Red; break;
        //                    case Enums.StatusSeverityType.Fatal: Console.ForegroundColor = ConsoleColor.White; Console.BackgroundColor = ConsoleColor.Red; break;
        //                    default: Console.ForegroundColor = ConsoleColor.Gray; break;
        //                }
        //                //Check if debug is not enabled and the severity is debug, if so, skip printing to console
        //                if (!DebugMode)
        //                {
        //                    continue;
        //                }
        //                Console.WriteLine(logEntry);
        //            }
        //            finally
        //            {
        //                Console.ForegroundColor = originalForeground;
        //                Console.BackgroundColor = originalBackground;
        //            }
        //        }
        //    }
        //    catch(Exception ex)
        //    {
        //        Log($"Failed to print pending logs: {ex}", StatusSeverityType.Error);
        //    }
        //}
    }
}