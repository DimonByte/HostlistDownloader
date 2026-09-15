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

using HostlistDownloader.Modules.Helpers;
using HostlistDownloader.Modules.HostlistManagement.Generation;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HostlistDownloader.Modules.Network
{
    internal class URLSecurityValidator
    {

        private static readonly string[] HtmlSignatures =
        [
            "<html",
            "<!doctype html",
            "<head>",
            "<body>",
            "<!doctype",
            "<!document",
            "<!--"
        ];

        private static readonly string[] AllowedContentTypePrefixes =
        [
            "text/plain",
            "text/comma-separated-values",
            "application/octet-stream",
            "text/csv"
        ];
        private const int ContentValidationBufferSize = 4096;

        internal static bool ValidateURLFromConfigInstance(string urlInstance)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(urlInstance) || urlInstance.StartsWith('#'))
                    return false;
                if (!Uri.TryCreate(urlInstance, UriKind.Absolute, out var uriResult) || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
                {
                    TraceLogger.Log($"Invalid URL format in config instance: {urlInstance}", Enums.StatusSeverityType.Warning);
                    return false;
                }
                urlInstance = urlInstance.Trim();
                if (urlInstance.Length == 0)
                {
                    TraceLogger.Log($"Empty URL in config instance: {urlInstance}", Enums.StatusSeverityType.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                HostlistOrchestrator.ProblemDuringUpdate = true;
                TraceLogger.Log($"Error validating URL {urlInstance} in config instance: {ex}", Enums.StatusSeverityType.Fatal, ErrorCodes.InvalidConfigEntry);
                return false;
            }
        }

        /// <summary>
        ///Prevents SSRF by resolving the host via DNS and checking if any resulting IP is in a private, loopback, or link-local range.
        /// </summary>
        internal static async Task<bool> IsInternalAddressDNS(Uri uri)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
                foreach (var addr in addresses)
                {
                    if (addr.Equals(IPAddress.Loopback)) return true;

                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] bytes = addr.GetAddressBytes();

                        //IPv4 Loopback range (127.0.0.0/8)
                        //(e.g., 127.0.0.2)
                        if (bytes[0] == 127) return true;

                        //IPv4 Link-Local (169.254.0.0/16)
                        if (bytes[0] == 169 && bytes[1] == 254) return true;
                        //10.0.0.0/8
                        if (bytes[0] == 10) return true;
                        //172.16.0.0/12 (172.16.x.x through 172.31.x.x)
                        if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;
                        //192.168.0.0/16
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                    }
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        //IPv6 Link-Local (fe80::/10)
                        if (addr.IsIPv6LinkLocal) return true;
                        //IPv6 Unique Local Address (fc00::/7)
                        byte[] v6Bytes = addr.GetAddressBytes();
                        if (v6Bytes[0] == 0xfc || v6Bytes[0] == 0xfd) return true;
                    }
                }
            }
            catch (SocketException)
            {
                TraceLogger.Log($"DNS resolution failed for {uri.DnsSafeHost}. Treating as internal to prevent SSRF.", Enums.StatusSeverityType.Warning);
                return true;
            }
            TraceLogger.Log($"DNS resolution for {uri.DnsSafeHost} returned no internal/private IPs.", Enums.StatusSeverityType.Debug);
            return false;
        }

        /// <summary>
        /// Returns true if the given address is in a private, loopback, or link-local range. Used for SSRF protection class.
        /// </summary>
        internal static bool IsInternalAddressSSRF(IPAddress address)
        {
            if (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
                return true;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();

                // 127.0.0.0/8
                if (bytes[0] == 127) return true;
                // 169.254.0.0/16 (link-local - crucial for cloud metadata endpoints)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fe80::/10
                if (address.IsIPv6LinkLocal) return true;
                // fc00::/7 (unique local)
                byte[] v6Bytes = address.GetAddressBytes();
                if (v6Bytes[0] == 0xfc || v6Bytes[0] == 0xfd) return true;
            }

            return false;
        }

        /// <summary>
        /// Inspects the leading bytes of the content to determine whether it looks like a plain-text
        /// host list or an HTML / non-text document.
        /// </summary>
        internal static bool IsPlainTextContent(byte[] content)
        {
            if (content == null || content.Length == 0)
                return true; // Empty/null is treated as valid (adjust if needed)

            int inspectLength = Math.Min(content.Length, ContentValidationBufferSize);
            string prefix;
            try
            {
                prefix = Encoding.UTF8.GetString(content, 0, inspectLength);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            int trimmedStart = 0;
            while (trimmedStart < Math.Min(prefix.Length, 64) &&
                   (char.IsWhiteSpace(prefix[trimmedStart]) || prefix[trimmedStart] == '\uFEFF'))
            {
                trimmedStart++;
            }

            ReadOnlySpan<char> effectivePrefix = prefix.AsSpan(trimmedStart);

            foreach (string sig in HtmlSignatures)
            {
                if (effectivePrefix.StartsWith(sig, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (effectivePrefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                effectivePrefix.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int controlChars = 0;
            for (int i = 0; i < inspectLength; i++)
            {
                byte b = content[i];
                if (b < 32 && b != 9 && b != 10 && b != 13)
                {
                    controlChars++;
                }
            }
            return controlChars <= 1;
        }

        /// <summary>
        /// Returns true if the HTTP Content-Type header matches one of the allowed types for a host list.
        /// </summary>
        internal static bool IsAllowedContentType(string mediaType)
        {
            string normalized = mediaType.Trim().ToLowerInvariant();
            foreach (string prefix in AllowedContentTypePrefixes)
            {
                if (normalized == prefix || normalized.StartsWith(prefix + ";", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}