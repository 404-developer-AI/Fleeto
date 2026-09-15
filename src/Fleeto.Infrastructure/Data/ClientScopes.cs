using Fleeto.Core.Interfaces;

namespace Fleeto.Infrastructure.Data;

/// <summary>System context: every client is visible. For background services, the signer and migrations.</summary>
public sealed class SystemClientScope : IClientScope
{
    public static readonly SystemClientScope Instance = new();

    private SystemClientScope()
    {
    }

    public bool AllClients => true;
    public IReadOnlyCollection<Guid> ClientIds => [];
}

/// <summary>A fixed set of clients, e.g. for an API key restricted to clients, or in tests.</summary>
public sealed class RestrictedClientScope : IClientScope
{
    public RestrictedClientScope(IEnumerable<Guid> clientIds)
    {
        ClientIds = clientIds.Distinct().ToArray();
    }

    public bool AllClients => false;
    public IReadOnlyCollection<Guid> ClientIds { get; }
}
