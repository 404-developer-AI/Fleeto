using System.Security.Cryptography;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Testing;
using Google.Protobuf;
using Npgsql;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Stands in for fleeto-signer: polls pending signing requests and completes them with certificates from the test CA.
/// Only what the gateway tests need; the real rules live in fleeto-signer and its tests.
/// </summary>
public sealed class FakeSigner : IAsyncDisposable
{
    private readonly GatewayFixture _fixture;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeSigner(GatewayFixture fixture, Func<SigningRequest, string?>? refuse = null)
    {
        _fixture = fixture;
        Refuse = refuse;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Returns a refusal reason for a request, or null to complete it.</summary>
    public Func<SigningRequest, string?>? Refuse { get; set; }

    public List<string> HostNames { get; } = ["localhost", "127.0.0.1"];

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(cancellationToken);
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var database = _fixture.Database;
        List<SigningRequest> pending = [];
        await using (var command = database.DataSource.CreateCommand("""
            SELECT "Id", "ClientId", "Kind", "SubjectId", "Payload" FROM "SigningRequests" WHERE "State" = 'Pending'
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add(new SigningRequest
                {
                    Id = reader.GetGuid(0),
                    ClientId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    Kind = Enum.Parse<SigningRequestKind>(reader.GetString(2)),
                    SubjectId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    Payload = reader.GetFieldValue<byte[]>(4)
                });
            }
        }

        foreach (var request in pending)
        {
            var refusal = Refuse?.Invoke(request);
            if (refusal is not null)
            {
                await CompleteAsync(request.Id, SigningRequestState.Refused, null, refusal, cancellationToken);
                continue;
            }

            var result = request.Kind switch
            {
                SigningRequestKind.GatewayCertificate => InternalCertificateAuthority.IssueGatewayCertificate(_fixture.Ca.CertificateDer,
                    _fixture.Ca.PrivateKeyPkcs8, request.Payload, HostNames, DateTime.UtcNow).CertificateDer,
                SigningRequestKind.AgentRenewal => await RenewAsync(request, cancellationToken),
                SigningRequestKind.AgentRecovery => await RenewAsync(new SigningRequest
                {
                    Id = request.Id, SubjectId = request.SubjectId, Payload = RecoverRequest.Parser.ParseFrom(request.Payload).CsrDer.ToByteArray()
                }, cancellationToken),
                SigningRequestKind.AgentEnrollment => await EnrollAsync(request, cancellationToken),
                // The gateway passes the agent's vouched request on (0.3.0 step 7); this fake signer only issues for its CSR.
                SigningRequestKind.WatchdogCertificate => await RenewAsync(new SigningRequest
                {
                    Id = request.Id, SubjectId = request.SubjectId,
                    Payload = WatchdogCertificateRequest.Parser.ParseFrom(request.Payload).CsrDer.ToByteArray()
                }, cancellationToken, AgentComponent.Watchdog),
                _ => null
            };

            await CompleteAsync(request.Id, result is null ? SigningRequestState.Failed : SigningRequestState.Completed, result, null,
                cancellationToken);
        }
    }

    private async Task<byte[]?> RenewAsync(SigningRequest request, CancellationToken cancellationToken, AgentComponent role = AgentComponent.Agent)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var endpoint = db.Endpoints.Single(e => e.Id == request.SubjectId);
        var issued = InternalCertificateAuthority.IssueAgentCertificate(_fixture.Ca.CertificateDer, _fixture.Ca.PrivateKeyPkcs8,
            request.Payload, endpoint.Id, _fixture.Database.InstanceId, DateTime.UtcNow);
        Record(db, endpoint, issued, role);
        await db.SaveChangesAsync(cancellationToken);
        return issued.CertificateDer;
    }

    private async Task<byte[]?> EnrollAsync(SigningRequest request, CancellationToken cancellationToken)
    {
        var enroll = EnrollRequest.Parser.ParseFrom(request.Payload);
        if (!OpaqueTokens.TryParse(enroll.Token, OpaqueTokens.EnrollmentPrefix, out var tokenId, out _))
        {
            return null;
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var token = db.EnrollmentTokens.Single(t => t.Id == tokenId);
        var site = db.Sites.Single(s => s.Id == token.SiteId);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, hostname: enroll.Hostname);
        var issued = InternalCertificateAuthority.IssueAgentCertificate(_fixture.Ca.CertificateDer, _fixture.Ca.PrivateKeyPkcs8,
            enroll.CsrDer.ToByteArray(), endpoint.Id, _fixture.Database.InstanceId, DateTime.UtcNow);
        Record(db, endpoint, issued);
        token.UseCount++;
        await db.SaveChangesAsync(cancellationToken);

        return new EnrollResponse
        {
            EndpointId = endpoint.Id.ToString("D"),
            InstanceId = _fixture.Database.InstanceId.ToString("D"),
            CertificateDer = ByteString.CopyFrom(issued.CertificateDer),
            CaCertificateDer = ByteString.CopyFrom(_fixture.Ca.CertificateDer),
            InstanceSigningPublicKey = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            InstanceSigningKeyId = "test"
        }.ToByteArray();
    }

    private static void Record(Infrastructure.Data.FleetoDbContext db, Endpoint endpoint, InternalCertificateAuthority.IssuedCertificate issued,
        AgentComponent role = AgentComponent.Agent) =>
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint,
            SerialNumber = issued.SerialNumber,
            IssuedAt = issued.NotBefore,
            ExpiresAt = issued.NotAfter,
            Role = role
        });

    private async Task CompleteAsync(Guid id, SigningRequestState state, byte[]? result, string? refusal, CancellationToken cancellationToken)
    {
        await using var command = _fixture.Database.DataSource.CreateCommand("""
            UPDATE "SigningRequests" SET "State" = $2, "Result" = $3, "RefusalReason" = $4, "CompletedAt" = now() WHERE "Id" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state.ToString() });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)result ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)refusal ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _loop;
        _cts.Dispose();
    }
}
