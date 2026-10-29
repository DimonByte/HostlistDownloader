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

using System.Net;

namespace HostlistDownloader.Lists;

internal enum FormatNote
{
    None,

    /// <summary>"*.example.com" written as "example.com" (or an equivalent that already covers subdomains).</summary>
    WildcardStripped,

    /// <summary>The wildcard cannot be expressed in this format, so the entry was left out.</summary>
    WildcardDropped,

    /// <summary>The entry type does not exist in this format (an IP in a domain list, or a domain without an IP in iponly).</summary>
    NotApplicable
}

/// <summary>Writes normalised entries in the configured output syntax.</summary>
internal sealed class OutputFormatter(OutputFormat format, IPAddress hostsBlockIp)
{
    private readonly string _blockIp = hostsBlockIp.ToString();

    internal OutputFormat Format => format;

    /// <summary>Formats a block entry. Returns null when the entry cannot be written in this format.</summary>
    internal string? FormatBlock(in HostEntry entry, out FormatNote note)
    {
        note = FormatNote.None;

        if (entry.Kind == EntryKind.Ip)
        {
            if (format == OutputFormat.IpOnly)
            {
                return entry.Value;
            }
            note = FormatNote.NotApplicable;
            return null;
        }

        string d = entry.Value;
        switch (format)
        {
            case OutputFormat.Hosts:
                if (entry.Wildcard != WildcardKind.None)
                {
                    note = FormatNote.WildcardDropped;
                    return null;
                }
                return $"{_blockIp} {d}";

            case OutputFormat.Domain:
                switch (entry.Wildcard)
                {
                    case WildcardKind.None:
                        return d;
                    case WildcardKind.Subdomains:
                        note = FormatNote.WildcardStripped;
                        return d;
                    default:
                        note = FormatNote.WildcardDropped;
                        return null;
                }

            case OutputFormat.Wildcard:
                return entry.Wildcard switch
                {
                    WildcardKind.AnyTld => d + ".*",
                    WildcardKind.Pattern => d,
                    _ => "*." + d
                };

            case OutputFormat.Adblock:
                switch (entry.Wildcard)
                {
                    case WildcardKind.Subdomains:
                        note = FormatNote.WildcardStripped; // ||example.com^ already matches every subdomain
                        return $"||{d}^";
                    case WildcardKind.AnyTld:
                        return $"||{d}.*^";
                    default:
                        return $"||{d}^";
                }

            case OutputFormat.Dnsmasq:
                return StripOrDrop(entry, out note, static v => $"address=/{v}/0.0.0.0");

            case OutputFormat.Unbound:
                return StripOrDrop(entry, out note, static v => $"local-zone: \"{v}.\" always_null");

            case OutputFormat.Rpz:
                return entry.Wildcard switch
                {
                    WildcardKind.None => $"{d} CNAME .",
                    WildcardKind.Subdomains => $"*.{d} CNAME .",
                    _ => DropWildcard(out note)
                };

            case OutputFormat.IpOnly:
                if (entry.SourceIp is not null && IPAddress.TryParse(entry.SourceIp, out IPAddress? ip) && !DomainRules.IsSinkAddress(ip))
                {
                    return entry.SourceIp;
                }
                note = FormatNote.NotApplicable;
                return null;

            default:
                return d;
        }
    }

    /// <summary>Formats an entry for the combined whitelist output file.</summary>
    internal string? FormatAllowList(in HostEntry entry)
    {
        if (entry.Kind == EntryKind.Ip)
        {
            return null;
        }

        return format switch
        {
            OutputFormat.Adblock => entry.Wildcard == WildcardKind.AnyTld ? $"@@||{entry.Value}.*^" : $"@@||{entry.Value}^",
            _ => entry.DisplayText
        };
    }

    /// <summary>
    /// For formats where one entry blocks a whole domain tree, an allowed subdomain of a blocked domain needs an
    /// explicit exception line in the combined list. Returns null if the format has no exception syntax.
    /// </summary>
    internal string? FormatException(in HostEntry entry)
    {
        if (entry.Kind == EntryKind.Ip || entry.Wildcard is WildcardKind.AnyTld or WildcardKind.Pattern)
        {
            return null;
        }

        return format switch
        {
            OutputFormat.Adblock => $"@@||{entry.Value}^",
            OutputFormat.Unbound => $"local-zone: \"{entry.Value}.\" transparent",
            OutputFormat.Dnsmasq => $"server=/{entry.Value}/#",
            OutputFormat.Rpz => $"{entry.Value} CNAME rpz-passthru.",
            _ => null
        };
    }

    private static string? StripOrDrop(in HostEntry entry, out FormatNote note, Func<string, string> write)
    {
        note = FormatNote.None;
        switch (entry.Wildcard)
        {
            case WildcardKind.None:
                return write(entry.Value);
            case WildcardKind.Subdomains:
                note = FormatNote.WildcardStripped; // the base entry already covers subdomains in this format
                return write(entry.Value);
            default:
                note = FormatNote.WildcardDropped;
                return null;
        }
    }

    private static string? DropWildcard(out FormatNote note)
    {
        note = FormatNote.WildcardDropped;
        return null;
    }
}
