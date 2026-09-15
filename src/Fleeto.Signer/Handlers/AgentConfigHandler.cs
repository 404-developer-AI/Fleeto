using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// Signs the configuration an endpoint should run, computed from the database by the signer itself. The request
/// only names the endpoint; nothing the requester proposes is signed.
/// </summary>
public sealed class AgentConfigHandler : ISigningRequestHandler
{
    private readonly AgentConfigSigner _signer;

    public AgentConfigHandler(AgentConfigSigner signer)
    {
        _signer = signer;
    }

    public SigningRequestKind Kind => SigningRequestKind.AgentConfig;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        if (context.Request.SubjectId is not { } endpointId)
        {
            return SigningOutcome.Refused("The configuration request names no endpoint. Check the version of the component that sent it.");
        }

        var endpointClientId = await context.Db.Endpoints.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == endpointId)
            .Select(e => (Guid?)e.ClientId)
            .SingleOrDefaultAsync(cancellationToken);
        if (endpointClientId is null)
        {
            // Deleted in the meantime: nothing to sign and nothing wrong.
            return SigningOutcome.Completed(null);
        }

        if (context.Request.ClientId != endpointClientId)
        {
            return SigningOutcome.Refused("The configuration request names a client that does not own the endpoint. Check the component that sent it.");
        }

        var result = await _signer.SignAsync(context, endpointId, cancellationToken);
        return result.Status switch
        {
            ConfigSignStatus.Issued => SigningOutcome.Completed(null,
                new PendingNotification(NotificationChannels.EndpointConfig, endpointId.ToString())),
            ConfigSignStatus.TierViolation => SigningOutcome.Refused(AgentConfigSigner.TierViolationReason),
            _ => SigningOutcome.Completed(null)
        };
    }
}
