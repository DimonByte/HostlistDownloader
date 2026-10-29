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

namespace HostlistDownloader.IO;

/// <summary>Cache validators and details stored next to each downloaded list as "&lt;file&gt;.meta".</summary>
internal sealed record SourceMetadata
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("downloadedUtc")]
    public DateTimeOffset? DownloadedUtc { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    internal bool HasValidator => !string.IsNullOrEmpty(ETag) || LastModified is not null;

    internal static string MetaPathFor(string listPath) => listPath + ".meta";

    private static string LegacyETagPathFor(string listPath) => listPath + ".etag";

    /// <summary>Loads metadata for a cached list, falling back to the .etag file written by older versions.</summary>
    internal static SourceMetadata? Load(string listPath)
    {
        string metaPath = MetaPathFor(listPath);
        try
        {
            if (File.Exists(metaPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(metaPath), StateJsonContext.Default.SourceMetadata);
            }

            string legacy = LegacyETagPathFor(listPath);
            if (File.Exists(legacy))
            {
                string etag = File.ReadAllText(legacy).Trim();
                return etag.Length == 0 ? null : new SourceMetadata { ETag = etag };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable metadata just means the next download is unconditional.
        }
        return null;
    }

    internal static void Save(string listPath, SourceMetadata metadata)
    {
        AtomicFile.WriteAllText(MetaPathFor(listPath), JsonSerializer.Serialize(metadata, StateJsonContext.Default.SourceMetadata));
        AtomicFile.TryDelete(LegacyETagPathFor(listPath));
    }

    internal static void Delete(string listPath)
    {
        AtomicFile.TryDelete(MetaPathFor(listPath));
        AtomicFile.TryDelete(LegacyETagPathFor(listPath));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SourceMetadata))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class StateJsonContext : JsonSerializerContext
{
}
