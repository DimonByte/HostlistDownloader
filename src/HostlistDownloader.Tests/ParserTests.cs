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

namespace HostlistDownloader.Tests;

public class ParserTests
{
    private static List<HostEntry> Parse(string line, out LineStatus status, ParserOptions? options = null)
    {
        var entries = new List<HostEntry>();
        status = HostEntryParser.ParseLine(line, entries, options ?? ParserOptions.Default);
        return entries;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("! adblock comment")]
    [InlineData("// comment")]
    [InlineData("; zone comment")]
    [InlineData("[Adblock Plus 2.0]")]
    [InlineData("127.0.0.1 localhost")]
    [InlineData("::1 ip6-localhost ip6-loopback")]
    [InlineData("0.0.0.0 0.0.0.0")]
    public void Boilerplate_IsEmpty(string line)
    {
        List<HostEntry> entries = Parse(line, out LineStatus status);
        Assert.Equal(LineStatus.Empty, status);
        Assert.Empty(entries);
    }

    [Fact]
    public void HostsLine_WithSeveralDomains_YieldsEachDomain()
    {
        List<HostEntry> entries = Parse("127.0.0.1 a.com b.com   # trailing comment", out LineStatus status);
        Assert.Equal(LineStatus.Parsed, status);
        Assert.Equal(["a.com", "b.com"], entries.Select(e => e.Value));
        Assert.All(entries, e => Assert.Equal("127.0.0.1", e.SourceIp));
    }

    [Fact]
    public void SingleIp_IsIpEntry()
    {
        List<HostEntry> entries = Parse("1.2.3.4", out LineStatus status);
        Assert.Equal(LineStatus.Parsed, status);
        HostEntry entry = Assert.Single(entries);
        Assert.Equal(EntryKind.Ip, entry.Kind);
        Assert.Equal("1.2.3.4", entry.Value);
    }

    [Theory]
    [InlineData("||Example.COM^", "example.com", false)]
    [InlineData("||example.com^$important,third-party", "example.com", false)]
    [InlineData("@@||example.com^", "example.com", true)]
    [InlineData("example.com.", "example.com", false)]
    [InlineData("address=/example.com/0.0.0.0", "example.com", false)]
    [InlineData("local-zone: \"example.com.\" always_null", "example.com", false)]
    [InlineData("local-zone: \"example.com.\" transparent", "example.com", true)]
    [InlineData("example.com CNAME .", "example.com", false)]
    [InlineData("example.com CNAME rpz-passthru.", "example.com", true)]
    public void SupportedSyntax_Normalises(string line, string expected, bool isAllow)
    {
        HostEntry entry = Assert.Single(Parse(line, out LineStatus status));
        Assert.Equal(LineStatus.Parsed, status);
        Assert.Equal(expected, entry.Value);
        Assert.Equal(isAllow, entry.IsAllow);
    }

    [Theory]
    [InlineData("*.example.com", "Subdomains", "example.com")]
    [InlineData("||*.example.com^", "Subdomains", "example.com")]
    [InlineData("example.*", "AnyTld", "example")]
    [InlineData("ads*.example.com", "Pattern", "ads*.example.com")]
    public void Wildcards_AreClassified(string line, string kind, string value)
    {
        HostEntry entry = Assert.Single(Parse(line, out _));
        Assert.Equal(Enum.Parse<WildcardKind>(kind), entry.Wildcard);
        Assert.Equal(value, entry.Value);
    }

    [Theory]
    [InlineData("example.com##.ad-banner")]
    [InlineData("example.com#@#.ad-banner")]
    [InlineData("||example.com/ads/banner.js")]
    [InlineData("||example.com^$dnsrewrite=1.2.3.4")]
    [InlineData("/ads[0-9]+\\.example\\.com/")]
    [InlineData("address=/example.com/192.168.1.10")]
    public void NonDnsRules_AreUnsupported(string line)
    {
        Parse(line, out LineStatus status);
        Assert.Equal(LineStatus.Unsupported, status);
    }

    [Theory]
    [InlineData("Rate limit exceeded")]
    [InlineData("{\"message\": \"Not Found\"}")]
    [InlineData("com")]
    [InlineData("-bad-.example.com")]
    public void Garbage_IsInvalid(string line)
    {
        Parse(line, out LineStatus status);
        Assert.Equal(LineStatus.Invalid, status);
    }

    [Fact]
    public void HostsComment_AfterWhitespace_IsNotCosmetic()
    {
        HostEntry entry = Assert.Single(Parse("0.0.0.0 ads.example.com ## note", out LineStatus status));
        Assert.Equal(LineStatus.Parsed, status);
        Assert.Equal("ads.example.com", entry.Value);
    }

    [Fact]
    public void PublicSuffix_IsRejected_ButCanBeAllowed()
    {
        Parse("co.uk", out LineStatus strict);
        Assert.Equal(LineStatus.Invalid, strict);

        List<HostEntry> lenient = Parse("co.uk", out LineStatus status, ParserOptions.Lenient);
        Assert.Equal(LineStatus.Parsed, status);
        Assert.Single(lenient);
    }

    [Fact]
    public void Idn_IsConvertedToPunycode()
    {
        HostEntry entry = Assert.Single(Parse("bücher.example", out _));
        Assert.Equal("xn--bcher-kva.example", entry.Value);
    }

    [Theory]
    [InlineData("xn--p1ai.xn--p1ai")]
    [InlineData("_dmarc.example.com")]
    [InlineData("a-b.example.co.uk")]
    public void ValidNames_AreAccepted(string domain)
    {
        Assert.True(DomainRules.IsValidHostname(domain));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("1.2.3")]
    [InlineData("example.com")]
    public void StrictIp_RejectsShorthand(string token)
    {
        Assert.False(DomainRules.TryParseStrictIp(token, out _));
    }
}
