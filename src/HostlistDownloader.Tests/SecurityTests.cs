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

using HostlistDownloader.Diagnostics;
using HostlistDownloader.Network;
using System.Net;
using System.Text;

namespace HostlistDownloader.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.1.2.3")]
    [InlineData("100.100.100.200")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:c0a8:0101::1")]
    public void InternalAddresses_AreBlocked(string address)
    {
        Assert.True(AddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("140.82.112.3")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    public void PublicAddresses_AreAllowed(string address)
    {
        Assert.False(AddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://example.com/list.txt", true)]
    [InlineData("http://example.com/list.txt", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("mailto:a@example.com", false)]
    [InlineData("#https://example.com/list.txt", false)]
    [InlineData("", false)]
    public void SourceUrls_UseOneRule(string url, bool valid)
    {
        Assert.Equal(valid, UrlValidator.TryValidateSourceUrl(url, out _, out _));
    }

    [Fact]
    public void Redactor_HidesCredentialsAndQueries()
    {
        string redacted = LogRedactor.Redact("Downloading https://user:secret@example.com/list.txt?token=abc123 now");
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.Contains("example.com/list.txt", redacted);
    }

    [Fact]
    public void Neutralise_PreventsForgedLogLines()
    {
        string message = LogRedactor.Neutralise("bad\r\n[2026-01-01 00:00:00.000] [ERROR] forged");
        string[] lines = message.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("    | ", lines[1]);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html>")]
    [InlineData("  <html><body>Rate limited</body></html>")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void ErrorPages_AreNotText(string body)
    {
        Assert.False(DownloadContentValidator.LooksLikeText(Encoding.UTF8.GetBytes(body), truncated: false, out _));
    }

    [Fact]
    public void Sniff_ToleratesSplitMultiByteCharacter()
    {
        byte[] text = Encoding.UTF8.GetBytes("# é list\n0.0.0.0 example.com\n");
        int cut = Array.IndexOf(text, (byte)0xC3) + 1; // cut inside the two-byte é
        Assert.True(DownloadContentValidator.LooksLikeText(text.AsSpan(0, cut), truncated: true, out _));
    }

    [Fact]
    public void AdblockHeader_IsText()
    {
        Assert.True(DownloadContentValidator.LooksLikeText(Encoding.UTF8.GetBytes("[Adblock Plus 2.0]\n||example.com^\n"), truncated: false, out _));
    }

    [Fact]
    public void UpdateScript_QuotesPathsAndWaitsForCallerPid()
    {
        string script = UpdateChecker.BuildReplaceScript(4242, @"C:\Users\O'Brien\hld.exe.update", @"C:\Users\O'Brien\hld.exe", @"C:\Temp\s.ps1");
        Assert.Contains("$targetPid = 4242", script);
        Assert.Contains("'C:\\Users\\O''Brien\\hld.exe.update'", script);
        Assert.DoesNotContain("GetCurrentProcess", script);
    }
}
