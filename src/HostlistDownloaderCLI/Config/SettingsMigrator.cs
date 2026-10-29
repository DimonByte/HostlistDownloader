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
using System.Text.Json.Nodes;

namespace HostlistDownloader.Config;

/// <summary>
/// Upgrades older settings.json layouts to the current schema so existing installs keep working.
/// Version 1 (no "version" property): sources were bare strings, a leading '#' meant "commented out",
/// "whitelist" was singular and "maxDownloadThreads" named the concurrency limit.
/// </summary>
internal static class SettingsMigrator
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Returns true and the upgraded JSON when <paramref name="json"/> uses an older schema. Returns false when the
    /// file is already current or is not a JSON object (the strict deserializer then reports the problem).
    /// </summary>
    internal static bool TryMigrate(string json, out string migratedJson, out List<string> notes)
    {
        migratedJson = json;
        notes = [];

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json, nodeOptions: null, documentOptions: ReadOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject root)
        {
            return false;
        }

        int version = 1;
        if (root["version"] is JsonValue versionValue && versionValue.TryGetValue(out int parsedVersion))
        {
            version = parsedVersion;
        }

        if (version >= SettingsFile.CurrentVersion)
        {
            return false;
        }

        var upgraded = new JsonObject
        {
            ["version"] = SettingsFile.CurrentVersion
        };

        foreach (KeyValuePair<string, JsonNode?> property in root)
        {
            string name = property.Key;
            JsonNode? value = property.Value?.DeepClone();

            switch (name)
            {
                case "version":
                    break;

                case "blocklists":
                    upgraded["blocklists"] = ConvertSources(value, "blocklists", notes);
                    break;

                case "whitelist":
                case "whitelists":
                    upgraded["whitelists"] = ConvertSources(value, "whitelists", notes);
                    if (name == "whitelist")
                    {
                        notes.Add("Renamed \"whitelist\" to \"whitelists\".");
                    }
                    break;

                case "maxDownloadThreads":
                    upgraded["maxConcurrentDownloads"] = value;
                    notes.Add("Renamed \"maxDownloadThreads\" to \"maxConcurrentDownloads\".");
                    break;

                default:
                    upgraded[name] = value;
                    break;
            }
        }

        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                upgraded.WriteTo(writer);
            }
            migratedJson = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        notes.Insert(0, $"Upgraded settings.json from version {version} to version {SettingsFile.CurrentVersion}.");
        return true;
    }

    private static JsonArray ConvertSources(JsonNode? node, string listName, List<string> notes)
    {
        var result = new JsonArray();
        if (node is not JsonArray array)
        {
            return result;
        }

        int converted = 0;
        int disabled = 0;
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && text is not null)
            {
                text = text.Trim();
                bool enabled = !text.StartsWith('#');
                string url = text.TrimStart('#').Trim();
                if (url.Length == 0)
                {
                    continue;
                }

                var entry = new JsonObject { ["url"] = url };
                if (!enabled)
                {
                    entry["enabled"] = false;
                    disabled++;
                }
                result.Add((JsonNode)entry); // the JsonNode overload; Add<T> is not trim-safe
                converted++;
            }
            else if (item is JsonObject obj)
            {
                result.Add(obj.DeepClone());
            }
        }

        if (converted > 0)
        {
            notes.Add($"Converted {converted} {listName} entr{(converted == 1 ? "y" : "ies")} to objects" +
                      (disabled > 0 ? $" ({disabled} commented-out entr{(disabled == 1 ? "y is" : "ies are")} now \"enabled\": false)." : "."));
        }
        return result;
    }
}
