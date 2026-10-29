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
using HostlistDownloader.IO;
using HostlistDownloader.Lists;
using System.Net;
using System.Text.Json;

namespace HostlistDownloader.Config;

internal enum ConfigIssueSeverity
{
    Warning,
    Error
}

internal sealed record ConfigIssue(ConfigIssueSeverity Severity, string Setting, string Message);

/// <summary>
/// Immutable, validated view of settings.json. Constructing an instance has no side effects; only
/// <see cref="SetCurrent"/> changes the configuration the rest of the program uses.
/// </summary>
internal sealed class AppConfig
{
    private static AppConfig? _current;
    private readonly List<ConfigIssue> _issues = [];

    internal static AppConfig Current => _current ?? throw new InvalidOperationException("Configuration has not been loaded yet.");

    internal static bool IsLoaded => _current is not null;

    internal static void SetCurrent(AppConfig config) => _current = config;

    /// <summary>The settings exactly as they will be saved (out-of-range values are kept so the user can see and fix them).</summary>
    internal SettingsFile Settings { get; }

    internal IReadOnlyList<SourceEntry> Blocklists { get; }
    internal IReadOnlyList<SourceEntry> Whitelists { get; }
    internal IReadOnlyList<string> UserBlocklist { get; }
    internal IReadOnlyList<string> UserWhitelist { get; }
    internal IReadOnlyList<string> NeverBlock { get; }
    internal OutputFormat Format { get; }
    internal int MaxConcurrentDownloads { get; }
    internal int LogExpiryInDays { get; }
    internal bool AllowInsecureSources { get; }
    internal long MaxListSizeInMB { get; }
    internal bool AllowRevert { get; }
    internal bool CheckForUpdates { get; }
    internal int UpdateCheckIntervalHours { get; }
    internal NetworkSettings Network { get; }
    internal OutputSettings Output { get; }
    internal SanitySettings Sanity { get; }
    internal IPAddress HostsBlockIp { get; }
    internal string NewLine { get; }
    internal Uri? ProxyUri { get; }
    internal IReadOnlyList<ConfigIssue> Issues => _issues;
    internal bool HasErrors => _issues.Exists(i => i.Severity == ConfigIssueSeverity.Error);

    internal AppConfig(SettingsFile settings)
    {
        // JSON "null" bypasses the property initialisers, so normalise here once.
        settings = settings with
        {
            Blocklists = CleanSources(settings.Blocklists),
            Whitelists = CleanSources(settings.Whitelists),
            FormatType = settings.FormatType ?? "domain",
            UserWebsiteBlocklist = CleanStrings(settings.UserWebsiteBlocklist),
            UserWebsiteWhitelist = CleanStrings(settings.UserWebsiteWhitelist),
            NeverBlock = CleanStrings(settings.NeverBlock),
            Network = settings.Network ?? new NetworkSettings(),
            Output = settings.Output ?? new OutputSettings(),
            SourceSanityCheck = settings.SourceSanityCheck ?? new SanitySettings()
        };
        Settings = settings;

        Blocklists = settings.Blocklists;
        Whitelists = settings.Whitelists;
        UserBlocklist = settings.UserWebsiteBlocklist;
        UserWhitelist = settings.UserWebsiteWhitelist;
        NeverBlock = settings.NeverBlock;
        AllowInsecureSources = settings.AllowInsecureSources;
        AllowRevert = settings.AllowRevert;
        CheckForUpdates = settings.CheckForUpdates;

        if (settings.Version > SettingsFile.CurrentVersion)
        {
            AddIssue(ConfigIssueSeverity.Warning, "version",
                $"settings.json is version {settings.Version}, newer than this program understands ({SettingsFile.CurrentVersion}). Some settings may be ignored.");
        }

        if (OutputFormats.TryParse(settings.FormatType, out OutputFormat format))
        {
            Format = format;
        }
        else
        {
            Format = OutputFormat.Domain;
            AddIssue(ConfigIssueSeverity.Error, "formattype",
                $"Unknown formattype \"{settings.FormatType}\". Allowed values: {OutputFormats.AllowedValuesText}.");
        }

        MaxConcurrentDownloads = Clamp("maxConcurrentDownloads", settings.MaxConcurrentDownloads, 1, 25);
        LogExpiryInDays = Clamp("logExpiryInDays", settings.LogExpiryInDays, 0, 3650);
        MaxListSizeInMB = Clamp("maxListSizeInMB", settings.MaxListSizeInMB, 1, 1000);
        UpdateCheckIntervalHours = Clamp("updateCheckIntervalHours", settings.UpdateCheckIntervalHours, 1, 24 * 90);

        NetworkSettings network = settings.Network;
        Network = network with
        {
            ConnectTimeoutSeconds = Clamp("network.connectTimeoutSeconds", network.ConnectTimeoutSeconds, 1, 300),
            ResponseTimeoutSeconds = Clamp("network.responseTimeoutSeconds", network.ResponseTimeoutSeconds, 1, 600),
            InactivityTimeoutSeconds = Clamp("network.inactivityTimeoutSeconds", network.InactivityTimeoutSeconds, 1, 3600),
            MaxRetries = Clamp("network.maxRetries", network.MaxRetries, 1, 10)
        };

        if (!string.IsNullOrWhiteSpace(network.Proxy))
        {
            if (Uri.TryCreate(network.Proxy, UriKind.Absolute, out Uri? proxy) && (proxy.Scheme == Uri.UriSchemeHttp || proxy.Scheme == Uri.UriSchemeHttps))
            {
                ProxyUri = proxy;
            }
            else
            {
                AddIssue(ConfigIssueSeverity.Error, "network.proxy", $"\"{network.Proxy}\" is not a valid http:// or https:// proxy URL.");
            }
        }

        OutputSettings output = settings.Output;
        Output = output;
        if (IPAddress.TryParse(output.HostsBlockIp, out IPAddress? blockIp))
        {
            HostsBlockIp = blockIp;
        }
        else
        {
            HostsBlockIp = IPAddress.Any;
            AddIssue(ConfigIssueSeverity.Warning, "output.hostsBlockIp", $"\"{output.HostsBlockIp}\" is not an IP address. Using 0.0.0.0.");
        }

        switch ((output.LineEnding ?? "lf").Trim().ToLowerInvariant())
        {
            case "lf":
                NewLine = "\n";
                break;
            case "crlf":
                NewLine = "\r\n";
                break;
            case "native":
                NewLine = Environment.NewLine;
                break;
            default:
                NewLine = "\n";
                AddIssue(ConfigIssueSeverity.Warning, "output.lineEnding", $"Unknown lineEnding \"{output.LineEnding}\". Use \"lf\", \"crlf\" or \"native\". Using \"lf\".");
                break;
        }

        SanitySettings sanity = settings.SourceSanityCheck;
        Sanity = sanity with
        {
            MaxShrinkPercent = Clamp("sourceSanityCheck.maxShrinkPercent", sanity.MaxShrinkPercent, 1, 100),
            MaxGrowPercent = Clamp("sourceSanityCheck.maxGrowPercent", sanity.MaxGrowPercent, 1, 100_000),
            MinimumBytes = Clamp("sourceSanityCheck.minimumBytes", sanity.MinimumBytes, 0, long.MaxValue)
        };

        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SourceEntry source in Sources(kind))
            {
                if (!seen.Add(SourceKey(source.Url)))
                {
                    AddIssue(ConfigIssueSeverity.Warning, kind == ListKind.Block ? "blocklists" : "whitelists",
                        $"Duplicate source {source.Url} is listed more than once; it will only be downloaded once.");
                }
            }
        }
    }

    internal IReadOnlyList<SourceEntry> Sources(ListKind kind) => kind == ListKind.Block ? Blocklists : Whitelists;

    /// <summary>Enabled sources of one kind with duplicate URLs removed (first occurrence wins).</summary>
    internal IReadOnlyList<SourceEntry> EnabledSources(ListKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SourceEntry>();
        foreach (SourceEntry source in Sources(kind))
        {
            if (source.Enabled && seen.Add(SourceKey(source.Url)))
            {
                result.Add(source);
            }
        }
        return result;
    }

    internal IReadOnlyList<string> UserEntries(ListKind kind) => kind == ListKind.Block ? UserBlocklist : UserWhitelist;

    /// <summary>
    /// Key used to decide whether two URLs are the same source. Scheme and host are case-insensitive, but the
    /// path and query are compared exactly because they are case-sensitive on GitHub raw and most other hosts.
    /// </summary>
    internal static string SourceKey(string url)
    {
        string trimmed = url.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ? uri.AbsoluteUri : trimmed;
    }

    internal AppConfig With(Func<SettingsFile, SettingsFile> change) => new(change(Settings));

    internal void Save(string path)
    {
        string json = JsonSerializer.Serialize(Settings, SettingsJsonContext.Default.SettingsFile);
        AtomicFile.WriteAllText(path, json + Environment.NewLine);
    }

    internal static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw TraceLogger.Fatal($"Settings file not found: {path}", ExitCodes.FileMissing);
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw TraceLogger.Fatal($"Could not read {path}: {ex.Message}", ExitCodes.FileMissing, ex);
        }

        if (SettingsMigrator.TryMigrate(json, out string migrated, out List<string> notes))
        {
            foreach (string note in notes)
            {
                TraceLogger.Log(note, LogSeverity.Notice);
            }

            string backup = path + ".v1.bak";
            try
            {
                File.Copy(path, backup, overwrite: true);
                AtomicFile.WriteAllText(path, migrated + Environment.NewLine);
                TraceLogger.Log($"The previous settings.json was saved as {backup}.", LogSeverity.Notice);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Could not save the upgraded settings.json ({ex.Message}). Continuing with the upgraded settings in memory.", LogSeverity.Warn);
            }
            json = migrated;
        }

        SettingsFile? settings;
        try
        {
            settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsFile);
        }
        catch (JsonException ex)
        {
            throw TraceLogger.Fatal(
                $"settings.json is not valid: {ex.Message} Check for typos in setting names and for missing commas or quotes.",
                ExitCodes.ConfigurationCorrupted, ex);
        }

        if (settings is null)
        {
            throw TraceLogger.Fatal("settings.json is empty.", ExitCodes.ConfigurationCorrupted);
        }

        var config = new AppConfig(settings);
        TraceLogger.Log(
            $"Loaded settings: {config.EnabledSources(ListKind.Block).Count} blocklist(s), {config.EnabledSources(ListKind.Allow).Count} whitelist(s), " +
            $"format {config.Format.CanonicalName()}, {config.UserBlocklist.Count} user block(s), {config.UserWhitelist.Count} user allow(s), " +
            $"{config.MaxConcurrentDownloads} concurrent download(s).", LogSeverity.Debug);
        return config;
    }

    internal static void CreateDefaultFile(string path)
    {
        var defaults = new SettingsFile
        {
            NeverBlock =
            [
                "windowsupdate.com",
                "update.microsoft.com",
                "delivery.mp.microsoft.com",
                "swscan.apple.com",
                "mesu.apple.com",
                "updates.cdn-apple.com",
                "security.ubuntu.com",
                "archive.ubuntu.com",
                "deb.debian.org",
                "security.debian.org"
            ]
        };

        try
        {
            new AppConfig(defaults).Save(path);
        }
        catch (Exception ex)
        {
            throw TraceLogger.Fatal($"Failed to create default settings at {path}: {ex.Message}", ExitCodes.GeneralError, ex);
        }
    }

    private int Clamp(string setting, int value, int min, int max) => (int)Clamp(setting, (long)value, min, max);

    private long Clamp(string setting, long value, long min, long max)
    {
        if (value < min)
        {
            AddIssue(ConfigIssueSeverity.Warning, setting, $"{setting} is {value}, below the minimum of {min}. Using {min}.");
            return min;
        }
        if (value > max)
        {
            AddIssue(ConfigIssueSeverity.Warning, setting, $"{setting} is {value}, above the maximum of {max}. Using {max}.");
            return max;
        }
        return value;
    }

    private void AddIssue(ConfigIssueSeverity severity, string setting, string message) =>
        _issues.Add(new ConfigIssue(severity, setting, message));

    private static SourceEntry[] CleanSources(SourceEntry[]? sources) =>
        sources is null
            ? []
            : [.. sources.Where(s => s is not null).Select(s => s with { Url = (s.Url ?? string.Empty).Trim() })];

    private static string[] CleanStrings(string[]? values) =>
        values is null ? [] : [.. values.Where(v => v is not null).Select(v => v.Trim()).Where(v => v.Length > 0)];
}
