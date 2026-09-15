using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Fleeto.Workers.Email;

/// <summary>A delivery failure. Permanent failures (address refused or invalid) are not retried.</summary>
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message, bool permanent, Exception? inner = null)
        : base(message, inner)
    {
        IsPermanent = permanent;
    }

    /// <summary>True when retrying cannot help; such failures also do not count towards the circuit breaker.</summary>
    public bool IsPermanent { get; }
}

/// <summary>An open delivery session for one pass of the outbox (one SMTP connection for many emails).</summary>
public interface IEmailSession : IAsyncDisposable
{
    Task SendAsync(OutboxEmail email, CancellationToken cancellationToken);
}

/// <summary>Chooses how email leaves the instance.</summary>
public interface IEmailTransportFactory
{
    /// <summary>A session, or null when email is not configured.</summary>
    Task<IEmailSession?> CreateSessionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Chooses the transport per pass (0.2.0): Microsoft Graph when it is the chosen provider and complete; SMTP when chosen, or
/// as fallback while the Graph credential has expired (an expired secret cannot even send its own warning); otherwise the
/// development pickup directory when set; otherwise nothing, and emails stay pending until an admin configures email.
/// </summary>
public sealed class EmailTransportFactory : IEmailTransportFactory, IDisposable
{
    private static readonly TimeSpan FallbackLogInterval = TimeSpan.FromHours(1);

    private readonly SettingsStore _settings;
    private readonly EmailOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly HttpClient _graphHttp;
    private DateTimeOffset _lastFallbackLog = DateTimeOffset.MinValue;

    public EmailTransportFactory(SettingsStore settings, IOptions<EmailOptions> options, ILoggerFactory loggerFactory, TimeProvider time)
        : this(settings, options, loggerFactory, time, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
    {
    }

    internal EmailTransportFactory(SettingsStore settings, IOptions<EmailOptions> options, ILoggerFactory loggerFactory, TimeProvider time,
        HttpMessageHandler graphHandler)
    {
        _settings = settings;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _time = time;
        _graphHttp = new HttpClient(graphHandler) { Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.SmtpTimeoutSeconds)) };
    }

    public async Task<IEmailSession?> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var smtp = await _settings.GetAsync<SmtpSettings>(SettingKeys.Smtp, cancellationToken);
        var smtpConfigured = smtp is not null && !string.IsNullOrWhiteSpace(smtp.Host) && !string.IsNullOrWhiteSpace(smtp.FromAddress);

        var provider = await _settings.GetStringAsync(SettingKeys.EmailProvider, cancellationToken);
        if (provider == nameof(EmailProvider.MicrosoftGraph) &&
            await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) is { IsComplete: true } graph)
        {
            var now = _time.GetUtcNow();
            if (graph.CredentialExpiresAt > now.UtcDateTime || !smtpConfigured)
            {
                return new GraphEmailSession(graph, _graphHttp, _time);
            }

            if (now - _lastFallbackLog >= FallbackLogInterval)
            {
                _loggerFactory.CreateLogger<EmailTransportFactory>().LogWarning(
                    "The Microsoft Graph credential expired on {ExpiresAt:yyyy-MM-dd}; sending through SMTP until a new credential is saved in Settings, Email",
                    graph.CredentialExpiresAt);
                _lastFallbackLog = now;
            }
        }

        if (smtpConfigured)
        {
            return new SmtpEmailSession(smtp!, TimeSpan.FromSeconds(Math.Max(5, _options.SmtpTimeoutSeconds)),
                _loggerFactory.CreateLogger<SmtpEmailSession>());
        }

        if (!string.IsNullOrWhiteSpace(_options.PickupDirectory))
        {
            return new PickupDirectoryEmailSession(Environment.ExpandEnvironmentVariables(_options.PickupDirectory), _options.PickupFromAddress);
        }

        return null;
    }

    public void Dispose() => _graphHttp.Dispose();
}

/// <summary>Builds the MIME message: sender name Fleeto, HTML with a plain-text alternative, marked as auto-generated.</summary>
internal static class MimeMessages
{
    public static MimeMessage Build(OutboxEmail email, string fromName, string fromAddress)
    {
        MailboxAddress to;
        try
        {
            to = MailboxAddress.Parse(email.ToAddress);
        }
        catch (ParseException ex)
        {
            throw new EmailDeliveryException("The recipient address is not valid. Correct it in the notification channel.", permanent: true, ex);
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(fromName) ? EmailTemplates.SenderName : fromName, fromAddress));
        message.To.Add(to);
        message.Subject = EmailTemplates.Subject(email.Subject);
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Body = new BodyBuilder { HtmlBody = email.HtmlBody, TextBody = email.TextBody }.ToMessageBody();
        return message;
    }
}

/// <summary>
/// SMTP via MailKit with certificate validation. The connection is opened on the first email and reused for the rest of
/// the pass; after a failure it is dropped and reopened for the next email.
/// </summary>
internal sealed class SmtpEmailSession : IEmailSession
{
    private readonly SmtpSettings _settings;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private SmtpClient? _client;

    public SmtpEmailSession(SmtpSettings settings, TimeSpan timeout, ILogger logger)
    {
        _settings = settings;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task SendAsync(OutboxEmail email, CancellationToken cancellationToken)
    {
        var message = MimeMessages.Build(email, _settings.FromName, _settings.FromAddress);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout * 3);

        try
        {
            var client = await ConnectAsync(timeoutCts.Token);
            await client.SendAsync(message, timeoutCts.Token);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted ||
                                               ex.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed
                                                   or SmtpStatusCode.UserNotLocalTryAlternatePath)
        {
            // The server answered and refused this recipient: the connection is fine, retrying changes nothing.
            throw new EmailDeliveryException($"The mail server refused the recipient ({(int)ex.StatusCode}).", permanent: true, ex);
        }
        catch (EmailDeliveryException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await DropClientAsync();
            throw new EmailDeliveryException(DescribeFailure(ex), permanent: false, ex);
        }
    }

    private async Task<SmtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        await DropClientAsync();
        var client = new SmtpClient { Timeout = (int)_timeout.TotalMilliseconds };
        try
        {
            var socketOptions = _settings.Security switch
            {
                SmtpSecurity.None => SecureSocketOptions.None,
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.StartTls
            };
            await client.ConnectAsync(_settings.Host, _settings.Port, socketOptions, cancellationToken);
            if (!string.IsNullOrEmpty(_settings.Username))
            {
                await client.AuthenticateAsync(_settings.Username, _settings.Password ?? string.Empty, cancellationToken);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        return client;
    }

    private async Task DropClientAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.DisconnectAsync(quit: true, cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing the SMTP connection failed");
        }
        finally
        {
            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>Cause and next step, without credentials (MailKit messages never contain the password).</summary>
    private string DescribeFailure(Exception ex) => ex switch
    {
        AuthenticationException => $"The mail server {_settings.Host} rejected the user name or password. Check the SMTP settings.",
        SslHandshakeException => $"The TLS connection to {_settings.Host}:{_settings.Port} failed. Check the security mode and the server certificate.",
        OperationCanceledException => $"The mail server {_settings.Host}:{_settings.Port} did not respond in time.",
        System.Net.Sockets.SocketException => $"Could not connect to the mail server {_settings.Host}:{_settings.Port}. Check the host and port.",
        _ => $"Sending through {_settings.Host}:{_settings.Port} failed: {ex.Message}"
    };

    public async ValueTask DisposeAsync() => await DropClientAsync();
}

/// <summary>Development: writes each email as an .eml file instead of sending it.</summary>
internal sealed class PickupDirectoryEmailSession : IEmailSession
{
    private readonly string _directory;
    private readonly string _fromAddress;

    public PickupDirectoryEmailSession(string directory, string fromAddress)
    {
        _directory = directory;
        _fromAddress = fromAddress;
    }

    public async Task SendAsync(OutboxEmail email, CancellationToken cancellationToken)
    {
        var message = MimeMessages.Build(email, EmailTemplates.SenderName, _fromAddress);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss}-{email.Id:N}.eml");
            await using var stream = File.Create(path);
            await message.WriteToAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EmailDeliveryException($"Could not write to the pickup directory {_directory}: {ex.Message}", permanent: false, ex);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
