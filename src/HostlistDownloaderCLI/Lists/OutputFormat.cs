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

namespace HostlistDownloader.Lists;

internal enum OutputFormat
{
    Domain,
    Hosts,
    IpOnly,
    Adblock,
    Dnsmasq,
    Wildcard,
    Raw,
    Unbound,
    Rpz
}

internal static class OutputFormats
{
    // Matched case-insensitively, so "AdGuard", "uBlock Origin" and "ublock" all work.
    private static readonly Dictionary<string, OutputFormat> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["domain"] = OutputFormat.Domain,
        ["domains"] = OutputFormat.Domain,
        ["hosts"] = OutputFormat.Hosts,
        ["host"] = OutputFormat.Hosts,
        ["pihole"] = OutputFormat.Hosts,
        ["pi-hole"] = OutputFormat.Hosts,
        ["iponly"] = OutputFormat.IpOnly,
        ["ip"] = OutputFormat.IpOnly,
        ["adblock"] = OutputFormat.Adblock,
        ["abp"] = OutputFormat.Adblock,
        ["ublock"] = OutputFormat.Adblock,
        ["ublockorigin"] = OutputFormat.Adblock,
        ["ublock origin"] = OutputFormat.Adblock,
        ["ublock-origin"] = OutputFormat.Adblock,
        ["adguard"] = OutputFormat.Adblock,
        ["ad-guard"] = OutputFormat.Adblock,
        ["dnsmasq"] = OutputFormat.Dnsmasq,
        ["wildcard"] = OutputFormat.Wildcard,
        ["raw"] = OutputFormat.Raw,
        ["unbound"] = OutputFormat.Unbound,
        ["rpz"] = OutputFormat.Rpz
    };

    internal static string AllowedValuesText =>
        "domain, hosts (host, pihole), iponly, adblock (ublock, adguard), dnsmasq, wildcard, raw, unbound, rpz";

    internal static bool TryParse(string? value, out OutputFormat format)
    {
        format = OutputFormat.Domain;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return Aliases.TryGetValue(value.Trim(), out format);
    }

    internal static string CanonicalName(this OutputFormat format) => format switch
    {
        OutputFormat.Domain => "domain",
        OutputFormat.Hosts => "hosts",
        OutputFormat.IpOnly => "iponly",
        OutputFormat.Adblock => "adblock",
        OutputFormat.Dnsmasq => "dnsmasq",
        OutputFormat.Wildcard => "wildcard",
        OutputFormat.Raw => "raw",
        OutputFormat.Unbound => "unbound",
        OutputFormat.Rpz => "rpz",
        _ => format.ToString().ToLowerInvariant()
    };

    /// <summary>True when one entry for example.com also blocks every subdomain, so compressing the list is safe.</summary>
    internal static bool MatchesSubdomains(this OutputFormat format) =>
        format is OutputFormat.Adblock or OutputFormat.Dnsmasq or OutputFormat.Unbound;

    internal static string CommentPrefix(this OutputFormat format) => format switch
    {
        OutputFormat.Adblock => "!",
        OutputFormat.Rpz => ";",
        _ => "#"
    };
}
