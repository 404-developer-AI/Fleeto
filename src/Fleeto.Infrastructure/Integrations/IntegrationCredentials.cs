using System.Text.Json;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;

namespace Fleeto.Infrastructure.Integrations;

/// <summary>The credentials of an Action1 enterprise: OAuth2 client credentials made in the Action1 console (0.4.0).</summary>
public sealed record Action1Credentials(string ClientId, string ClientSecret);

/// <summary>
/// Stores integration credentials as ciphertext bound to the integration row, like webhook targets are bound to their
/// channel (ARCHITECTURE §5). Moving the ciphertext to another row makes it unreadable.
/// </summary>
public static class IntegrationCredentials
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Protect(ISecretProtector protector, Guid integrationId, Action1Credentials credentials) =>
        protector.Protect(SecretPurposes.Settings, JsonSerializer.Serialize(credentials, JsonOptions), AssociatedData(integrationId));

    public static Action1Credentials Unprotect(ISecretProtector protector, Guid integrationId, string stored) =>
        JsonSerializer.Deserialize<Action1Credentials>(protector.Unprotect(SecretPurposes.Settings, stored, AssociatedData(integrationId)), JsonOptions)
        ?? throw new InvalidOperationException("The stored integration credentials are empty.");

    private static string AssociatedData(Guid integrationId) => "Integrations|" + integrationId.ToString("D");
}
