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
using System.Net;

namespace HostlistDownloader.Tests;

public class FormatterTests
{
    private static readonly HostEntry Plain = new("example.com", EntryKind.Domain, WildcardKind.None, false);
    private static readonly HostEntry Sub = new("example.com", EntryKind.Domain, WildcardKind.Subdomains, false);
    private static readonly HostEntry WithIp = new("example.com", EntryKind.Domain, WildcardKind.None, false, SourceIp: "1.2.3.4");
    private static readonly HostEntry SinkIp = new("example.com", EntryKind.Domain, WildcardKind.None, false, SourceIp: "127.0.0.1");

    private static string? Format(OutputFormat format, HostEntry entry) =>
        new OutputFormatter(format, IPAddress.Any).FormatBlock(entry, out _);

    [Theory]
    [InlineData("AdGuard", "Adblock")]
    [InlineData("uBlock Origin", "Adblock")]
    [InlineData("ublock", "Adblock")]
    [InlineData("Pi-hole", "Hosts")]
    [InlineData(" DNSMASQ ", "Dnsmasq")]
    public void FormatNames_AreCaseInsensitive(string name, string expected)
    {
        Assert.True(OutputFormats.TryParse(name, out OutputFormat format));
        Assert.Equal(Enum.Parse<OutputFormat>(expected), format);
    }

    [Fact]
    public void UnknownFormat_IsRejected()
    {
        Assert.False(OutputFormats.TryParse("domian", out _));
    }

    [Fact]
    public void Hosts_UsesNormalisedBlockIp_AndDropsWildcards()
    {
        Assert.Equal("0.0.0.0 example.com", Format(OutputFormat.Hosts, WithIp));
        Assert.Null(Format(OutputFormat.Hosts, Sub));
    }

    [Fact]
    public void Adblock_WritesWildcardAsAnchoredRule()
    {
        Assert.Equal("||example.com^", Format(OutputFormat.Adblock, Plain));
        Assert.Equal("||example.com^", Format(OutputFormat.Adblock, Sub));
    }

    [Fact]
    public void Dnsmasq_StripsWildcard()
    {
        Assert.Equal("address=/example.com/0.0.0.0", Format(OutputFormat.Dnsmasq, Sub));
    }

    [Fact]
    public void Unbound_And_Rpz()
    {
        Assert.Equal("local-zone: \"example.com.\" always_null", Format(OutputFormat.Unbound, Plain));
        Assert.Equal("*.example.com CNAME .", Format(OutputFormat.Rpz, Sub));
    }

    [Fact]
    public void IpOnly_SkipsSinkAddresses()
    {
        Assert.Equal("1.2.3.4", Format(OutputFormat.IpOnly, WithIp));
        Assert.Null(Format(OutputFormat.IpOnly, SinkIp));
        Assert.Equal("5.6.7.8", Format(OutputFormat.IpOnly, new HostEntry("5.6.7.8", EntryKind.Ip, WildcardKind.None, false)));
    }

    [Fact]
    public void Matcher_HandlesExactSuffixAndParsedForms()
    {
        var matcher = new DomainMatcher();
        matcher.Add(new HostEntry("good.com", EntryKind.Domain, WildcardKind.None, true));
        matcher.Add(new HostEntry("cdn.net", EntryKind.Domain, WildcardKind.Subdomains, true));
        matcher.Add(new HostEntry("tree.org", EntryKind.Domain, WildcardKind.None, true, IncludeSubdomains: true));

        Assert.True(matcher.Matches(new HostEntry("good.com", EntryKind.Domain, WildcardKind.None, false)));
        Assert.False(matcher.Matches(new HostEntry("ads.good.com", EntryKind.Domain, WildcardKind.None, false)));
        Assert.True(matcher.Matches(new HostEntry("a.b.cdn.net", EntryKind.Domain, WildcardKind.None, false)));
        Assert.False(matcher.Matches(new HostEntry("cdn.net", EntryKind.Domain, WildcardKind.None, false)));
        Assert.True(matcher.Matches(new HostEntry("x.tree.org", EntryKind.Domain, WildcardKind.None, false)));
        Assert.True(matcher.Matches(new HostEntry("tree.org", EntryKind.Domain, WildcardKind.None, false)));
    }

    [Fact]
    public void DomainTree_FindsParents()
    {
        var tree = new DomainTree();
        tree.Add("example.com");
        Assert.True(tree.HasParent("ads.example.com"));
        Assert.True(tree.HasParent("a.b.example.com"));
        Assert.False(tree.HasParent("example.com"));
        Assert.False(tree.HasParent("notexample.com"));
    }
}
