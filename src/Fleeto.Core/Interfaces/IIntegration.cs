using Fleeto.Core.Entities;

namespace Fleeto.Core.Interfaces;

/// <summary>
/// One connector to an external product (0.4.0). Every connector answers the same two questions: can Fleeto reach the
/// product with these credentials, and which tenants does the account hold, so an admin can map them to clients. Reading
/// patch state and starting a deployment are Action1's own operations and come with the patch steps of 0.4.0.
/// </summary>
public interface IIntegration
{
    IntegrationType Type { get; }

    /// <summary>
    /// Contacts the product with the stored credentials. Never throws for a failure the admin can act on: the result
    /// carries cause and next step.
    /// </summary>
    Task<IntegrationResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>The tenants of the account (Action1: the organizations of the enterprise), for mapping them to clients.</summary>
    Task<IntegrationResult<IReadOnlyList<ExternalTenant>>> ListTenantsAsync(CancellationToken cancellationToken = default);
}

/// <summary>One tenant of an external product: an Action1 organization, later a Sophos tenant.</summary>
public sealed record ExternalTenant(string Id, string Name);

/// <summary>
/// The outcome of one call to an external product. <paramref name="Message"/> is shown to the admin, so it states cause
/// and next step and never holds a token, a secret or a URL with one in it.
/// </summary>
/// <param name="Permanent">
/// True when trying again changes nothing without an admin: refused credentials, a tenant the product does not know, a
/// request it will not accept. False for anything that may pass on the next attempt, such as a timeout or a rate limit.
/// </param>
public record IntegrationResult(bool Ok, string Message, bool Permanent = false)
{
    public static IntegrationResult Success(string message = "") => new(true, message);
    public static IntegrationResult Fail(string message, bool permanent = false) => new(false, message, permanent);
}

/// <summary>An outcome that carries data when it succeeded.</summary>
public sealed record IntegrationResult<T>(bool Ok, string Message, T? Value, bool Permanent = false)
    : IntegrationResult(Ok, Message, Permanent)
{
    public static IntegrationResult<T> Success(T value) => new(true, string.Empty, value);
    public static new IntegrationResult<T> Fail(string message, bool permanent = false) => new(false, message, default, permanent);
}
