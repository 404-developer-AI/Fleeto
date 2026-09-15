using System.Security.Cryptography;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Gateway.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleetify.Gateway.Releases;

/// <summary>One agent binary of the current release, verified against the manifest when the release was loaded.</summary>
public sealed record ReleaseBinary(AgentBinary Binary, string Path);

/// <summary>The release the gateway offers: the signed manifest bytes, the binaries it can serve and the controls set in web.</summary>
public sealed record CurrentRelease(string Version, byte[] Manifest, byte[] Signature, IReadOnlyDictionary<string, ReleaseBinary> Binaries,
    ReleaseControl Control)
{
    /// <summary>True when endpoints of <paramref name="ring"/> may install this release now (not paused, ring reached or released to all).</summary>
    public bool IsAllowed(UpdateRing ring, DateTime now) => UpdateRings.IsAllowed(new AgentRelease
    {
        Version = Version, InstalledAt = Control.InstalledAt, PausedAt = Control.PausedAt, ReleasedToAllAt = Control.ReleasedToAllAt
    }, ring, now);
}

/// <summary>
/// Loads the release manifest that install.sh placed next to the instance (<see cref="GatewayOptions.ReleaseDirectory"/>, 0.2.1): checks its
/// Steaan release signature against the release public keys compiled into this build, reads the agent binaries it lists, and keeps only
/// binaries whose file in <see cref="GatewayOptions.AgentBinariesDirectory"/> has exactly the listed size and SHA-256. The release is recorded
/// in the database (its install time starts the update rings) and its pause and release-to-all controls are followed from web.
/// <para>
/// The gateway checks the signature so it never offers a broken release; the agent checks it again, and that check is the one that counts.
/// Without a verified manifest no update is offered and nothing can be downloaded.
/// </para>
/// </summary>
public sealed class ReleaseCatalog : BackgroundService
{
    public const string ManifestFileName = "manifest.json";
    private const int MaxManifestBytes = 256 * 1024;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly GatewayStore _store;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<ReleaseCatalog> _logger;
    private readonly IReadOnlyList<byte[]> _releaseKeys;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private (DateTime Written, long Length, DateTime SignatureWritten)? _loadedFile;
    private volatile CurrentRelease? _current;
    private string? _lastProblem;

    public ReleaseCatalog(GatewayStore store, INotificationBus bus, TimeProvider time, IOptions<GatewayOptions> options, ILogger<ReleaseCatalog> logger,
        IReadOnlyList<byte[]>? releaseKeys = null)
    {
        _store = store;
        _bus = bus;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _releaseKeys = releaseKeys ?? TrustedKeys.ReleaseKeys;
    }

    /// <summary>Raised after the offered release or its controls changed.</summary>
    public event Action? Changed;

    public CurrentRelease? Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.Subscribe(NotificationChannels.AgentReleases, async (_, token) => await RefreshControlAsync(token));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await LoadAsync(stoppingToken))
                {
                    await RefreshControlAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Loading the agent release failed; retrying in {Interval}", Interval);
            }

            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Loads the manifest when its files changed since the last load. Returns true when a release was (re)loaded.</summary>
    internal async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var directory = ResolveDirectory(_options.ReleaseDirectory);
            var manifestPath = System.IO.Path.Combine(directory, ManifestFileName);
            var signaturePath = manifestPath + ".sig";
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
            {
                Report($"No agent release is installed: {manifestPath} or its signature is missing. Agent updates are off until install.sh places them.");
                return false;
            }

            var manifestInfo = new FileInfo(manifestPath);
            var signatureInfo = new FileInfo(signaturePath);
            var stamp = (manifestInfo.LastWriteTimeUtc, manifestInfo.Length, signatureInfo.LastWriteTimeUtc);
            if (_current is not null && _loadedFile == stamp)
            {
                return false;
            }

            if (manifestInfo.Length > MaxManifestBytes || signatureInfo.Length != 64)
            {
                Report("The agent release manifest or its signature has an invalid size. Agent updates are off.");
                return false;
            }

            var manifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken);
            if (_releaseKeys.Count == 0)
            {
                Report("This gateway was built without release public keys, so it cannot verify an agent release. Agent updates are off.");
                return false;
            }

            if (!_releaseKeys.Any(key => key.Length == 32 && Ed25519.VerifyRaw(key, manifest, signature)))
            {
                Report("The signature of the agent release manifest does not verify against the release public keys of this build. Agent updates are off.");
                return false;
            }

            var parsed = ReleaseManifest.TryParse(manifest, out var problem);
            if (parsed is null)
            {
                Report(problem + " Agent updates are off.");
                return false;
            }

            var binaries = new Dictionary<string, ReleaseBinary>(StringComparer.Ordinal);
            var binariesDirectory = System.IO.Path.GetFullPath(ResolveDirectory(_options.AgentBinariesDirectory));
            foreach (var binary in parsed.AgentBinaries)
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(binariesDirectory, binary.File));
                if (!path.StartsWith(binariesDirectory, StringComparison.Ordinal) || !File.Exists(path))
                {
                    _logger.LogError("Agent release {Version}: {File} is listed in the manifest but not present in {Directory}; it is not offered",
                        parsed.Version, binary.File, binariesDirectory);
                    continue;
                }

                await using var stream = File.OpenRead(path);
                if (stream.Length != binary.Size || !SecureCompare.HexEquals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), binary.Sha256))
                {
                    _logger.LogError("Agent release {Version}: {File} does not match the size and SHA-256 in the signed manifest; it is not offered",
                        parsed.Version, binary.File);
                    continue;
                }

                binaries[binary.File] = new ReleaseBinary(binary, path);
            }

            var control = await _store.RecordCurrentReleaseAsync(parsed.Version, Convert.ToHexStringLower(SHA256.HashData(manifest)),
                _time.GetUtcNow().UtcDateTime, cancellationToken);
            _current = new CurrentRelease(parsed.Version, manifest, signature, binaries, control);
            _loadedFile = stamp;
            _lastProblem = null;
            _logger.LogInformation("Agent release {Version} loaded: {Count} binaries, installed on this instance at {InstalledAt:u}", parsed.Version,
                binaries.Count, control.InstalledAt);
            RaiseChanged();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "Could not load the agent release; retrying in {Interval}", Interval);
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Reads pause and release-to-all of the current release again.</summary>
    internal async Task RefreshControlAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is null)
        {
            return;
        }

        try
        {
            var control = await _store.ReadReleaseControlAsync(current.Version, cancellationToken);
            if (control is not null && control != current.Control)
            {
                _current = current with { Control = control };
                _logger.LogInformation("Agent release {Version}: {State}", current.Version,
                    control.PausedAt is not null ? "paused" : control.ReleasedToAllAt is not null ? "released to every ring" : "following the update rings");
                RaiseChanged();
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not read the controls of agent release {Version}", current.Version);
        }
    }

    private void Report(string problem)
    {
        if (_current is not null)
        {
            // Keep offering the release that was loaded; the files may be in the middle of being replaced by install.sh.
            _logger.LogWarning("{Problem} The previously loaded release {Version} stays offered.", problem, _current.Version);
            return;
        }

        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            _logger.LogWarning("{Problem}", problem);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent release change handler failed");
        }
    }

    private static string ResolveDirectory(string directory) =>
        System.IO.Path.IsPathRooted(directory) ? directory : System.IO.Path.Combine(AppContext.BaseDirectory, directory);
}
