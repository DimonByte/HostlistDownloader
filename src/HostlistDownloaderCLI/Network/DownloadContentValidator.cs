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
using System.Text.Unicode;

namespace HostlistDownloader.Network;

internal readonly record struct ContentCheck(bool Accepted, string? Reason, long SampledRules);

/// <summary>Checks that a download is really a list and not an error page, rate-limit notice or binary file.</summary>
internal static class DownloadContentValidator
{
    internal const int SniffSize = 4096;
    private const int SampleLines = 500;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/csv",
        "text/comma-separated-values",
        "application/octet-stream",
        "binary/octet-stream"
    };

    private static readonly string[] HtmlSignatures =
    [
        "<html",
        "<!doctype",
        "<head",
        "<body",
        "<!--",
        "<?xml"
    ];

    /// <summary>Content-Type check. A missing header is allowed; the content is sniffed instead.</summary>
    internal static bool IsAllowedContentType(string? mediaType) =>
        string.IsNullOrWhiteSpace(mediaType) || AllowedContentTypes.Contains(mediaType.Trim());

    /// <summary>
    /// Inspects the first bytes: rejects HTML/XML documents, invalid UTF-8 and binary data. The sample may end in
    /// the middle of a multi-byte character, which is not treated as an error.
    /// </summary>
    internal static bool LooksLikeText(ReadOnlySpan<byte> prefix, bool truncated, out string? reason)
    {
        reason = null;
        if (prefix.Length == 0)
        {
            return true;
        }

        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            prefix = prefix[3..]; // UTF-8 BOM
        }

        ReadOnlySpan<byte> checkable = prefix;
        if (truncated)
        {
            // Drop an incomplete trailing UTF-8 sequence (at most 3 bytes) cut off by the sample boundary.
            int back = 0;
            while (back < 3 && back < checkable.Length && (checkable[^(back + 1)] & 0xC0) == 0x80)
            {
                back++;
            }
            if (back < checkable.Length && (checkable[^(back + 1)] & 0xC0) == 0xC0)
            {
                checkable = checkable[..^(back + 1)];
            }
        }

        if (!Utf8.IsValid(checkable))
        {
            reason = "content is not valid UTF-8 text";
            return false;
        }

        int controlChars = 0;
        foreach (byte b in checkable)
        {
            if (b < 32 && b is not (9 or 10 or 13))
            {
                controlChars++;
            }
        }
        if (controlChars > 1)
        {
            reason = "content contains binary data";
            return false;
        }

        string text = System.Text.Encoding.UTF8.GetString(checkable).TrimStart();
        foreach (string signature in HtmlSignatures)
        {
            if (text.StartsWith(signature, StringComparison.OrdinalIgnoreCase))
            {
                reason = "content is an HTML/XML page, not a list";
                return false;
            }
        }
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            // JSON error bodies ({"message": "rate limited"}). "[Adblock Plus 2.0]" headers start with '[' too.
            if (!text.StartsWith("[Adblock", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("[uBlock", StringComparison.OrdinalIgnoreCase)
                && !text.StartsWith("[AdGuard", StringComparison.OrdinalIgnoreCase))
            {
                reason = "content looks like JSON, not a list";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Requires that the first non-comment lines mostly parse as hosts, domain or adblock rules. Catches rate-limit
    /// pages and other plain-text error bodies that pass the text sniff.
    /// </summary>
    internal static ContentCheck CheckLooksLikeList(string path)
    {
        var stats = new ParseStats();
        var buffer = new List<HostEntry>(4);
        int sampled = 0;
        foreach (string line in File.ReadLines(path))
        {
            buffer.Clear();
            LineStatus status = HostEntryParser.ParseLine(line, buffer, ParserOptions.Lenient, stats);
            if (status != LineStatus.Empty && ++sampled >= SampleLines)
            {
                break;
            }
        }

        long recognised = stats.ParsedLines + stats.Unsupported;
        if (sampled == 0)
        {
            return new ContentCheck(true, null, 0); // only comments; accepted, the build reports it as empty
        }
        if (recognised == 0)
        {
            return new ContentCheck(false, $"none of the first {sampled} line(s) is a host, domain or filter rule", 0);
        }
        if (stats.Invalid > recognised)
        {
            return new ContentCheck(false, $"{stats.Invalid} of the first {sampled} line(s) are not host, domain or filter rules", stats.ParsedLines);
        }
        return new ContentCheck(true, null, stats.ParsedLines);
    }
}
