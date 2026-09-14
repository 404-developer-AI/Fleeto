using System.Buffers;
using System.Buffers.Binary;
using System.Net;

namespace Fleetify.Gateway.Tls;

/// <summary>
/// Parser for the binary PROXY protocol version 2 header (haproxy.org proxy-protocol.txt §2.2), sent by the host Caddy in
/// front of the TLS ClientHello so the gateway learns the agent's address through the SNI passthrough. Only the parts
/// the gateway needs are read: command, TCP over IPv4 or IPv6, and the source address. TLVs are skipped. The header is
/// untrusted input: every length is checked, and a header longer than <see cref="MaxHeaderLength"/> is refused.
/// </summary>
public static class ProxyProtocolV2
{
    /// <summary>Fixed part: 12-byte signature, version and command, family and transport, 2-byte length.</summary>
    public const int FixedLength = 16;

    /// <summary>Largest accepted header: the fixed part plus room for addresses and a few TLVs.</summary>
    public const int MaxHeaderLength = FixedLength + 512;

    private static ReadOnlySpan<byte> Signature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    public enum Status
    {
        /// <summary>A complete, valid header; <see cref="Result.Consumed"/> bytes belong to it.</summary>
        Parsed,
        /// <summary>The bytes so far match a header; read more.</summary>
        NeedMoreData,
        /// <summary>The connection does not start with a PROXY v2 signature (for example a TLS ClientHello).</summary>
        NotProxyProtocol,
        /// <summary>The signature matches but the header is malformed; the connection must be closed.</summary>
        Invalid
    }

    /// <param name="Source">The original client address; null for a LOCAL command or an unsupported family.</param>
    public readonly record struct Result(Status Status, int Consumed, IPEndPoint? Source);

    public static Result Parse(ReadOnlySequence<byte> buffer)
    {
        Span<byte> fixedPart = stackalloc byte[FixedLength];
        var available = (int)Math.Min(buffer.Length, FixedLength);
        buffer.Slice(0, available).CopyTo(fixedPart);

        var signatureBytes = Math.Min(available, Signature.Length);
        if (!fixedPart[..signatureBytes].SequenceEqual(Signature[..signatureBytes]))
        {
            return new Result(Status.NotProxyProtocol, 0, null);
        }

        if (available < FixedLength)
        {
            return new Result(Status.NeedMoreData, 0, null);
        }

        var versionCommand = fixedPart[12];
        if (versionCommand >> 4 != 0x2)
        {
            return new Result(Status.Invalid, 0, null);
        }

        var command = versionCommand & 0x0F;
        if (command > 0x1)
        {
            return new Result(Status.Invalid, 0, null);
        }

        var family = fixedPart[13] >> 4;
        var transport = fixedPart[13] & 0x0F;
        int length = BinaryPrimitives.ReadUInt16BigEndian(fixedPart[14..16]);
        var total = FixedLength + length;
        if (total > MaxHeaderLength)
        {
            return new Result(Status.Invalid, 0, null);
        }

        if (buffer.Length < total)
        {
            return new Result(Status.NeedMoreData, 0, null);
        }

        // LOCAL: a connection made by the proxy itself (health check); keep the connection's own address.
        if (command == 0x0)
        {
            return new Result(Status.Parsed, total, null);
        }

        Span<byte> addresses = stackalloc byte[36];
        switch (family)
        {
            case 0x1 when transport == 0x1:
            {
                if (length < 12)
                {
                    return new Result(Status.Invalid, 0, null);
                }

                buffer.Slice(FixedLength, 12).CopyTo(addresses);
                var address = new IPAddress(addresses[..4]);
                var port = BinaryPrimitives.ReadUInt16BigEndian(addresses.Slice(8, 2));
                return new Result(Status.Parsed, total, new IPEndPoint(address, port));
            }
            case 0x2 when transport == 0x1:
            {
                if (length < 36)
                {
                    return new Result(Status.Invalid, 0, null);
                }

                buffer.Slice(FixedLength, 36).CopyTo(addresses);
                var address = new IPAddress(addresses[..16]);
                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }

                var port = BinaryPrimitives.ReadUInt16BigEndian(addresses.Slice(32, 2));
                return new Result(Status.Parsed, total, new IPEndPoint(address, port));
            }
            default:
                // UNSPEC, UNIX sockets or UDP: valid, but there is no TCP source address to use.
                return new Result(Status.Parsed, total, null);
        }
    }
}
