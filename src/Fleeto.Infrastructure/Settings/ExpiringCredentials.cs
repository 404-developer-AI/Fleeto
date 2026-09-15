using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Settings;

/// <param name="Key">Stable key, used to remember which warnings were sent.</param>
/// <param name="Name">What the credential is, for example "The Microsoft Graph client secret for email".</param>
/// <param name="StopsWorking">What stops when it expires.</param>
/// <param name="NextStep">What an admin does about it.</param>
/// <param name="SettingsPath">The settings page where it is renewed.</param>
/// <param name="FallbackInUse">After expiry: true when a fallback keeps the function working (SMTP for email).</param>
public sealed record ExpiringCredential(string Key, string Name, DateTime ExpiresAt, string StopsWorking, string NextStep, string SettingsPath,
    bool FallbackInUse)
{
    public CredentialExpiryStage StageAt(DateTime now) => CredentialExpiryRules.StageFor(ExpiresAt, now);
}

/// <summary>
/// Every stored credential that is in use and has an end date. Starts with the Microsoft Graph credential for email; later
/// credentials (Entra ID sign-in, integrations) add their own entry here, and the dashboard and the warning emails follow.
/// </summary>
public static class ExpiringCredentials
{
    public static async Task<IReadOnlyList<ExpiringCredential>> ListAsync(SettingsStore settings, CancellationToken cancellationToken)
    {
        var list = new List<ExpiringCredential>();

        var provider = await settings.GetStringAsync(SettingKeys.EmailProvider, cancellationToken);
        if (provider == nameof(EmailProvider.MicrosoftGraph) &&
            await settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) is { CredentialExpiresAt: { } expiresAt } graph)
        {
            var smtp = await settings.GetAsync<SmtpSettings>(SettingKeys.Smtp, cancellationToken);
            var secret = graph.CredentialType == GraphCredentialType.ClientSecret;
            list.Add(new ExpiringCredential(
                secret ? "email.graph.client-secret" : "email.graph.certificate",
                secret ? "The Microsoft Graph client secret for email" : "The Microsoft Graph certificate for email",
                DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
                "email delivery through Microsoft Graph",
                secret
                    ? "Create a new client secret in the app registration and save it with its end date in Settings, Email."
                    : "Create a new certificate in Settings, Email, upload it to the app registration and switch to it.",
                "/settings/email",
                smtp is not null && !string.IsNullOrWhiteSpace(smtp.Host) && !string.IsNullOrWhiteSpace(smtp.FromAddress)));
        }

        return list;
    }
}
