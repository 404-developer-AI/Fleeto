using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Web.Services;

/// <summary>One run of a script check: its result, without the output.</summary>
/// <param name="At">When the agent ran it (its own clock, unless that is far off), as in the check history.</param>
/// <param name="OutputBytes">What the script wrote in bytes; null from an agent older than 0.9.0.</param>
public sealed record ScriptRunView(long Id, DateTime Time, DateTime At, double Value, string Error, string Detail, CheckStatus Status,
    long? OutputBytes, int? OutputLimitBytes)
{
    public bool IsCut => ScriptOutputRules.IsCut(OutputBytes, OutputLimitBytes);
}

public enum ScriptOutputState
{
    /// <summary>The output of the run is shown.</summary>
    Available,
    /// <summary>The agent was older than 0.9.0 and reported only the first line, which is the detail.</summary>
    OldAgent,
    /// <summary>The run left its output out because it did not change, and the run that had it is no longer kept.</summary>
    Missing
}

/// <summary>The output of one run of a script check (0.9.0). <paramref name="CutMessage"/> says how to keep more when it was cut off.</summary>
public sealed record ScriptOutputView(string CheckName, ScriptRunView Run, ScriptOutputState State, string Stdout, string Stderr, string? CutMessage);

/// <summary>
/// The runs of a script check and their output (0.9.0, <see cref="ScriptOutputRules"/>), with the same access as the check history:
/// the permission to view checks, a managed endpoint the caller sees and a check that applies to it. Showing an output is audited
/// with the check and the time of the run, never the output.
/// </summary>
public sealed class ScriptOutputService
{
    /// <summary>The most runs listed for one block of the status timeline.</summary>
    public const int MaxRuns = 200;

    /// <summary>How far back the latest run of a check is looked for: the retention of raw check results.</summary>
    private static readonly TimeSpan LatestWindow = TimeSpan.FromDays(31);

    // Raw SQL skips the EF query filters, so each statement filters on the caller's clients itself, as in CheckHistoryService.
    private static readonly string RunsSql = $$"""
        SELECT r."Id", r."Time", {{CheckHistoryRules.EffectiveTimeSql}} AS "At", r."Value", r."Error", r."Detail", r."OutputBytes", r."OutputLimitBytes"
        FROM "CheckResults" r
        WHERE r."EndpointId" = @endpointId AND r."CheckDefinitionId" = @checkId
          AND (@allClients OR r."ClientId" = ANY(@clientIds))
          AND r."Time" >= @from AND r."Time" < @to + interval '7 days'
          AND {{CheckHistoryRules.EffectiveTimeSql}} >= @from AND {{CheckHistoryRules.EffectiveTimeSql}} < @to
        ORDER BY 3 DESC, r."Id" DESC
        LIMIT @limit
        """;

    private static readonly string RunSql = $$"""
        SELECT r."Id", r."Time", {{CheckHistoryRules.EffectiveTimeSql}} AS "At", r."Value", r."Error", r."Detail", r."OutputBytes", r."OutputLimitBytes"
        FROM "CheckResults" r
        WHERE r."EndpointId" = @endpointId AND r."CheckDefinitionId" = @checkId
          AND (@allClients OR r."ClientId" = ANY(@clientIds))
          AND (@id IS NULL OR (r."Id" = @id AND r."Time" = @time))
          AND r."Time" >= @since
        ORDER BY r."Time" DESC, r."Id" DESC
        LIMIT 1
        """;

    // The output of a run is its own, or that of the latest earlier result with output: a result that left it out did not change.
    // Results of one batch share their ingest time, so the id orders them.
    private const string OutputSql = """
        SELECT r."Output", r."ErrorOutput"
        FROM "CheckResults" r
        WHERE r."EndpointId" = @endpointId AND r."CheckDefinitionId" = @checkId
          AND (@allClients OR r."ClientId" = ANY(@clientIds))
          AND r."Output" IS NOT NULL
          AND (r."Time", r."Id") <= (@time, @id) AND r."Time" >= @since
        ORDER BY r."Time" DESC, r."Id" DESC
        LIMIT 1
        """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public ScriptOutputService(IFleetoDbContextFactory dbFactory, LicenseService licenses, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _time = time;
    }

    /// <summary>The runs of a script check between two times (a block of the status timeline), newest first, at most <see cref="MaxRuns"/>.</summary>
    public async Task<ServiceResult<IReadOnlyList<ScriptRunView>>> GetRunsAsync(Caller caller, Guid endpointId, Guid checkId, DateTime from,
        DateTime to, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ChecksView))
        {
            return ServiceResult<IReadOnlyList<ScriptRunView>>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (await LoadCheckAsync(db, endpointId, checkId, cancellationToken) is not { } context)
        {
            return ServiceResult<IReadOnlyList<ScriptRunView>>.NotFound("check");
        }

        var rows = await db.Database.SqlQueryRaw<RunRow>(RunsSql,
                new NpgsqlParameter("endpointId", endpointId),
                new NpgsqlParameter("checkId", checkId),
                new NpgsqlParameter("allClients", caller.Scope.AllClients),
                new NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                new NpgsqlParameter("from", DateTime.SpecifyKind(from, DateTimeKind.Utc)),
                new NpgsqlParameter("to", DateTime.SpecifyKind(to, DateTimeKind.Utc)),
                new NpgsqlParameter("limit", MaxRuns))
            .ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ScriptRunView>>.Ok(rows.Select(r => context.View(r)).ToList());
    }

    /// <summary>
    /// The output of one run, or of the latest run when <paramref name="runId"/> is null (the status of the check). The run is named by
    /// its id and its time, which together are the key of a check result.
    /// </summary>
    public async Task<ServiceResult<ScriptOutputView>> GetOutputAsync(Caller caller, Guid endpointId, Guid checkId, long? runId, DateTime? runTime,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ChecksView))
        {
            return ServiceResult<ScriptOutputView>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (await LoadCheckAsync(db, endpointId, checkId, cancellationToken) is not { } context)
        {
            return ServiceResult<ScriptOutputView>.NotFound("check");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var time = runTime is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : now;
        var run = (await db.Database.SqlQueryRaw<RunRow>(RunSql,
                new NpgsqlParameter("endpointId", endpointId),
                new NpgsqlParameter("checkId", checkId),
                new NpgsqlParameter("allClients", caller.Scope.AllClients),
                new NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = runId is { } id ? id : DBNull.Value },
                new NpgsqlParameter("time", time),
                new NpgsqlParameter("since", runId is null ? now - LatestWindow : time))
            .ToListAsync(cancellationToken)).FirstOrDefault();
        if (run is null)
        {
            return runId is null
                ? ServiceResult<ScriptOutputView>.Fail("This check has no results yet. Its output appears here after its first run.")
                : ServiceResult<ScriptOutputView>.Fail("This run is no longer kept. Results are kept as long as Settings, Retention says for check results.");
        }

        var view = context.View(run);
        ScriptOutputView output;
        if (run.OutputBytes is null)
        {
            output = new ScriptOutputView(context.Check.Name, view, ScriptOutputState.OldAgent, string.Empty, string.Empty, null);
        }
        else
        {
            var text = (await db.Database.SqlQueryRaw<OutputRow>(OutputSql,
                    new NpgsqlParameter("endpointId", endpointId),
                    new NpgsqlParameter("checkId", checkId),
                    new NpgsqlParameter("allClients", caller.Scope.AllClients),
                    new NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                    new NpgsqlParameter("time", DateTime.SpecifyKind(run.Time, DateTimeKind.Utc)),
                    new NpgsqlParameter("id", run.Id),
                    new NpgsqlParameter("since", DateTime.SpecifyKind(run.Time, DateTimeKind.Utc) - ScriptOutputRules.Lookback))
                .ToListAsync(cancellationToken)).FirstOrDefault();
            var cut = view.IsCut ? ScriptOutputRules.CutMessage(run.OutputBytes.Value, run.OutputLimitBytes, context.Where) : null;
            output = text is null
                ? new ScriptOutputView(context.Check.Name, view, ScriptOutputState.Missing, string.Empty, string.Empty, cut)
                : new ScriptOutputView(context.Check.Name, view, ScriptOutputState.Available, text.Output ?? string.Empty, text.ErrorOutput ?? string.Empty, cut);
        }

        if (output.State == ScriptOutputState.Available)
        {
            // Script output can hold personal data: every time it is shown is in the audit log, without the output.
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptOutputViewed, "Endpoint", endpointId.ToString(), context.ClientId,
                new { context.Hostname, CheckId = checkId, Check = context.Check.Name, Run = view.At }), now));
            await db.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult<ScriptOutputView>.Ok(output);
    }

    /// <summary>The script check of a managed endpoint, as it applies to it; null when the caller may not see it.</summary>
    private async Task<CheckContext?> LoadCheckAsync(FleetoDbContext db, Guid endpointId, Guid checkId, CancellationToken cancellationToken)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null ||
            TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return null;
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { } check || !ScriptOutputRules.Applies(check.Type))
        {
            return null;
        }

        var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
        var exitCodes = Guid.TryParse(parameters.GetValueOrDefault(CheckCatalog.ScriptParameter), out var scriptId)
            ? (await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scriptId, cancellationToken))?.ExitCodes
            : null;
        return new CheckContext(check, parameters, exitCodes, endpoint.ClientId, endpoint.Hostname);
    }

    private sealed record CheckContext(EffectiveCheck Check, IReadOnlyDictionary<string, string> Parameters, ExitCodeRules? ExitCodes,
        Guid ClientId, string Hostname)
    {
        /// <summary>Where the output limit of the check is changed, for the message of an output that was cut off.</summary>
        public string Where => Check.Source == CheckSource.Endpoint || Check.TemplateName is null
            ? "the Checks tab of this endpoint"
            : $"monitoring template {Check.TemplateName}";

        public ScriptRunView View(RunRow r) => new(r.Id, DateTime.SpecifyKind(r.Time, DateTimeKind.Utc), DateTime.SpecifyKind(r.At, DateTimeKind.Utc),
            r.Value, r.Error, r.Detail,
            CheckEvaluator.Evaluate(Check.Type, string.Empty, r.Value, r.Error, Check.WarningThreshold, Check.CriticalThreshold, Parameters, ExitCodes),
            r.OutputBytes, r.OutputLimitBytes);
    }

    private sealed class RunRow
    {
        public long Id { get; set; }
        public DateTime Time { get; set; }
        public DateTime At { get; set; }
        public double Value { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public long? OutputBytes { get; set; }
        public int? OutputLimitBytes { get; set; }
    }

    private sealed class OutputRow
    {
        public string? Output { get; set; }
        public string? ErrorOutput { get; set; }
    }
}
