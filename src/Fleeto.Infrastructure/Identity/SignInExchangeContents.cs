using System.Text.Json;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;

namespace Fleeto.Infrastructure.Identity;

/// <summary>
/// The two encrypted columns of a <see cref="Core.Entities.SignInExchange"/> (0.5.0): what web asks the workers to
/// exchange, and the claims they write back. Both are bound to their row, so a ciphertext moved to another row does not
/// decrypt (ARCHITECTURE §5).
/// </summary>
public static class SignInExchangeContents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ProtectRequest(ISecretProtector protector, Guid exchangeId, SignInExchangeRequest request) =>
        protector.Protect(SecretPurposes.Identity, JsonSerializer.Serialize(request, Json), AssociatedData(exchangeId, "request"));

    public static SignInExchangeRequest UnprotectRequest(ISecretProtector protector, Guid exchangeId, string stored) =>
        JsonSerializer.Deserialize<SignInExchangeRequest>(protector.Unprotect(SecretPurposes.Identity, stored, AssociatedData(exchangeId, "request")), Json)
        ?? throw new InvalidOperationException("The stored sign-in request is empty.");

    public static string ProtectClaims(ISecretProtector protector, Guid exchangeId, SignInClaims claims) =>
        protector.Protect(SecretPurposes.Identity, JsonSerializer.Serialize(claims, Json), AssociatedData(exchangeId, "claims"));

    public static SignInClaims UnprotectClaims(ISecretProtector protector, Guid exchangeId, string stored) =>
        JsonSerializer.Deserialize<SignInClaims>(protector.Unprotect(SecretPurposes.Identity, stored, AssociatedData(exchangeId, "claims")), Json)
        ?? throw new InvalidOperationException("The stored sign-in claims are empty.");

    private static string AssociatedData(Guid exchangeId, string part) => "SignInExchanges|" + exchangeId.ToString("D") + "|" + part;
}
