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
using HostlistDownloader.IO;
using HostlistDownloader.Network;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Lists;

/// <summary>A configured source resolved to its cache file.</summary>
internal sealed record SourcePlan(ListKind Kind, SourceEntry Source, Uri? Uri, string? InvalidReason, string FileName, string FilePath)
{
    internal bool IsValid => Uri is not null;

    internal string DisplayName => SourceManager.DisplayName(Source);
}

/// <summary>
/// Maps sources to cache files and keeps the cache folders tidy.
/// Cache files are named "&lt;host&gt;_&lt;hash of URL&gt;.txt", so adding, removing or disabling one source never renames
/// the others (the old "3 - hosts" numbering renamed every later file, losing their ETags and forcing re-downloads).
/// </summary>
internal static partial class SourceManager
{
    internal const string ManifestFileName = "_sources.json";
    private const int SlugLength = 40;

    internal static string DisplayName(SourceEntry source)
    {
        if (!string.IsNullOrWhiteSpace(source.Name))
        {
            return source.Name.Trim();
        }
        return Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? uri)
            ? LogRedactor.Redact(uri.GetLeftPart(UriPartial.Path))
            : source.Url;
    }

    /// <summary>Stable cache file name derived from the URL only (not from the editable display name).</summary>
    internal static string StableFileName(string url)
    {
        string key = AppConfig.SourceKey(url);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        string host = Uri.TryCreate(key, UriKind.Absolute, out Uri? uri) ? uri.Host : "source";
        return $"{Slugify(host)}_{hash}.txt";
    }

    internal static bool IsManagedFileName(string fileName) =>
        ManagedFileRegex().IsMatch(fileName) || LegacyFileRegex().IsMatch(fileName);

    /// <summary>Resolves every enabled source of one kind to its cache file, validating URLs with the shared validator.</summary>
    internal static List<SourcePlan> Plan(AppConfig config, ListKind kind)
    {
        string folder = Paths.ListFolder(kind);
        var plans = new List<SourcePlan>();
        foreach (SourceEntry source in config.EnabledSources(kind))
        {
            if (UrlValidator.TryValidateSourceUrl(source.Url, out Uri? uri, out string? reason))
            {
                string fileName = StableFileName(source.Url);
                plans.Add(new SourcePlan(kind, source, uri, null, fileName, Path.Combine(folder, fileName)));
            }
            else
            {
                plans.Add(new SourcePlan(kind, source, null, reason, string.Empty, string.Empty));
            }
        }
        return plans;
    }

    /// <summary>
    /// Writes the fileName → URL manifest for every configured source (enabled or disabled). Search and
    /// --getsource use it to show where a matched line came from.
    /// </summary>
    internal static void WriteManifest(AppConfig config, ListKind kind)
    {
        var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceEntry source in config.Sources(kind))
        {
            if (UrlValidator.TryValidateSourceUrl(source.Url, out _, out _))
            {
                manifest[StableFileName(source.Url)] = source.Url;
            }
        }

        string path = Path.Combine(Paths.ListFolder(kind), ManifestFileName);
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>(manifest), StateJsonContext.Default.DictionaryStringString));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TraceLogger.Log($"Failed to write {path}: {ex.Message}. Search results will show file names instead of URLs.", LogSeverity.Warn);
        }
    }

    internal static Dictionary<string, string> ReadManifest(ListKind kind)
    {
        string path = Path.Combine(Paths.ListFolder(kind), ManifestFileName);
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), StateJsonContext.Default.DictionaryStringString)
                    ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TraceLogger.Log($"Could not read {path}: {ex.Message}", LogSeverity.Warn);
        }
        return [];
    }

    /// <summary>
    /// One-time migration from the numbered file names used before 3.0 ("3 - hosts"): files whose URL is still
    /// configured are renamed to their stable name (keeping the cached copy and its ETag), so nothing needs to be
    /// downloaded again.
    /// </summary>
    internal static void MigrateLegacyFiles(AppConfig config, ListKind kind)
    {
        string folder = Paths.ListFolder(kind);
        Dictionary<string, string> manifest = ReadManifest(kind);
        if (manifest.Count == 0)
        {
            return;
        }

        var configured = new HashSet<string>(config.Sources(kind).Select(s => AppConfig.SourceKey(s.Url)), StringComparer.Ordinal);
        int migrated = 0;
        foreach ((string fileName, string url) in manifest)
        {
            if (!LegacyFileRegex().IsMatch(fileName) || Path.GetFileName(fileName) != fileName || !configured.Contains(AppConfig.SourceKey(url)))
            {
                continue;
            }

            string oldPath = Path.Combine(folder, fileName);
            string newPath = Path.Combine(folder, StableFileName(url));
            if (!File.Exists(oldPath) || File.Exists(newPath))
            {
                continue;
            }

            try
            {
                File.Move(oldPath, newPath);
                if (File.Exists(oldPath + ".etag"))
                {
                    File.Move(oldPath + ".etag", newPath + ".etag", overwrite: true);
                }
                migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TraceLogger.Log($"Could not rename cached {fileName}: {ex.Message}", LogSeverity.Warn);
            }
        }

        if (migrated > 0)
        {
            TraceLogger.Log($"Renamed {migrated} cached {kind.DisplayName()} file(s) to the new stable naming scheme.", LogSeverity.Notice);
        }
    }

    /// <summary>
    /// Deletes cached files for sources that are no longer in settings.json at all. Disabled sources keep their
    /// copy. Only files that match this program's naming scheme are touched, so anything else a user put in the
    /// folder is left alone, and a source that merely failed to download this run is never deleted.
    /// </summary>
    internal static void RemoveUnconfiguredFiles(AppConfig config, ListKind kind)
    {
        string folder = Paths.ListFolder(kind);
        if (!Directory.Exists(folder))
        {
            return;
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceEntry source in config.Sources(kind))
        {
            if (UrlValidator.TryValidateSourceUrl(source.Url, out _, out _))
            {
                expected.Add(StableFileName(source.Url));
            }
        }

        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(path);
            string listName = name;
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".etag", StringComparison.OrdinalIgnoreCase))
            {
                listName = name[..^5];
            }

            if (!IsManagedFileName(listName) || expected.Contains(listName))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                TraceLogger.Log($"Removed cached file for a source no longer in settings.json: {name}", LogSeverity.Debug);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TraceLogger.Log($"Could not remove {name}: {ex.Message}", LogSeverity.Warn);
            }
        }
    }

    /// <summary>Looks up the URL for a cache file name in either manifest. Returns null if unknown.</summary>
    internal static (string Url, ListKind Kind)? FindSourceForFile(string fileName)
    {
        foreach (ListKind kind in new[] { ListKind.Block, ListKind.Allow })
        {
            if (ReadManifest(kind).TryGetValue(fileName, out string? url))
            {
                return (url, kind);
            }
        }
        return null;
    }

    private static string Slugify(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, SlugLength));
        bool lastDash = false;
        foreach (char raw in text.ToLowerInvariant())
        {
            char c = raw;
            bool ok = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '.';
            if (!ok)
            {
                if (lastDash || sb.Length == 0)
                {
                    continue;
                }
                c = '-';
            }
            lastDash = c == '-';
            sb.Append(c);
            if (sb.Length >= SlugLength)
            {
                break;
            }
        }
        string slug = sb.ToString().Trim('-', '.');
        return slug.Length == 0 ? "source" : slug;
    }

    [GeneratedRegex(@"^[a-z0-9.\-]{1,40}_[0-9a-f]{10}\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedFileRegex();

    [GeneratedRegex(@"^\d+ - .+$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyFileRegex();
}
