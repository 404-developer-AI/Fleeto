using Fleeto.Web.Security;

namespace Fleeto.Web.Tests;

/// <summary>
/// Values from a request reach the line-based console log only through <see cref="LogText.Clean"/>, so a line break in a path
/// or query value cannot forge a log entry.
/// </summary>
public class LogTextTests
{
    [Theory]
    [InlineData("/account/login", "/account/login")]
    [InlineData("/x\r\nwarn: forged entry", "/x\\r\\nwarn: forged entry")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("tab\there", "tab?here")]
    [InlineData("esc\u001b[31m", "esc?[31m")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Line_breaks_and_control_characters_are_made_visible(string? value, string expected) =>
        Assert.Equal(expected, LogText.Clean(value));

    [Fact]
    public void A_long_value_is_cut_off() =>
        Assert.Equal(new string('a', 10) + "...", LogText.Clean(new string('a', 50), 10));
}
