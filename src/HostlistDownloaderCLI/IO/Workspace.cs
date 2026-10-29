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

using HostlistDownloader.Config;
using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;

namespace HostlistDownloader.IO;

/// <summary>Creates the data folder layout and cleans up leftovers from interrupted or older runs.</summary>
internal static class Workspace
{
    private static readonly string[] LegacyTempFiles =
    [
        "HLDcombined-blocklist-TEMP.txt",
        "HLDcombined-whitelist-TEMP.txt",
        "HLDcombined-list-TEMP.txt"
    ];

    /// <summary>
    /// Ensures all folders exist. Returns true when settings.json did not exist and a default one was created
    /// (first run). A missing logs or hostfiles folder on its own is simply recreated.
    /// </summary>
    internal static bool EnsureCreated()
    {
        string[] directories =
        [
            Paths.Root,
            Paths.LogsFolder,
            Paths.StateFolder,
            Paths.HostfilesFolder,
            Paths.BlockListFolder,
            Paths.WhiteListFolder,
            Paths.CombinedFolder
        ];

        foreach (string directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw TraceLogger.Fatal($"Could not create directory {directory}: {ex.Message}", ExitCodes.DirectoryCreationFailed, ex);
            }
        }

        if (File.Exists(Paths.SettingsFile))
        {
            return false;
        }

        AppConfig.CreateDefaultFile(Paths.SettingsFile);
        TraceLogger.Log($"Created default settings at {Paths.SettingsFile}.", LogSeverity.Notice);
        return true;
    }

    /// <summary>
    /// Deletes partial downloads, temp outputs and the TEMP lists used by versions before 3.0.
    /// Only call this while holding the <see cref="RunLock"/>, otherwise another run's temp files could be removed.
    /// </summary>
    internal static void RemoveLeftovers()
    {
        foreach (string legacy in LegacyTempFiles)
        {
            AtomicFile.TryDelete(Path.Combine(Paths.CombinedFolder, legacy));
        }

        foreach (string folder in new[] { Paths.BlockListFolder, Paths.WhiteListFolder, Paths.CombinedFolder, Paths.StateFolder, Paths.Root })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) || name.EndsWith(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    AtomicFile.TryDelete(file);
                }
            }
        }
    }
}

/// <summary>
/// Prevents two runs (for example an overlapping scheduled task) from writing the same data folder at once.
/// </summary>
internal sealed class RunLock : IDisposable
{
    private readonly FileStream _stream;

    private RunLock(FileStream stream) => _stream = stream;

    internal static RunLock Acquire()
    {
        try
        {
            var stream = new FileStream(Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new RunLock(stream);
        }
        catch (IOException ex)
        {
            throw TraceLogger.Fatal($"Another HostlistDownloader process is already using {Paths.Root}. Wait for it to finish and try again.", ExitCodes.AlreadyRunning, ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
