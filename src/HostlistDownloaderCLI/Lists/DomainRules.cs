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

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace HostlistDownloader.Lists;

/// <summary>Domain name normalisation and validation shared by parsing, config validation and search.</summary>
internal static class DomainRules
{
    internal const int MaxDomainLength = 253;
    internal const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };

    /// <summary>Host names found in default hosts files that must never be treated as block entries.</summary>
    private static readonly HashSet<string> JunkDomains = new(StringComparer.Ordinal)
    {
        "localhost",
        "localhost.localdomain",
        "local",
        "localdomain",
        "broadcasthost",
        "ip6-localhost",
        "ip6-loopback",
        "ip6-localnet",
        "ip6-mcastprefix",
        "ip6-allnodes",
        "ip6-allrouters",
        "ip6-allhosts",
        "0.0.0.0"
    };

    /// <summary>
    /// Common multi-label public suffixes (ICANN section). Blocking one of these would block an entire country
    /// namespace. Single-label TLDs (com, net, uk) are rejected separately because they have no dot.
    /// </summary>
    private static readonly HashSet<string> PublicSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "ac.uk", "gov.uk", "nhs.uk", "sch.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "id.au",
        "co.nz", "net.nz", "org.nz", "govt.nz", "ac.nz",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "gr.jp",
        "co.kr", "or.kr", "ne.kr", "go.kr",
        "co.in", "net.in", "org.in", "gov.in", "ac.in",
        "co.za", "org.za", "gov.za",
        "com.br", "net.br", "org.br", "gov.br",
        "com.cn", "net.cn", "org.cn", "gov.cn",
        "com.hk", "com.tw", "com.sg", "com.my", "com.ph", "com.vn",
        "com.mx", "com.ar", "com.co", "com.pe", "com.tr", "com.ua", "com.pl", "com.ru",
        "co.il", "co.id", "co.th"
    };

    internal static bool IsJunk(string domain) => JunkDomains.Contains(domain);

    internal static bool IsPublicSuffix(string domain) => !domain.Contains('.') || PublicSuffixes.Contains(domain);

    /// <summary>
    /// Converts an internationalised domain to its ASCII (punycode) form. Returns false if the name cannot be
    /// converted. ASCII input is returned unchanged.
    /// </summary>
    internal static bool TryConvertToAscii(string domain, out string ascii, out bool converted)
    {
        converted = false;
        ascii = domain;
        foreach (char c in domain)
        {
            if (c > 0x7F)
            {
                converted = true;
                break;
            }
        }
        if (!converted)
        {
            return true;
        }

        try
        {
            ascii = Idn.GetAscii(domain).ToLowerInvariant();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// RFC 1035-style host name check: total length up to 253, 1-63 characters per label, letters, digits,
    /// hyphens and underscores (underscores appear in real-world tracker names), no label starting or ending with
    /// a hyphen, at least two labels, and a TLD that is not all digits. Punycode labels (xn--) are allowed.
    /// </summary>
    internal static bool IsValidHostname(ReadOnlySpan<char> domain)
    {
        if (domain.Length == 0 || domain.Length > MaxDomainLength)
        {
            return false;
        }

        int labels = 0;
        int labelStart = 0;
        for (int i = 0; i <= domain.Length; i++)
        {
            if (i == domain.Length || domain[i] == '.')
            {
                ReadOnlySpan<char> label = domain[labelStart..i];
                if (!IsValidLabel(label))
                {
                    return false;
                }
                labels++;
                if (i == domain.Length)
                {
                    bool allDigits = true;
                    foreach (char c in label)
                    {
                        if (!char.IsAsciiDigit(c))
                        {
                            allDigits = false;
                            break;
                        }
                    }
                    if (allDigits)
                    {
                        return false;
                    }
                }
                labelStart = i + 1;
            }
        }
        return labels >= 2;
    }

    /// <summary>Checks the characters of a wildcard pattern (the '*' itself plus normal host name characters).</summary>
    internal static bool IsValidPattern(ReadOnlySpan<char> pattern)
    {
        if (pattern.Length == 0 || pattern.Length > MaxDomainLength)
        {
            return false;
        }
        foreach (char c in pattern)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.' or '*'))
            {
                return false;
            }
        }
        return pattern.Contains('.') || pattern.Contains('*');
    }

    private static bool IsValidLabel(ReadOnlySpan<char> label)
    {
        if (label.Length == 0 || label.Length > MaxLabelLength || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }
        foreach (char c in label)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Strict IP check. IPAddress.TryParse accepts shorthand such as "1234" or "1.2.3" (read as 0.0.4.210 and
    /// 1.2.0.3), which would misread some tokens as addresses, so IPv4 must be four dotted decimal parts.
    /// </summary>
    internal static bool TryParseStrictIp(string token, out IPAddress? address)
    {
        address = null;
        if (token.Length == 0)
        {
            return false;
        }

        if (token.Contains(':'))
        {
            string candidate = token;
            int zone = candidate.IndexOf('%');
            if (zone >= 0)
            {
                candidate = candidate[..zone];
            }
            return IPAddress.TryParse(candidate, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        int dots = 0;
        foreach (char c in token)
        {
            if (c == '.')
            {
                dots++;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return dots == 3 && IPAddress.TryParse(token, out address) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    /// <summary>True for the addresses blocklists use to mean "nowhere" (0.0.0.0, ::, 127.x.x.x, ::1).</summary>
    internal static bool IsSinkAddress(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address);

    /// <summary>Returns true if <paramref name="predicate"/> holds for any proper parent of the domain (a.b.com → b.com, com).</summary>
    internal static bool AnyParent(string domain, Func<string, bool> predicate)
    {
        for (int i = domain.IndexOf('.'); i >= 0; i = domain.IndexOf('.', i + 1))
        {
            if (predicate(domain[(i + 1)..]))
            {
                return true;
            }
        }
        return false;
    }
}
