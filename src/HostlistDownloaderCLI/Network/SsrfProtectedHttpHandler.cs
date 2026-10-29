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

using HostlistDownloader.Config;
using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HostlistDownloader.Network;

/// <summary>
/// Builds the HTTP handler used for list downloads. A connect callback resolves the host once per connection,
/// drops internal/reserved addresses, and connects to the resolved IP directly, so a DNS rebinding answer cannot
/// slip an internal address in between the check and the connection. TLS, HTTP versions, decompression and
/// redirects are left to .NET; every redirect hop goes through the same callback.
/// </summary>
internal static class SsrfProtectedHttpHandler
{
    internal static SocketsHttpHandler Create(NetworkSettings network, Uri? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(network.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // Without an explicit proxy the system proxy is NOT used: with a proxy the connection goes to the proxy
            // and the proxy resolves the target, which would bypass the address check below.
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, proxy, cancellationToken)
        };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Uri? proxy, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;
        bool isProxyConnection = proxy is not null
            && string.Equals(host, proxy.Host, StringComparison.OrdinalIgnoreCase)
            && port == proxy.Port;

        // DNS failures are ordinary (often transient) network errors, never an "SSRF attempt".
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        List<IPAddress> allowed = [.. addresses.Where(a => isProxyConnection || !AddressPolicy.IsBlocked(a))];

        if (allowed.Count == 0)
        {
            throw new SsrfBlockedException($"'{host}' resolves only to internal or reserved addresses ({string.Join(", ", addresses.Select(a => a.ToString()))}). Connection refused.");
        }

        // Try IPv4 first: on dual-stack machines with broken IPv6 the A record still works.
        allowed.Sort((a, b) => a.AddressFamily.CompareTo(b.AddressFamily));

        Exception? lastError = null;
        foreach (IPAddress address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                TraceLogger.Log($"Connected to {host} ({address}:{port}).", LogSeverity.Debug);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                TraceLogger.Log($"Connection to {host} ({address}:{port}) failed: {ex.Message}. Trying the next address.", LogSeverity.Debug);
                lastError = ex;
            }
        }

        // An ordinary connection failure: let the retry logic treat it as transient.
        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>
    /// With a proxy the target is resolved by the proxy, so the connect callback cannot see it. This best-effort
    /// pre-check refuses targets that resolve to internal addresses locally.
    /// </summary>
    internal static async Task EnsurePublicHostAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return; // the proxy may resolve names we cannot; nothing to check
            }
        }

        if (addresses.Length > 0 && addresses.All(AddressPolicy.IsBlocked))
        {
            throw new SsrfBlockedException($"'{host}' resolves only to internal or reserved addresses. Connection refused.");
        }
    }
}
