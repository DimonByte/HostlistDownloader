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
using HostlistDownloader.Diagnostics;
using Spectre.Console;
using System.Diagnostics;

namespace HostlistDownloader.UI;

/// <summary>Thread-safe handle given to work running inside a progress bar.</summary>
internal interface IProgressReporter
{
    /// <summary>Shows which item is being worked on without advancing the bar.</summary>
    void SetCurrent(string item);

    /// <summary>Advances the bar by one step, optionally updating the current item.</summary>
    void Advance(string? item = null);
}

/// <summary>
/// All Spectre.Console rendering: progress bars, banner and report tables. Reports are rendered here rather than
/// through the logger, so they are readable without debug mode and not timestamped. Everything respects quiet mode.
/// </summary>
internal static class ConsoleUI
{
    private const int ItemWidth = 42;

    internal static bool Enabled => !TraceLogger.QuietMode;

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
            if (item is not null)
            {
                _current = item;
            }
            Flush(false);
        }

        /// <summary>Pushes counters into Spectre, throttled to about 12 updates a second.</summary>
        public void Flush(bool force)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastFlush);
            if (!force)
            {
                if (now - last < Stopwatch.Frequency / 12)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref _lastFlush, now, last) != last)
                {
                    return;
                }
            }
            task.Value = Math.Min(Completed, task.MaxValue);
            task.Description = $"[blue]{Markup.Escape(verb)}[/] [grey]{Markup.Escape(Fit(LogRedactor.Redact(_current)))}[/]";
        }

        private static string Fit(string text)
        {
            if (text.Length > ItemWidth)
            {
                text = text[..(ItemWidth - 1)] + "…";
            }
            return text.PadRight(ItemWidth);
        }
    }

    private static ProgressColumn[] Columns() =>
    [
        new SpinnerColumn(Spinner.Known.Dots),
        new TaskDescriptionColumn { Alignment = Justify.Left },
        new ProgressBarColumn
        {
            CompletedStyle = new Style(Color.Green),
            FinishedStyle = new Style(Color.Lime),
            RemainingStyle = new Style(Color.Grey)
        },
        new PercentageColumn(),
        new ElapsedTimeColumn(),
        new RemainingTimeColumn()
    ];

    internal static void Progress(string verb, int max, string unit, Action<IProgressReporter> work)
    {
        if (!Enabled)
        {
            work(NullReporter.Instance);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        long completed = 0;
        AnsiConsole.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(Columns())
            .Start(ctx =>
            {
                ProgressTask task = ctx.AddTask($"[cyan]{Markup.Escape(verb)}[/]", maxValue: Math.Max(1, max));
                var reporter = new SpectreReporter(task, verb);
                work(reporter);
                reporter.Flush(true);
                completed = reporter.Completed;
                task.Value = task.MaxValue;
                task.StopTask();
            });
        PrintDone(verb, completed, unit, stopwatch.Elapsed);
    }

    internal static async Task ProgressAsync(string verb, int max, string unit, Func<IProgressReporter, Task> work)
    {
        if (!Enabled)
        {
            await work(NullReporter.Instance).ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        long completed = 0;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(Columns())
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask($"[cyan]{Markup.Escape(verb)}[/]", maxValue: Math.Max(1, max));
                var reporter = new SpectreReporter(task, verb);
                await work(reporter).ConfigureAwait(false);
                reporter.Flush(true);
                completed = reporter.Completed;
                task.Value = task.MaxValue;
                task.StopTask();
            }).ConfigureAwait(false);
        PrintDone(verb, completed, unit, stopwatch.Elapsed);
    }

    private static void PrintDone(string verb, long completed, string unit, TimeSpan elapsed)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] [bold]{Markup.Escape(verb)}[/]: [cyan]{completed:N0}[/] {Markup.Escape(unit)} in [cyan]{elapsed.TotalSeconds:F1}s[/]");
    }

    internal static void ShowBanner()
    {
        if (!Enabled)
        {
            return;
        }
        AnsiConsole.MarkupLine($"[bold cyan]{AppInfo.Name}[/] [grey]v{Markup.Escape(AppInfo.ShortVersion)}[/]");
        AnsiConsole.MarkupLine($"[grey]Copyright (c) {Markup.Escape(AppInfo.Copyright)} under the MIT License. Third-party libraries are subject to their own licenses.[/]");
    }

    internal static void ShowFirstRun(string settingsPath)
    {
        string message =
            $"A default settings file has been created:\n[cyan]{Markup.Escape(settingsPath)}[/]\n\n" +
            "Add your blocklist and whitelist sources to it (see the README on GitHub for every setting),\n" +
            "then run HostlistDownloader again.";

        if (!Enabled)
        {
            Console.Error.WriteLine($"First run: edit {settingsPath} and run HostlistDownloader again.");
            return;
        }

        AnsiConsole.Write(new Panel(new Markup(message))
        {
            Header = new PanelHeader("[yellow] First run [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("yellow")
        });
    }

    /// <summary>Renders a table. Cell text is escaped; use <paramref name="markupColumns"/> for columns that already contain markup.</summary>
    internal static void ShowTable(string title, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows,
                                   string? caption = null, ISet<int>? rightAligned = null, ISet<int>? markupColumns = null)
    {
        if (!Enabled)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        if (!string.IsNullOrEmpty(title))
        {
            table.Title($"[bold]{Markup.Escape(title)}[/]");
        }
        if (!string.IsNullOrEmpty(caption))
        {
            table.Caption($"[grey]{Markup.Escape(caption)}[/]");
        }

        for (int i = 0; i < headers.Count; i++)
        {
            var column = new TableColumn(Markup.Escape(headers[i]));
            if (rightAligned?.Contains(i) == true)
            {
                column.RightAligned();
            }
            table.AddColumn(column);
        }

        foreach (IReadOnlyList<string> row in rows)
        {
            var cells = new string[headers.Count];
            for (int i = 0; i < headers.Count; i++)
            {
                string cell = i < row.Count ? row[i] : string.Empty;
                cells[i] = markupColumns?.Contains(i) == true ? cell : Markup.Escape(LogRedactor.Redact(cell));
            }
            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
    }

    internal static void ShowRule(string title)
    {
        if (Enabled)
        {
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]") { Justification = Justify.Left, Style = Style.Parse("grey") });
        }
    }

    /// <summary>Writes one line of report text (escaped).</summary>
    internal static void Line(string text, string style = "default")
    {
        if (Enabled)
        {
            AnsiConsole.MarkupLine($"[{style}]{Markup.Escape(text)}[/]");
        }
    }

    internal static void BlankLine()
    {
        if (Enabled)
        {
            AnsiConsole.WriteLine();
        }
    }

    internal static string Colour(string text, string style) => $"[{style}]{Markup.Escape(text)}[/]";
}
