using System.Net;
using System.Net.Sockets;

namespace Fleetify.Infrastructure.Security;

/// <summary>
/// Which addresses outgoing requests to admin-entered URLs (webhooks) may reach. Only public unicast addresses: a URL must
/// not become a way into the instance's own network (the database, other containers, the host, cloud metadata services).
/// Checked on the resolved address at connect time, so a host name that resolves to a private address, or changes to one
/// later (DNS rebinding), is refused too.
/// </summary>
public static class NetworkAddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublic(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false
        };
    }

    private static bool IsPublicIPv4(byte[] b) => !(
        b[0] == 0 ||                                   // 0.0.0.0/8 "this network"
        b[0] == 10 ||                                  // 10.0.0.0/8 private
        (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // 100.64.0.0/10 carrier-grade NAT
        b[0] == 127 ||                                 // loopback
        (b[0] == 169 && b[1] == 254) ||                // link-local, cloud metadata
        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||   // 172.16.0.0/12 private (Docker networks)
        (b[0] == 192 && b[1] == 0 && b[2] == 0) ||     // 192.0.0.0/24 protocol assignments
        (b[0] == 192 && b[1] == 0 && b[2] == 2) ||     // TEST-NET-1
        (b[0] == 192 && b[1] == 88 && b[2] == 99) ||   // 6to4 relay anycast
        (b[0] == 192 && b[1] == 168) ||                // 192.168.0.0/16 private
        (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || // benchmarking
        (b[0] == 198 && b[1] == 51 && b[2] == 100) ||  // TEST-NET-2
        (b[0] == 203 && b[1] == 0 && b[2] == 113) ||   // TEST-NET-3
        b[0] >= 224);                                  // multicast, reserved, broadcast

    private static bool IsPublicIPv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        // Addresses that embed an IPv4 address are judged by that address: NAT64 (64:ff9b::/96), 6to4 (2002::/16) and
        // IPv4-compatible (::/96).
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublicIPv4(b[12..16]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicIPv4(b[2..6]);
        }

        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            return false;
        }

        // Only global unicast (2000::/3), minus documentation (2001:db8::/32) and Teredo (2001::/32, embeds an obfuscated address).
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0x0d && b[3] == 0xb8) || (b[2] == 0x00 && b[3] == 0x00)))
        {
            return false;
        }

        return true;
    }
}
