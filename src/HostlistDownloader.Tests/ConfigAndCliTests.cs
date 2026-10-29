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

using HostlistDownloader.Cli;
using HostlistDownloader.Config;
using HostlistDownloader.Core;
using System.Text.Json;

namespace HostlistDownloader.Tests;

public class ConfigAndCliTests
{
    private const string V1Settings = """
        {
          // comments and trailing commas are allowed
          "blocklists": [
            "https://example.com/a.txt",
            "#https://example.com/disabled.txt",
          ],
          "whitelist": [],
          "formattype": "domain",
          "maxDownloadThreads": 3
        }
        """;

    [Fact]
    public void V1Settings_AreMigrated()
    {
        Assert.True(SettingsMigrator.TryMigrate(V1Settings, out string migrated, out List<string> notes));
        Assert.NotEmpty(notes);

        SettingsFile? settings = JsonSerializer.Deserialize(migrated, SettingsJsonContext.Default.SettingsFile);
        Assert.NotNull(settings);
        Assert.Equal(SettingsFile.CurrentVersion, settings!.Version);
        Assert.Equal(2, settings.Blocklists.Length);
        Assert.True(settings.Blocklists[0].Enabled);
        Assert.False(settings.Blocklists[1].Enabled);
        Assert.Equal("https://example.com/disabled.txt", settings.Blocklists[1].Url);
        Assert.Equal(3, settings.MaxConcurrentDownloads);
    }

    [Fact]
    public void MissingSettings_KeepTheirDefaults()
    {
        SettingsFile? settings = JsonSerializer.Deserialize(
            "{ \"version\": 2, \"blocklists\": [ { \"url\": \"https://example.com/a.txt\" } ] }",
            SettingsJsonContext.Default.SettingsFile);
        Assert.NotNull(settings);
        Assert.True(settings!.Blocklists[0].Enabled);
        Assert.Equal(4, settings.MaxConcurrentDownloads);
        Assert.Equal(100L, settings.MaxListSizeInMB);
        Assert.True(settings.CheckForUpdates);
        Assert.NotNull(settings.Network);
        Assert.Equal(3, settings.Network.MaxRetries);
        Assert.True(settings.Output.Sort);
        Assert.Equal("domain", settings.FormatType);
    }

    [Fact]
    public void CurrentSettings_AreNotMigratedAgain()
    {
        Assert.False(SettingsMigrator.TryMigrate("{ \"version\": 2 }", out _, out _));
    }

    [Fact]
    public void UnknownSetting_IsRejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("{ \"version\": 2, \"formatype\": \"hosts\" }", SettingsJsonContext.Default.SettingsFile));
    }

    [Fact]
    public void InvalidFormat_IsAConfigError()
    {
        var config = new AppConfig(new SettingsFile { FormatType = "domian" });
        Assert.True(config.HasErrors);
    }

    [Fact]
    public void ConstructingConfig_DoesNotReplaceCurrent()
    {
        var first = new AppConfig(new SettingsFile());
        AppConfig.SetCurrent(first);
        _ = new AppConfig(new SettingsFile { FormatType = "hosts" });
        Assert.Same(first, AppConfig.Current);
    }

    [Fact]
    public void SourceKey_KeepsPathCase()
    {
        Assert.NotEqual(AppConfig.SourceKey("https://raw.githubusercontent.com/A/hosts"), AppConfig.SourceKey("https://raw.githubusercontent.com/a/hosts"));
        Assert.Equal(AppConfig.SourceKey("HTTPS://EXAMPLE.com/x"), AppConfig.SourceKey("https://example.com/x"));
    }

    [Fact]
    public void Editor_AddsAndRemovesSources()
    {
        var settings = new SettingsFile();
        settings = ConfigEditor.Apply(settings, new ConfigEdit(EditAction.Add, ConfigTarget.Blocklist, "https://example.com/a.txt"), out EditOutcome added);
        Assert.True(added.Success);
        Assert.Single(settings.Blocklists);

        settings = ConfigEditor.Apply(settings, new ConfigEdit(EditAction.Add, ConfigTarget.Blocklist, "not a url"), out EditOutcome rejected);
        Assert.False(rejected.Success);
        Assert.Single(settings.Blocklists);

        settings = ConfigEditor.Apply(settings, new ConfigEdit(EditAction.Remove, ConfigTarget.Blocklist, "https://example.com/a.txt"), out EditOutcome removed);
        Assert.True(removed.Success);
        Assert.Empty(settings.Blocklists);
    }

    [Fact]
    public void Cli_ParsesLongAndLegacyOptions()
    {
        CliRequest modern = ArgumentParser.Parse(["--search", "example.com", "-q"]);
        Assert.Equal(CommandKind.Search, modern.Command);
        Assert.Equal("example.com", modern.Argument);
        Assert.True(modern.Global.Quiet);
        Assert.Empty(modern.DeprecatedArguments);

        CliRequest legacy = ArgumentParser.Parse(["/q", "/fresh"]);
        Assert.Equal(CommandKind.Update, legacy.Command);
        Assert.True(legacy.Run.Fresh);
        Assert.Equal(2, legacy.DeprecatedArguments.Count);
    }

    [Fact]
    public void Cli_RejectsUnknownAndConflictingArguments()
    {
        Assert.Equal(ExitCodes.InvalidArguments, Assert.Throws<CliException>(() => ArgumentParser.Parse(["--bogus"])).ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, Assert.Throws<CliException>(() => ArgumentParser.Parse(["--stats", "--diff"])).ExitCode);
        Assert.Equal(ExitCodes.MissingOrInvalidParameters, Assert.Throws<CliException>(() => ArgumentParser.Parse(["--search"])).ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, Assert.Throws<CliException>(() => ArgumentParser.Parse(["--fix"])).ExitCode);
    }

    [Fact]
    public void Cli_AllowsSeveralEditsAndPathValues()
    {
        CliRequest request = ArgumentParser.Parse(["--add-user-block", "a.com", "--add-user-allow", "b.com", "--data-dir", "/var/lib/hld"]);
        Assert.Equal(CommandKind.EditConfig, request.Command);
        Assert.Equal(2, request.Edits.Count);
        Assert.Equal("/var/lib/hld", request.Global.DataDirectory);
    }
}
