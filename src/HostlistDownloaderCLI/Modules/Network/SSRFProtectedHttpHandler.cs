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

using HostlistDownloaderCLI.Modules.Helpers;
using System.Net;
using System.Net.Sockets;

namespace HostlistDownloaderCLI.Modules.Network
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
                UseProxy = false,
                ConnectCallback = ConnectAsync
            };
        }

        /// <summary>
        /// This callback is invoked by SocketsHttpHandler for each outbound connection. It resolves the hostname to IP addresses, 
        /// filters out any internal/private addresses, and connects to the first public IP found. 
        /// If connection fails, it falls back to the next allowed address. If no public IPs are available, it throws an exception.
        /// </summary>
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
                TraceLogger.Log($"SSRF protection: DNS resolution failed for '{host}': {ex.Message}", Enums.StatusSeverityType.Warn);
                throw;
            }

            if (addresses.Length == 0)
            {
                throw new ExceptionHandler.SSRFProtectionException($"DNS resolution for '{host}' returned no addresses.");
            }

            // Filter out internal/private IPs using the hardened validator
            var allowedAddresses = addresses.Where(addr => !URLSecurityValidator.IsInternalAddressSSRF(addr)).ToList();

            if (allowedAddresses.Count == 0)
            {
                throw new ExceptionHandler.SSRFProtectionException($"SSRF protection: '{host}' resolved only to internal/private IP address(es). Aborting connection.");
            }

            //Prefer IPv4 for reliability on dual-stack hosts with broken IPv6
            allowedAddresses.Sort((a, b) => a.AddressFamily.CompareTo(b.AddressFamily));

            Exception lastException = null;
            foreach (var addr in allowedAddresses)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    TraceLogger.Log($"SSRF protection: Connecting to {addr}:{port}...", Enums.StatusSeverityType.Debug);
                    await socket.ConnectAsync(addr, port, cancellationToken).ConfigureAwait(false);
                    TraceLogger.Log($"SSRF protection: Connected to {addr}:{port}.", Enums.StatusSeverityType.Debug);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    // Don't swallow cancellation tokens
                    if (cancellationToken.IsCancellationRequested)
                    {
                        socket.Dispose();
                        throw;
                    }

                    TraceLogger.Log($"SSRF protection: Connection to {addr}:{port} failed. Disposing socket and trying next address.", Enums.StatusSeverityType.Warn);
                    socket.Dispose();
                    lastException = ex;
                }
            }

            // Throw dedicated exception with fallback context instead of generic HttpRequestException
            throw new ExceptionHandler.SSRFProtectionException($"SSRF protection: All allowed addresses for '{host}' failed to connect. Last error: {lastException?.Message}", lastException);
        }
    }
}