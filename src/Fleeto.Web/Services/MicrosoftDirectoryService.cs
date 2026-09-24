using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>A user of the tenant as Settings shows it, with the Fleeto user it is already linked to.</summary>
/// <param name="LinkedTo">The email address of the Fleeto user linked to this account, or null.</param>
public sealed record DirectoryUserView(Guid ObjectId, string DisplayName, string? Account, string? Mail, string? LinkedTo)
{
    /// <summary>The address a new Fleeto user gets: the mail address, else the account name.</summary>
    public string? Email => Mail ?? Account;

    public override string ToString() => Account is null ? DisplayName : $"{DisplayName} ({Account})";
}

/// <summary>
/// What Settings asks Microsoft (0.5.0): testing the app registration of the sign-in or of Graph email, and finding users of
/// the tenant to link or add. fleeto-web has no outbound access, so every question is a <see cref="MicrosoftRequest"/> the
/// workers answer; this service writes it, waits for the answer and deletes the row once read. Admins only.
/// </summary>
public sealed class MicrosoftDirectoryService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal const string NoAnswerProblem = "Microsoft did not answer in time, or the workers are busy. Try again in a minute.";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public MicrosoftDirectoryService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
    }

    /// <summary>Tests the saved app registration of the sign-in: its credential, and whether users can be chosen from Microsoft.</summary>
    public Task<ServiceResult<IReadOnlyList<MicrosoftCheck>>> TestSignInAsync(Caller caller, CancellationToken cancellationToken = default) =>
        TestAsync(caller, MicrosoftRequestKind.TestSignIn, cancellationToken);

    /// <summary>Tests the saved app registration of Microsoft Graph email: its credential, and whether it may send.</summary>
    public Task<ServiceResult<IReadOnlyList<MicrosoftCheck>>> TestEmailAsync(Caller caller, CancellationToken cancellationToken = default) =>
        TestAsync(caller, MicrosoftRequestKind.TestEmail, cancellationToken);

    /// <summary>Members of the tenant whose name, account or address starts with <paramref name="query"/>.</summary>
    public async Task<ServiceResult<IReadOnlyList<DirectoryUserView>>> SearchUsersAsync(Caller caller, string? query,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<IReadOnlyList<DirectoryUserView>>.Forbidden();
        }

        var answer = await AskAsync(caller, MicrosoftRequestKind.SearchUsers, MicrosoftGraphClient.CleanQuery(query), cancellationToken);
        if (answer is not { State: MicrosoftRequestState.Completed })
        {
            return ServiceResult<IReadOnlyList<DirectoryUserView>>.Fail(answer?.FailureReason ?? NoAnswerProblem);
        }

        var users = MicrosoftAnswers.Users(answer.ResultJson);
        var ids = users.Select(u => (Guid?)u.ObjectId).ToList();
        await using var db = _dbFactory.CreateSystem();
        var linked = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.EntraObjectId))
            .Select(u => new { u.EntraObjectId, u.Email })
            .ToDictionaryAsync(u => u.EntraObjectId!.Value, u => u.Email, cancellationToken);
        return ServiceResult<IReadOnlyList<DirectoryUserView>>.Ok(users
            .Select(u => new DirectoryUserView(u.ObjectId, u.DisplayName, u.UserPrincipalName, u.Mail, linked.GetValueOrDefault(u.ObjectId)))
            .ToList());
    }

    private async Task<ServiceResult<IReadOnlyList<MicrosoftCheck>>> TestAsync(Caller caller, MicrosoftRequestKind kind, CancellationToken cancellationToken)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<IReadOnlyList<MicrosoftCheck>>.Forbidden();
        }

        var answer = await AskAsync(caller, kind, null, cancellationToken);
        return answer is { State: MicrosoftRequestState.Completed }
            ? ServiceResult<IReadOnlyList<MicrosoftCheck>>.Ok(MicrosoftAnswers.Checks(answer.ResultJson))
            : ServiceResult<IReadOnlyList<MicrosoftCheck>>.Fail(answer?.FailureReason ?? NoAnswerProblem);
    }

    /// <summary>Writes the question, waits for the workers and deletes the row: the answer is read once.</summary>
    private async Task<MicrosoftRequest?> AskAsync(Caller caller, MicrosoftRequestKind kind, string? query, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var request = new MicrosoftRequest
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            RequestedBy = caller.UserId,
            Kind = kind,
            Query = query,
            State = MicrosoftRequestState.Requested
        };
        db.MicrosoftRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(NotificationChannels.MicrosoftRequests, request.Id.ToString("D"), cancellationToken);

        var deadline = _time.GetUtcNow() + MicrosoftAnswers.Wait;
        MicrosoftRequest? answer;
        while (true)
        {
            answer = await db.MicrosoftRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
            if (answer is null || answer.State != MicrosoftRequestState.Requested || _time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(PollInterval, _time, cancellationToken);
        }

        db.ChangeTracker.Clear();
        await db.MicrosoftRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync(CancellationToken.None);
        return answer is { State: MicrosoftRequestState.Requested } ? null : answer;
    }
}
