using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees that the real host works end to end over TLS: the gateway obtains its certificate from the signer and
/// sends the CA chain, an agent enrolls after pinning the CA fingerprint, and then opens an mTLS WebSocket session with
/// the issued certificate.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class GatewayEndToEndTests
{
    private readonly GatewayFixture _fixture;

    public GatewayEndToEndTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Agent_enrolls_and_opens_an_mtls_session_against_the_real_host()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var (token, _) = await database.CreateEnrollmentTokenAsync(site);
        await using var signer = new FakeSigner(_fixture);

        var agentPort = FreePort();
        var healthPort = FreePort();
        await using var app = GatewayApplication.Build(
            [
                "--Gateway:ListenAddress=127.0.0.1",
                $"--Gateway:AgentPort={agentPort}",
                $"--Gateway:HealthPort={healthPort}",
                "--Logging:LogLevel:Default=Warning"
            ],
            builder =>
            {
                builder.Services.AddSingleton(database.DataSource);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
            });
        await app.StartAsync();

        using var plain = new HttpClient();
        await WaitForHealthyAsync(plain, healthPort);

        // Enrollment: the agent trusts the server only through the CA fingerprint from the install command. Like the agent, it first
        // fetches the CA bundle without verification (nothing secret is sent) and keeps only the CA with that fingerprint: TLS stacks
        // leave a self-signed root out of the handshake, so the CA cannot be taken from the chain (it can on Windows, not on Linux).
        using var bootstrapHandler = new SocketsHttpHandler();
        bootstrapHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var bootstrap = new HttpClient(bootstrapHandler);
        var pinnedFromBundle = PinnedCa(await bootstrap.GetStringAsync($"https://localhost:{agentPort}/v1/ca"), _fixture.Ca.Fingerprint);
        using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enroll = new EnrollRequest
        {
            Token = token,
            CsrDer = ByteString.CopyFrom(new CertificateRequest("CN=agent", agentKey, HashAlgorithmName.SHA256).CreateSigningRequest()),
            Hostname = "WS-E2E",
            AgentVersion = "0.1.0",
            Os = new OsInfo { Platform = "windows", Name = "Windows 11 Pro", Version = "10.0.26200", Architecture = "amd64" }
        };
        using var enrollHandler = new SocketsHttpHandler();
        enrollHandler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => ChainsTo(certificate, pinnedFromBundle);
        using var https = new HttpClient(enrollHandler);
        using var content = new ByteArrayContent(enroll.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var enrollResponse = await https.PostAsync($"https://localhost:{agentPort}/v1/enroll", content);
        Assert.Equal(HttpStatusCode.OK, enrollResponse.StatusCode);
        var enrolled = EnrollResponse.Parser.ParseFrom(await enrollResponse.Content.ReadAsByteArrayAsync());

        // No allow list reload here: the gateway allows a certificate it just returned at once.
        // Session over mTLS.
        using var clientCertificate = TestCertificates.WithKey(enrolled.CertificateDer.ToByteArray(), agentKey);
        using var socket = new ClientWebSocket();
        socket.Options.ClientCertificates.Add(clientCertificate);
        var pinnedCa = X509CertificateLoader.LoadCertificate(enrolled.CaCertificateDer.ToByteArray());
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => ChainsTo(certificate, pinnedCa);
        await socket.ConnectAsync(new Uri($"wss://localhost:{agentPort}/v1/connect"), CancellationToken.None);

        await SendAsync(socket, new AgentMessage
        {
            Hello = new Hello { AgentVersion = "0.1.0", Hostname = "WS-E2E", Os = enroll.Os }
        });
        var helloAck = await ReceiveUntilAsync(socket, ServerMessage.BodyOneofCase.HelloAck);
        Assert.Equal(enrolled.EndpointId, helloAck.HelloAck.EndpointId);

        await SendAsync(socket, new AgentMessage { Heartbeat = new Heartbeat { AgentTime = Timestamp.FromDateTime(DateTime.UtcNow) } });
        await SendAsync(socket, new AgentMessage { CheckResults = new CheckResultBatch { Sequence = 1 } });
        var ack = await ReceiveUntilAsync(socket, ServerMessage.BodyOneofCase.BatchAck);
        Assert.Equal(1UL, ack.BatchAck.Sequence);

        // Read while the host stops: the agent must receive Disconnect before the connection goes away.
        var disconnectTask = ReceiveUntilAsync(socket, ServerMessage.BodyOneofCase.Disconnect);
        await app.StopAsync();
        var disconnect = await disconnectTask;
        Assert.Equal(DisconnectCode.ServerShutdown, disconnect.Disconnect.Code);
    }

    [Fact]
    public async Task Health_reports_503_with_a_reason_while_the_signer_does_not_answer()
    {
        var healthPort = FreePort();
        await using var app = GatewayApplication.Build(
            ["--Gateway:ListenAddress=127.0.0.1", $"--Gateway:AgentPort={FreePort()}", $"--Gateway:HealthPort={healthPort}"],
            builder =>
            {
                builder.Services.AddSingleton(_fixture.Database.DataSource);
                builder.Logging.SetMinimumLevel(LogLevel.Error);
            });
        await app.StartAsync();

        using var plain = new HttpClient();
        using var response = await plain.GetAsync($"http://127.0.0.1:{healthPort}/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("certificate", await response.Content.ReadAsStringAsync());
        await app.StopAsync();
    }

    private static X509Certificate2 PinnedCa(string pemBundle, string caFingerprint)
    {
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPem(pemBundle);
        return bundle.Single(c => KeyIds.Sha256Hex(c.RawData) == caFingerprint);
    }

    private static bool ChainsTo(X509Certificate? certificate, X509Certificate2 ca)
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

    private static async Task WaitForHealthyAsync(HttpClient client, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var body = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(200);
        }

        Assert.Fail("The gateway did not become healthy: " + body);
    }

    private static Task SendAsync(ClientWebSocket socket, AgentMessage message) =>
        socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<ServerMessage> ReceiveUntilAsync(ClientWebSocket socket, ServerMessage.BodyOneofCase body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), timeout.Token);
                count += result.Count;
            }
            while (!result.EndOfMessage);

            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            var message = ServerMessage.Parser.ParseFrom(buffer, 0, count);
            if (message.BodyCase == body)
            {
                return message;
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
