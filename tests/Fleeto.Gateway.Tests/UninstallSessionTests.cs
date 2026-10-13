using System.IO.Pipelines;
using System.Net.WebSockets;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// The uninstall of a deleted endpoint (0.6.0): its certificates open no ordinary session any more, only the one that delivers the
/// signed command; a live session is told to reconnect for it; the answer of the agent or the watchdog is recorded once.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class UninstallSessionTests
{
    private readonly GatewayFixture _fixture;

    public UninstallSessionTests(GatewayFixture fixture) => _fixture = fixture;

    /// <summary>Deletes the endpoint and leaves the row web writes, signed or not yet.</summary>
    private async Task<AgentUninstall> DeleteWithUninstallAsync(Endpoint endpoint, string fingerprint, bool signed = true)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var uninstall = new AgentUninstall
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, ClientCode = "UNI", EndpointId = endpoint.Id, Hostname = endpoint.Hostname,
            Fingerprints = [fingerprint], RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tess Tech", RequestedAt = now,
            ExpiresAt = now + AgentUninstallRules.Lifetime,
            Payload = signed ? [1, 2, 3] : null, Signature = signed ? new byte[64] : null, SigningKeyId = signed ? "key-1" : null,
            SignedAt = signed ? now : null
        };
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.AgentUninstalls.Add(uninstall);
        await db.SaveChangesAsync();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteDeleteAsync();
        return uninstall;
    }

    [Fact]
    public async Task A_live_session_of_a_deleted_endpoint_is_told_to_reconnect_for_its_uninstall()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        using var session = harness.NewSession(credential.Identity(endpoint.Id));
        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        await DeleteWithUninstallAsync(endpoint, credential.Issued.Fingerprint, signed: false);
        await harness.AllowList.ReloadAsync(CancellationToken.None, endpoint.Id);

        var disconnect = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.Disconnect);
        Assert.Equal(DisconnectCode.Reconnect, disconnect.Disconnect.Code);
    }

    [Fact]
    public async Task The_certificate_of_a_deleted_endpoint_only_receives_its_command_and_the_answer_is_recorded_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint, role: AgentComponent.Watchdog);
        var uninstall = await DeleteWithUninstallAsync(endpoint, credential.Issued.Fingerprint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));

        using var certificate = credential.PublicCertificate();
        Assert.Equal(AllowListDecision.Refused, harness.AllowList.Authorize(certificate, out _));
        Assert.Equal(AllowListDecision.Accepted, harness.AllowList.AuthorizeUninstall(certificate, out var pending));
        Assert.True(pending!.IsSigned);

        var handler = new AgentConnectionHandler(harness.Manager, harness.AllowList, harness.Metrics, _fixture.Database.Time,
            Options.Create(harness.Options), harness.Store, NullLogger<AgentConnectionHandler>.Instance);
        var (server, client) = SocketPair();
        var run = handler.RunUninstallAsync(server, pending, "203.0.113.5", CancellationToken.None);

        var hello = GatewayHarness.Hello();
        hello.Component = Component.Watchdog;
        await SendAsync(client, new AgentMessage { Hello = hello });
        var command = await ReceiveAsync(client);
        Assert.Equal(ServerMessage.BodyOneofCase.Uninstall, command.BodyCase);
        Assert.Equal(new byte[] { 1, 2, 3 }, command.Uninstall.Payload.ToByteArray());
        Assert.Equal("key-1", command.Uninstall.KeyId);

        await SendAsync(client, new AgentMessage { UninstallAccepted = new UninstallAccepted { UninstallId = uninstall.Id.ToString("D") } });
        var disconnect = await ReceiveAsync(client);
        Assert.Equal(DisconnectCode.Revoked, disconnect.Disconnect.Code);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.AgentUninstalls.AsNoTracking().SingleAsync(u => u.Id == uninstall.Id);
        Assert.Equal(AgentComponent.Watchdog, row.AcceptedBy);
        Assert.NotNull(row.AcceptedAt);
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AgentUninstallAccepted && a.TargetId == endpoint.Id.ToString()));
        // The agent of the same endpoint does not get it a second time, before and after the next reload.
        Assert.Equal(AllowListDecision.Refused, harness.AllowList.AuthorizeUninstall(certificate, out _));
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        Assert.Equal(AllowListDecision.Refused, harness.AllowList.AuthorizeUninstall(certificate, out _));
    }

    private static async Task SendAsync(WebSocket socket, AgentMessage message) =>
        await socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<ServerMessage> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
            count += result.Count;
            if (result.EndOfMessage)
            {
                return ServerMessage.Parser.ParseFrom(buffer.AsSpan(0, count));
            }
        }
    }

    private static (WebSocket Server, WebSocket Client) SocketPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader, toClient.Writer), new WebSocketCreationOptions { IsServer = true });
        var client = WebSocket.CreateFromStream(new DuplexStream(toClient.Reader, toServer.Writer), new WebSocketCreationOptions { IsServer = false });
        return (server, client);
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _read = reader.AsStream();
        private readonly Stream _write = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writer.Complete();
                reader.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
