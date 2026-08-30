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

using HostlistDownloader.Modules.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostlistDownloader.Modules.WindowsSystem
{
    /// <summary>
    /// POCO that mirrors the JSON format exactly.
    /// </summary>
    [JsonSerializable(typeof(Settings))]
    internal partial class SettingsJsonSerializerContext : JsonSerializerContext
    {
    }

    internal sealed record Settings(
        [property: JsonPropertyName("blocklists")] string[]? Blocklists = null,
        [property: JsonPropertyName("whitelist")] string[]? Whitelist = null,
        [property: JsonPropertyName("formattype")] string? Formattype = null,
        [property: JsonPropertyName("userWebsiteBlocklist")] string[]? UserWebsiteBlocklist = null,
        [property: JsonPropertyName("userWebsiteWhitelist")] string[]? UserWebsiteWhitelist = null,
        [property: JsonPropertyName("maxDownloadThreads")] int? MaxDownloadThreads = null,
        [property: JsonPropertyName("logExpiryInDays")] int? LogExpiryInDays = null,
        [property: JsonPropertyName("allowInsecureSources")] bool? AllowInsecureSources = null,
        [property: JsonPropertyName("maxListSizeInMB")] long? MaxListSizeInMB = null,
        [property: JsonPropertyName("allowRevert")] bool? AllowRevert = null
        )
    {
        private Settings() : this(null) { }
    }

    /// <summary>
    /// Public, read‑only view of the configuration.
    /// To modify configuration, use AddEntry or RemoveEntry which returns a NEW instance.
    /// </summary>
    public sealed class ConfigManager
    {
        // Singleton instance for the global config
        internal static ConfigManager Instance => _instance ?? throw new InvalidOperationException("ConfigReader not yet initialised.");

        private static ConfigManager? _instance;

        // Properties are now immutable (init-only setters)
        public IReadOnlyList<string> Blocklists { get; init; }
        public IReadOnlyList<string> Whitelist { get; init; }
        public string Formattype { get; init; }
        public IReadOnlyList<string> UserWebsiteBlocklist { get; init; }
        public IReadOnlyList<string> UserWebsiteWhitelist { get; init; }
        public int MaxDownloadThreads { get; init; } = 4;
        public int LogExpiryInDays { get; init; } = 7;
        public bool AllowInsecureSources { get; init; } = false;
        public long MaxListSizeInMB { get; init; } = 100;
        public bool AllowRevert { get; init; } = false;

        internal ConfigManager(Settings raw)
        {
            Blocklists = Array.AsReadOnly(raw.Blocklists ?? []);
            Whitelist = Array.AsReadOnly(raw.Whitelist ?? []);
            Formattype = !string.IsNullOrWhiteSpace(raw.Formattype) ? raw.Formattype : "domain";
            UserWebsiteBlocklist = Array.AsReadOnly(raw.UserWebsiteBlocklist ?? []);
            UserWebsiteWhitelist = Array.AsReadOnly(raw.UserWebsiteWhitelist ?? []);

            if (raw.MaxDownloadThreads.HasValue)
                MaxDownloadThreads = raw.MaxDownloadThreads.Value;

            if (raw.MaxListSizeInMB.HasValue)
                MaxListSizeInMB = raw.MaxListSizeInMB.Value;
            if (MaxListSizeInMB < 1)
            {
                TraceLogger.Log($"Configured maxListSizeInMB ({MaxListSizeInMB}) is invalid. Falling back to 100.", Enums.StatusSeverityType.Warning);
                MaxListSizeInMB = 100;
            }
            if (MaxListSizeInMB > 1000)
            {
                TraceLogger.Log($"Configured maxListSizeInMB ({MaxListSizeInMB}) is unreasonably high and may cause performance issues. Clamping to 1000.", Enums.StatusSeverityType.Warning);
                MaxListSizeInMB = 1000;
            }
            if (raw.AllowRevert.HasValue)
                AllowRevert = raw.AllowRevert.Value;

            // Validation Logic
            if (MaxDownloadThreads < 1)
            {
                TraceLogger.Log($"Configured maxDownloadThreads ({MaxDownloadThreads}) is invalid. Falling back to 1.", Enums.StatusSeverityType.Warning);
                MaxDownloadThreads = 1;
            }
            else if (MaxDownloadThreads > 25)
            {
                TraceLogger.Log($"Configured maxDownloadThreads ({MaxDownloadThreads}) is unreasonably high and may get you rate-limited or blocked. Clamping to 25.", Enums.StatusSeverityType.Warning);
                MaxDownloadThreads = 25;
            }

            if (raw.LogExpiryInDays.HasValue)
                LogExpiryInDays = raw.LogExpiryInDays.Value;

            if (LogExpiryInDays < 0)
            {
                TraceLogger.Log($"Configured logExpiryInDays ({LogExpiryInDays}) is invalid. Falling back to 7.", Enums.StatusSeverityType.Warning);
                LogExpiryInDays = 7;
            }

            if (raw.AllowInsecureSources.HasValue)
                AllowInsecureSources = raw.AllowInsecureSources.Value;

            _instance = this;
        }

        /// <summary>
        /// Creates a new ConfigManager instance with an added entry to the specified list.
        /// </summary>
        public ConfigManager AddEntry(string targetList, string entry)
        {
            var settings = new Settings(
                Blocklists: HandleListUpdate(Blocklists, entry, targetList == "blocklists"),
                Whitelist: HandleListUpdate(Whitelist, entry, targetList == "whitelist"),
                Formattype: Formattype,
                UserWebsiteBlocklist: HandleListUpdate(UserWebsiteBlocklist, entry, targetList == "userWebsiteBlocklist"),
                UserWebsiteWhitelist: HandleListUpdate(UserWebsiteWhitelist, entry, targetList == "userWebsiteWhitelist"),
                MaxDownloadThreads: MaxDownloadThreads,
                LogExpiryInDays: LogExpiryInDays,
                AllowInsecureSources: AllowInsecureSources,
                MaxListSizeInMB: MaxListSizeInMB,
                AllowRevert: AllowRevert
            );

            // Create new instance to update the singleton if this is the global reader
            var newReader = new ConfigManager(settings);

            if (_instance == this)
            {
                _instance = newReader;
            }

            return newReader;
        }

        /// <summary>
        /// Creates a new ConfigManager instance with an removed entry from the specified list.
        /// </summary>
        public ConfigManager RemoveEntry(string targetList, string entry)
        {
            var settings = new Settings(
                Blocklists: HandleListRemove(Blocklists, entry, targetList == "blocklists"),
                Whitelist: HandleListRemove(Whitelist, entry, targetList == "whitelist"),
                Formattype: Formattype,
                UserWebsiteBlocklist: HandleListRemove(UserWebsiteBlocklist, entry, targetList == "userWebsiteBlocklist"),
                UserWebsiteWhitelist: HandleListRemove(UserWebsiteWhitelist, entry, targetList == "userWebsiteWhitelist"),
                MaxDownloadThreads: MaxDownloadThreads,
                LogExpiryInDays: LogExpiryInDays,
                AllowInsecureSources: AllowInsecureSources,
                MaxListSizeInMB: MaxListSizeInMB,
                AllowRevert: AllowRevert
            );

            var newReader = new ConfigManager(settings);
            if (_instance == this)
            {
                _instance = newReader;
            }
            return newReader;
        }

        private static string[] HandleListUpdate(IReadOnlyList<string> list, string entry, bool isSelected)
        {
            if (!isSelected) return [.. list];

            var currentList = list as List<string> ?? [.. list];
            if (currentList.Contains(entry)) return [.. list]; // Already exists, no change needed

            currentList.Add(entry);
            return [.. currentList];
        }

        private static string[] HandleListRemove(IReadOnlyList<string> list, string entry, bool isSelected)
        {
            if (!isSelected) return [.. list];

            var currentList = list as List<string> ?? [.. list];
            if (!currentList.Remove(entry)) return [.. list]; // Not found, no change needed

            return [.. currentList];
        }

        /// <summary>
        /// Save the current configuration state to the JSON file.
        /// </summary>
        public void SaveToDisk(string filePath)
        {
            var settings = new Settings(
                Blocklists: [.. Blocklists],
                Whitelist: [.. Whitelist],
                Formattype: Formattype,
                UserWebsiteBlocklist: [.. UserWebsiteBlocklist],
                UserWebsiteWhitelist: [.. UserWebsiteWhitelist],
                MaxDownloadThreads: MaxDownloadThreads,
                LogExpiryInDays: LogExpiryInDays,
                AllowInsecureSources: AllowInsecureSources,
                MaxListSizeInMB: MaxListSizeInMB,
                AllowRevert: AllowRevert
            );

            string json = JsonSerializer.Serialize(settings, SettingsJsonSerializerContext.Default.Settings);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Create the global configuration from a JSON file path.
        /// </summary>
        public static void Init(string jsonFilePath)
        {
            if (_instance != null)
                return;

            if (!File.Exists(jsonFilePath))
                throw new FileNotFoundException("Configuration file missing.", jsonFilePath);

            var json = File.ReadAllText(jsonFilePath);

            Settings? raw;
            try
            {
                raw = JsonSerializer.Deserialize(json, SettingsJsonSerializerContext.Default.Settings);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Failed to deserialize settings. {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationCorrupted);
                return;
            }

            if (raw == null)
            {
                TraceLogger.Log($"Deserialized configuration from '{jsonFilePath}' is null.", Enums.StatusSeverityType.Fatal, ErrorCodes.ConfigurationCorrupted);
                return;
            }
            _ = new ConfigManager(raw);
            TraceLogger.Log($"Successfully read {jsonFilePath} settings.", Enums.StatusSeverityType.Debug);
        }

        public static void CreateDefaultConfig(string filePath)
        {
            var defaultConfig = new Settings
            {
                Blocklists = [],
                Whitelist = [],
                Formattype = "domain",
                UserWebsiteBlocklist = [],
                UserWebsiteWhitelist = [],
                MaxDownloadThreads = 3,
                LogExpiryInDays = 7,
                AllowInsecureSources = false,
                MaxListSizeInMB = 100,
                AllowRevert = false
            };

            string defaultJson = JsonSerializer.Serialize(
                defaultConfig,
                SettingsJsonSerializerContext.Default.Settings);
            File.WriteAllText(filePath, defaultJson);
        }
    }
}