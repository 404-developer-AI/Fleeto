using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using MimeKit;

namespace Fleeto.Workers.Email;

/// <summary>
/// Sends through Microsoft Graph <c>sendMail</c> as the configured mailbox, with the client credentials flow (client secret or
/// certificate assertion) of <see cref="MicrosoftGraphClient"/>, the one place that asks Microsoft for a token. One token per
/// pass, renewed when it is about to expire or refused. Graph takes one HTML body; the plain-text alternative is not sent.
/// Failures carry cause and next step, never tokens or secrets.
/// </summary>
internal sealed class GraphEmailSession : IEmailSession
{
    private const string SettingsPage = "Settings, Email";
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(5);

    private readonly GraphMailSettings _settings;
    private readonly MicrosoftGraphClient _microsoft;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private GraphToken? _token;

    public GraphEmailSession(GraphMailSettings settings, MicrosoftGraphClient microsoft, HttpClient http, TimeProvider time)
    {
        _settings = settings;
        _microsoft = microsoft;
        _http = http;
        _time = time;
    }

    public async Task SendAsync(OutboxEmail email, CancellationToken cancellationToken)
    {
        try
        {
            MailboxAddress.Parse(email.ToAddress);
        }
        catch (ParseException ex)
        {
            throw new EmailDeliveryException("The recipient address is not valid. Correct it in the notification channel.", permanent: true, ex);
        }

        var message = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = EmailTemplates.Subject(email.Subject),
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = email.HtmlBody },
                ["toRecipients"] = new JsonArray { new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email.ToAddress } } },
                ["internetMessageHeaders"] = new JsonArray { new JsonObject { ["name"] = "X-Auto-Response-Suppress", ["value"] = "All" } }
            },
            ["saveToSentItems"] = false
        };

        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphMail.SendMailEndpoint(_settings.SenderAddress))
            {
                Content = JsonContent.Create(message)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await PostAsync(request, "Microsoft Graph", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var code = await ErrorCodeAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // The token was refused (revoked or clock skew): get a new one once.
                _token = null;
                continue;
            }

            throw response.StatusCode switch
            {
                HttpStatusCode.BadRequest when code is "ErrorInvalidRecipients" or "ErrorInvalidRecipientsException" =>
                    new EmailDeliveryException("Microsoft Graph refused the recipient. Correct the address in the notification channel.", permanent: true),
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge =>
                    new EmailDeliveryException($"Microsoft Graph refused the message ({code ?? "400"}).", permanent: true),
                HttpStatusCode.Forbidden =>
                    new EmailDeliveryException($"Microsoft Graph refused to send as {_settings.SenderAddress} ({code ?? "403"}). Check the Mail.Send application permission, admin consent and the application access policy.", permanent: false),
                HttpStatusCode.NotFound =>
                    new EmailDeliveryException($"Microsoft Graph did not find the mailbox {_settings.SenderAddress}. Check the sender address in Settings, Email.", permanent: false),
                _ => new EmailDeliveryException($"Microsoft Graph answered {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}. Fleeto tries again.", permanent: false)
            };
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _time.GetUtcNow() < _token.ExpiresAt - TokenMargin)
        {
            return _token.AccessToken;
        }

        if (GraphMail.Credential(_settings) is not { } credential)
        {
            throw new EmailDeliveryException($"No certificate is in use for Microsoft Graph. Create one in {SettingsPage}.", permanent: false);
        }

        try
        {
            _token = await _microsoft.GetTokenAsync(credential, SettingsPage, cancellationToken);
            return _token.AccessToken;
        }
        catch (MicrosoftGraphException ex)
        {
            throw new EmailDeliveryException(ex.Message, permanent: false, ex);
        }
    }

    private async Task<HttpResponseMessage> PostAsync(HttpRequestMessage request, string service, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailDeliveryException($"{service} did not answer in time.", permanent: false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new EmailDeliveryException($"Could not reach {service}: {ex.HttpRequestError}.", permanent: false, ex);
        }
    }

    /// <summary>The Graph error code (for example ErrorInvalidRecipients), or null.</summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) ? Safe(code.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Safe(string? code) =>
        code is { Length: > 0 and <= 80 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? code : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
