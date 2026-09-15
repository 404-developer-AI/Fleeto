using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Npgsql;

namespace Fleeto.Gateway.Enrollment;

/// <summary>
/// Pre-check of an enrollment token before a signing request is written. The signer checks the token again (and marks
/// it used); this check keeps junk out of the signer queue. Every failure looks the same to the caller.
/// </summary>
public sealed class EnrollmentTokenValidator
{
    // Compared against when the token id is unknown, so a miss costs about the same as a wrong secret.
    private static readonly string DummyHash = new('0', 64);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    public EnrollmentTokenValidator(NpgsqlDataSource dataSource, TimeProvider time)
    {
        _dataSource = dataSource;
        _time = time;
    }

    /// <summary>Returns the ClientId of the token's site when the token is usable, otherwise null.</summary>
    public async Task<Guid?> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!OpaqueTokens.TryParse(token, OpaqueTokens.EnrollmentPrefix, out var id, out var secretHash))
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT "ClientId", "TokenHash", "ExpiresAt", "MaxUses", "UseCount", "RevokedAt" FROM "EnrollmentTokens" WHERE "Id" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            SecureCompare.HexEquals(secretHash, DummyHash);
            return null;
        }

        var row = new EnrollmentToken
        {
            Id = id,
            ClientId = reader.GetGuid(0),
            TokenHash = reader.GetString(1),
            ExpiresAt = reader.GetDateTime(2),
            MaxUses = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            UseCount = reader.GetInt32(4),
            RevokedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
        };

        var hashMatches = SecureCompare.HexEquals(secretHash, row.TokenHash);
        return hashMatches && row.IsUsable(_time.GetUtcNow().UtcDateTime) ? row.ClientId : null;
    }
}
