using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The output of script checks (0.9.0): the output limit of a check stays within its bounds, an output is cut only when the script
/// wrote more than the limit of its run, and the message says how much was written, how much is kept and how to keep more.
/// </summary>
public sealed class ScriptOutputRuleTests
{
    [Theory]
    [InlineData(null, 8)]
    [InlineData("", 8)]
    [InlineData("abc", 8)]
    [InlineData("0", 8)]
    [InlineData("1", 1)]
    [InlineData("64", 64)]
    [InlineData("100000", 256)]
    public void The_output_limit_stays_within_its_bounds(string? value, int expected)
    {
        var parameters = value is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ScriptOutputRules.LimitParameter] = value };
        Assert.Equal(expected, ScriptOutputRules.LimitKb(parameters));
    }

    [Theory]
    [InlineData(8192L, 8192, false)]
    [InlineData(8193L, 8192, true)]
    [InlineData(null, 8192, false)]
    [InlineData(9000L, null, false)]
    public void An_output_is_cut_only_beyond_the_limit_of_its_run(long? total, int? limit, bool cut) =>
        Assert.Equal(cut, ScriptOutputRules.IsCut(total, limit));

    [Fact]
    public void The_message_of_a_cut_output_says_how_to_keep_more()
    {
        var message = ScriptOutputRules.CutMessage(20_000, 8192, "monitoring template Servers");

        Assert.Equal("The script wrote 20 KB of output and only the first 8 KB is kept. To keep more, edit the check in monitoring template Servers " +
                     "and raise Output limit (KB), for example to 20 (at most 256).", message);
        Assert.Contains("for example to 16 ", ScriptOutputRules.CutMessage(9000, 8192, "x"), StringComparison.Ordinal);
        Assert.Contains("for example to 256 ", ScriptOutputRules.CutMessage(10_000_000, 200 * 1024, "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void At_the_largest_limit_the_message_says_to_write_less()
    {
        var message = ScriptOutputRules.CutMessage(1_000_000, 256 * 1024, "x");

        Assert.Contains("the most a script check can keep", message, StringComparison.Ordinal);
        Assert.DoesNotContain("raise", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_check_offers_the_output_limit_with_its_default()
    {
        var spec = Assert.Single(CheckCatalog.Get(CheckType.Script).Parameters, p => p.Name == ScriptOutputRules.LimitParameter);

        Assert.Equal("8", spec.Default);
        Assert.Equal(ScriptOutputRules.MinLimitKb, spec.Min);
        Assert.Equal(ScriptOutputRules.MaxLimitKb, spec.Max);
        Assert.True(ScriptOutputRules.Applies(CheckType.Script));
        Assert.False(ScriptOutputRules.Applies(CheckType.CpuUsage));
    }
}
