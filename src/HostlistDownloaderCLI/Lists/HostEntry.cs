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

internal enum EntryKind
{
    Domain,
    Ip
}

internal enum WildcardKind
{
    /// <summary>A plain domain: example.com</summary>
    None,

    /// <summary>*.example.com (Value holds example.com)</summary>
    Subdomains,

    /// <summary>example.* (Value holds example)</summary>
    AnyTld,

    /// <summary>Any other use of '*', e.g. ads*.example.com (Value holds the whole pattern)</summary>
    Pattern
}

/// <summary>
/// One normalised rule parsed from a source line. Domain values are lowercase ASCII (IDNs converted to punycode)
/// without a trailing dot.
/// </summary>
/// <param name="Value">The domain (without wildcard prefix/suffix), the wildcard pattern, or the IP address.</param>
/// <param name="Kind">Domain or bare IP address entry.</param>
/// <param name="Wildcard">How the entry uses wildcards.</param>
/// <param name="IsAllow">True for exception/allow rules (adblock @@, unbound transparent, RPZ passthru).</param>
/// <param name="IncludeSubdomains">
/// True when the source syntax already means "this domain and all its subdomains" (adblock ||x^, dnsmasq
/// address=/x/, unbound local-zone). Used when matching whitelist entries.
/// </param>
/// <param name="SourceIp">The IP address from a hosts-file line, if any (used by the iponly format).</param>
internal readonly record struct HostEntry(
    string Value,
    EntryKind Kind,
    WildcardKind Wildcard,
    bool IsAllow,
    bool IncludeSubdomains = false,
    string? SourceIp = null)
{
    /// <summary>Identity used for de-duplication: the same rule from two sources is one entry.</summary>
    internal (string Value, WildcardKind Wildcard, EntryKind Kind) Key => (Value, Wildcard, Kind);

    /// <summary>The rule written back in plain wildcard notation, for reports.</summary>
    internal string DisplayText => Wildcard switch
    {
        WildcardKind.Subdomains => "*." + Value,
        WildcardKind.AnyTld => Value + ".*",
        _ => Value
    };
}

/// <summary>Result of parsing one source line.</summary>
internal enum LineStatus
{
    /// <summary>Blank line, comment, or list header.</summary>
    Empty,

    /// <summary>At least one entry was produced.</summary>
    Parsed,

    /// <summary>A valid rule that cannot be expressed as a DNS-level entry (cosmetic filters, URL paths, regex rules, redirects).</summary>
    Unsupported,

    /// <summary>Not a recognisable rule, or every domain on the line was rejected.</summary>
    Invalid
}

/// <summary>Counters filled in while parsing; shared across all lines of a build.</summary>
internal sealed class ParseStats
{
    public long Lines;
    public long EmptyOrComment;
    public long ParsedLines;
    public long Unsupported;
    public long Invalid;
    public long JunkEntries;
    public long InvalidDomains;
    public long PublicSuffixesRejected;
    public long IdnConverted;

    public void Add(ParseStats other)
    {
        Lines += other.Lines;
        EmptyOrComment += other.EmptyOrComment;
        ParsedLines += other.ParsedLines;
        Unsupported += other.Unsupported;
        Invalid += other.Invalid;
        JunkEntries += other.JunkEntries;
        InvalidDomains += other.InvalidDomains;
        PublicSuffixesRejected += other.PublicSuffixesRejected;
        IdnConverted += other.IdnConverted;
    }
}
