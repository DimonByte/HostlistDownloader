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

using System.Text;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Diagnostics;

/// <summary>
/// Makes untrusted or sensitive text safe to write to the log.
/// </summary>
internal static partial class LogRedactor
{
    private const string ContinuationPrefix = "    | ";

    /// <summary>
    /// Hides credentials (user:pass@) and query strings in any URL in the message. Private list URLs often
    /// carry access tokens in the query string, and log files get pasted into bug reports.
    /// </summary>
    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains("://", StringComparison.Ordinal))
        {
            return message;
        }

        return UrlRegex().Replace(message, static m =>
            m.Groups["scheme"].Value
            + (m.Groups["userinfo"].Success ? "***@" : string.Empty)
            + m.Groups["rest"].Value
            + (m.Groups["query"].Success ? "?***" : string.Empty));
    }

    /// <summary>
    /// Prevents log forging: server-supplied strings (status text, exception messages, list content) could
    /// contain CR/LF and fake a new "[timestamp] [ERROR]" entry. Continuation lines are indented and marked,
    /// and other control characters (including ANSI escape sequences) are replaced.
    /// </summary>
    public static string Neutralise(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        bool needsWork = false;
        foreach (char c in message)
        {
            if (char.IsControl(c) && c != '\t')
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork)
        {
            return message;
        }

        var sb = new StringBuilder(message.Length + 16);
        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];
            if (c == '\r')
            {
                if (i + 1 < message.Length && message[i + 1] == '\n')
                {
                    i++;
                }
                sb.Append(Environment.NewLine).Append(ContinuationPrefix);
            }
            else if (c == '\n')
            {
                sb.Append(Environment.NewLine).Append(ContinuationPrefix);
            }
            else if (char.IsControl(c) && c != '\t')
            {
                sb.Append('?');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<userinfo>[^/\s@?#]+@)?(?<rest>[^\s?#]*)(?<query>\?[^\s#]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
