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
using System.Net.Sockets;

namespace HostlistDownloader.Network;

/// <summary>
/// Decides which IP addresses downloads may connect to (SSRF protection). Only globally routable unicast addresses
/// are allowed; loopback, private, link-local, CGNAT, multicast, reserved, documentation and transition ranges that
/// embed such an address are refused.
/// </summary>
internal static class AddressPolicy
{
    internal static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlockedIPv4(address.MapToIPv4().GetAddressBytes());
            }
            return IsBlockedIPv6(address);
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedIPv4(address.GetAddressBytes());
        }

        return true; // unknown address family
    }

    private static bool IsBlockedIPv4(byte[] b)
    {
        return b[0] switch
        {
            0 => true,                                   // 0.0.0.0/8 "this network" (reaches localhost on Linux/macOS)
            10 => true,                                  // 10.0.0.0/8 private
            100 when b[1] >= 64 && b[1] <= 127 => true,  // 100.64.0.0/10 CGNAT (also cloud metadata, e.g. Alibaba 100.100.100.200)
            127 => true,                                 // 127.0.0.0/8 loopback
            169 when b[1] == 254 => true,                // 169.254.0.0/16 link-local (cloud metadata 169.254.169.254)
            172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12 private
            192 when b[1] == 0 && b[2] == 0 => true,     // 192.0.0.0/24 IETF protocol assignments
            192 when b[1] == 0 && b[2] == 2 => true,     // 192.0.2.0/24 documentation
            192 when b[1] == 88 && b[2] == 99 => true,   // 192.88.99.0/24 6to4 relay anycast (deprecated)
            192 when b[1] == 168 => true,                // 192.168.0.0/16 private
            198 when b[1] == 18 || b[1] == 19 => true,   // 198.18.0.0/15 benchmarking
            198 when b[1] == 51 && b[2] == 100 => true,  // 198.51.100.0/24 documentation
            203 when b[1] == 0 && b[2] == 113 => true,   // 203.0.113.0/24 documentation
            >= 224 => true,                              // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
            _ => false
        };
    }

    private static bool IsBlockedIPv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
        {
            return true; // :: and ::1
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true; // fe80::/10, fec0::/10, ff00::/8
        }

        byte[] b = address.GetAddressBytes();

        if ((b[0] & 0xFE) == 0xFC)
        {
            return true; // fc00::/7 unique local
        }

        // ::/96 IPv4-compatible (deprecated) - first 12 bytes zero
        bool firstTwelveZero = true;
        for (int i = 0; i < 12; i++)
        {
            if (b[i] != 0)
            {
                firstTwelveZero = false;
                break;
            }
        }
        if (firstTwelveZero)
        {
            return true;
        }

        // 64:ff9b::/96 and 64:ff9b:1::/48 NAT64: check the embedded IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
        {
            return IsBlockedIPv4([b[12], b[13], b[14], b[15]]);
        }

        // 2002::/16 6to4: the IPv4 address is in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlockedIPv4([b[2], b[3], b[4], b[5]]);
        }

        // 2001::/32 Teredo (client address is obfuscated, so refuse) and 2001:db8::/32 documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0x00 && b[3] == 0x00) || (b[2] == 0x0d && b[3] == 0xb8)))
        {
            return true;
        }

        // 100::/64 discard-only
        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0)
        {
            return true;
        }

        return false;
    }
}
