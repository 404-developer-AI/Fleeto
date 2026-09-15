using System.Security.Cryptography;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Infrastructure.Data;

/// <summary>
/// Data from before the rename to Fleeto (0.2.1) that needs a key only the migrator holds besides the signer: the instance signing key
/// and the internal CA key are sealed again with the current signer label, and the marker for recovery codes still to be shown gets its
/// current provider name. Run by <c>fleeto-tool migrate</c> after the initializer, before the signer starts. Idempotent.
/// </summary>
public static class LegacyRenameUpgrade
{
    /// <summary>Login provider of the recovery codes marker; must equal <c>AccountService.MarkerProvider</c> in Fleeto.Web.</summary>
    public const string RecoveryCodesMarkerProvider = "[Fleeto]";

    public static async Task RunAsync(IFleetoDbContextFactory dbFactory, SignerKey signerKey, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var db = dbFactory.CreateSystem();
        try
        {
            foreach (var key in await db.InstanceSigningKeys.ToListAsync(cancellationToken))
            {
                if (signerKey.UpgradeLegacySeal(key.EncryptedPrivateKey, "InstanceSigningKeys|" + key.Id) is { } resealed)
                {
                    key.EncryptedPrivateKey = resealed;
                    logger.LogInformation("Sealed instance signing key {KeyId} with the Fleeto label", key.Id);
                }
            }

            foreach (var ca in await db.CertificateAuthorities.ToListAsync(cancellationToken))
            {
                if (signerKey.UpgradeLegacySeal(ca.EncryptedPrivateKey, "CertificateAuthorities|" + ca.Id.ToString("D")) is { } resealed)
                {
                    ca.EncryptedPrivateKey = resealed;
                    logger.LogInformation("Sealed internal CA {CaId} with the Fleeto label", ca.Id);
                }
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"The signing keys of this instance do not open with the loaded signer key {signerKey.Id}. The signer key file does not belong to this database.", ex);
        }

        await db.SaveChangesAsync(cancellationToken);

        var markers = await db.Database.ExecuteSqlAsync($"""
            UPDATE "AspNetUserTokens" t SET "LoginProvider" = {RecoveryCodesMarkerProvider}
            WHERE t."LoginProvider" = {LegacyNames.RecoveryCodesMarkerProvider}
              AND NOT EXISTS (SELECT 1 FROM "AspNetUserTokens" c
                              WHERE c."UserId" = t."UserId" AND c."Name" = t."Name" AND c."LoginProvider" = {RecoveryCodesMarkerProvider})
            """, cancellationToken);
        if (markers > 0)
        {
            logger.LogInformation("Renamed {Count} recovery codes marker(s) to the Fleeto provider name", markers);
        }
    }
}
