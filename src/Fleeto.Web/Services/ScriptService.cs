using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record ScriptListItem(Guid Id, string Name, string? Description, ScriptLanguage Language, Guid? ClientId, string? ClientCode,
    int? CurrentVersionNumber, bool CurrentApproved, DateTime UpdatedAt);

public sealed record ScriptVersionView(Guid Id, int Number, string Sha256, int TimeoutSeconds, Guid AuthorUserId, string AuthorName, DateTime CreatedAt,
    string? ApprovedByName, DateTime? ApprovedAt, bool IsApproved, bool IsCurrent);

public sealed record ScriptDetail(Guid Id, string Name, string? Description, ScriptLanguage Language, Guid? ClientId, string? ClientCode,
    Guid? CurrentVersionId, string Body, int TimeoutSeconds, IReadOnlyList<ScriptVersionView> Versions);

/// <summary>A script a check can use: global or of the check's client.</summary>
public sealed record ScriptOption(Guid Id, string Name, ScriptLanguage Language, string? ClientCode);

public sealed record ScriptInput(string? Name, string? Description, ScriptLanguage Language, Guid? ClientId, string? Body, int TimeoutSeconds);

/// <summary>Script checks (0.2.0): which script a check may use, and the configuration changes a script change causes.</summary>
public static class ScriptChecks
{
    public static ConfigChangeEvent ChangeEvent(Guid scriptId, DateTime now) =>
        new() { Scope = ConfigChangeScope.Script, ScopeId = scriptId, CreatedAt = now };

    /// <summary>Checks of every client that use the script (the database role sees them all; the count reveals nothing else).</summary>
    public static async Task<int> CountChecksAsync(FleetoDbContext db, Guid scriptId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM "CheckDefinitions" WHERE "Type" = 'Script' AND "ParametersJson"->>'script' = {scriptId.ToString("D")}
            """).SingleAsync(cancellationToken);

    /// <summary>
    /// Checks the script of a script check against the check's owner and copies the script's language into the parameters. The script
    /// must be visible to the caller and global, or of the same client as the check; a global monitoring template only uses global
    /// scripts. Returns a problem (cause and next step), or null.
    /// </summary>
    public static async Task<string?> BindAsync(FleetoDbContext db, Guid? checkClientId, Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        parameters.Remove(CheckCatalog.ScriptLanguageParameter);
        if (!parameters.TryGetValue(CheckCatalog.ScriptParameter, out var text) || !Guid.TryParse(text, out var scriptId))
        {
            return "Choose a script from the list.";
        }

        var script = await db.Scripts.AsNoTracking().Where(s => s.Id == scriptId).Select(s => new { s.ClientId, s.Language })
            .SingleOrDefaultAsync(cancellationToken);
        if (script is null)
        {
            return "The script does not exist or you have no access to it. Choose another script.";
        }

        if (script.ClientId is { } scriptClient && scriptClient != checkClientId)
        {
            return checkClientId is null
                ? "A global monitoring template can only use global scripts. Choose a global script, or copy the template for the client."
                : "The script belongs to another client. Choose a global script or one of this client.";
        }

        parameters[CheckCatalog.ScriptParameter] = scriptId.ToString("D");
        parameters[CheckCatalog.ScriptLanguageParameter] = script.Language.ToString();
        return null;
    }
}

/// <summary>
/// The script library (0.2.0): scripts, global or per client, with immutable versions. Saving a changed body creates a new
/// version, which needs a new approval where a policy requires approval. Approval is by an admin who did not write the version,
/// with a fresh two-factor code, and is bound to the body hash (ARCHITECTURE.md §4, Script approval).
/// </summary>
public sealed class ScriptService
{
    /// <summary>A two-factor code used for an approval cannot be used again within this period.</summary>
    internal static readonly TimeSpan CodeReuseWindow = TimeSpan.FromMinutes(3);

    internal const string ApprovalTokenProvider = "Fleeto";
    internal const string ApprovalTokenName = "ScriptApprovalLastCode";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    public ScriptService(IFleetoDbContextFactory dbFactory, IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _time = time;
    }

    public async Task<IReadOnlyList<ScriptListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var rows = await db.Scripts.AsNoTracking()
            .OrderBy(s => s.ClientId != null).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.Description, s.Language, s.ClientId, s.UpdatedAt,
                ClientCode = db.Clients.Where(c => c.Id == s.ClientId).Select(c => c.Code).FirstOrDefault(),
                Current = db.ScriptVersions.Where(v => v.Id == s.CurrentVersionId)
                    .Select(v => new { v.Number, v.ApprovedAt, v.ApprovedByUserId, v.AuthorUserId, v.ApprovedSha256, v.Sha256 })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ScriptListItem(r.Id, r.Name, r.Description, r.Language, r.ClientId, r.ClientCode, r.Current?.Number,
            r.Current is { ApprovedAt: not null, ApprovedByUserId: { } approver } current && approver != current.AuthorUserId && current.ApprovedSha256 == current.Sha256,
            r.UpdatedAt)).ToList();
    }

    /// <summary>
    /// The scripts a script check may use: global ones, and those of <paramref name="clientId"/> when the check belongs to a client.
    /// With <paramref name="osPlatform"/> only scripts whose language runs there.
    /// </summary>
    public async Task<IReadOnlyList<ScriptOption>> ListForChecksAsync(Caller caller, Guid? clientId, string? osPlatform = null,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var rows = await db.Scripts.AsNoTracking()
            .Where(s => s.ClientId == null || s.ClientId == clientId)
            .OrderBy(s => s.ClientId != null).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Language, ClientCode = db.Clients.Where(c => c.Id == s.ClientId).Select(c => c.Code).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return rows.Where(r => osPlatform is null || ScriptLanguages.RunsOn(r.Language, osPlatform))
            .Select(r => new ScriptOption(r.Id, r.Name, r.Language, r.ClientCode)).ToList();
    }

    public async Task<ScriptDetail?> GetAsync(Caller caller, Guid scriptId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var script = await db.Scripts.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        if (script is null)
        {
            return null;
        }

        var clientCode = script.ClientId is { } clientId
            ? await db.Clients.Where(c => c.Id == clientId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken)
            : null;
        var current = script.Versions.SingleOrDefault(v => v.Id == script.CurrentVersionId);
        return new ScriptDetail(script.Id, script.Name, script.Description, script.Language, script.ClientId, clientCode, script.CurrentVersionId,
            current?.Body ?? string.Empty, current?.TimeoutSeconds ?? ScriptRules.DefaultTimeoutSeconds,
            script.Versions.OrderByDescending(v => v.Number).Select(v => View(v, script.CurrentVersionId)).ToList());
    }

    /// <summary>The body of one version, for review before approval and for history.</summary>
    public async Task<(ScriptVersionView Version, string Body)?> GetVersionAsync(Caller caller, Guid versionId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var version = await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var currentId = await db.Scripts.Where(s => s.Id == version.ScriptId).Select(s => s.CurrentVersionId).SingleOrDefaultAsync(cancellationToken);
        return (View(version, currentId), version.Body);
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, ScriptInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        if ((ValidateDetails(input.Name, input.Description) ?? ValidateVersion(input.Body, input.TimeoutSeconds)) is { } problem)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        if (!Enum.IsDefined(input.Language))
        {
            return ServiceResult<Guid>.Fail("Choose the language of the script.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        if (input.ClientId is { } clientId && !await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            return ServiceResult<Guid>.NotFound("client");
        }

        var name = input.Name!.Trim();
        if (await NameTakenAsync(db, input.ClientId, name, null, cancellationToken))
        {
            return ServiceResult<Guid>.Fail($"A script named {name} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var script = new Script
        {
            Id = Guid.NewGuid(), ClientId = input.ClientId, Name = name, Description = ServiceSupport.Clean(input.Description), Language = input.Language,
            CreatedAt = now, UpdatedAt = now
        };
        var version = NewVersion(caller, script, 1, input.Body!, input.TimeoutSeconds, now);
        script.CurrentVersionId = version.Id;
        db.Scripts.Add(script);
        db.ScriptVersions.Add(version);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptCreated, "Script", script.Id.ToString(), script.ClientId,
            new { script.Name, Language = script.Language.ToString(), Version = 1, version.Sha256, version.TimeoutSeconds }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(script.Id);
    }

    public async Task<ServiceResult> UpdateDetailsAsync(Caller caller, Guid scriptId, string? name, string? description, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        if (ValidateDetails(name, description) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var script = await db.Scripts.SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        if (script is null)
        {
            return ServiceResult.NotFound("script");
        }

        var cleanName = name!.Trim();
        if (await NameTakenAsync(db, script.ClientId, cleanName, script.Id, cancellationToken))
        {
            return ServiceResult.Fail($"A script named {cleanName} already exists here. Choose another name.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (script.Name != cleanName)
        {
            // The name travels in the signed configuration of script checks.
            db.ConfigChangeEvents.Add(ScriptChecks.ChangeEvent(script.Id, now));
        }

        script.Name = cleanName;
        script.Description = ServiceSupport.Clean(description);
        script.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptChanged, "Script", script.Id.ToString(), script.ClientId, new { script.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Saves a new version when the body or timeout changed. Returns the current version number.</summary>
    public async Task<ServiceResult<int>> SaveVersionAsync(Caller caller, Guid scriptId, string? body, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<int>.Forbidden();
        }

        if (ValidateVersion(body, timeoutSeconds) is { } problem)
        {
            return ServiceResult<int>.Fail(problem);
        }

        await using var db = _dbFactory.Create(caller.Scope);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scripts = await db.Scripts.FromSql($"""SELECT * FROM "Scripts" WHERE "Id" = {scriptId} FOR UPDATE""").ToListAsync(cancellationToken);
        if (scripts.Count == 0)
        {
            return ServiceResult<int>.NotFound("script");
        }

        var script = scripts[0];
        var current = await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == script.CurrentVersionId, cancellationToken);
        var normalized = ScriptLanguages.Normalize(script.Language, body!);
        if (current is not null && current.Sha256 == ScriptLanguages.Sha256(normalized) && current.TimeoutSeconds == timeoutSeconds)
        {
            return ServiceResult<int>.Ok(current.Number);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var number = (await db.ScriptVersions.Where(v => v.ScriptId == script.Id).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
        var version = NewVersion(caller, script, number, body!, timeoutSeconds, now);
        db.ScriptVersions.Add(version);
        script.CurrentVersionId = version.Id;
        script.UpdatedAt = now;
        db.ConfigChangeEvents.Add(ScriptChecks.ChangeEvent(script.Id, now));
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptVersionSaved, "Script", script.Id.ToString(), script.ClientId,
            new { script.Name, Version = number, version.Sha256, PreviousSha256 = current?.Sha256, version.TimeoutSeconds }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<int>.Ok(number);
    }

    /// <summary>
    /// Approves a version: the caller is an admin, did not write the version, and proves presence with a fresh two-factor code
    /// that was not used for an approval in the last minutes. Wrong codes count towards lockout.
    /// </summary>
    public async Task<ServiceResult> ApproveAsync(Caller caller, Guid versionId, string? code, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Fail("Only an admin can approve a script. Ask an admin who did not write this version.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var version = await db.ScriptVersions.SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version is null)
        {
            return ServiceResult.NotFound("script version");
        }

        if (version.AuthorUserId == caller.UserId)
        {
            return ServiceResult.Fail("You wrote this version. A second admin has to approve it.");
        }

        if (version.IsApproved)
        {
            return ServiceResult.Ok();
        }

        var normalizedCode = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (normalizedCode.Length != 6)
        {
            return ServiceResult.Fail("Enter the 6-digit code from your authenticator app.");
        }

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(caller.UserId.ToString());
            if (user is null || !user.TwoFactorEnabled)
            {
                return ServiceResult.Fail("Your account has no two-factor authentication. Set it up, then approve the script.");
            }

            if (await users.IsLockedOutAsync(user))
            {
                return ServiceResult.Fail("Your account is locked after too many wrong codes. Try again later.");
            }

            var now = _time.GetUtcNow();
            var codeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caller.UserId.ToString("N") + ":" + normalizedCode)));
            var last = await users.GetAuthenticationTokenAsync(user, ApprovalTokenProvider, ApprovalTokenName);
            if (last?.Split('|') is [var lastHash, var lastTicks] && lastHash == codeHash && long.TryParse(lastTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                now.UtcTicks - ticks < CodeReuseWindow.Ticks)
            {
                return ServiceResult.Fail("This code was already used. Wait for the next code in your authenticator app.");
            }

            if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalizedCode))
            {
                await users.AccessFailedAsync(user);
                db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptVersionApproved, "ScriptVersion", version.Id.ToString(), version.ClientId,
                    new { Outcome = "refused: wrong two-factor code", version.Number }), now.UtcDateTime));
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult.Fail("The code is not valid. Enter the current code from your authenticator app.");
            }

            await users.ResetAccessFailedCountAsync(user);
            await users.SetAuthenticationTokenAsync(user, ApprovalTokenProvider, ApprovalTokenName,
                codeHash + "|" + now.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        var approvedAt = _time.GetUtcNow().UtcDateTime;
        version.ApprovedByUserId = caller.UserId;
        version.ApprovedByName = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name;
        version.ApprovedAt = approvedAt;
        version.ApprovedSha256 = version.Sha256;
        db.ConfigChangeEvents.Add(ScriptChecks.ChangeEvent(version.ScriptId, approvedAt));
        var scriptName = await db.Scripts.Where(s => s.Id == version.ScriptId).Select(s => s.Name).SingleAsync(cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptVersionApproved, "ScriptVersion", version.Id.ToString(), version.ClientId,
            new { Outcome = "approved", Script = scriptName, version.ScriptId, version.Number, version.Sha256, Author = version.AuthorName }), approvedAt));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Deletes a script and its versions. Jobs keep their history; queued jobs still run what was signed. A script that checks use
    /// cannot be deleted, so a check never silently stops running.
    /// </summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid scriptId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var script = await db.Scripts.SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        if (script is null)
        {
            return ServiceResult.NotFound("script");
        }

        var checks = await ScriptChecks.CountChecksAsync(db, script.Id, cancellationToken);
        if (checks > 0)
        {
            return ServiceResult.Fail(checks == 1
                ? "One check uses this script. Remove the check or choose another script for it, then delete the script."
                : $"{checks} checks use this script. Remove them or choose another script for them, then delete the script.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.Scripts.Remove(script);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ScriptDeleted, "Script", script.Id.ToString(), script.ClientId, new { script.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static ScriptVersion NewVersion(Caller caller, Script script, int number, string body, int timeoutSeconds, DateTime now)
    {
        var normalized = ScriptLanguages.Normalize(script.Language, body);
        return new ScriptVersion
        {
            Id = Guid.NewGuid(), ScriptId = script.Id, ClientId = script.ClientId, Number = number, Body = normalized, Sha256 = ScriptLanguages.Sha256(normalized),
            TimeoutSeconds = timeoutSeconds, AuthorUserId = caller.UserId, AuthorName = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name, CreatedAt = now
        };
    }

    private static ScriptVersionView View(ScriptVersion v, Guid? currentId) =>
        new(v.Id, v.Number, v.Sha256, v.TimeoutSeconds, v.AuthorUserId, v.AuthorName, v.CreatedAt, v.ApprovedByName, v.ApprovedAt, v.IsApproved, v.Id == currentId);

    private static Task<bool> NameTakenAsync(FleetoDbContext db, Guid? clientId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Scripts.IgnoreQueryFilters().AnyAsync(s => s.ClientId == clientId && s.Name.ToLower() == name.ToLower() && s.Id != exceptId, cancellationToken);

    internal static string? ValidateDetails(string? name, string? description)
    {
        var clean = ServiceSupport.Clean(name);
        if (clean is null || clean.Length > 100)
        {
            return "Enter a script name of at most 100 characters.";
        }

        return description is { Length: > 1000 } ? "The description can be at most 1000 characters." : null;
    }

    internal static string? ValidateVersion(string? body, int timeoutSeconds)
    {
        if (ScriptLanguages.Validate(body) is { } problem)
        {
            return problem;
        }

        return timeoutSeconds is < ScriptRules.MinTimeoutSeconds or > ScriptRules.MaxTimeoutSeconds
            ? "The timeout must be between 30 seconds and 24 hours."
            : null;
    }
}
