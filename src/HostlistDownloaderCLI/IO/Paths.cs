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

namespace HostlistDownloader.IO;

/// <summary>
/// All file locations, rooted at the data directory. Paths are absolute, so the program no longer changes the
/// process working directory (which broke relative paths passed on the command line).
/// </summary>
internal static class Paths
{
    internal const string SettingsFileName = "settings.json";
    internal const string PortableMarkerFileName = "portable.txt";
    internal const string DataDirEnvironmentVariable = "HLD_DATA_DIR";

    // Output file names are a public contract: DNS servers and filtering tools point at them.
    internal const string CombinedBlockListFileName = "HLDcombined-blocklist.txt";
    internal const string CombinedWhiteListFileName = "HLDcombined-whitelist.txt";
    internal const string CombinedListFileName = "HLDcombined-list.txt";
    internal const string CombinedFilePrefix = "HLDcombined-";

    private static string? _root;

    internal static bool IsInitialized => _root is not null;

    internal static string Root => _root ?? throw new InvalidOperationException("Paths.Initialize has not been called.");

    internal static string SettingsFile => Path.Combine(Root, SettingsFileName);
    internal static string LogsFolder => Path.Combine(Root, "logs");
    internal static string UpdateStatsFile => Path.Combine(LogsFolder, "updatestats.txt");
    internal static string StateFolder => Path.Combine(Root, "state");
    internal static string UpdateCheckStampFile => Path.Combine(StateFolder, "last-update-check.txt");
    internal static string BuildStampFile => Path.Combine(StateFolder, "last-build.txt");
    internal static string LockFile => Path.Combine(StateFolder, "hld.lock");
    internal static string DisabledMarkerFile => Path.Combine(StateFolder, "lists-disabled.txt");
    internal const string DisabledSuffix = ".disabled";

    /// <summary>True after --disable: the published files are empty and real output goes to the .disabled copies.</summary>
    internal static bool ListsDisabled => File.Exists(DisabledMarkerFile);
    internal static string HostfilesFolder => Path.Combine(Root, "hostfiles");
    internal static string BlockListFolder => Path.Combine(HostfilesFolder, "blocklist");
    internal static string WhiteListFolder => Path.Combine(HostfilesFolder, "whitelist");
    internal static string CombinedFolder => Path.Combine(HostfilesFolder, "combined");
    internal static string CombinedBlockListFile => Path.Combine(CombinedFolder, CombinedBlockListFileName);
    internal static string CombinedWhiteListFile => Path.Combine(CombinedFolder, CombinedWhiteListFileName);
    internal static string CombinedListFile => Path.Combine(CombinedFolder, CombinedListFileName);

    internal static string[] CombinedOutputFiles => [CombinedBlockListFile, CombinedWhiteListFile, CombinedListFile];

    internal static string ListFolder(ListKind kind) => kind == ListKind.Block ? BlockListFolder : WhiteListFolder;

    internal static void Initialize(string? overrideDirectory)
    {
        _root = ResolveDataDirectory(overrideDirectory);
    }

    /// <summary>
    /// Resolution order:
    /// 1. --data-dir argument (relative paths are relative to the caller's working directory).
    /// 2. HLD_DATA_DIR environment variable.
    /// 3. The executable's folder, if settings.json or portable.txt is already there (existing installs keep working).
    /// 4. The per-user application data folder (%LOCALAPPDATA%\HostlistDownloader, ~/.local/share/hostlistdownloader,
    ///    ~/Library/Application Support/HostlistDownloader), which works when installed under Program Files.
    /// </summary>
    internal static string ResolveDataDirectory(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        string exeDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(exeDirectory, SettingsFileName)) || File.Exists(Path.Combine(exeDirectory, PortableMarkerFileName)))
        {
            return exeDirectory;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return exeDirectory;
        }

        return Path.Combine(appData, OperatingSystem.IsLinux() ? "hostlistdownloader" : "HostlistDownloader");
    }

    /// <summary>
    /// True for files the program creates for its own bookkeeping inside a list folder (manifest, metadata,
    /// partial downloads, legacy combined/etag files). Matches on the file NAME only, never the full path.
    /// </summary>
    internal static bool IsInternalFile(string fileName)
    {
        return fileName.Equals("_sources.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".etag", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(CombinedFilePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
