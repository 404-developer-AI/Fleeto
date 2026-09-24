using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record ChannelClient(Guid Id, string Code, string Name);

/// <summary>The latest webhook delivery of a channel that has been tried: delivered, or failed with its error.</summary>
public sealed record ChannelDelivery(DateTime At, bool Delivered, string? Error, bool GivenUp);

public sealed record NotificationChannelView(Guid Id, string Name, NotificationChannelType Type, string Recipients, WebhookFormat? WebhookFormat,
    string? WebhookHost, AlertSeverity MinimumSeverity, bool NotifyOnResolve, bool Enabled, bool AllClients, IReadOnlyList<ChannelClient> Clients,
    DateTime UpdatedAt, ChannelDelivery? LastDelivery);

/// <param name="WebhookUrl">Webhook channels: the URL; blank when editing keeps the current URL.</param>
/// <param name="ClientIds">The clients whose alerts the channel receives when <paramref name="AllClients"/> is false.</param>
public sealed record NotificationChannelInput(string? Name, NotificationChannelType Type, string? Recipients, WebhookFormat WebhookFormat,
    string? WebhookUrl, AlertSeverity MinimumSeverity, bool NotifyOnResolve, bool Enabled, bool AllClients, IReadOnlyCollection<Guid> ClientIds);

/// <param name="SigningSecret">Set only when a new signing secret was created; shown once and never again.</param>
public sealed record NotificationChannelSaved(Guid Id, string? SigningSecret);

/// <summary>
/// Notification channels for admins: email and webhook channels with their routing rules (clients, minimum severity,
/// resolves). Webhook URLs and signing secrets are write-only: stored encrypted, bound to the channel, never returned; a
/// signing secret is shown once when it is created.
/// </summary>
public sealed class NotificationChannelService
{
    public const int MaxClients = 1000;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;

    public NotificationChannelService(IFleetoDbContextFactory dbFactory, ISecretProtector protector, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _time = time;
    }

    public async Task<IReadOnlyList<NotificationChannelView>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var channels = await db.NotificationChannels.AsNoTracking().Include(c => c.Clients).OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var clientIds = channels.SelectMany(c => c.Clients).Select(c => c.ClientId).Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .Select(c => new ChannelClient(c.Id, c.Code, c.Name)).ToDictionaryAsync(c => c.Id, cancellationToken);

        var views = new List<NotificationChannelView>(channels.Count);
        foreach (var channel in channels)
        {
            ChannelDelivery? last = null;
            if (channel.Type == NotificationChannelType.Webhook)
            {
                last = await db.OutboxWebhooks.AsNoTracking()
                    // A notification combined into a digest (0.6.0) did not go out itself; the digest row says how delivery went.
                    .Where(w => w.NotificationChannelId == channel.Id && w.BundledInto == null && (w.SentAt != null || w.LastError != null))
                    .OrderByDescending(w => w.CreatedAt)
                    .Select(w => new ChannelDelivery(w.SentAt ?? w.CreatedAt, w.SentAt != null, w.LastError, w.SentAt == null && w.Attempts >= WebhookTargets.MaxDeliveryAttempts))
                    .FirstOrDefaultAsync(cancellationToken);
            }

            views.Add(new NotificationChannelView(channel.Id, channel.Name, channel.Type, channel.Recipients, channel.WebhookFormat, channel.WebhookHost,
                channel.MinimumSeverity, channel.NotifyOnResolve, channel.Enabled, channel.AllClients,
                channel.Clients.Select(c => clients.GetValueOrDefault(c.ClientId)).OfType<ChannelClient>().OrderBy(c => c.Code).ToList(),
                channel.UpdatedAt, last));
        }

        return views;
    }

    /// <summary>Every client, for the routing choice.</summary>
    public async Task<IReadOnlyList<ChannelClient>> ListClientsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        return await db.Clients.AsNoTracking().OrderBy(c => c.Code).Select(c => new ChannelClient(c.Id, c.Code, c.Name)).ToListAsync(cancellationToken);
    }

    public async Task<ServiceResult<NotificationChannelSaved>> SaveAsync(Caller caller, Guid? channelId, NotificationChannelInput input,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<NotificationChannelSaved>.Forbidden();
        }

        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > 100)
        {
            return ServiceResult<NotificationChannelSaved>.Fail("Enter a channel name of at most 100 characters.");
        }

        if (!Enum.IsDefined(input.Type) || !Enum.IsDefined(input.WebhookFormat) || !Enum.IsDefined(input.MinimumSeverity))
        {
            return ServiceResult<NotificationChannelSaved>.Fail("Choose the type, format and minimum severity of the channel.");
        }

        var recipients = new List<string>();
        if (input.Type == NotificationChannelType.Email)
        {
            recipients = ParseRecipients(input.Recipients, out var recipientProblem);
            if (recipientProblem is not null)
            {
                return ServiceResult<NotificationChannelSaved>.Fail(recipientProblem);
            }
        }

        var clientIds = input.AllClients ? [] : input.ClientIds.Distinct().ToList();
        if (!input.AllClients && clientIds.Count == 0)
        {
            return ServiceResult<NotificationChannelSaved>.Fail("Choose at least one client, or send alerts of all clients.");
        }

        if (clientIds.Count > MaxClients)
        {
            return ServiceResult<NotificationChannelSaved>.Fail($"A channel can be limited to at most {MaxClients} clients. Send alerts of all clients instead.");
        }

        await using var db = _dbFactory.CreateSystem();
        if (clientIds.Count > 0 && await db.Clients.CountAsync(c => clientIds.Contains(c.Id), cancellationToken) != clientIds.Count)
        {
            return ServiceResult<NotificationChannelSaved>.Fail("One of the chosen clients no longer exists. Reopen the dialog and choose again.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        NotificationChannel channel;
        if (channelId is { } id)
        {
            var existing = await db.NotificationChannels.Include(c => c.Clients).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (existing is null)
            {
                return ServiceResult<NotificationChannelSaved>.NotFound("notification channel");
            }

            if (existing.Type != input.Type)
            {
                return ServiceResult<NotificationChannelSaved>.Fail("The type of a channel cannot change. Create a new channel instead.");
            }

            channel = existing;
        }
        else
        {
            channel = new NotificationChannel { Id = Guid.NewGuid(), Type = input.Type, CreatedAt = now };
            db.NotificationChannels.Add(channel);
        }

        string? newSecret = null;
        var urlChanged = false;
        if (input.Type == NotificationChannelType.Webhook)
        {
            var current = ReadTarget(channel);
            var url = current?.Url;
            string? host = channel.WebhookHost;
            if (!string.IsNullOrWhiteSpace(input.WebhookUrl) || current is null)
            {
                if (WebhookTargets.ValidateUrl(input.WebhookUrl, out var uri) is { } urlProblem)
                {
                    return ServiceResult<NotificationChannelSaved>.Fail(urlProblem);
                }

                urlChanged = url != uri!.AbsoluteUri;
                url = uri.AbsoluteUri;
                host = uri.IdnHost;
            }

            var secret = current?.SigningSecret;
            if (input.WebhookFormat == WebhookFormat.Generic && secret is null)
            {
                secret = newSecret = WebhookTargets.NewSigningSecret();
            }
            else if (input.WebhookFormat != WebhookFormat.Generic)
            {
                secret = null;
            }

            channel.WebhookFormat = input.WebhookFormat;
            channel.WebhookHost = host;
            channel.EncryptedWebhook = WebhookTargets.Protect(_protector, channel.Id, new WebhookTarget(url!, secret));
            channel.Recipients = string.Empty;
        }
        else
        {
            channel.Recipients = string.Join(", ", recipients);
        }

        channel.Name = name;
        channel.MinimumSeverity = input.MinimumSeverity;
        channel.NotifyOnResolve = input.NotifyOnResolve;
        channel.Enabled = input.Enabled;
        channel.AllClients = input.AllClients;
        channel.Clients.RemoveAll(c => !clientIds.Contains(c.ClientId));
        foreach (var clientId in clientIds.Where(c => channel.Clients.All(x => x.ClientId != c)))
        {
            channel.Clients.Add(new NotificationChannelClient { NotificationChannelId = channel.Id, ClientId = clientId });
        }

        channel.UpdatedAt = now;
        var action = urlChanged || newSecret is not null ? AuditActions.CredentialChanged : AuditActions.NotificationChannelChanged;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, "NotificationChannel", channel.Id.ToString(), null, new
        {
            Change = channelId is null ? "created" : "updated",
            channel.Name,
            Type = channel.Type.ToString(),
            WebhookFormat = channel.WebhookFormat?.ToString(),
            channel.WebhookHost,
            UrlChanged = urlChanged,
            SigningSecretCreated = newSecret is not null,
            Recipients = recipients.Count,
            MinimumSeverity = channel.MinimumSeverity.ToString(),
            channel.NotifyOnResolve,
            channel.Enabled,
            channel.AllClients,
            Clients = clientIds.Count
        }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<NotificationChannelSaved>.Ok(new NotificationChannelSaved(channel.Id, newSecret));
    }

    /// <summary>Replaces the signing secret of a generic webhook channel. The old secret stops working at once.</summary>
    public async Task<ServiceResult<string>> RotateSigningSecretAsync(Caller caller, Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<string>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ServiceResult<string>.NotFound("notification channel");
        }

        if (channel is not { Type: NotificationChannelType.Webhook, WebhookFormat: WebhookFormat.Generic, EncryptedWebhook: not null })
        {
            return ServiceResult<string>.Fail("Only generic webhook channels have a signing secret.");
        }

        if (ReadTarget(channel) is not { } target)
        {
            return ServiceResult<string>.Fail("The stored webhook URL could not be decrypted. Enter the URL again, then create a new secret.");
        }

        var secret = WebhookTargets.NewSigningSecret();
        var now = _time.GetUtcNow().UtcDateTime;
        channel.EncryptedWebhook = WebhookTargets.Protect(_protector, channel.Id, target with { SigningSecret = secret });
        channel.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.CredentialChanged, "NotificationChannel", channel.Id.ToString(), null,
            new { Change = "signing secret replaced", channel.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<string>.Ok(secret);
    }

    /// <summary>Queues a test message on a webhook channel; the workers deliver it like any notification.</summary>
    public async Task<ServiceResult> SendTestAsync(Caller caller, Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var channel = await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ServiceResult.NotFound("notification channel");
        }

        if (channel is not { Type: NotificationChannelType.Webhook, WebhookFormat: { } format })
        {
            return ServiceResult.Fail("Send a test email from Settings, Email instead.");
        }

        var instance = await db.InstanceSettings.AsNoTracking().Select(i => new { i.Fqdn, i.WebBaseUrl }).SingleAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        db.OutboxWebhooks.Add(new OutboxWebhook
        {
            Id = id,
            NotificationChannelId = channel.Id,
            Category = "test",
            Payload = WebhookPayloads.Test(format, id, instance.Fqdn, instance.WebBaseUrl.TrimEnd('/') + "/settings/notification-channels", now),
            NextAttemptAt = now,
            CreatedAt = now
        });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NotificationChannelChanged, "NotificationChannel", channel.Id.ToString(), null,
            new { Change = "test message queued", channel.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ServiceResult.NotFound("notification channel");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.NotificationChannels.Remove(channel);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NotificationChannelChanged, "NotificationChannel", channel.Id.ToString(), null,
            new { Change = "deleted", channel.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The stored target, or null when there is none or it can no longer be decrypted (then a new URL is required).</summary>
    private WebhookTarget? ReadTarget(NotificationChannel channel)
    {
        if (channel.EncryptedWebhook is null)
        {
            return null;
        }

        try
        {
            return WebhookTargets.Unprotect(_protector, channel.Id, channel.EncryptedWebhook);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Splits recipients on commas, semicolons and line breaks and validates each address.</summary>
    internal static List<string> ParseRecipients(string? text, out string? problem)
    {
        problem = null;
        var recipients = (text ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            problem = "Enter at least one email address.";
            return recipients;
        }

        if (recipients.Count > 50)
        {
            problem = "A channel can have at most 50 recipients.";
            return recipients;
        }

        var invalid = recipients.FirstOrDefault(r => !ServiceSupport.IsValidEmail(r));
        if (invalid is not null)
        {
            problem = $"{invalid} is not a valid email address. Correct it and try again.";
            return recipients;
        }

        if (string.Join(", ", recipients).Length > 2000)
        {
            problem = "The recipient list is too long. Use fewer addresses, or a distribution list.";
        }

        return recipients;
    }
}
