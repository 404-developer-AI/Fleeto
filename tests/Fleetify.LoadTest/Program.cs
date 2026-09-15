// =====================================================================================================================
// Fleetify.LoadTest: agent simulator for fleetify-gateway
// =====================================================================================================================
//
// Usage
//   dotnet run -c Release --project tests/Fleetify.LoadTest -- --server localhost:7200 --token fet_... \
//     --ca-fingerprint <hex> --agents 1000 --ramp-seconds 60 --duration-minutes 10 --results-per-minute 20 \
//     [--identity-cache <dir>]
//
// Every simulated agent generates an ECDSA P-256 key and a CSR, enrolls over HTTPS (trusting the gateway only through
// the CA fingerprint, like the real agent), opens the mTLS WebSocket with its certificate, sends Hello and heartbeats,
// answers Ping, sends an inventory when asked, confirms configurations, and sends CheckResultBatch messages with random
// values for the check ids of its configuration (random ids when it has none), timing each BatchAck. It runs no real
// checks. Every 10 seconds it prints connected agents, enrollment and connect failures, acknowledged batches per second
// and the ack latency p50/p95/p99.
//
// Before a run
//   - Create an enrollment token with enough uses (unlimited or at least --agents) for a site in a test instance.
//     The CA fingerprint is in the install command shown with the token.
//   - The gateway allows 20 enrollments per minute per remote address. All simulated agents share one address, so
//     start the gateway for the first (enrolling) run with --Gateway:EnrollmentsPerMinutePerAddress=100000, or enroll
//     slowly once with --identity-cache and reuse the identities afterwards.
//   - Enrolled endpoints start agent-only: the gateway acknowledges their results without storing them. Switch the
//     endpoints to managed (and link a monitoring template) to load the ingest path for real.
//   - --identity-cache stores each simulated agent's certificate AND PRIVATE KEY as an unprotected .pfx file so a rerun
//     does not enroll again. Test data only: never point it at a real instance and delete the directory afterwards.
//
// The 10,000-agent scenario (CLAUDE.md, Performance requirements)
//   Run the simulator on one or more machines other than the gateway host, for example 4 processes x 2,500 agents with
//   --ramp-seconds 600, --duration-minutes 30 and --results-per-minute 20. Watch the gateway summary line (sessions,
//   batches, results/s) and its /health endpoint, PostgreSQL CPU and I/O, and the simulator's p99 ack latency. Pass
//   criteria: every agent connected, no connect failures after the ramp, p99 ack latency well below one second.
//
// Operating system limits
//   Windows
//   - Ephemeral ports: every connection uses one local port; the default dynamic range is 49152-65535 (16,384 ports) and
//     closed connections sit in TIME_WAIT for up to 120 s, so reconnect storms exhaust it. Check with
//     `netsh int ipv4 show dynamicport tcp`; widen with `netsh int ipv4 set dynamicport tcp start=10000 num=55535`
//     (administrator) and consider lowering HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\TcpTimedWaitDelay.
//   - Handles and memory: Windows has no per-process file handle limit like ulimit, but every TLS connection costs
//     non-paged pool and roughly 50-100 KiB of process memory; plan about 1 GiB per 10,000 connections.
//   - Client certificates: SChannel cannot use in-memory keys, so each agent key is imported into a temporary key
//     container in the user profile (removed when the agent stops). Thousands of agents means thousands of short-lived
//     key files; run from a disposable test account or VM.
//   Linux
//   - `ulimit -n` must exceed the agent count (e.g. `ulimit -n 65535`); widen `net.ipv4.ip_local_port_range`.
// =====================================================================================================================

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Fleetify.LoadTest;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LoadTestOptions options;
        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(LoadTestOptions.Usage);
            return 2;
        }

        if (options.IdentityCache is not null)
        {
            Console.WriteLine($"WARNING: --identity-cache stores agent private keys unprotected in {options.IdentityCache}. Test data only.");
        }

        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        stop.CancelAfter(TimeSpan.FromMinutes(options.DurationMinutes) + TimeSpan.FromSeconds(options.RampSeconds));

        var stats = new Stats();
        using var enrollClient = await CreateEnrollClientAsync(options);
        var agents = Enumerable.Range(1, options.Agents).Select(i => new SimulatedAgent(i, options, stats, enrollClient)).ToArray();

        Console.WriteLine($"Starting {options.Agents} agents against {options.Server} over {options.RampSeconds} s, " +
                          $"running {options.DurationMinutes} min, {options.ResultsPerMinute} results per agent per minute.");

        var reporter = ReportAsync(stats, options, stop.Token);
        var tasks = new List<Task>(agents.Length);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < agents.Length && !stop.IsCancellationRequested; i++)
        {
            var due = TimeSpan.FromSeconds(options.RampSeconds * (double)i / agents.Length);
            var wait = due - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            tasks.Add(agents[i].RunAsync(stop.Token));
        }

        await Task.WhenAll(tasks);
        await reporter;
        stats.Print(stopwatch.Elapsed, options.Agents, final: true);
        return 0;
    }

    /// <summary>
    /// Like the agent: fetches the CA bundle from /v1/ca without verification (nothing secret is sent), keeps the CA with the pinned
    /// fingerprint, and verifies the gateway against it for every enrollment. TLS stacks leave a self-signed root out of the handshake,
    /// so the CA cannot be taken from the chain.
    /// </summary>
    private static async Task<HttpClient> CreateEnrollClientAsync(LoadTestOptions options)
    {
        using var bootstrapHandler = new SocketsHttpHandler();
        bootstrapHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var bootstrap = new HttpClient(bootstrapHandler) { Timeout = TimeSpan.FromSeconds(30) };
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPem(await bootstrap.GetStringAsync($"https://{options.Server}{ProtocolLimits.CaPath}"));
        var ca = bundle.FirstOrDefault(c => Convert.ToHexStringLower(SHA256.HashData(c.RawData)) == options.CaFingerprint.ToLowerInvariant())
                 ?? throw new InvalidOperationException("The gateway's CA bundle has no certificate with the given --ca-fingerprint. Check the fingerprint.");

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => Tls.ChainsTo(certificate, ca);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static async Task ReportAsync(Stats stats, LoadTestOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                stats.Print(stopwatch.Elapsed, options.Agents, final: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal sealed class LoadTestOptions
{
    public const string Usage =
        "Usage: --server host:port --token fet_... --ca-fingerprint <hex> [--agents 1000] [--ramp-seconds 60] " +
        "[--duration-minutes 10] [--results-per-minute 20] [--identity-cache <dir>]";

    public string Server { get; private set; } = string.Empty;
    public string Token { get; private set; } = string.Empty;
    public string CaFingerprint { get; private set; } = string.Empty;
    public int Agents { get; private set; } = 1000;
    public int RampSeconds { get; private set; } = 60;
    public double DurationMinutes { get; private set; } = 10;
    public int ResultsPerMinute { get; private set; } = 20;
    public string? IdentityCache { get; private set; }

    public static LoadTestOptions Parse(string[] args)
    {
        var options = new LoadTestOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--server": options.Server = Value(); break;
                case "--token": options.Token = Value(); break;
                case "--ca-fingerprint": options.CaFingerprint = Value().Replace(":", string.Empty).ToLowerInvariant(); break;
                case "--agents": options.Agents = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--ramp-seconds": options.RampSeconds = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--duration-minutes": options.DurationMinutes = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--results-per-minute": options.ResultsPerMinute = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--identity-cache": options.IdentityCache = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.CaFingerprint))
        {
            throw new ArgumentException("--server and --ca-fingerprint are required.");
        }

        if (options.CaFingerprint.Length != 64)
        {
            throw new ArgumentException("--ca-fingerprint must be the 64-character hex SHA-256 of the CA certificate.");
        }

        if (options.Agents < 1 || options.ResultsPerMinute < 0 || options.RampSeconds < 0)
        {
            throw new ArgumentException("--agents must be at least 1; --ramp-seconds and --results-per-minute cannot be negative.");
        }

        return options;
    }
}

internal static class Tls
{
    public static bool ChainsTo(X509Certificate? certificate, X509Certificate2 ca)
    {
        if (certificate is null)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }

    /// <summary>A certificate with private key that SChannel (Windows) and OpenSSL (Linux) can use as a client certificate.</summary>
    public static X509Certificate2 WithKey(byte[] certificateDer, ECDsa key)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        var withKey = certificate.CopyWithPrivateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            return withKey;
        }

        using (withKey)
        {
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }
}

/// <summary>Counters shared by all simulated agents.</summary>
internal sealed class Stats
{
    private readonly ConcurrentQueue<double> _latencies = new();
    private long _connected;
    private long _enrollFailures;
    private long _connectFailures;
    private long _enrolled;
    private long _batchesAcked;
    private long _lastBatchesAcked;
    private TimeSpan _lastPrint;
    private readonly List<double> _allLatencies = [];

    public void Connected() => Interlocked.Increment(ref _connected);
    public void Disconnected() => Interlocked.Decrement(ref _connected);
    public void EnrollFailed() => Interlocked.Increment(ref _enrollFailures);
    public void Enrolled() => Interlocked.Increment(ref _enrolled);
    public void ConnectFailed() => Interlocked.Increment(ref _connectFailures);

    private long _reports;

    /// <summary>Limits detailed error lines to the first few.</summary>
    public bool ShouldReport() => Interlocked.Increment(ref _reports) <= 10;

    public void BatchAcked(double latencyMs)
    {
        Interlocked.Increment(ref _batchesAcked);
        _latencies.Enqueue(latencyMs);
    }

    public void Print(TimeSpan elapsed, int agents, bool final)
    {
        var samples = new List<double>();
        while (_latencies.TryDequeue(out var latency))
        {
            samples.Add(latency);
        }

        lock (_allLatencies)
        {
            _allLatencies.AddRange(samples);
            if (final)
            {
                samples = [.. _allLatencies];
            }
        }

        samples.Sort();
        var acked = Interlocked.Read(ref _batchesAcked);
        var window = final ? elapsed : elapsed - _lastPrint;
        var ackedInWindow = final ? acked : acked - _lastBatchesAcked;
        _lastBatchesAcked = acked;
        _lastPrint = elapsed;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{elapsed:hh\\:mm\\:ss}]{(final ? " FINAL" : string.Empty)} agents {agents} connected {Interlocked.Read(ref _connected)} " +
            $"enrolled {Interlocked.Read(ref _enrolled)} | enroll failures {Interlocked.Read(ref _enrollFailures)} " +
            $"| connect failures {Interlocked.Read(ref _connectFailures)} " +
            $"| acks/s {(window.TotalSeconds > 0 ? ackedInWindow / window.TotalSeconds : 0):0.0} " +
            $"| ack latency p50 {Percentile(samples, 0.50):0.0} ms p95 {Percentile(samples, 0.95):0.0} ms p99 {Percentile(samples, 0.99):0.0} ms"));
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
}

/// <summary>One simulated agent: enroll once, then keep a session open until the run ends.</summary>
internal sealed class SimulatedAgent
{
    private const string InventoryHash = "loadtest-inventory-v1";
    private static readonly object CacheLock = new();
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(15);

    private readonly int _index;
    private readonly LoadTestOptions _options;
    private readonly Stats _stats;
    private readonly HttpClient _enrollClient;
    private readonly ConcurrentDictionary<ulong, long> _pendingAcks = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private X509Certificate2? _certificate;
    private X509Certificate2? _ca;
    private string[] _checkIds = [];
    private ulong _configVersion;
    private ulong _nextSequence = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public SimulatedAgent(int index, LoadTestOptions options, Stats stats, HttpClient enrollClient)
    {
        _index = index;
        _options = options;
        _stats = stats;
        _enrollClient = enrollClient;
        // Sequences survive reruns: start above anything a previous run could have used for this identity.
        _nextSequence += (ulong)index;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested && _certificate is null)
        {
            if (TryLoadIdentity() || await TryEnrollAsync(cancellationToken))
            {
                break;
            }

            await DelayAsync(Jitter(backoff), cancellationToken);
            backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, backoff.Ticks * 2));
        }

        backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested && _certificate is not null)
        {
            var stopReconnecting = false;
            try
            {
                stopReconnecting = await RunSessionAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or InvalidProtocolBufferException)
            {
                _stats.ConnectFailed();
                if (_stats.ShouldReport())
                {
                    Console.WriteLine($"Agent {_index}: session failed: {ex.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (stopReconnecting)
            {
                break;
            }

            await DelayAsync(Jitter(backoff), cancellationToken);
            backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, backoff.Ticks * 2));
        }

        _certificate?.Dispose();
    }

    private async Task<bool> TryEnrollAsync(CancellationToken cancellationToken)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new EnrollRequest
            {
                Token = _options.Token,
                CsrDer = ByteString.CopyFrom(new CertificateRequest("CN=loadtest", key, HashAlgorithmName.SHA256).CreateSigningRequest()),
                Hostname = $"LOADTEST-{_index:D5}",
                AgentVersion = "0.1.0-loadtest",
                Os = OsInfo()
            };
            using var content = new ByteArrayContent(request.ToByteArray());
            content.Headers.ContentType = new MediaTypeHeaderValue(ProtocolLimits.ProtobufContentType);
            using var response = await _enrollClient.PostAsync($"https://{_options.Server}{ProtocolLimits.EnrollPath}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _stats.EnrollFailed();
                if (_index <= 3 || response.StatusCode != System.Net.HttpStatusCode.TooManyRequests && _stats.ShouldReport())
                {
                    Console.WriteLine($"Agent {_index}: enrollment returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
                }

                return false;
            }

            var enrolled = EnrollResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            _ca = X509CertificateLoader.LoadCertificate(enrolled.CaCertificateDer.ToByteArray());
            _certificate = Tls.WithKey(enrolled.CertificateDer.ToByteArray(), key);
            try
            {
                SaveIdentity(enrolled, key);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Agent {_index}: could not store its identity in the cache: {ex.Message}");
            }

            _stats.Enrolled();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidProtocolBufferException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            _stats.EnrollFailed();
            if (_index <= 3)
            {
                Console.WriteLine($"Agent {_index}: enrollment failed: {ex.Message}");
            }

            return false;
        }
        finally
        {
            key.Dispose();
        }
    }

    /// <summary>Runs one session. Returns true when the gateway said not to reconnect (revoked, duplicate identity).</summary>
    private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.ClientCertificates.Add(_certificate!);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => Tls.ChainsTo(certificate, _ca!);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(new Uri($"wss://{_options.Server}{ProtocolLimits.ConnectPath}"), cancellationToken);

        _stats.Connected();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? sender = null;
        try
        {
            await SendAsync(socket, new AgentMessage
            {
                Hello = new Hello
                {
                    AgentVersion = "0.1.0-loadtest",
                    Hostname = $"LOADTEST-{_index:D5}",
                    Os = OsInfo(),
                    ConfigVersion = _configVersion,
                    InventoryHash = InventoryHash
                }
            }, sessionCts.Token);

            var buffer = new byte[64 * 1024];
            while (!sessionCts.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, buffer, sessionCts.Token);
                if (message is null)
                {
                    return false;
                }

                switch (message.BodyCase)
                {
                    case ServerMessage.BodyOneofCase.HelloAck:
                        var heartbeat = TimeSpan.FromSeconds(Math.Max(5, message.HelloAck.HeartbeatIntervalSeconds));
                        sender ??= SendLoopAsync(socket, heartbeat, sessionCts.Token);
                        break;
                    case ServerMessage.BodyOneofCase.Ping:
                        await SendAsync(socket, new AgentMessage { Pong = new Pong { Nonce = message.Ping.Nonce } }, sessionCts.Token);
                        break;
                    case ServerMessage.BodyOneofCase.InventoryRequest:
                        await SendAsync(socket, new AgentMessage { Inventory = Inventory() }, sessionCts.Token);
                        break;
                    case ServerMessage.BodyOneofCase.Config:
                        var config = AgentConfig.Parser.ParseFrom(message.Config.Payload);
                        _configVersion = config.Version;
                        _checkIds = config.Checks.Select(c => c.Id).ToArray();
                        await SendAsync(socket, new AgentMessage { ConfigApplied = new ConfigApplied { ConfigVersion = config.Version } }, sessionCts.Token);
                        break;
                    case ServerMessage.BodyOneofCase.BatchAck:
                        if (_pendingAcks.TryRemove(message.BatchAck.Sequence, out var sentAt))
                        {
                            _stats.BatchAcked(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                        }

                        break;
                    case ServerMessage.BodyOneofCase.Disconnect:
                        var code = message.Disconnect.Code;
                        if (_index <= 3 || code != DisconnectCode.ServerShutdown)
                        {
                            Console.WriteLine($"Agent {_index}: disconnected by the gateway ({code}): {message.Disconnect.Reason}");
                        }

                        return code is DisconnectCode.Revoked or DisconnectCode.DuplicateIdentity;
                }
            }

            return false;
        }
        finally
        {
            await sessionCts.CancelAsync();
            if (sender is not null)
            {
                try
                {
                    await sender;
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                }
            }

            _pendingAcks.Clear();
            _stats.Disconnected();
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                }
            }
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, TimeSpan heartbeat, CancellationToken cancellationToken)
    {
        var nextHeartbeat = Stopwatch.GetTimestamp();
        // Spread batches over the interval so thousands of agents never send in the same second.
        var nextBatch = Stopwatch.GetTimestamp() + (long)(Random.Shared.NextDouble() * BatchInterval.TotalSeconds * Stopwatch.Frequency);
        var resultsPerBatch = (int)Math.Round(_options.ResultsPerMinute * BatchInterval.TotalMinutes);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= nextHeartbeat)
            {
                await SendAsync(socket, new AgentMessage { Heartbeat = new Heartbeat { AgentTime = Timestamp.FromDateTime(DateTime.UtcNow) } },
                    cancellationToken);
                nextHeartbeat = now + (long)(heartbeat.TotalSeconds * Stopwatch.Frequency);
            }

            if (resultsPerBatch > 0 && now >= nextBatch)
            {
                var batch = new CheckResultBatch { Sequence = Interlocked.Increment(ref _nextSequence) };
                var checkIds = _checkIds;
                for (var i = 0; i < resultsPerBatch; i++)
                {
                    batch.Results.Add(new CheckResult
                    {
                        CheckId = checkIds.Length > 0 ? checkIds[i % checkIds.Length] : Guid.NewGuid().ToString(),
                        ConfigVersion = _configVersion,
                        CollectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                        Value = Random.Shared.NextDouble() * 100,
                        Target = string.Empty,
                        Detail = "load test"
                    });
                }

                _pendingAcks[batch.Sequence] = Stopwatch.GetTimestamp();
                await SendAsync(socket, new AgentMessage { CheckResults = batch }, cancellationToken);
                nextBatch = now + (long)(BatchInterval.TotalSeconds * Stopwatch.Frequency);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, AgentMessage message, CancellationToken cancellationToken)
    {
        var size = message.CalculateSize();
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            message.WriteTo(buffer.AsSpan(0, size));
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(buffer.AsMemory(0, size), WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<ServerMessage?> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (true)
        {
            if (count == buffer.Length)
            {
                throw new IOException("A server message is larger than the simulator buffer.");
            }

            var result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            count += result.Count;
            if (result.EndOfMessage)
            {
                return ServerMessage.Parser.ParseFrom(buffer, 0, count);
            }
        }
    }

    private bool TryLoadIdentity()
    {
        if (_options.IdentityCache is null)
        {
            return false;
        }

        var pfx = Path.Combine(_options.IdentityCache, $"agent-{_index:D5}.pfx");
        var ca = Path.Combine(_options.IdentityCache, "ca.der");
        if (!File.Exists(pfx) || !File.Exists(ca))
        {
            return false;
        }

        _ca = X509CertificateLoader.LoadCertificateFromFile(ca);
        _certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (_certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(5))
        {
            _certificate.Dispose();
            _certificate = null;
            return false;
        }

        return true;
    }

    private void SaveIdentity(EnrollResponse enrolled, ECDsa key)
    {
        if (_options.IdentityCache is null)
        {
            return;
        }

        Directory.CreateDirectory(_options.IdentityCache);
        using var certificate = X509CertificateLoader.LoadCertificate(enrolled.CertificateDer.ToByteArray());
        using var withKey = certificate.CopyWithPrivateKey(key);
        File.WriteAllBytes(Path.Combine(_options.IdentityCache, $"agent-{_index:D5}.pfx"), withKey.Export(X509ContentType.Pkcs12));
        lock (CacheLock)
        {
            var caPath = Path.Combine(_options.IdentityCache, "ca.der");
            if (!File.Exists(caPath))
            {
                File.WriteAllBytes(caPath, enrolled.CaCertificateDer.ToByteArray());
            }
        }
    }

    private InventoryReport Inventory() => new()
    {
        Hash = InventoryHash,
        Inventory = new Inventory
        {
            Hostname = $"LOADTEST-{_index:D5}",
            Os = OsInfo(),
            Manufacturer = "Fleeto load test",
            Model = "Simulated agent",
            CpuModel = "Simulated CPU",
            CpuCores = 4,
            CpuLogicalProcessors = 8,
            MemoryTotalBytes = 16UL << 30,
            Disks = { new Disk { Mount = "C:", Filesystem = "NTFS", TotalBytes = 256UL << 30, FreeBytes = 100UL << 30 } },
            NetworkInterfaces = { new NetworkInterface { Name = "Ethernet", MacAddress = "02:00:00:00:00:01", IpAddresses = { "192.0.2.1" } } }
        }
    };

    private static OsInfo OsInfo() => new() { Platform = "windows", Name = "Windows 11 Pro", Version = "10.0.26200", Architecture = "amd64" };

    private static TimeSpan Jitter(TimeSpan value) => value * (0.5 + Random.Shared.NextDouble());

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
