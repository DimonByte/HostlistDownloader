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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostlistDownloader.Config;

/// <summary>One downloadable list.</summary>
internal sealed record SourceEntry
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Disabled sources are kept in settings.json (and their cached copy on disk) but not downloaded or merged.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Optional friendly name, used in reports and in the cached file name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Optional per-source size limit, overriding maxListSizeInMB.</summary>
    [JsonPropertyName("maxSizeMB")]
    public long? MaxSizeMB { get; set; }

    /// <summary>Optional expected SHA-256 (hex) of the downloaded file, for pinned lists that never change.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

internal sealed record NetworkSettings
{
    /// <summary>Time allowed to open the TCP/TLS connection.</summary>
    [JsonPropertyName("connectTimeoutSeconds")]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Time allowed for the server to start responding (headers received).</summary>
    [JsonPropertyName("responseTimeoutSeconds")]
    public int ResponseTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum time without receiving any data while downloading the body. Large lists on slow links are fine as long as data keeps flowing.</summary>
    [JsonPropertyName("inactivityTimeoutSeconds")]
    public int InactivityTimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Overrides the default "HostlistDownloader/version" User-Agent.</summary>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Optional HTTP proxy, e.g. "http://proxy.example:8080". When unset, connections are made directly and the
    /// system proxy is ignored, so the SSRF protection can see the real destination address.
    /// </summary>
    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }
}

internal sealed record OutputSettings
{
    /// <summary>Sort entries so the output is stable and diffs between runs are meaningful.</summary>
    [JsonPropertyName("sort")]
    public bool Sort { get; set; } = true;

    /// <summary>"lf" (default, what Linux resolvers expect), "crlf", or "native".</summary>
    [JsonPropertyName("lineEnding")]
    public string LineEnding { get; set; } = "lf";

    /// <summary>Write a comment header (timestamp, entry count, sources) at the top of each output file.</summary>
    [JsonPropertyName("header")]
    public bool Header { get; set; } = true;

    /// <summary>Address used for every entry in hosts format.</summary>
    [JsonPropertyName("hostsBlockIp")]
    public string HostsBlockIp { get; set; } = "0.0.0.0";

    /// <summary>Drop sub.example.com when example.com is already blocked. Only applied to formats that block subdomains (adblock, dnsmasq, unbound).</summary>
    [JsonPropertyName("compress")]
    public bool Compress { get; set; }

    /// <summary>Drop entries that are not valid domain names (label/length/character rules).</summary>
    [JsonPropertyName("validateDomains")]
    public bool ValidateDomains { get; set; } = true;

    /// <summary>Drop bare TLDs and common public suffixes (com, co.uk) that would block an entire namespace.</summary>
    [JsonPropertyName("rejectPublicSuffixes")]
    public bool RejectPublicSuffixes { get; set; } = true;
}

/// <summary>Rejects a new download that differs too much in size from the cached copy (broken or hijacked source).</summary>
internal sealed record SanitySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("maxShrinkPercent")]
    public int MaxShrinkPercent { get; set; } = 50;

    [JsonPropertyName("maxGrowPercent")]
    public int MaxGrowPercent { get; set; } = 1000;

    /// <summary>Cached copies smaller than this are not checked (tiny lists legitimately change a lot).</summary>
    [JsonPropertyName("minimumBytes")]
    public long MinimumBytes { get; set; } = 10_240;
}

/// <summary>
/// Mirrors settings.json exactly. Properties use "set" rather than "init" on purpose: the System.Text.Json source
/// generator assigns every init-only property (missing ones become null/0/false), which would discard the defaults below.
/// <para> Unknown properties are rejected when reading so typos are reported instead of
/// silently ignored; comments and trailing commas are allowed because the file is edited by hand.
/// </para>
/// </summary>
internal sealed record SettingsFile
{
    internal const int CurrentVersion = 2;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("blocklists")]
    public SourceEntry[] Blocklists { get; set; } = [];

    [JsonPropertyName("whitelists")]
    public SourceEntry[] Whitelists { get; set; } = [];

    [JsonPropertyName("formattype")]
    public string FormatType { get; set; } = "domain";

    [JsonPropertyName("userWebsiteBlocklist")]
    public string[] UserWebsiteBlocklist { get; set; } = [];

    [JsonPropertyName("userWebsiteWhitelist")]
    public string[] UserWebsiteWhitelist { get; set; } = [];

    /// <summary>Domains that must never be blocked, whatever a source says (OS updates, your own services).</summary>
    [JsonPropertyName("neverBlock")]
    public string[] NeverBlock { get; set; } = [];

    [JsonPropertyName("maxConcurrentDownloads")]
    public int MaxConcurrentDownloads { get; set; } = 4;

    [JsonPropertyName("logExpiryInDays")]
    public int LogExpiryInDays { get; set; } = 7;

    [JsonPropertyName("allowInsecureSources")]
    public bool AllowInsecureSources { get; set; }

    [JsonPropertyName("maxListSizeInMB")]
    public long MaxListSizeInMB { get; set; } = 100;

    [JsonPropertyName("allowRevert")]
    public bool AllowRevert { get; set; }

    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; set; } = true;

    [JsonPropertyName("updateCheckIntervalHours")]
    public int UpdateCheckIntervalHours { get; set; } = 24;

    [JsonPropertyName("network")]
    public NetworkSettings Network { get; set; } = new();

    [JsonPropertyName("output")]
    public OutputSettings Output { get; set; } = new();

    [JsonPropertyName("sourceSanityCheck")]
    public SanitySettings SourceSanityCheck { get; set; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsFile))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
