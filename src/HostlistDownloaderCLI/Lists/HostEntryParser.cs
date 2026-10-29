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

internal readonly record struct ParserOptions(bool ValidateDomains = true, bool RejectPublicSuffixes = true)
{
    internal static ParserOptions Default => new(true, true);

    /// <summary>Accepts anything that looks like a domain; used for searching and duplicate analysis.</summary>
    internal static ParserOptions Lenient => new(false, false);
}

/// <summary>
/// Turns one line of any supported list syntax into normalised <see cref="HostEntry"/> values, so every input format
/// can feed every output format. Supported input:
/// <list type="bullet">
/// <item>hosts files: "0.0.0.0 a.com b.com" (every domain on the line), and bare IP lines "1.2.3.4"</item>
/// <item>domain lists: "a.com", "*.a.com", "a.*"</item>
/// <item>adblock/AdGuard DNS rules: "||a.com^", "||a.com^$important", "@@||a.com^" (modifiers are removed)</item>
/// <item>dnsmasq: "address=/a.com/0.0.0.0", "address=/a.com/b.com/", "local=/a.com/", "server=/a.com/"</item>
/// <item>unbound: local-zone: "a.com." always_null (transparent zones become allow rules)</item>
/// <item>RPZ: "a.com CNAME ." and "*.a.com CNAME ." (rpz-passthru. becomes an allow rule)</item>
/// </list>
/// Comments (#, !, //, ;), adblock headers ([Adblock Plus 2.0]) and zone boilerplate are skipped.
/// </summary>
internal static class HostEntryParser
{
    // Adblock modifiers whose meaning cannot be represented as a plain DNS block, so the rule is skipped.
    private static readonly HashSet<string> UnsupportedModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "badfilter", "dnsrewrite", "denyallow", "redirect", "redirect-rule", "removeparam", "csp", "replace",
        "client", "ctag", "dnstype", "app", "domain", "header", "permissions", "cookie", "stealth"
    };

    private static readonly HashSet<string> UnboundBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "always_null", "always_nxdomain", "always_refuse", "always_deny", "refuse", "deny", "inform_deny", "static", "redirect"
    };

    private static readonly HashSet<string> UnboundAllowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "transparent", "typetransparent", "always_transparent", "inform"
    };

    private static readonly char[] Whitespace = [' ', '\t'];

    /// <summary>Parses one line, appending any entries to <paramref name="output"/>.</summary>
    internal static LineStatus ParseLine(string? line, List<HostEntry> output, ParserOptions options, ParseStats? stats = null)
    {
        stats ??= new ParseStats();
        stats.Lines++;
        LineStatus status = ParseLineCore(line, output, options, stats);
        switch (status)
        {
            case LineStatus.Empty:
                stats.EmptyOrComment++;
                break;
            case LineStatus.Parsed:
                stats.ParsedLines++;
                break;
            case LineStatus.Unsupported:
                stats.Unsupported++;
                break;
            case LineStatus.Invalid:
                stats.Invalid++;
                break;
        }
        return status;
    }

    private static LineStatus ParseLineCore(string? line, List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        if (line is null)
        {
            return LineStatus.Empty;
        }

        string text = line.Trim().TrimStart('﻿');
        if (text.Length == 0)
        {
            return LineStatus.Empty;
        }

        char first = text[0];
        if (first is '#' or '!' or ';' || text.StartsWith("//", StringComparison.Ordinal))
        {
            return LineStatus.Empty;
        }

        if (first == '[' && text[^1] == ']')
        {
            return LineStatus.Empty; // [Adblock Plus 2.0]
        }

        if (IsCosmeticRule(text))
        {
            return LineStatus.Unsupported;
        }

        if (text.StartsWith("@@", StringComparison.Ordinal))
        {
            return ParseAdblock(text[2..], isAllow: true, output, options, stats);
        }

        if (text.StartsWith("||", StringComparison.Ordinal))
        {
            return ParseAdblock(text, isAllow: false, output, options, stats);
        }

        if (first == '|' || (first == '/' && text.Length > 1 && text[^1] == '/'))
        {
            return LineStatus.Unsupported; // |https://url rules and /regex/ rules
        }

        if (text.StartsWith("address=/", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("local=/", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("server=/", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDnsmasq(text, output, options, stats);
        }

        if (text.StartsWith("local-zone:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUnboundZone(text, output, options, stats);
        }

        if (text.StartsWith("local-data", StringComparison.OrdinalIgnoreCase))
        {
            return LineStatus.Unsupported;
        }

        if (text.Equals("server:", StringComparison.OrdinalIgnoreCase) || first is '$' or '@')
        {
            return LineStatus.Empty; // unbound "server:" header, zone file $TTL/$ORIGIN/@ SOA lines
        }

        // Hosts and plain domain lists. Domains can never contain '#', so anything after it is a comment.
        int hash = text.IndexOf('#');
        if (hash >= 0)
        {
            text = text[..hash].TrimEnd();
            if (text.Length == 0)
            {
                return LineStatus.Empty;
            }
        }

        string[] tokens = text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);

        int cname = Array.FindIndex(tokens, t => t.Equals("CNAME", StringComparison.OrdinalIgnoreCase));
        if (cname > 0)
        {
            return ParseRpz(tokens, cname, output, options, stats);
        }

        if (tokens.Length >= 2 && IsZoneBoilerplate(tokens))
        {
            return LineStatus.Empty;
        }

        if (DomainRules.TryParseStrictIp(tokens[0], out var ip) && ip is not null)
        {
            if (tokens.Length == 1)
            {
                output.Add(new HostEntry(ip.ToString(), EntryKind.Ip, WildcardKind.None, IsAllow: false));
                return LineStatus.Parsed;
            }

            // "127.0.0.1 a.com b.com" is valid hosts syntax: every name on the line is an entry.
            string ipText = ip.ToString();
            long junkBefore = stats.JunkEntries;
            int added = 0;
            for (int i = 1; i < tokens.Length; i++)
            {
                if (TryAddDomain(tokens[i], isAllow: false, includeSubdomains: false, ipText, output, options, stats))
                {
                    added++;
                }
            }
            return StatusFor(added, stats.JunkEntries > junkBefore);
        }

        if (tokens.Length == 1 && TryParseCidr(tokens[0], out string? cidr) && cidr is not null)
        {
            output.Add(new HostEntry(cidr, EntryKind.Ip, WildcardKind.None, IsAllow: false));
            return LineStatus.Parsed;
        }

        if (tokens.Length == 1)
        {
            string token = tokens[0];
            if (token.EndsWith('^'))
            {
                token = token[..^1]; // "example.com^" without the leading ||
            }
            long junkBefore = stats.JunkEntries;
            bool added = TryAddDomain(token, isAllow: false, includeSubdomains: false, null, output, options, stats);
            return StatusFor(added ? 1 : 0, stats.JunkEntries > junkBefore);
        }

        // Several words without a leading IP: not a hosts line (often an HTML or error page that slipped through).
        return LineStatus.Invalid;
    }

    private static LineStatus ParseAdblock(string text, bool isAllow, List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        bool anchored = text.StartsWith("||", StringComparison.Ordinal);
        string body = anchored ? text[2..] : text;

        int dollar = body.IndexOf('$');
        if (dollar >= 0)
        {
            string modifiers = body[(dollar + 1)..];
            body = body[..dollar];
            foreach (string modifier in modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string name = modifier.TrimStart('~');
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    name = name[..eq];
                }
                if (UnsupportedModifiers.Contains(name))
                {
                    return LineStatus.Unsupported;
                }
            }
        }

        body = body.TrimEnd('|');
        if (body.EndsWith('^'))
        {
            body = body[..^1];
        }

        if (body.Length == 0 || body.IndexOfAny(['^', '/', ':', '?', '=', '&', '|']) >= 0)
        {
            return LineStatus.Unsupported; // URL path, port, or query: not a DNS-level rule
        }

        if (!anchored && !body.Contains('.') && !body.Contains('*'))
        {
            return LineStatus.Unsupported; // "@@keyword" style partial matches
        }

        return TryAddDomain(body, isAllow, includeSubdomains: anchored, null, output, options, stats)
            ? LineStatus.Parsed
            : LineStatus.Invalid;
    }

    private static LineStatus ParseDnsmasq(string text, List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        // address=/a.com/b.com/0.0.0.0 -> ["address=", "a.com", "b.com", "0.0.0.0"]
        string[] parts = text.Split('/');
        if (parts.Length < 3)
        {
            return LineStatus.Invalid;
        }

        string directive = parts[0].TrimEnd('=').Trim().ToLowerInvariant();
        string target = parts[^1].Trim();
        bool isAllow;

        switch (directive)
        {
            case "address":
                if (target.Length == 0 || target == "#")
                {
                    isAllow = false;
                }
                else if (DomainRules.TryParseStrictIp(target, out var targetIp) && targetIp is not null && DomainRules.IsSinkAddress(targetIp))
                {
                    isAllow = false;
                }
                else
                {
                    return LineStatus.Unsupported; // redirects a domain to a real address
                }
                break;

            case "local":
                isAllow = false;
                break;

            case "server":
                if (target.Length == 0)
                {
                    isAllow = false; // answered locally only, i.e. blocked
                }
                else if (target == "#")
                {
                    isAllow = true; // forwarded to the normal upstream: an exception
                }
                else
                {
                    return LineStatus.Unsupported; // conditional forwarding
                }
                break;

            default:
                return LineStatus.Unsupported;
        }

        int added = 0;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length > 0 && TryAddDomain(parts[i], isAllow, includeSubdomains: true, null, output, options, stats))
            {
                added++;
            }
        }
        return added > 0 ? LineStatus.Parsed : LineStatus.Invalid;
    }

    private static LineStatus ParseUnboundZone(string text, List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        // local-zone: "example.com." always_null
        string rest = text["local-zone:".Length..].Trim();
        string[] tokens = rest.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return LineStatus.Invalid;
        }

        string zone = tokens[0].Trim('"');
        string type = tokens[1];
        bool isAllow;
        if (UnboundBlockTypes.Contains(type))
        {
            isAllow = false;
        }
        else if (UnboundAllowTypes.Contains(type))
        {
            isAllow = true;
        }
        else
        {
            return LineStatus.Unsupported;
        }

        return TryAddDomain(zone, isAllow, includeSubdomains: true, null, output, options, stats)
            ? LineStatus.Parsed
            : LineStatus.Invalid;
    }

    private static LineStatus ParseRpz(string[] tokens, int cnameIndex, List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        if (cnameIndex + 1 >= tokens.Length)
        {
            return LineStatus.Invalid;
        }

        string name = tokens[0];
        string target = tokens[cnameIndex + 1];
        bool isAllow;
        if (target is "." or "*." || target.Equals("rpz-drop.", StringComparison.OrdinalIgnoreCase))
        {
            isAllow = false;
        }
        else if (target.Equals("rpz-passthru.", StringComparison.OrdinalIgnoreCase))
        {
            isAllow = true;
        }
        else
        {
            return LineStatus.Unsupported; // CNAME to a real name: a redirect
        }

        return TryAddDomain(name, isAllow, includeSubdomains: false, null, output, options, stats)
            ? LineStatus.Parsed
            : LineStatus.Invalid;
    }

    /// <summary>Lines whose only names are localhost-style junk count as boilerplate, not as invalid content.</summary>
    private static LineStatus StatusFor(int added, bool hadJunk) =>
        added > 0 ? LineStatus.Parsed : hadJunk ? LineStatus.Empty : LineStatus.Invalid;

    /// <summary>Normalises and validates one domain or wildcard token and adds it to the output.</summary>
    internal static bool TryAddDomain(string raw, bool isAllow, bool includeSubdomains, string? sourceIp,
                                      List<HostEntry> output, ParserOptions options, ParseStats stats)
    {
        string candidate = raw.Trim().Trim('"').TrimEnd('.').ToLowerInvariant();
        if (candidate.Length == 0)
        {
            stats.InvalidDomains++;
            return false;
        }

        WildcardKind wildcard = WildcardKind.None;
        string core = candidate;
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            wildcard = WildcardKind.Subdomains;
            core = candidate[2..];
        }
        else if (candidate.StartsWith('.'))
        {
            wildcard = WildcardKind.Subdomains; // ".example.com" is a common shorthand for *.example.com
            core = candidate.TrimStart('.');
        }
        else if (candidate.EndsWith(".*", StringComparison.Ordinal))
        {
            wildcard = WildcardKind.AnyTld;
            core = candidate[..^2];
        }

        if (core.Contains('*'))
        {
            wildcard = WildcardKind.Pattern;
            core = candidate;
        }

        if (DomainRules.IsJunk(core) || DomainRules.TryParseStrictIp(core, out _))
        {
            stats.JunkEntries++;
            return false;
        }

        if (!DomainRules.TryConvertToAscii(core, out string ascii, out bool converted))
        {
            stats.InvalidDomains++;
            return false;
        }
        if (converted)
        {
            stats.IdnConverted++;
            core = ascii;
        }

        bool valid = wildcard switch
        {
            WildcardKind.Pattern => DomainRules.IsValidPattern(core),
            WildcardKind.AnyTld => !options.ValidateDomains || DomainRules.IsValidHostname(core + ".tld"),
            _ => !options.ValidateDomains || DomainRules.IsValidHostname(core)
        };
        if (!valid)
        {
            stats.InvalidDomains++;
            return false;
        }

        if (options.RejectPublicSuffixes && wildcard is WildcardKind.None or WildcardKind.Subdomains && DomainRules.IsPublicSuffix(core))
        {
            stats.PublicSuffixesRejected++;
            return false;
        }

        output.Add(new HostEntry(core, EntryKind.Domain, wildcard, isAllow, includeSubdomains, sourceIp));
        return true;
    }

    /// <summary>
    /// Cosmetic (element hiding / scriptlet) rules such as "example.com##.ad" or "##.banner". A "##" preceded by
    /// whitespace is a hosts-file comment instead ("0.0.0.0 a.com ## note").
    /// </summary>
    private static bool IsCosmeticRule(string text)
    {
        ReadOnlySpan<string> markers = ["##", "#@#", "#?#", "#$#", "#%#", "#@$#", "#@?#"];
        foreach (string marker in markers)
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index == 0 || (index > 0 && !char.IsWhiteSpace(text[index - 1])))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsZoneBoilerplate(string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (token.Equals("SOA", StringComparison.OrdinalIgnoreCase) || token.Equals("NS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseCidr(string token, out string? cidr)
    {
        cidr = null;
        int slash = token.IndexOf('/');
        if (slash <= 0 || slash == token.Length - 1)
        {
            return false;
        }

        if (!DomainRules.TryParseStrictIp(token[..slash], out var address) || address is null)
        {
            return false;
        }

        int maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(token[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefix)
            || prefix > maxPrefix)
        {
            return false;
        }

        cidr = $"{address}/{prefix}";
        return true;
    }
}
