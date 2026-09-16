using Fleeto.Web.Components.Shared;

namespace Fleeto.Web.Tests;

/// <summary>
/// The endpoint Summary calls the connection address a public IP only when it is one. An agent inside the same network as the
/// instance (local DNS, VPN) connects from a private address, whose public IP Fleeto does not know.
/// </summary>
public class NetworkAddressTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.10.96")]
    [InlineData("192.168.1.20")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.1")]
    [InlineData("::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.20")]
    public void A_private_address_is_a_private_connection(string address) =>
        Assert.True(Ui.IsPrivateConnection(address));

    [Theory]
    [InlineData("172.32.0.189")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2a02:1810::1")]
    [InlineData("::ffff:172.32.0.189")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown")]
    public void A_public_or_unknown_address_is_not_a_private_connection(string? address) =>
        Assert.False(Ui.IsPrivateConnection(address));
}
