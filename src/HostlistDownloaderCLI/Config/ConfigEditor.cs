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

using HostlistDownloader.Lists;
using HostlistDownloader.Network;

namespace HostlistDownloader.Config;

internal enum ConfigTarget
{
    Blocklist,
    Whitelist,
    UserBlock,
    UserAllow
}

internal enum EditAction
{
    Add,
    Remove
}

internal sealed record ConfigEdit(EditAction Action, ConfigTarget Target, string Value);

internal sealed record EditOutcome(bool Success, string Message);

/// <summary>Applies --add-*/--remove-* command line edits to settings.json as pure transformations.</summary>
internal static class ConfigEditor
{
    internal static SettingsFile Apply(SettingsFile settings, ConfigEdit edit, out EditOutcome outcome)
    {
        string value = edit.Value.Trim();
        return edit.Target switch
        {
            ConfigTarget.Blocklist => settings with { Blocklists = EditSources(settings.Blocklists, edit.Action, value, "blocklist", out outcome) },
            ConfigTarget.Whitelist => settings with { Whitelists = EditSources(settings.Whitelists, edit.Action, value, "whitelist", out outcome) },
            ConfigTarget.UserBlock => settings with { UserWebsiteBlocklist = EditRules(settings.UserWebsiteBlocklist, edit.Action, value, "user block rule", out outcome) },
            ConfigTarget.UserAllow => settings with { UserWebsiteWhitelist = EditRules(settings.UserWebsiteWhitelist, edit.Action, value, "user allow rule", out outcome) },
            _ => throw new ArgumentOutOfRangeException(nameof(edit))
        };
    }

    private static SourceEntry[] EditSources(SourceEntry[] sources, EditAction action, string url, string label, out EditOutcome outcome)
    {
        string key = AppConfig.SourceKey(url);
        int index = Array.FindIndex(sources, s => AppConfig.SourceKey(s.Url) == key);

        if (action == EditAction.Add)
        {
            if (!UrlValidator.TryValidateSourceUrl(url, out _, out string? reason))
            {
                outcome = new EditOutcome(false, $"Not added: {reason}");
                return sources;
            }
            if (index >= 0)
            {
                if (sources[index].Enabled)
                {
                    outcome = new EditOutcome(true, $"{url} is already a {label} source.");
                    return sources;
                }
                SourceEntry[] enabled = [.. sources];
                enabled[index] = enabled[index] with { Enabled = true };
                outcome = new EditOutcome(true, $"Re-enabled {label} source {url}.");
                return enabled;
            }
            outcome = new EditOutcome(true, $"Added {label} source {url}.");
            return [.. sources, new SourceEntry { Url = url }];
        }

        if (index < 0)
        {
            outcome = new EditOutcome(false, $"{url} is not a configured {label} source.");
            return sources;
        }
        outcome = new EditOutcome(true, $"Removed {label} source {url}.");
        return [.. sources.Where(s => AppConfig.SourceKey(s.Url) != key)];
    }

    private static string[] EditRules(string[] rules, EditAction action, string rule, string label, out EditOutcome outcome)
    {
        bool exists = rules.Any(r => string.Equals(r.Trim(), rule, StringComparison.OrdinalIgnoreCase));

        if (action == EditAction.Add)
        {
            var parsed = new List<HostEntry>();
            if (HostEntryParser.ParseLine(rule, parsed, ParserOptions.Default) != LineStatus.Parsed)
            {
                outcome = new EditOutcome(false, $"Not added: \"{rule}\" is not a valid domain or rule (e.g. example.com, *.example.com, ||example.com^).");
                return rules;
            }
            if (exists)
            {
                outcome = new EditOutcome(true, $"\"{rule}\" is already a {label}.");
                return rules;
            }
            outcome = new EditOutcome(true, $"Added {label} \"{rule}\".");
            return [.. rules, rule];
        }

        if (!exists)
        {
            outcome = new EditOutcome(false, $"\"{rule}\" is not a configured {label}.");
            return rules;
        }
        outcome = new EditOutcome(true, $"Removed {label} \"{rule}\".");
        return [.. rules.Where(r => !string.Equals(r.Trim(), rule, StringComparison.OrdinalIgnoreCase))];
    }
}
