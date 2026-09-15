using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Fleeto.Infrastructure.Security;
using Fleeto.Workers.Options;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Webhooks;

/// <summary>A webhook delivery failure. Permanent failures are not retried and do not count towards the circuit breaker.</summary>
public sealed class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message, bool permanent, Exception? inner = null)
        : base(message, inner)
    {
        IsPermanent = permanent;
    }

    public bool IsPermanent { get; }
}

/// <summary>One HTTPS POST of a webhook body.</summary>
public interface IWebhookSender
{
    /// <summary>Returns when the receiver answered with a 2xx status; throws <see cref="WebhookDeliveryException"/> otherwise.</summary>
    Task SendAsync(Uri url, string body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);
}

/// <summary>
/// Posts webhooks over HTTPS with certificate validation. Refuses to connect to any address that is not public
/// (<see cref="NetworkAddressPolicy"/>), checked on the addresses the host name resolves to at connect time; follows no
/// redirects, uses no proxy and no cookies, and never reads more than a small part of the response.
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _client;

    public HttpWebhookSender(IOptions<WebhookOptions> options)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 60));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectToPublicAddressAsync
        };
        _client = new HttpClient(handler) { Timeout = timeout };
    }

    public async Task SendAsync(Uri url, string body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new WebhookDeliveryException("The webhook URL is not an https address. Enter it again in Settings, Notification channels.", permanent: true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fleeto-Webhook", "1"));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebhookDeliveryException refused)
        {
            throw refused;
        }
        catch (HttpRequestException ex)
        {
            throw new WebhookDeliveryException(Describe(url, ex), permanent: false, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebhookDeliveryException($"{url.Host} did not answer in time.", permanent: false, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return;
            }

            // 408, 425, 429 and server errors can pass; any other answer means the request itself is refused.
            var transient = status is 408 or 425 or 429 or >= 500;
            var next = status switch
            {
                >= 300 and < 400 => "The URL redirects; enter the final URL.",
                401 or 403 => "Check that the webhook URL is still valid and allowed to post.",
                404 or 410 => "The webhook no longer exists at the receiver; create a new one and update the URL.",
                _ => transient ? "Fleeto tries again." : "Check the webhook URL and the format of the channel."
            };
            throw new WebhookDeliveryException($"{url.Host} answered {status}. {next}", permanent: !transient);
        }
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new WebhookDeliveryException($"The host name {host} could not be resolved.", permanent: false, ex);
        }

        var allowed = addresses.Where(NetworkAddressPolicy.IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new WebhookDeliveryException(
                $"{host} resolves to a private or local address. Fleeto only sends webhooks to public addresses.", permanent: true);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Describe(Uri url, HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.SecureConnectionError => $"The TLS connection to {url.Host} failed. Check that the receiver has a valid certificate.",
        HttpRequestError.NameResolutionError => $"The host name {url.Host} could not be resolved.",
        HttpRequestError.ConnectionError => $"Could not connect to {url.Host}.",
        _ => $"Posting to {url.Host} failed: {ex.Message}"
    };

    public void Dispose() => _client.Dispose();
}
