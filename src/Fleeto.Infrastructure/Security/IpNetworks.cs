using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// Addresses and networks of the security monitor (0.7.0): parsing what an admin types, the canonical CIDR text Fleeto stores,
/// the unit one attacker controls, and the ranges that may never be blocked. IPv4 addresses mapped into IPv6 are treated as IPv4.
/// </summary>
public static class IpNetworks
{
    /// <summary>The shortest IPv4 prefix an admin may block or allow: a /8 is already 16 million addresses.</summary>
    public const int MinIPv4Prefix = 8;

    /// <summary>The shortest IPv6 prefix an admin may block or allow.</summary>
    public const int MinIPv6Prefix = 19;

    /// <summary>
    /// Ranges never blocked, by an admin, the security monitor or a blocklist: private, loopback, link-local, carrier-grade NAT,
    /// unique local and the other special ranges. The containers of the instance and the host proxy live in them, so blocking one
    /// would cut Fleeto off from itself; a public blocklist that lists them (FireHOL level1 does) is cleaned of them.
    /// </summary>
    public static readonly IReadOnlyList<IPNetwork> Reserved =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/3"),
        IPNetwork.Parse("::/127"), IPNetwork.Parse("64:ff9b::/96"), IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/32"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"),
    ];

    /// <summary>An IPv4 address mapped into IPv6 as plain IPv4; anything else unchanged.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>The network of <paramref name="address"/> with <paramref name="prefixLength"/> bits, host bits cleared.</summary>
    public static IPNetwork NetworkOf(IPAddress address, int prefixLength)
    {
        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        var max = bytes.Length * 8;
        prefixLength = Math.Clamp(prefixLength, 0, max);
        for (var bit = prefixLength; bit < max; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }

    /// <summary>The unit one attacker controls: an IPv4 address on its own, an IPv6 address with its /64.</summary>
    public static IPNetwork AttackerNetwork(IPAddress address)
    {
        address = Normalize(address);
        return NetworkOf(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 64);
    }

    /// <summary>The network a sign-in counts for: an IPv4 /24 (one office, one provider pool) or an IPv6 /64.</summary>
    public static IPNetwork SignInNetwork(IPAddress address)
    {
        address = Normalize(address);
        return NetworkOf(address, address.AddressFamily == AddressFamily.InterNetwork ? 24 : 64);
    }

    /// <summary>The canonical text Fleeto stores: <c>203.0.113.7/32</c>, <c>2001:db8:1:2::/64</c>.</summary>
    public static string Format(IPNetwork network) => $"{network.BaseAddress}/{network.PrefixLength}";

    /// <summary>
    /// Parses an address or network an admin typed (<c>203.0.113.7</c>, <c>203.0.113.0/24</c>, <c>2001:db8::/48</c>), clearing host
    /// bits so <c>203.0.113.7/24</c> means <c>203.0.113.0/24</c>. A bare address is one address.
    /// </summary>
    public static bool TryParse(string? text, out IPNetwork network)
    {
        network = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 50)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(addressText, out var address) || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) || addressText.Contains('%'))
        {
            return false;
        }

        address = Normalize(address);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out prefix) || prefix < 0 || prefix > max))
        {
            return false;
        }

        network = NetworkOf(address, prefix);
        return true;
    }

    /// <summary>
    /// Whether two networks share at least one address. By range rather than <see cref="IPNetwork.Contains"/>, which treats an
    /// IPv4-mapped IPv6 base address as IPv4 and so finds <c>::ffff:0:0/96</c> inside any IPv6 network.
    /// </summary>
    public static bool Overlaps(IPNetwork a, IPNetwork b)
    {
        if (a.BaseAddress.AddressFamily != b.BaseAddress.AddressFamily)
        {
            return false;
        }

        var (firstA, lastA) = Range(a);
        var (firstB, lastB) = Range(b);
        return firstA <= lastB && firstB <= lastA;
    }

    /// <summary>Whether a network holds an address; an IPv4 address mapped into IPv6 counts as IPv4.</summary>
    public static bool Contains(IPNetwork network, IPAddress address)
    {
        address = Normalize(address);
        if (network.BaseAddress.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var (first, last) = Range(network);
        var number = ToNumber(address);
        return number >= first && number <= last;
    }

    /// <summary>Whether a network touches a range that may never be blocked (<see cref="Reserved"/>).</summary>
    public static bool TouchesReserved(IPNetwork network) => Reserved.Any(r => Overlaps(r, network));

    /// <summary>Whether a network is wider than an admin may block or allow in one entry.</summary>
    public static bool IsTooWide(IPNetwork network) =>
        network.PrefixLength < (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? MinIPv4Prefix : MinIPv6Prefix);

    /// <summary>The first and last address of a network as 128-bit numbers, IPv4 in the low 32 bits, for range lookups.</summary>
    public static (UInt128 First, UInt128 Last) Range(IPNetwork network)
    {
        var first = ToNumber(network.BaseAddress);
        var bits = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var hostBits = bits - network.PrefixLength;
        var span = hostBits >= 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - 1;
        return (first, first + span);
    }

    /// <summary>An address as a 128-bit number, IPv4 in the low 32 bits.</summary>
    public static UInt128 ToNumber(IPAddress address)
    {
        address = Normalize(address);
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            return UInt128.Zero;
        }

        return written == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }
}
