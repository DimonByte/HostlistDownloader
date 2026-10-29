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
using HostlistDownloader.Lists;

namespace HostlistDownloader.Tests;

public sealed class BuilderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hld-tests-" + Guid.NewGuid().ToString("N"));

    public BuilderTests()
    {
        Directory.CreateDirectory(_folder);
        TraceLogger.QuietMode = true; // no progress bars in tests
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, params string[] lines)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private (ListBuildResult Result, string[] Combined, string[] Block, string[] Allow) Build(SettingsFile settings, params SourceFile[] sources)
    {
        var targets = new OutputTargets(Path.Combine(_folder, "block.txt"), Path.Combine(_folder, "allow.txt"), Path.Combine(_folder, "list.txt"));
        var config = new AppConfig(settings with { Output = settings.Output with { Header = false } });
        ListBuildResult result = new ListBuilder(config, targets).Build(sources, CancellationToken.None);
        return (result,
            File.ReadAllLines(targets.Combined + ".tmp"),
            File.ReadAllLines(targets.BlockList + ".tmp"),
            File.ReadAllLines(targets.AllowList + ".tmp"));
    }

    [Fact]
    public void DifferentSyntaxForSameDomain_IsOneEntry_AfterFormatting()
    {
        string a = Write("a.txt", "0.0.0.0 ads.example.com", "127.0.0.1 ads.example.com tracker.example.com", "# comment");
        string b = Write("b.txt", "||ads.example.com^", "ads.example.com", "*.tracker.example.com");
        var (result, combined, _, _) = Build(new SettingsFile { FormatType = "domain" },
            new SourceFile(a, "a", ListKind.Block), new SourceFile(b, "b", ListKind.Block));

        Assert.Equal(new[] { "ads.example.com", "tracker.example.com" }, combined);
        Assert.Equal(2, result.CombinedCount);
    }

    [Fact]
    public void Whitelist_MatchesParsedDomains_AndUserRulesAreFormatted()
    {
        string block = Write("block.txt", "0.0.0.0 good.example.com", "0.0.0.0 bad.example.com", "0.0.0.0 x.cdn.example.net");
        string allow = Write("allow.txt", "good.example.com", "*.cdn.example.net");
        var settings = new SettingsFile
        {
            FormatType = "hosts",
            UserWebsiteBlocklist = ["mine.example.org"],
            UserWebsiteWhitelist = ["bad.example.com"]
        };
        var (result, combined, blockList, allowList) = Build(settings,
            new SourceFile(block, "block", ListKind.Block), new SourceFile(allow, "allow", ListKind.Allow));

        Assert.Equal(new[] { "0.0.0.0 mine.example.org" }, combined);
        Assert.Equal(4, blockList.Length);
        Assert.Contains("*.cdn.example.net", string.Join("\n", allowList));
        Assert.Equal(3, result.Whitelisted);
    }

    [Fact]
    public void Adblock_AddsExceptionForAllowedSubdomain_AndCompresses()
    {
        string block = Write("block.txt", "||example.com^", "||ads.example.com^", "||deep.ads.example.com^");
        var settings = new SettingsFile
        {
            FormatType = "adblock",
            UserWebsiteWhitelist = ["good.example.com"],
            Output = new OutputSettings { Compress = true, Header = false }
        };
        var (result, combined, _, _) = Build(settings, new SourceFile(block, "block", ListKind.Block));

        Assert.Equal(new[] { "||example.com^", "@@||good.example.com^" }, combined);
        Assert.Equal(2, result.Compressed);
        Assert.Equal(1, result.Exceptions);
    }

    [Fact]
    public void NeverBlock_ProtectsSubdomainsInEveryOutput()
    {
        string block = Write("block.txt", "0.0.0.0 windowsupdate.com", "0.0.0.0 dl.windowsupdate.com", "0.0.0.0 ads.example.com");
        var settings = new SettingsFile { FormatType = "hosts", NeverBlock = ["windowsupdate.com"] };
        var (result, combined, blockList, _) = Build(settings, new SourceFile(block, "block", ListKind.Block));

        Assert.Equal(new[] { "0.0.0.0 ads.example.com" }, combined);
        Assert.Equal(new[] { "0.0.0.0 ads.example.com" }, blockList);
        Assert.Equal(2, result.NeverBlocked);
    }

    [Fact]
    public void IpOnly_EmitsEachRealAddressOnce()
    {
        string block = Write("ips.txt", "1.2.3.4", "5.6.7.8 a.example.com", "5.6.7.8 b.example.com", "127.0.0.1 c.example.com", "0.0.0.0 d.example.com");
        var (_, combined, _, _) = Build(new SettingsFile { FormatType = "iponly" }, new SourceFile(block, "ips", ListKind.Block));

        Assert.Equal(new[] { "1.2.3.4", "5.6.7.8" }, combined);
    }

    [Fact]
    public void Output_UsesConfiguredLineEnding()
    {
        string block = Write("block.txt", "a.example.com", "b.example.com");
        var targets = new OutputTargets(Path.Combine(_folder, "b.txt"), Path.Combine(_folder, "w.txt"), Path.Combine(_folder, "l.txt"));
        var config = new AppConfig(new SettingsFile { Output = new OutputSettings { Header = false, LineEnding = "lf" } });
        new ListBuilder(config, targets).Build([new SourceFile(block, "b", ListKind.Block)], CancellationToken.None);

        string text = File.ReadAllText(targets.Combined + ".tmp");
        Assert.Equal("a.example.com\nb.example.com\n", text);
    }
}
