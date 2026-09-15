using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Settings;
using MimeKit;

namespace Fleeto.Workers.Email;

/// <summary>
/// Sends through Microsoft Graph <c>sendMail</c> as the configured mailbox, with the client credentials flow (client secret or
/// certificate assertion). One token per pass, renewed when it is about to expire or refused. Graph takes one HTML body; the
/// plain-text alternative is not sent. Failures carry cause and next step, never tokens or secrets.
/// </summary>
internal sealed class GraphEmailSession : IEmailSession
{
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(5);

    private readonly GraphMailSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public GraphEmailSession(GraphMailSettings settings, HttpClient http, TimeProvider time)
    {
        _settings = settings;
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
        var now = _time.GetUtcNow();
        if (_token is not null && now < _tokenExpiresAt - TokenMargin)
        {
            return _token;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", _settings.ClientId),
            new("scope", GraphMail.Scope),
            new("grant_type", "client_credentials")
        };
        if (_settings.CredentialType == GraphCredentialType.Certificate)
        {
            if (_settings.CertificatePfx is null)
            {
                throw new EmailDeliveryException("No certificate is in use for Microsoft Graph. Create one in Settings, Email.", permanent: false);
            }

            form.Add(new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
            form.Add(new("client_assertion", GraphMail.ClientAssertion(_settings.CertificatePfx, _settings.TenantId, _settings.ClientId, now)));
        }
        else
        {
            form.Add(new("client_secret", _settings.ClientSecret ?? string.Empty));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphMail.TokenEndpoint(_settings.TenantId)) { Content = new FormUrlEncodedContent(form) };
        using var response = await PostAsync(request, "Microsoft Entra ID", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var aadsts = await EntraErrorAsync(response, cancellationToken);
            throw new EmailDeliveryException(aadsts switch
            {
                "AADSTS7000222" => "The Microsoft Graph client secret has expired. Create a new secret in the app registration and save it in Settings, Email.",
                "AADSTS7000215" => "Microsoft Entra ID refused the client secret. Enter the secret value (not its ID) in Settings, Email.",
                "AADSTS700027" or "AADSTS700024" => "Microsoft Entra ID refused the certificate. Upload the certificate from Settings, Email to the app registration.",
                "AADSTS700016" => "Microsoft Entra ID does not know the application ID. Check it in Settings, Email.",
                "AADSTS90002" or "AADSTS900023" => "Microsoft Entra ID does not know the tenant. Check the tenant ID in Settings, Email.",
                _ => $"Microsoft Entra ID refused the sign-in ({aadsts ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}). Check the tenant ID, application ID and credential in Settings, Email."
            }, permanent: false);
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("access_token", out var accessToken) || accessToken.GetString() is not { Length: > 0 } value)
        {
            throw new EmailDeliveryException("Microsoft Entra ID answered without an access token. Fleeto tries again.", permanent: false);
        }

        var lifetime = json.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var secondsValue) ? secondsValue : 3599;
        _token = value;
        _tokenExpiresAt = now.AddSeconds(lifetime);
        return value;
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

    /// <summary>The AADSTS code from a token error; the description is not kept (it carries trace and correlation ids).</summary>
    private static async Task<string?> EntraErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0 &&
                codes[0].TryGetInt32(out var first))
            {
                return "AADSTS" + first.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return json.RootElement.TryGetProperty("error", out var error) ? Safe(error.GetString()) : null;
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
