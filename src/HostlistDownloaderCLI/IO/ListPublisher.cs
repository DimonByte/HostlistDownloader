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

using HostlistDownloader.Diagnostics;
using HostlistDownloader.Lists;
using System.Globalization;

namespace HostlistDownloader.IO;

internal sealed record PublishResult(bool Success, long Added, long Removed, string? Error);

/// <summary>
/// Moves freshly built outputs into place. Every output is fully written to a temp file first, so a failure while
/// building never touches the published lists. If one of the three moves fails, the files already replaced are
/// restored, so the three outputs never end up from different runs.
/// </summary>
internal static class ListPublisher
{
    private const string RollbackSuffix = ".rollback";
    private const string BackupSuffix = ".bak";

    internal static PublishResult Publish(OutputTargets targets, bool keepBackup)
    {
        string[] finals = [.. targets.All];
        foreach (string final in finals)
        {
            if (!File.Exists(AtomicFile.TempPathFor(final)))
            {
                DiscardTemps(targets);
                return new PublishResult(false, 0, 0, $"Expected build output {AtomicFile.TempPathFor(final)} is missing.");
            }
        }

        (long added, long removed) = ComputeDiff(targets.Combined, AtomicFile.TempPathFor(targets.Combined));

        var committed = new List<string>();
        try
        {
            foreach (string final in finals)
            {
                string temp = AtomicFile.TempPathFor(final);
                if (File.Exists(final))
                {
                    // Atomically: final -> final.rollback, temp -> final.
                    AtomicFile.TryDelete(final + RollbackSuffix);
                    File.Replace(temp, final, final + RollbackSuffix, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, final);
                }
                committed.Add(final);
                TraceLogger.Log($"Published {Path.GetFileName(final)}.", LogSeverity.Debug);
            }
        }
        catch (Exception ex)
        {
            TraceLogger.Log($"Publishing failed after {committed.Count} of {finals.Length} file(s): {ex.Message}", LogSeverity.Error);
            foreach (string final in committed)
            {
                string rollback = final + RollbackSuffix;
                try
                {
                    if (File.Exists(rollback))
                    {
                        File.Move(rollback, final, overwrite: true);
                        TraceLogger.Log($"Restored the previous {Path.GetFileName(final)}.", LogSeverity.Warn);
                    }
                }
                catch (Exception restoreEx)
                {
                    TraceLogger.Log($"Could not restore {Path.GetFileName(final)} ({restoreEx.Message}). The previous version is at {rollback}.", LogSeverity.Error);
                }
            }
            DiscardTemps(targets);
            return new PublishResult(false, 0, 0, ex.Message);
        }

        foreach (string final in finals)
        {
            string rollback = final + RollbackSuffix;
            if (!File.Exists(rollback))
            {
                continue;
            }
            try
            {
                if (keepBackup)
                {
                    File.Move(rollback, final + BackupSuffix, overwrite: true);
                }
                else
                {
                    File.Delete(rollback);
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Could not tidy up {rollback}: {ex.Message}", LogSeverity.Warn);
            }
        }

        WriteDiffStats(added, removed);
        return new PublishResult(true, added, removed, null);
    }

    internal static void DiscardTemps(OutputTargets targets)
    {
        foreach (string final in targets.All)
        {
            AtomicFile.TryDelete(AtomicFile.TempPathFor(final));
        }
    }

    /// <summary>
    /// Swaps each output with its .bak copy, so running revert twice returns to where you started instead of
    /// losing the newer version.
    /// </summary>
    internal static int Revert(OutputTargets targets)
    {
        int reverted = 0;
        foreach (string final in targets.All)
        {
            string backup = final + BackupSuffix;
            string name = Path.GetFileName(final);
            if (!File.Exists(backup))
            {
                TraceLogger.Log($"No backup found for {name}. Backups are only kept when \"allowRevert\" is true in settings.json.", LogSeverity.Warn);
                continue;
            }

            if (new FileInfo(backup).Length == 0)
            {
                TraceLogger.Log($"Backup for {name} is empty. Skipping.", LogSeverity.Warn);
                continue;
            }

            string? firstLine = File.ReadLines(backup).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstLine is not null && firstLine.TrimStart().StartsWith('<'))
            {
                TraceLogger.Log($"Backup for {name} looks like HTML, not a list. Skipping.", LogSeverity.Error);
                continue;
            }

            try
            {
                if (File.Exists(final))
                {
                    AtomicFile.TryDelete(backup + ".swap");
                    File.Replace(backup, final, backup + ".swap", ignoreMetadataErrors: true);
                    File.Move(backup + ".swap", backup, overwrite: true);
                }
                else
                {
                    File.Move(backup, final);
                }
                reverted++;
                TraceLogger.Log($"Reverted {name}. The version that was replaced is now {Path.GetFileName(backup)}, so reverting again undoes this.", LogSeverity.Notice);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to revert {name}: {ex.Message}", LogSeverity.Error);
            }
        }
        return reverted;
    }

    /// <summary>Counts entries added and removed between the previous and new combined list (header comments ignored).</summary>
    private static (long Added, long Removed) ComputeDiff(string previousPath, string newPath)
    {
        try
        {
            if (!File.Exists(previousPath))
            {
                return (CountEntries(newPath), 0);
            }

            var previous = new HashSet<string>(ReadEntries(previousPath), StringComparer.Ordinal);
            long added = 0;
            foreach (string line in ReadEntries(newPath))
            {
                if (!previous.Remove(line))
                {
                    added++;
                }
            }
            return (added, previous.Count);
        }
        catch (Exception ex)
        {
            TraceLogger.Log($"Could not compare the new list with the previous one: {ex.Message}", LogSeverity.Debug);
            return (0, 0);
        }
    }

    private static long CountEntries(string path) => ReadEntries(path).LongCount();

    internal static IEnumerable<string> ReadEntries(string path) =>
        File.ReadLines(path).Where(l => l.Length > 0 && l[0] is not ('#' or '!' or ';' or '$' or '@' or ' '));

    private static void WriteDiffStats(long added, long removed)
    {
        try
        {
            string content = string.Join(Environment.NewLine,
                $"added={added.ToString(CultureInfo.InvariantCulture)}",
                $"removed={removed.ToString(CultureInfo.InvariantCulture)}",
                $"generatedUtc={DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
            AtomicFile.WriteAllText(Paths.UpdateStatsFile, content + Environment.NewLine);
        }
        catch (Exception ex)
        {
            TraceLogger.Log($"Could not write {Paths.UpdateStatsFile}: {ex.Message}", LogSeverity.Debug);
        }
    }
}
