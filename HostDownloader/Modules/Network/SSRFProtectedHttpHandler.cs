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
using System.Net;
using System.Net.Sockets;

namespace HostlistDownloader.Modules.Network
{
    /// <summary>
    /// Prevents DNS rebinding attacks by resolving the domain name exactly once per connection 
    /// and blocking any internal or private IP addresses. Instead of building a full custom 
    /// HTTP client, this uses a focused callback that only controls which IP gets connected. 
    /// This lets .NET's built-in networking stack safely handle TLS, HTTP protocols, and 
    /// redirects automatically, avoiding the complexity and hidden bugs of reinventing the wheel.
    /// </summary>
    internal static class SSRFProtectedHttpHandler
    {
        /// <summary>
        /// Creates a SocketsHttpHandler configured to route all outbound connections through the SSRF-safe connect callback below.
        /// </summary>
        internal static SocketsHttpHandler Create()
        {
            TraceLogger.Log("SSRF protection: Creating SSRF-protected SocketsHttpHandler...", Enums.StatusSeverityType.Debug);
            return new SocketsHttpHandler
            {
                ConnectCallback = ConnectAsync
            };
        }

        /// <summary>
        /// This callback is invoked by SocketsHttpHandler for each outbound connection. It resolves the hostname to IP addresses, filters out any internal/private addresses, and connects to the first public IP found. If no public IPs are available, it throws an exception to prevent SSRF attacks.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="HttpRequestException"></exception>
        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            string host = context.DnsEndPoint.Host;
            int port = context.DnsEndPoint.Port;

            IPAddress[] addresses;
            try
            {
                TraceLogger.Log($"SSRF protection: Resolving DNS for '{host}'...", Enums.StatusSeverityType.Debug);
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                TraceLogger.Log($"SSRF protection: DNS resolution failed for '{host}': {ex.Message}", Enums.StatusSeverityType.Warning);
                throw;
            }

            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"DNS resolution for '{host}' returned no addresses.");
            }

            IPAddress? chosen = null;
            foreach (var addr in addresses)
            {
                if (URLSecurityValidator.IsInternalAddressSSRF(addr))
                {
                    TraceLogger.Log($"SSRF protection: DNS resolved '{host}' to prohibited internal/private IP {addr}. Skipping this address.", Enums.StatusSeverityType.Warning);
                    continue;
                }
                chosen = addr;
                break;
            }

            if (chosen == null)
            {
                throw new HttpRequestException($"SSRF protection: '{host}' resolved only to internal/private IP address(es). Aborting connection.");
            }
            TraceLogger.Log($"SSRF protection: DNS resolved '{host}' to public IP {chosen}. Proceeding with connection.", Enums.StatusSeverityType.Debug);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                TraceLogger.Log($"SSRF protection: Connecting to {chosen}:{port}...", Enums.StatusSeverityType.Debug);
                await socket.ConnectAsync(chosen, port, cancellationToken).ConfigureAwait(false);
                TraceLogger.Log($"SSRF protection: Connected to {chosen}:{port}.", Enums.StatusSeverityType.Debug);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                TraceLogger.Log($"SSRF protection: Connection to {chosen}:{port} failed. Disposing socket.", Enums.StatusSeverityType.Warning);
                socket.Dispose();
                throw;
            }
        }
    }
}