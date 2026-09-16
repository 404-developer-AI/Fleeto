using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Web.Components.Shared;

namespace Fleeto.Web.Tests;

/// <summary>The run window names a signed-in user with their sessions, and the job history names the chosen user until the agent reports one (0.2.2).</summary>
public class SignedInUserLabelTests
{
    [Fact]
    public void A_user_is_named_with_their_sessions()
    {
        UserSessionInfo Console() => new("1", true);
        UserSessionInfo Remote(string id) => new(id, false);

        Assert.Equal("jan · console", Ui.SignedInUserLabel(new SignedInUserInfo("1000", "jan", [Console()])));
        Assert.Equal("jan · console and 1 remote session", Ui.SignedInUserLabel(new SignedInUserInfo("1000", "jan", [Console(), Remote("2")])));
        Assert.Equal("jan · 2 remote sessions", Ui.SignedInUserLabel(new SignedInUserInfo("1000", "jan", [Remote("2"), Remote("3")])));
    }

    [Fact]
    public void The_job_names_the_reported_account_else_the_chosen_one()
    {
        Assert.Equal("as the signed-in user piet", Ui.JobRunAsPhrase(JobRunAs.LoggedOnUser, "piet", "jan"));
        Assert.Equal("as the signed-in user jan", Ui.JobRunAsPhrase(JobRunAs.LoggedOnUser, null, "jan"));
        Assert.Equal("as the signed-in user", Ui.JobRunAsPhrase(JobRunAs.LoggedOnUser));
    }
}
