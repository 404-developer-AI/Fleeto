using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The library scripts a technician can run in a remote background terminal of one endpoint (0.6.0).</summary>
/// <param name="ApprovalRequired">The endpoint's policy requires approved scripts, so only approved current versions are offered.</param>
/// <param name="SignedInUsers">Who the agent last reported as signed in; null when it never reported.</param>
public sealed record TerminalScriptChoices(IReadOnlyList<RunnableScript> Scripts, bool ApprovalRequired, SignedInUsersView? SignedInUsers);

/// <summary>
/// A library script on its way to the endpoint through the end-to-end encrypted session (0.6.0): the current version's body and hash, and
/// whom it runs as. <paramref name="Language"/> is the name the endpoint knows (powershell, batch, shell, bash).
/// </summary>
public sealed record TerminalScript(string Name, int Version, string Language, string Body, string Sha256, bool RunAsUser, string? UserId);

/// <summary>
/// Library scripts in a remote background terminal (0.6.0, ARCHITECTURE.md §4 Remote session): the technician picks a script in the
/// session window and the endpoint runs it in a terminal, so its output shows as it comes and its prompts can be answered. Nothing is
/// signed: the session is the authority, like the terminal it runs in (accepted risk in ARCHITECTURE.md §5). Web offers only the
/// scripts the endpoint's policy allows (an approved current version where approval is required), only to the technician of a connected
/// participant of that endpoint, and records every run in the audit log; the endpoint checks the body against its hash and reports the run
/// as an action of the session.
/// </summary>
public sealed class RemoteScriptService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly JobService _jobs;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public RemoteScriptService(IFleetoDbContextFactory dbFactory, JobService jobs, LicenseService licenses, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _jobs = jobs;
        _licenses = licenses;
        _time = time;
    }

    /// <summary>The scripts the caller can run on the endpoint now; null when the caller may not, or the endpoint is not visible.</summary>
    public async Task<TerminalScriptChoices?> ListAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return null;
        }

        bool approvalRequired;
        await using (var db = _dbFactory.Create(caller.Scope))
        {
            if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId, cancellationToken))
            {
                return null;
            }

            approvalRequired = (await EffectivePolicies.LoadAsync(db, endpointId, cancellationToken))?.ScriptApprovalRequired == true;
        }

        var scripts = await _jobs.ListRunnableScriptsAsync(caller, [endpointId], cancellationToken);
        return new TerminalScriptChoices(approvalRequired ? scripts.Where(s => s.Approved).ToList() : scripts, approvalRequired,
            await _jobs.GetSignedInUsersAsync(caller, endpointId, cancellationToken));
    }

    /// <summary>
    /// The current version of a script for the caller's connected remote background participant, after the checks of a job (managed, the
    /// platform, the client, approval where the policy requires it) and an audit entry. <paramref name="userId"/> is a SID or uid the agent
    /// reported, or null for whoever is signed in.
    /// </summary>
    public async Task<ServiceResult<TerminalScript>> PrepareAsync(Caller caller, Guid participantId, Guid scriptId, bool runAsUser, string? userId,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<TerminalScript>.Forbidden();
        }

        if (userId is not null && (!runAsUser || !SignedInUserRules.IsValidUserId(userId)))
        {
            return ServiceResult<TerminalScript>.Fail("A user can only be chosen when the script runs as the signed-in user.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var participant = await db.RemoteSessionParticipants.AsNoTracking()
            .Where(p => p.Id == participantId && p.UserId == caller.UserId)
            .Select(p => new { p.Id, p.SessionId, p.EndpointId, p.ClientId, p.State, Kind = db.RemoteSessions.Where(s => s.Id == p.SessionId).Select(s => s.Kind).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);
        if (participant is null || participant.Kind != RemoteSessionKind.RemoteBackground)
        {
            return ServiceResult<TerminalScript>.NotFound("remote session");
        }

        if (participant.State != RemoteParticipantState.Connected)
        {
            return ServiceResult<TerminalScript>.Fail("The session is not connected. Connect again, then run the script.");
        }

        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == participant.EndpointId)
            .Select(e => new { e.Id, e.Hostname, e.ClientId, e.Tier, e.OsPlatform, e.SignedInUsersJson })
            .SingleOrDefaultAsync(cancellationToken);
        var script = await db.Scripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        var version = script?.CurrentVersionId is { } versionId
            ? await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        if (endpoint is null || script is null || version is null)
        {
            return ServiceResult<TerminalScript>.NotFound(endpoint is null ? "endpoint" : "script");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var approvalRequired = (await EffectivePolicies.LoadAsync(db, endpoint.Id, cancellationToken))?.ScriptApprovalRequired == true;
        var chosen = userId is null ? null
            : SignedInUserRules.Parse(endpoint.SignedInUsersJson).FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.OrdinalIgnoreCase));
        var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed ? "Scripts run only on managed endpoints. Switch it to managed first."
            : !ScriptLanguages.RunsOn(script.Language, endpoint.OsPlatform) ? $"{ScriptLanguages.Label(script.Language)} does not run on this operating system."
            : script.ClientId is { } scriptClient && scriptClient != endpoint.ClientId ? "The script belongs to another client."
            : approvalRequired && !version.IsApproved ? "The endpoint's policy requires an approved script. Ask a second admin to approve this version."
            : userId is not null && chosen is null
                ? "The chosen user is no longer signed in on this endpoint, as far as the agent reported. Open Run script again to see who is signed in."
            : null;
        if (problem is not null)
        {
            return ServiceResult<TerminalScript>.Fail(problem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RemoteSessionScript, "RemoteSession", participant.SessionId.ToString(),
            endpoint.ClientId, new
            {
                endpoint.Hostname, EndpointId = endpoint.Id, ParticipantId = participant.Id, ScriptId = script.Id, Script = script.Name,
                Version = version.Number, version.Sha256, RunAs = runAsUser ? chosen?.Account ?? "signed-in user" : "system",
                Approved = version.IsApproved
            }), now));
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<TerminalScript>.Ok(new TerminalScript(script.Name, version.Number, script.Language.ToString().ToLowerInvariant(),
            version.Body, version.Sha256, runAsUser, chosen?.Id));
    }
}
