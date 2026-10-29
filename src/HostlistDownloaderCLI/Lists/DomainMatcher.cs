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

using System.Text.RegularExpressions;

namespace HostlistDownloader.Lists;

/// <summary>
/// Matches block entries against a set of allow (or never-block) entries, on parsed domains rather than formatted
/// lines, so "example.com" in a whitelist matches "0.0.0.0 example.com" from a blocklist.
/// Suffix wildcards (*.example.com) are checked with one set lookup per label instead of a regex per entry;
/// regexes are only used for the rare patterns that cannot be expressed as a suffix.
/// </summary>
internal sealed class DomainMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subdomainsOf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _patternText = new(StringComparer.Ordinal);
    private readonly List<Regex> _patterns = [];
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _subdomainsLookup;

    internal DomainMatcher()
    {
        _subdomainsLookup = _subdomainsOf.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    internal int Count { get; private set; }

    /// <param name="entry">The allow entry.</param>
    /// <param name="protectSubdomains">Treat a plain domain as covering its subdomains too (used for neverBlock).</param>
    internal void Add(in HostEntry entry, bool protectSubdomains = false)
    {
        if (entry.Kind != EntryKind.Domain)
        {
            return;
        }

        Count++;
        switch (entry.Wildcard)
        {
            case WildcardKind.None:
                _exact.Add(entry.Value);
                if (entry.IncludeSubdomains || protectSubdomains)
                {
                    _subdomainsOf.Add(entry.Value);
                }
                break;

            case WildcardKind.Subdomains:
                _subdomainsOf.Add(entry.Value);
                break;

            case WildcardKind.AnyTld:
            case WildcardKind.Pattern:
                string text = entry.DisplayText;
                if (_patternText.Add(text))
                {
                    string regex = "^" + Regex.Escape(text).Replace("\\*", "[a-z0-9._-]*") + "$";
                    _patterns.Add(new Regex(regex, RegexOptions.CultureInvariant, RegexTimeout));
                }
                break;
        }
    }

    /// <summary>True if the block entry is covered by an allow entry.</summary>
    internal bool Matches(in HostEntry block)
    {
        if (block.Kind != EntryKind.Domain || Count == 0)
        {
            return false;
        }

        string value = block.Value;
        switch (block.Wildcard)
        {
            case WildcardKind.None:
                return _exact.Contains(value) || HasAllowedParent(value) || MatchesPattern(value);

            case WildcardKind.Subdomains:
                // *.x.com is only fully allowed by an allow rule covering all of x.com's subdomains.
                return _subdomainsOf.Contains(value) || HasAllowedParent(value) || _patternText.Contains(block.DisplayText);

            default:
                return _patternText.Contains(block.DisplayText);
        }
    }

    /// <summary>True if any proper parent of <paramref name="domain"/> has an allow rule covering its subdomains.</summary>
    private bool HasAllowedParent(string domain)
    {
        if (_subdomainsOf.Count == 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = domain;
        for (int i = span.IndexOf('.'); i >= 0; )
        {
            ReadOnlySpan<char> parent = span[(i + 1)..];
            if (_subdomainsLookup.Contains(parent))
            {
                return true;
            }
            int next = parent.IndexOf('.');
            if (next < 0)
            {
                break;
            }
            i += next + 1;
        }
        return false;
    }

    private bool MatchesPattern(string domain)
    {
        foreach (Regex pattern in _patterns)
        {
            try
            {
                if (pattern.IsMatch(domain))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern must not stall the build; treat as no match.
            }
        }
        return false;
    }
}

/// <summary>Set of block-entry domains with fast "is any parent present" lookups, used for compression and exceptions.</summary>
internal sealed class DomainTree
{
    private readonly HashSet<string> _domains = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    internal DomainTree()
    {
        _lookup = _domains.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    internal void Add(string domain) => _domains.Add(domain);

    internal bool Contains(string domain) => _domains.Contains(domain);

    internal bool HasParent(string domain)
    {
        ReadOnlySpan<char> span = domain;
        int i = span.IndexOf('.');
        while (i >= 0)
        {
            ReadOnlySpan<char> parent = span[(i + 1)..];
            if (_lookup.Contains(parent))
            {
                return true;
            }
            int next = parent.IndexOf('.');
            if (next < 0)
            {
                return false;
            }
            i += next + 1;
        }
        return false;
    }
}
