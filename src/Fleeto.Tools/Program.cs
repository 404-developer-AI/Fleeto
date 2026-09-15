using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Fleeto.Infrastructure;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var root = new RootCommand("fleeto-tool: Fleeto instance maintenance and Steaan signing utilities.");

// ---------------------------------------------------------------------------------------------------------------
// migrate
// ---------------------------------------------------------------------------------------------------------------
var fqdnOption = new Option<string>("--fqdn") { Description = "Instance FQDN, e.g. rmm.customer.example.", Required = true };
var agentHostOption = new Option<string?>("--agent-host") { Description = "Host name agents connect to. Default: agents.<fqdn>." };
var agentPortOption = new Option<int>("--agent-port") { Description = "Port agents connect to.", DefaultValueFactory = _ => 443 };
var webUrlOption = new Option<string?>("--web-url") { Description = "Public base URL of the web UI. Default: https://<fqdn>." };

var migrate = new Command("migrate", "Apply database migrations and grants, and initialise the instance. Idempotent.")
{
    fqdnOption, agentHostOption, agentPortOption, webUrlOption
};
migrate.SetAction(async (parse, cancellationToken) =>
{
    var fqdn = parse.GetValue(fqdnOption)!.Trim().TrimEnd('.').ToLowerInvariant();
    var options = new InstanceInitOptions(
        fqdn,
        parse.GetValue(agentHostOption) ?? $"agents.{fqdn}",
        parse.GetValue(agentPortOption),
        parse.GetValue(webUrlOption) ?? $"https://{fqdn}");

    using var host = BuildHost(args);
    var dbFactory = host.Services.GetRequiredService<IFleetoDbContextFactory>();
    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    await using (var db = dbFactory.CreateSystem())
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        logger.LogInformation("Applying {Count} pending migration(s)", pending.Count);
        await db.Database.MigrateAsync(cancellationToken);
    }

    var initializer = ActivatorUtilities.CreateInstance<InstanceInitializer>(host.Services);
    var result = await initializer.RunAsync(options, cancellationToken);
    // Before the signer starts: key material and markers from before the rename to Fleeto (0.2.1).
    await LegacyRenameUpgrade.RunAsync(dbFactory, host.Services.GetRequiredService<SignerKey>(), logger, cancellationToken);

    Console.WriteLine($"Instance {result.InstanceId} is ready on {options.Fqdn}.");
    if (result.SetupLink is not null)
    {
        Console.WriteLine();
        Console.WriteLine("No admin exists yet. Open this one-time link within 24 hours to create the first admin:");
        Console.WriteLine(result.SetupLink);
    }

    return 0;
});
root.Subcommands.Add(migrate);

// ---------------------------------------------------------------------------------------------------------------
// license
// ---------------------------------------------------------------------------------------------------------------
var license = new Command("license", "Steaan license keys and documents.");

var keyOutOption = new Option<DirectoryInfo>("--out") { Description = "Directory to write the key files to.", Required = true };
var licenseKeygen = new Command("keygen", "Generate a license signing key pair. The private key belongs on a hardware token or offline storage.") { keyOutOption };
licenseKeygen.SetAction(parse =>
{
    var directory = parse.GetValue(keyOutOption)!;
    directory.Create();
    var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
    WriteSecretFile(Path.Combine(directory.FullName, "license-signing.key"), "fleeto-license-key:" + Convert.ToBase64String(privateKey));
    File.WriteAllText(Path.Combine(directory.FullName, "license-signing.pub"), Convert.ToBase64String(publicKey) + "\n");
    Console.WriteLine($"License public key (id {KeyIds.For(publicKey)}): {Convert.ToBase64String(publicKey)}");
    return 0;
});
license.Subcommands.Add(licenseKeygen);

var signKeyOption = new Option<FileInfo>("--key") { Description = "License signing private key file.", Required = true };
var customerOption = new Option<string>("--customer") { Description = "Customer name.", Required = true };
var licenseFqdnOption = new Option<string>("--fqdn") { Description = "Instance FQDN the license is for.", Required = true };
var countOption = new Option<int>("--count") { Description = "Number of managed endpoints.", Required = true };
var expiresOption = new Option<string>("--expires") { Description = "Expiry date, yyyy-MM-dd (UTC end of day).", Required = true };
var serialOption = new Option<string?>("--serial") { Description = "Serial number. Default: generated." };
var licenseOutOption = new Option<FileInfo>("--out") { Description = "License file to write.", Required = true };
var licenseSign = new Command("sign", "Sign a license document.")
{
    signKeyOption, customerOption, licenseFqdnOption, countOption, expiresOption, serialOption, licenseOutOption
};
licenseSign.SetAction(parse =>
{
    var privateKey = ReadPrefixedKey(parse.GetValue(signKeyOption)!, "fleeto-license-key:", LegacyNames.LicenseKeyPrefix);
    var expires = DateTime.ParseExact(parse.GetValue(expiresOption)!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        .AddDays(1).AddSeconds(-1);
    var document = new LicenseDocument
    {
        Serial = parse.GetValue(serialOption) ?? $"FL-{DateTime.UtcNow:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}",
        CustomerName = parse.GetValue(customerOption)!,
        Fqdn = parse.GetValue(licenseFqdnOption)!.Trim().ToLowerInvariant(),
        ManagedEndpointCount = parse.GetValue(countOption),
        IssuedAt = DateTime.UtcNow,
        ExpiresAt = expires
    };
    var output = parse.GetValue(licenseOutOption)!;
    File.WriteAllText(output.FullName, LicenseCodec.Sign(document, privateKey));
    Console.WriteLine($"License {document.Serial} for {document.CustomerName} ({document.Fqdn}), {document.ManagedEndpointCount} managed endpoints, expires {document.ExpiresAt:yyyy-MM-dd}: {output.FullName}");
    return 0;
});
license.Subcommands.Add(licenseSign);
root.Subcommands.Add(license);

// ---------------------------------------------------------------------------------------------------------------
// release
// ---------------------------------------------------------------------------------------------------------------
var release = new Command("release", "Steaan release signing: install.sh, release manifest, agent binaries.");

var releaseKeygen = new Command("keygen", "Generate a release signing key pair (development only; production keys live on a hardware token).") { keyOutOption };
releaseKeygen.SetAction(parse =>
{
    var directory = parse.GetValue(keyOutOption)!;
    directory.Create();
    var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
    WriteSecretFile(Path.Combine(directory.FullName, "release-signing.key"), "fleeto-release-key:" + Convert.ToBase64String(privateKey));
    File.WriteAllText(Path.Combine(directory.FullName, "release-signing.pub"), Convert.ToBase64String(publicKey) + "\n");
    File.WriteAllText(Path.Combine(directory.FullName, "steaan-release.pub"), Ed25519.PublicKeyToPem(publicKey));
    Console.WriteLine($"Release public key (id {KeyIds.For(publicKey)}): {Convert.ToBase64String(publicKey)}");
    return 0;
});
release.Subcommands.Add(releaseKeygen);

var releaseKeyOption = new Option<FileInfo>("--key") { Description = "Release signing private key file.", Required = true };
var fileOption = new Option<FileInfo>("--file") { Description = "File to sign; the signature is written next to it as <file>.sig.", Required = true };
var releaseSign = new Command("sign", "Sign a release artifact. Verify with: openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in <file> -sigfile <file>.sig")
{
    releaseKeyOption, fileOption
};
releaseSign.SetAction(parse =>
{
    var privateKey = ReadPrefixedKey(parse.GetValue(releaseKeyOption)!, "fleeto-release-key:", LegacyNames.ReleaseKeyPrefix);
    var file = parse.GetValue(fileOption)!;
    var signature = Ed25519.SignRaw(privateKey, File.ReadAllBytes(file.FullName));
    File.WriteAllBytes(file.FullName + ".sig", signature);
    Console.WriteLine($"Signed {file.Name}: {file.FullName}.sig");
    return 0;
});
release.Subcommands.Add(releaseSign);

var publicKeyOption = new Option<FileInfo>("--public-key") { Description = "Release public key file (release-signing.pub, base64).", Required = true };
var verifyFileOption = new Option<FileInfo>("--file") { Description = "Signed file; the signature is read from <file>.sig.", Required = true };
var releaseVerify = new Command("verify", "Verify a release signature, as install.sh does, before publishing.") { publicKeyOption, verifyFileOption };
releaseVerify.SetAction(parse =>
{
    var publicKey = Convert.FromBase64String(File.ReadAllText(parse.GetValue(publicKeyOption)!.FullName).Trim());
    var file = parse.GetValue(verifyFileOption)!;
    var signature = File.ReadAllBytes(file.FullName + ".sig");
    if (publicKey.Length != 32 || signature.Length != 64 || !Ed25519.VerifyRaw(publicKey, File.ReadAllBytes(file.FullName), signature))
    {
        Console.Error.WriteLine($"The signature of {file.Name} does not verify with key {KeyIds.For(publicKey)}.");
        return 1;
    }

    Console.WriteLine($"Signature of {file.Name} verified with key {KeyIds.For(publicKey)}.");
    return 0;
});
release.Subcommands.Add(releaseVerify);

var versionOption = new Option<string>("--version") { Description = "Release version, e.g. 0.1.0.", Required = true };
var imageOption = new Option<string[]>("--image") { Description = "name=digest, repeatable, e.g. web=sha256:...", Required = true, AllowMultipleArgumentsPerToken = true };
var installShOption = new Option<FileInfo>("--install-sh") { Description = "The install.sh of this release.", Required = true };
var rollbackOption = new Option<string>("--rollback") { Description = "images (the previous release runs on the new schema) or restore.", DefaultValueFactory = _ => "images" };
var manifestOutOption = new Option<FileInfo>("--out") { Description = "Manifest file to write.", Required = true };
var agentBinariesOption = new Option<DirectoryInfo?>("--agent-binaries")
{
    Description = "Directory with the agent binaries laid out as <platform>-<architecture>/fleeto-{agent,watchdog}[.exe] (0.2.1)."
};
var releaseManifest = new Command("manifest", "Write a release manifest with image digests and agent binaries. Sign it with 'release sign'.")
{
    versionOption, imageOption, installShOption, rollbackOption, manifestOutOption, agentBinariesOption
};
releaseManifest.SetAction(parse =>
{
    var rollback = parse.GetValue(rollbackOption)!;
    if (rollback is not ("images" or "restore"))
    {
        Console.Error.WriteLine("--rollback must be images or restore.");
        return 2;
    }

    var images = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var pair in parse.GetValue(imageOption)!)
    {
        var parts = pair.Split('=', 2);
        if (parts.Length != 2 || !parts[1].StartsWith("sha256:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Invalid --image '{pair}'. Use name=sha256:<digest>.");
            return 2;
        }

        images[parts[0]] = parts[1];
    }

    var agentBinaries = parse.GetValue(agentBinariesOption) is { } binariesDirectory ? AgentBinaries(binariesDirectory) : [];
    var manifest = new
    {
        formatVersion = 1,
        version = parse.GetValue(versionOption),
        images,
        installShSha256 = KeyIds.Sha256Hex(File.ReadAllBytes(parse.GetValue(installShOption)!.FullName)),
        rollback,
        agentBinaries
    };
    var output = parse.GetValue(manifestOutOption)!;
    File.WriteAllText(output.FullName, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Manifest written: {output.FullName}");
    return 0;
});
release.Subcommands.Add(releaseManifest);

var agentVersionOption = new Option<string>("--version") { Description = "Release version of the agent binaries, e.g. 0.2.1.", Required = true };
var agentBinariesRequiredOption = new Option<DirectoryInfo>("--agent-binaries")
{
    Description = "Directory with the agent binaries laid out as <platform>-<architecture>/fleeto-{agent,watchdog}[.exe].", Required = true
};
var agentManifestOutOption = new Option<FileInfo>("--out") { Description = "Manifest file to write.", Required = true };
var releaseAgentManifest = new Command("agent-manifest",
    "Write a manifest with only agent binaries, for local development of agent updates. install.sh never accepts it (no images).")
{
    agentVersionOption, agentBinariesRequiredOption, agentManifestOutOption
};
releaseAgentManifest.SetAction(parse =>
{
    var version = parse.GetValue(agentVersionOption)!;
    if (!Fleeto.Core.Domain.SemanticVersion.TryParse(version, out _))
    {
        Console.Error.WriteLine($"'{version}' is not a semantic version.");
        return 2;
    }

    var manifest = new { formatVersion = 1, version, agentBinaries = AgentBinaries(parse.GetValue(agentBinariesRequiredOption)!) };
    var output = parse.GetValue(agentManifestOutOption)!;
    File.WriteAllText(output.FullName, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Agent manifest written: {output.FullName} ({manifest.agentBinaries.Count} binaries)");
    return 0;
});
release.Subcommands.Add(releaseAgentManifest);
root.Subcommands.Add(release);

// ---------------------------------------------------------------------------------------------------------------
// backup
// ---------------------------------------------------------------------------------------------------------------
var backup = new Command("backup", "Backup key pairs and offline decryption.");

var backupKeygen = new Command("keygen", "Generate a backup key pair during the key ceremony. Keep the private key offline.") { keyOutOption };
backupKeygen.SetAction(parse =>
{
    var directory = parse.GetValue(keyOutOption)!;
    directory.Create();
    var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
    WriteSecretFile(Path.Combine(directory.FullName, "backup.key"), BackupCipher.EncodePrivateKey(privateKey));
    File.WriteAllText(Path.Combine(directory.FullName, "backup.pub"), BackupCipher.EncodePublicKey(publicKey) + "\n");
    Console.WriteLine("Backup public key (paste into Settings, Backups):");
    Console.WriteLine(BackupCipher.EncodePublicKey(publicKey));
    return 0;
});
backup.Subcommands.Add(backupKeygen);

var backupKeyOption = new Option<FileInfo>("--key") { Description = "Backup private key file.", Required = true };
var inOption = new Option<FileInfo>("--in") { Description = "Input file.", Required = true };
var outOption = new Option<FileInfo>("--out") { Description = "Output file.", Required = true };
var backupDecrypt = new Command("decrypt", "Decrypt a backup file with the offline backup private key.") { backupKeyOption, inOption, outOption };
backupDecrypt.SetAction(async (parse, cancellationToken) =>
{
    var privateKey = BackupCipher.DecodePrivateKey(File.ReadAllText(parse.GetValue(backupKeyOption)!.FullName));
    await using var input = File.OpenRead(parse.GetValue(inOption)!.FullName);
    await using var output = File.Create(parse.GetValue(outOption)!.FullName);
    await BackupCipher.DecryptAsync(input, output, privateKey, cancellationToken);
    Console.WriteLine("Backup decrypted and verified.");
    return 0;
});
backup.Subcommands.Add(backupDecrypt);
root.Subcommands.Add(backup);

// ---------------------------------------------------------------------------------------------------------------
// keys
// ---------------------------------------------------------------------------------------------------------------
var keys = new Command("keys", "Instance key files.");
var keyFileOption = new Option<FileInfo>("--out") { Description = "Key file to write (refuses to overwrite).", Required = true };
var keysGenerate = new Command("generate", "Write a new random 32-byte key (root key or signer key) as base64.") { keyFileOption };
keysGenerate.SetAction(parse =>
{
    var file = parse.GetValue(keyFileOption)!;
    if (file.Exists)
    {
        Console.Error.WriteLine($"{file.FullName} already exists. Replacing an instance key makes its data unreadable; rotate instead.");
        return 1;
    }

    WriteSecretFile(file.FullName, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    return 0;
});
keys.Subcommands.Add(keysGenerate);
root.Subcommands.Add(keys);

return await root.Parse(args).InvokeAsync();

static IHost BuildHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Configuration.AddEnvironmentVariables("FLEETO_TOOL_");
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
    builder.Services.AddFleetoInfrastructure(builder.Configuration, FleetoComponent.Tool);
    return builder.Build();
}

// The agent binaries of a release directory, in a stable order. Only the names the agent and the gateway accept are listed.
static List<object> AgentBinaries(DirectoryInfo directory)
{
    var binaries = new List<object>();
    foreach (var platformDirectory in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
    {
        var parts = platformDirectory.Name.Split('-');
        if (parts.Length != 2)
        {
            continue;
        }

        foreach (var component in new[] { "agent", "watchdog" })
        {
            var file = Fleeto.Core.Domain.ReleaseManifest.ExpectedFile(
                component == "agent" ? Fleeto.Core.Entities.AgentComponent.Agent : Fleeto.Core.Entities.AgentComponent.Watchdog, parts[0], parts[1]);
            var path = Path.Combine(directory.FullName, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            binaries.Add(new { component, platform = parts[0], architecture = parts[1], file, sha256 = KeyIds.Sha256Hex(bytes), size = (long)bytes.Length });
        }
    }

    return binaries;
}

// Key files made before the rename to Fleeto (0.2.1) carry the legacy prefix; they are read as they are and never rewritten.
static byte[] ReadPrefixedKey(FileInfo file, string prefix, string legacyPrefix)
{
    var text = File.ReadAllText(file.FullName).Trim();
    if (text.StartsWith(legacyPrefix, StringComparison.Ordinal))
    {
        prefix = legacyPrefix;
    }
    else if (!text.StartsWith(prefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{file.Name} is not a {prefix.TrimEnd(':')} file.");
    }

    var key = Convert.FromBase64String(text[prefix.Length..]);
    return key.Length == 32 ? key : throw new InvalidOperationException($"{file.Name} does not contain a 32-byte key.");
}

static void WriteSecretFile(string path, string content)
{
    if (File.Exists(path))
    {
        throw new InvalidOperationException($"{path} already exists; refusing to overwrite a key.");
    }

    File.WriteAllText(path, content + "\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public partial class Program;
