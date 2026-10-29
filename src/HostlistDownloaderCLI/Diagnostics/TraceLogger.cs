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

using HostlistDownloader.Core;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace HostlistDownloader.Diagnostics;

internal enum LogSeverity
{
    Info = 0,
    Warn = 1,
    Error = 2,
    Debug = 3,
    Fatal = 4,
    Notice = 5 // Shown on the console even when debug mode is off.
}

/// <summary>
/// Writes to the daily log file and (unless quiet) to the console.
/// The log file is kept open for the whole run instead of being reopened for every line, and lines written
/// before <see cref="Initialize"/> is called are buffered and written once the log folder is known.
/// </summary>
internal static class TraceLogger
{
    private const int MaxPendingLines = 5000;
    private static readonly Lock Sync = new();
    private static readonly List<string> PendingLines = [];

    private static string? _logDirectory;
    private static StreamWriter? _writer;
    private static string? _writerDate;
    private static long _lastFlushTicks;
    private static int _warningCount;
    private static int _errorCount;
    private static bool _exitHookRegistered;

    /// <summary>Suppresses console output. The log file is still written.</summary>
    internal static bool QuietMode { get; set; }

    /// <summary>Shows Info and Debug lines on the console and adds caller details to log lines.</summary>
    internal static bool DebugMode { get; set; }

    /// <summary>Total warnings logged so far. Thread-safe; callers diff two readings to count warnings in a phase.</summary>
    internal static int WarningCount => Volatile.Read(ref _warningCount);

    internal static int ErrorCount => Volatile.Read(ref _errorCount);

    internal static string? CurrentLogFile
    {
        get
        {
            lock (Sync)
            {
                return _logDirectory is null ? null : Path.Combine(_logDirectory, $"{DateTime.Now:dd-MM-yyyy}.log");
            }
        }
    }

    internal static void Initialize(string logDirectory)
    {
        lock (Sync)
        {
            _logDirectory = logDirectory;
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not create log directory {logDirectory}: {ex}");
            }

            foreach (string line in PendingLines)
            {
                WriteLineLocked(DateTime.Now, line, flush: false);
            }
            PendingLines.Clear();
            _writer?.Flush();

            if (!_exitHookRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                _exitHookRegistered = true;
            }
        }
    }

    /// <summary>Flushes and closes the log file. Safe to call more than once.</summary>
    internal static void Shutdown()
    {
        lock (Sync)
        {
            CloseWriterLocked();
        }
    }

    /// <summary>
    /// Logs a fatal error and returns an exception for the caller to throw:
    /// <c>throw TraceLogger.Fatal("...", ExitCodes.X);</c>
    /// </summary>
    internal static FatalException Fatal(string message, int exitCode, Exception? innerException = null,
                                         [CallerMemberName] string memberName = "",
                                         [CallerLineNumber] int lineNumber = 0)
    {
        Log(message, LogSeverity.Fatal, memberName, lineNumber);
        Log($"[FAULT STOP] Exit code {exitCode} ({ExitCodes.GetDescription(exitCode)}). HostlistDownloader must exit.",
            LogSeverity.Error, memberName, lineNumber);
        return new FatalException(message, exitCode, innerException);
    }

    internal static void Log(string message, LogSeverity severity = LogSeverity.Info,
                             [CallerMemberName] string memberName = "",
                             [CallerLineNumber] int lineNumber = 0)
    {
        if (string.IsNullOrEmpty(message))
        {
            message = $"(empty log message from {memberName}:{lineNumber})";
        }

        string redacted = LogRedactor.Redact(message);

        if (severity == LogSeverity.Warn)
        {
            Interlocked.Increment(ref _warningCount);
        }
        else if (severity is LogSeverity.Error or LogSeverity.Fatal)
        {
            Interlocked.Increment(ref _errorCount);
        }

        if (ShouldPrintToConsole(severity))
        {
            WriteToConsole(severity, redacted, memberName, lineNumber);
        }
        else if (QuietMode && severity == LogSeverity.Fatal)
        {
            // Scripts running with --quiet still need to see why the program stopped.
            Console.Error.WriteLine($"FATAL: {redacted}");
        }

        DateTime now = DateTime.Now;
        string severityText = severity.ToString().ToUpperInvariant();
        string body = LogRedactor.Neutralise(redacted);
        string logEntry = DebugMode
            ? $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [PID: {Environment.ProcessId}] [{severityText}] [{memberName}]({lineNumber}): {body}"
            : $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{severityText}] {body}";

        lock (Sync)
        {
            WriteLineLocked(now, logEntry, flush: severity is LogSeverity.Warn or LogSeverity.Error or LogSeverity.Fatal or LogSeverity.Notice);
        }
    }

    /// <summary>Deletes log files whose last write is older than <paramref name="expiryDays"/> full days. Today's log is never deleted.</summary>
    internal static void ClearExpiredLogs(int expiryDays)
    {
        string? directory;
        string? currentFile;
        lock (Sync)
        {
            directory = _logDirectory;
            currentFile = _writer is null ? null : Path.Combine(_logDirectory!, $"{_writerDate}.log");
        }

        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        DateTime cutoff = DateTime.Today.AddDays(-Math.Max(0, expiryDays));
        foreach (string logFile in Directory.EnumerateFiles(directory, "*.log"))
        {
            try
            {
                var info = new FileInfo(logFile);
                bool isCurrent = currentFile is not null && string.Equals(info.FullName, Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
                bool isToday = info.Name.Equals($"{DateTime.Now:dd-MM-yyyy}.log", StringComparison.OrdinalIgnoreCase);
                if (!isCurrent && !isToday && info.LastWriteTime < cutoff)
                {
                    info.Delete();
                    Log($"Deleted expired log file: {info.Name}", LogSeverity.Debug);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to delete expired log {logFile}: {ex.Message}", LogSeverity.Warn);
            }
        }
    }

    /// <summary>Deletes all *.log files. Returns the number deleted.</summary>
    internal static int PurgeAllLogs()
    {
        int deleted = 0;
        lock (Sync)
        {
            CloseWriterLocked();
            if (_logDirectory is null || !Directory.Exists(_logDirectory))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(_logDirectory, "*.log"))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to delete log {file}: {ex}");
                }
            }
        }
        return deleted;
    }

    private static bool ShouldPrintToConsole(LogSeverity severity)
    {
        if (QuietMode)
        {
            return false;
        }

        bool hiddenUnlessDebug = severity is LogSeverity.Debug or LogSeverity.Info;
        return !hiddenUnlessDebug || DebugMode;
    }

    private static void WriteLineLocked(DateTime now, string line, bool flush)
    {
        if (_logDirectory is null)
        {
            if (PendingLines.Count < MaxPendingLines)
            {
                PendingLines.Add(line);
            }
            return;
        }

        try
        {
            string date = now.ToString("dd-MM-yyyy");
            if (_writer is null || _writerDate != date)
            {
                CloseWriterLocked();
                string path = Path.Combine(_logDirectory, $"{date}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _writerDate = date;
            }

            _writer.WriteLine(line);

            long nowTicks = Environment.TickCount64;
            if (flush || nowTicks - _lastFlushTicks > 1000)
            {
                _writer.Flush();
                _lastFlushTicks = nowTicks;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write to log: {ex}");
            CloseWriterLocked();
        }
    }

    private static void CloseWriterLocked()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to close log: {ex}");
        }
        _writer = null;
        _writerDate = null;
    }

    /// <summary>
    /// Console output via Spectre.Console. The message is escaped so URLs, brackets and exception text can never
    /// be interpreted as markup.
    /// </summary>
    private static void WriteToConsole(LogSeverity severity, string message, string memberName, int lineNumber)
    {
        var (tag, tagStyle, messageStyle) = severity switch
        {
            LogSeverity.Debug => ("DEBUG", "grey", "grey"),
            LogSeverity.Info => ("INFO", "default", "default"),
            LogSeverity.Notice => ("NOTE", "green", "default"),
            LogSeverity.Warn => ("WARN", "yellow", "yellow"),
            LogSeverity.Error => ("ERROR", "red", "red"),
            LogSeverity.Fatal => ("FATAL", "bold white on red", "bold red"),
            _ => (severity.ToString().ToUpperInvariant(), "default", "default")
        };

        string origin = DebugMode ? $" [dim]{Markup.Escape($"{memberName}:{lineNumber}")}[/]" : string.Empty;
        try
        {
            AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss}[/] [{tagStyle}]{tag,-5}[/]{origin} [{messageStyle}]{Markup.Escape(message)}[/]");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Console write failed: {ex}");
        }
    }
}
