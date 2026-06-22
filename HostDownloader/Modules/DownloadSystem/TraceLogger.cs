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

namespace HostlistDownloader.Modules.DownloadSystem
{
    public static class TraceLogger
    {
        private static readonly Lock _lock = new();
        private static readonly string _logDirectory = IOManager.LogsLocation;
        private static string _currentDate = DateTime.Now.ToString("dd-MM-yyyy");
        private static DateTime _lastDateCheck = DateTime.MinValue;

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
                    DateTime expiryDate = DateTime.Now.AddDays(-7);
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

        public static void Log(string message, StatusSeverityType severity = StatusSeverityType.Information,
                              [CallerMemberName] string memberName = "",
                              [CallerLineNumber] int lineNumber = 0)
        {

            if (string.IsNullOrEmpty(message))
            {
                Log($"The function {memberName} has called the TraceLogger.Log at line {lineNumber} but hasn't defined any of the log variables! That class may be malfunctioning.", StatusSeverityType.Warning);
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
                logEntry = $"[{timestamp}] [PID: {processID}] [{severityText}] [{memberName}]({lineNumber}): {message}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to prepare log entry: {ex}");
            }
            Console.WriteLine(logEntry);
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
                Log($"[FAULT STOP] A fatal exception has occurred - HostDirectory must exit. Trace Message: {logEntry}", StatusSeverityType.Error);
                Environment.Exit(1);
            }
        }
    }
}
