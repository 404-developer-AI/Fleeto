namespace Fleeto.Infrastructure.Security;

/// <summary>
/// Names from before the internal rename from Fleetify to Fleeto (0.2.1). Data written earlier still carries them: agent
/// certificates, wrapped data keys, license documents, backup files and key files. They are only ever read, never written, and
/// every use is a documented migration step (MD-Files/ARCHITECTURE.md §7, Rename to Fleeto). The branding check allows the old
/// name in this file only, in the C# code.
/// </summary>
public static class LegacyNames
{
    /// <summary>Endpoint URI in agent certificates issued before the rename; valid until those certificates are renewed.</summary>
    public const string EndpointUriPrefix = "urn:fleetify:endpoint:";

    /// <summary>Label of the associated data that wrapped data keys before the rename; rewrapped by <c>fleeto-tool migrate</c>.</summary>
    public const string DataKeyWrapLabel = "fleetify-dek";

    /// <summary>Label of the associated data that sealed the instance signing key and CA key; resealed by <c>fleeto-tool migrate</c>.</summary>
    public const string SignerKeyLabel = "fleetify-signer";

    /// <summary>Login provider of the marker that recovery codes still have to be shown; renamed by <c>fleeto-tool migrate</c>.</summary>
    public const string RecoveryCodesMarkerProvider = "[Fleetify]";

    /// <summary>Signature context of license documents signed before the rename.</summary>
    public const string LicenseSignatureContext = "fleetify-license-v1";

    /// <summary>HKDF salt of backup files written before the rename, so those backups can still be restored.</summary>
    public const string BackupSalt = "fleetify-backup-v1";

    public const string BackupPublicKeyPrefix = "fleetify-backup-pub:";
    public const string BackupPrivateKeyPrefix = "fleetify-backup-key:";
    public const string ReleaseKeyPrefix = "fleetify-release-key:";
    public const string LicenseKeyPrefix = "fleetify-license-key:";
}
