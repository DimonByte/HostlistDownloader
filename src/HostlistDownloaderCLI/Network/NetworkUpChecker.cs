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

using HostlistDownloader.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace HostlistDownloader.Network;

internal static class NetworkUpChecker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Quick connectivity check before downloading. Uses the OS network state and DNS lookups of the configured
    /// source hosts rather than an ICMP ping, which firewalls commonly block even when HTTPS works.
    /// </summary>
    internal static async Task<bool> IsNetworkAvailableAsync(IEnumerable<string> probeHosts, bool usingProxy, CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return false;
        }

        if (usingProxy)
        {
            return true; // names are resolved by the proxy, so local DNS proves nothing
        }

        string[] hosts = [.. probeHosts.Distinct(StringComparer.OrdinalIgnoreCase).Take(3)];
        if (hosts.Length == 0)
        {
            return true;
        }

        foreach (string host in hosts)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                TraceLogger.Log($"Connectivity probe: could not resolve {host} ({ex.Message}).", LogSeverity.Debug);
            }
        }
        return false;
    }
}
