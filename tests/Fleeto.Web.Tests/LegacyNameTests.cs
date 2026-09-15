using Fleeto.Infrastructure.Data;
using Fleeto.Web.Services;

namespace Fleeto.Web.Tests;

/// <summary>The rename to Fleeto (0.2.1) moves stored markers to the provider name the web app reads.</summary>
public class LegacyNameTests
{
    [Fact]
    public void Migrate_renames_the_recovery_codes_marker_to_the_provider_the_web_app_uses() =>
        Assert.Equal(AccountService.MarkerProvider, LegacyRenameUpgrade.RecoveryCodesMarkerProvider);
}
