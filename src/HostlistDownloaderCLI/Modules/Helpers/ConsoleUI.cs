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

using HostlistDownloaderCLI.Modules.HostlistManagement.Generation;
using HostlistDownloaderCLI.Modules.IO;
using Spectre.Console;
using System.ComponentModel;
using System.Diagnostics;

namespace HostlistDownloaderCLI.Modules.Helpers
{
    /// <summary>
    /// Thread-safe handle given to the work running inside a progress bar.
    /// </summary>
    internal interface IProgressReporter
    {
        /// <summary>Shows which item is being worked on without advancing the bar.</summary>
        void SetCurrent(string item);
        /// <summary>Advances the bar by one step, optionally updating the current item.</summary>
        void Advance(string? item = null);
    }

    /// <summary>
    /// Central place for all Spectre.Console UI (progress bars, banner, summary tables).
    /// Everything respects TraceLogger.QuietMode and degrades gracefully when output is redirected.
    /// </summary>
    internal static class ConsoleUI
    {
        private const int ItemWidth = 42;

        private sealed class NullReporter : IProgressReporter
        {
            public static readonly NullReporter Instance = new();
            public void SetCurrent(string item) { }
            public void Advance(string? item = null) { }
        }

        private sealed class SpectreReporter(ProgressTask task, string verb) : IProgressReporter
        {
            private long _count;
            private long _lastFlush;
            private volatile string _current = string.Empty;

            public long Completed => Interlocked.Read(ref _count);

            public void SetCurrent(string item)
            {
                _current = item;
                Flush(false);
            }

            public void Advance(string? item = null)
            {
                Interlocked.Increment(ref _count);
                if (item is not null) _current = item;
                Flush(false);
            }

            /// <summary>
            /// Pushes the counters into Spectre. Throttled to ~12 updates/sec so that
            /// processing hundreds of thousands of lines isn't slowed down by rendering.
            /// </summary>
            public void Flush(bool force)
            {
                long now = Stopwatch.GetTimestamp();
                long last = Interlocked.Read(ref _lastFlush);
                if (!force)
                {
                    if (now - last < Stopwatch.Frequency / 12) return;
                    if (Interlocked.CompareExchange(ref _lastFlush, now, last) != last) return;
                }
                task.Value = Completed;
                task.Description = $"[blue]{Markup.Escape(verb)}[/] [grey]{Markup.Escape(Fit(_current))}[/]";
            }

            private static string Fit(string text)
            {
                if (text.Length > ItemWidth) text = text[..(ItemWidth - 1)] + "…";
                return text.PadRight(ItemWidth);
            }
        }

        private static ProgressColumn[] Columns() =>
        [
            new SpinnerColumn(Spinner.Known.Dots),
            new TaskDescriptionColumn { Alignment = Justify.Left },
            new ProgressBarColumn(){
                CompletedStyle = new Style(Color.Green),
                FinishedStyle = new Style(Color.Lime),
                RemainingStyle = new Style(Color.Grey)
            },
            new PercentageColumn(),
            new ElapsedTimeColumn(),
            new RemainingTimeColumn(),
        ];

        private static void PrintDone(string verb, long completed, string unit, TimeSpan elapsed)
        {
            AnsiConsole.MarkupLine($"[green][/] [bold]{Markup.Escape(verb)}[/] done: [cyan]{completed:N0}[/] {Markup.Escape(unit)} in [cyan]{elapsed.TotalSeconds:F1}s[/]");
        }

        /// <summary>Runs synchronous work with a Spectre progress bar.</summary>
        internal static void Progress(string verb, int max, string unit, Action<IProgressReporter> work)
        {
            if (TraceLogger.QuietMode)
            {
                work(NullReporter.Instance);
                return;
            }

            var sw = Stopwatch.StartNew();
            long completed = 0;
            AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(false)
                .Columns(Columns())
                .Start(ctx =>
                {
                    var task = ctx.AddTask($"[cyan]{Markup.Escape(verb)}[/]", maxValue: Math.Max(1, max));
                    var reporter = new SpectreReporter(task, verb);
                    work(reporter);
                    reporter.Flush(true);
                    completed = reporter.Completed;
                    task.Value = task.MaxValue; // skipped items (comments/blank lines) shouldn't leave the bar short
                    task.StopTask();
                });
            PrintDone(verb, completed, unit, sw.Elapsed);
        }

        /// <summary>Runs asynchronous (e.g. parallel download) work with a Spectre progress bar.</summary>
        internal static async Task ProgressAsync(string verb, int max, string unit, Func<IProgressReporter, Task> work)
        {
            if (TraceLogger.QuietMode)
            {
                await work(NullReporter.Instance);
                return;
            }

            var sw = Stopwatch.StartNew();
            long completed = 0;
            await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(false)
                .Columns(Columns())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[cyan]{Markup.Escape(verb)}[/]", maxValue: Math.Max(1, max));
                    var reporter = new SpectreReporter(task, verb);
                    await work(reporter);
                    reporter.Flush(true);
                    completed = reporter.Completed;
                    task.Value = task.MaxValue;
                    task.StopTask();
                });
            PrintDone(verb, completed, unit, sw.Elapsed);
        }

        //internal static void ShowBanner(string? version = null)
        //{
        //    if (TraceLogger.QuietMode) return;
        //    string title = string.IsNullOrWhiteSpace(version)
        //        ? "[bold cyan]HostlistDownloader[/]"
        //        : $"[bold cyan]HostlistDownloader[/] [grey]v{Markup.Escape(version)}[/]";
        //    AnsiConsole.Write(new Rule(title) { Justification = Justify.Left, Style = Style.Parse("grey") });
        //}

        internal static void ShowDownloadSummary(TimeSpan elapsed, int succeeded, int succeededWithNoETag, int upToDate, int permanentFailures, int transientFailures, int cancelled, int downloadBlockedByConfig, int contentRejectedNonText, int securityViolation)
        {
            if (TraceLogger.QuietMode) return;

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Caption($"[grey]Completed in {elapsed.TotalSeconds:F1}s[/]")
                .AddColumn("Result")
                .AddColumn(new TableColumn("Count").RightAligned());

            table.AddRow("[green]Downloaded[/]", succeeded.ToString("N0"));
            table.AddRow("[green]Downloaded (no ETag)[/]", succeededWithNoETag.ToString("N0"));
            table.AddRow("[blue]Already up to date[/]", upToDate.ToString("N0"));
            table.AddRow(permanentFailures > 0 ? "[yellow]Permanently unreachable[/]" : "[grey]Permanently unreachable[/]", permanentFailures.ToString("N0"));
            table.AddRow(transientFailures > 0 ? "[red]Failed after retries[/]" : "[grey]Failed after retries[/]", transientFailures.ToString("N0"));
            table.AddRow(cancelled > 0 ? "[yellow]Cancelled[/]" : "[grey]Cancelled[/]", cancelled.ToString("N0"));
            table.AddRow(downloadBlockedByConfig > 0 ? "[yellow]Blocked by config (your settings is configured to block non HTTPS URLs)[/]" : "[grey]Blocked by config (HTTPS only)[/]", downloadBlockedByConfig.ToString("N0"));
            table.AddRow(contentRejectedNonText > 0 ? "[yellow]Content rejected (non-text was received from the server)[/]" : "[grey]Content rejected (non-text)[/]", contentRejectedNonText.ToString("N0"));
            table.AddRow(securityViolation > 0 ? "[red]SSRF Security Violation (A URL attempted to bypass security checks)[/]" : "[grey]SSRF Security Violation[/]", securityViolation.ToString("N0"));
            if (HostlistOrchestrator.NumberOfWarningsDuringDownloadPhase > 0)
            {
                AnsiConsole.MarkupLine($"[yellow][bold]{HostlistOrchestrator.NumberOfWarningsDuringDownloadPhase:N0} warnings were logged during the download phase. Check the log for details.[/][/]");
                table.AddRow("[yellow]Warnings reported during download[/]", HostlistOrchestrator.NumberOfWarningsDuringDownloadPhase.ToString("N0"));
            }
            AnsiConsole.Write(table);
        }

        internal static void PrintUpdateStats(double watchTime)
        {
            if (TraceLogger.QuietMode) return;
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Title("[bold]Hostlist Update Statistics[/]")
                .Caption($"[green]Updated {HostlistOrchestrator.UpdateStatistics.Count:N0} hosts in {watchTime:F1}s[/]")
                .AddColumn("Hostlist Name")
                .AddColumn("Update Status")
                .AddColumn("Download Time (in seconds)");
            foreach (var (hostlist, status, downloadTime) in HostlistOrchestrator.UpdateStatistics)
            {
                //downloadTime is in milliseconds, so we need to convert it to seconds for display
                table.AddRow(Markup.Escape(hostlist), Markup.Escape(status), (downloadTime / 1000).ToString("N2"));
            }
            AnsiConsole.Write(table);
            //HostlistOrchestrator uses internal static List<(string Hostlist, string UpdateStatus, double DownloadTime)> UpdateStatistics = [];
        }
        internal static void PrintDuplicateRemovalStats(double watchTime, int totalRemovedLines, long sizeSavedDiff, int duplicatesRemoved, int emptyRemoved)
        {
            if (TraceLogger.QuietMode) return;
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Title("[bold]Duplicate Removal Statistics[/]")
                //.Caption($"[green]Removed {totalRemovedLines:N0} lines in {watchTime:F1}s[/]")
                .AddColumn("Total Lines Removed")
                .AddColumn("Size Saved Difference")
                .AddColumn("Duplicates Removed")
                .AddColumn("Empty Lines Removed")
                .AddColumn("Processing Time (in seconds)");
            table.AddRow(totalRemovedLines.ToString("N0"), IOManager.FormatBytes(sizeSavedDiff), duplicatesRemoved.ToString("N0"), emptyRemoved.ToString("N0"), watchTime.ToString("N2"));
            AnsiConsole.Write(table);
            //AnsiConsole.MarkupLine($"[green]Total Lines Removed from combined list:[/] [cyan]{totalRemovedLines:N0}[/]");
            //AnsiConsole.MarkupLine($"[green]Size Saved Difference from combined list:[/] [cyan]{IOManager.FormatBytes(sizeSavedDiff)}[/]");
            //AnsiConsole.MarkupLine($"[green]Duplicates Removed from combined list:[/] [cyan]{duplicatesRemoved:N0}[/]");
            //AnsiConsole.MarkupLine($"[green]Empty Lines Removed from combined list:[/] [cyan]{emptyRemoved:N0}[/]");
            //AnsiConsole.MarkupLine($"[green]Processing Time for removing duplicates:[/] [cyan]{watchTime:F2}s[/]");
        }
    }
}
