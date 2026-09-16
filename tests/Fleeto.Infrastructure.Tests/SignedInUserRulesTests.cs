using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Choosing the signed-in user a script runs as (0.2.2): only a SID or uid can be named, only an agent from 0.2.2 honours the choice, and a
/// damaged stored list reads as empty.
/// </summary>
public class SignedInUserRulesTests
{
    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001")]
    [InlineData("S-1-5-18")]
    [InlineData("1000")]
    public void A_sid_or_uid_is_a_valid_user_id(string id) => Assert.True(SignedInUserRules.IsValidUserId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jan")]
    [InlineData("S-1-5-21-")]
    [InlineData("1000; rm -rf /")]
    [InlineData("S-1-5-21-1 ")]
    [InlineData("12345678901")]
    public void Anything_else_is_not(string? id) => Assert.False(SignedInUserRules.IsValidUserId(id));

    [Theory]
    [InlineData("0.2.2-alpha.1", true)]
    [InlineData("0.2.2", true)]
    [InlineData("0.3.0", true)]
    [InlineData("0.2.1", false)]
    [InlineData("0.2.2-alpha.0", false)]
    [InlineData("", false)]
    [InlineData("dev", false)]
    public void Only_an_agent_from_0_2_2_runs_a_script_as_a_chosen_user(string version, bool supported) =>
        Assert.Equal(supported, SignedInUserRules.AgentSupportsChosenUser(version));

    [Fact]
    public void The_stored_list_is_read_and_a_damaged_one_is_empty()
    {
        var users = SignedInUserRules.Parse("""[{"id":"1000","account":"jan","sessions":[{"id":"3","console":true}]},{"id":"bad","account":"x"}]""");
        var jan = Assert.Single(users);
        Assert.Equal("jan", jan.Account);
        Assert.True(Assert.Single(jan.Sessions).Console);

        Assert.Empty(SignedInUserRules.Parse("{not json"));
        Assert.Empty(SignedInUserRules.Parse(null));
    }
}
