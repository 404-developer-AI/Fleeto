using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Fleeto.Core.Performance;

namespace Fleeto.Workers.Performance;

/// <summary>
/// The edge check of the performance monitor (0.7.0 step 3, spec §5, Edge / proxy): the instance URL as a browser reaches it, from
/// the VPS through the host proxy. DNS, TCP and TLS with their times, the TLS version, HTTP/2 and the certificate; then the first
/// page and one of its stylesheets with their HTTP version, compression, caching and security headers. Run on an admin's request
/// only; it reads public responses and sends nothing but ordinary requests without cookies.
/// </summary>
public sealed partial class PerfEdgeCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>30 days, the HSTS age web sends (the default of ASP.NET Core); shorter leaves a first visit over http open longer.</summary>
    public const long MinHstsSeconds = 2_592_000;

    private readonly HttpMessageHandler? _handler;
    private readonly Func<string, CancellationToken, Task<List<PerfProbeItem>>>? _connection;

    public PerfEdgeCheck()
    {
    }

    /// <summary>For tests: the HTTP requests go to <paramref name="handler"/>, the connection part to <paramref name="connection"/>.</summary>
    public PerfEdgeCheck(HttpMessageHandler handler, Func<string, CancellationToken, Task<List<PerfProbeItem>>> connection)
    {
        _handler = handler;
        _connection = connection;
    }

    public async Task<IReadOnlyList<PerfProbeItem>> RunAsync(string fqdn, CancellationToken cancellationToken)
    {
        var items = await (_connection ?? ConnectionAsync)(fqdn, cancellationToken);

        using var http = new HttpClient(_handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = Timeout
        }, disposeHandler: true)
        {
            Timeout = Timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        // The first page: the root redirects to the sign-in page, which is what a browser loads first.
        var url = new Uri($"https://{fqdn}/");
        HttpResponseMessage? page = null;
        double firstByteMs = 0;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            page?.Dispose();
            var started = Stopwatch.GetTimestamp();
            page = await SendAsync(http, url, cancellationToken);
            firstByteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if ((int)page.StatusCode is >= 300 and < 400 && page.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!string.Equals(next.Host, fqdn, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                url = next;
                continue;
            }

            break;
        }

        using (page)
        {
            var html = page!.IsSuccessStatusCode ? await page.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            items.Add(new PerfProbeItem("First page", $"{(int)page.StatusCode} in {PerformanceRules.Ms(firstByteMs)} to the first byte", page.IsSuccessStatusCode ? null : false,
                page.IsSuccessStatusCode ? null : "The sign-in page did not answer 200. Check that web runs and that the host Caddy routes the FQDN to it."));
            items.AddRange(PageItems(page.Version, page.Headers, page.Content.Headers));

            if (StylesheetOf(html) is { } stylesheet && new Uri(url, stylesheet) is var asset && string.Equals(asset.Host, fqdn, StringComparison.OrdinalIgnoreCase))
            {
                using var response = await SendAsync(http, asset, cancellationToken);
                items.AddRange(AssetItems((int)response.StatusCode, response.Headers, response.Content.Headers));
            }
            else
            {
                items.Add(new PerfProbeItem("Static files", "No stylesheet found on the first page", null));
            }
        }

        return items;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("br, gzip");
        request.Headers.UserAgent.ParseAdd("Fleeto-EdgeCheck/1.0");
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    /// <summary>DNS, TCP and TLS to port 443, timed apart, with the TLS version, the protocol it agreed and the certificate.</summary>
    private static async Task<List<PerfProbeItem>> ConnectionAsync(string fqdn, CancellationToken cancellationToken)
    {
        var items = new List<PerfProbeItem>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var started = Stopwatch.GetTimestamp();
        var addresses = await Dns.GetHostAddressesAsync(fqdn, timeout.Token);
        items.Add(new PerfProbeItem("DNS", $"{PerformanceRules.Ms(Stopwatch.GetElapsedTime(started).TotalMilliseconds)} · {addresses.Length} address{(addresses.Length == 1 ? "" : "es")}", null));
        if (addresses.Length == 0)
        {
            return items;
        }

        using var tcp = new TcpClient(addresses[0].AddressFamily);
        started = Stopwatch.GetTimestamp();
        await tcp.ConnectAsync(addresses[0], 443, timeout.Token);
        items.Add(new PerfProbeItem("TCP connect", PerformanceRules.Ms(Stopwatch.GetElapsedTime(started).TotalMilliseconds), null));

        X509Certificate2? certificate = null;
        SslPolicyErrors errors = SslPolicyErrors.None;
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, policyErrors) =>
        {
            errors = policyErrors;
            certificate = cert is null ? null : new X509Certificate2(cert);
            return true;
        });
        started = Stopwatch.GetTimestamp();
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = fqdn,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
        }, timeout.Token);
        items.Add(new PerfProbeItem("TLS handshake", PerformanceRules.Ms(Stopwatch.GetElapsedTime(started).TotalMilliseconds), null));
        items.AddRange(TlsItems(tls.SslProtocol, tls.NegotiatedApplicationProtocol.ToString(), errors, certificate?.NotAfter.ToUniversalTime(), DateTime.UtcNow));
        certificate?.Dispose();
        return items;
    }

    /// <summary>Judges the TLS of the connection: its version, whether HTTP/2 was agreed, and the certificate.</summary>
    public static IEnumerable<PerfProbeItem> TlsItems(SslProtocols protocol, string alpn, SslPolicyErrors errors, DateTime? notAfter, DateTime utcNow)
    {
#pragma warning disable SYSLIB0039 // Old TLS versions are named only to report them.
        var version = protocol switch
        {
            SslProtocols.Tls13 => "TLS 1.3",
            SslProtocols.Tls12 => "TLS 1.2",
            SslProtocols.Tls11 => "TLS 1.1",
            SslProtocols.Tls => "TLS 1.0",
            _ => protocol.ToString()
        };
#pragma warning restore SYSLIB0039
        yield return new PerfProbeItem("TLS version", version, protocol is SslProtocols.Tls13 or SslProtocols.Tls12,
            protocol is SslProtocols.Tls13 or SslProtocols.Tls12 ? null : "Only TLS 1.2 and 1.3 are safe. The host Caddy offers them by default: check its configuration.");
        var http2 = alpn == "h2";
        yield return new PerfProbeItem("HTTP/2 offered", http2 ? "Yes (h2)" : $"No ({(alpn.Length == 0 ? "nothing agreed" : alpn)})", http2,
            http2 ? null : "Without HTTP/2 a browser opens several connections and queues requests. The host Caddy offers it by default.");
        if (errors != SslPolicyErrors.None)
        {
            yield return new PerfProbeItem("Certificate", $"Not trusted ({errors})", false,
                "Browsers warn on this certificate. Check that the host Caddy obtained one for the FQDN and that DNS points to this VPS.");
        }
        else if (notAfter is { } until)
        {
            var days = (until - utcNow).TotalDays;
            yield return new PerfProbeItem("Certificate", $"Valid until {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({Math.Floor(days).ToString(CultureInfo.InvariantCulture)} days)",
                days >= 14, days >= 14 ? null : "The certificate expires within two weeks and was not renewed. Check the log of the host Caddy.");
        }
    }

    /// <summary>Judges the first page: HTTP version, keep-alive, compression and the security headers.</summary>
    public static IEnumerable<PerfProbeItem> PageItems(Version version, HttpResponseHeaders headers, HttpContentHeaders content)
    {
        var http = version.Major >= 2 ? $"HTTP/{version.Major}" : $"HTTP/{version.Major}.{version.Minor}";
        yield return new PerfProbeItem("HTTP version", http, version.Major >= 2,
            version.Major >= 2 ? null : "The page came over HTTP/1.1. The host Caddy serves HTTP/2 by default: check that nothing in between downgrades it.");
        if (version.Major < 2)
        {
            var close = headers.ConnectionClose == true;
            yield return new PerfProbeItem("Keep-alive", close ? "No: the connection closes after each response" : "Yes", !close,
                close ? "Every request opens a new connection. Remove Connection: close from the proxy configuration." : null);
        }

        yield return Compression("Page compression", content);

        var hsts = headers.TryGetValues("Strict-Transport-Security", out var values) ? string.Join(", ", values) : null;
        var age = hsts is null ? 0 : HstsAge(hsts);
        yield return new PerfProbeItem("HSTS", hsts ?? "Missing", age >= MinHstsSeconds,
            age >= MinHstsSeconds ? null : "Without Strict-Transport-Security a visit over http can be intercepted before it is redirected. Web sends it with a max-age of 30 days; something in between removed or shortened it.");
        var csp = headers.Contains("Content-Security-Policy");
        yield return new PerfProbeItem("Content Security Policy", csp ? "Present" : "Missing", csp,
            csp ? null : "Web sends a Content Security Policy on every page; something in between removed it.");
    }

    /// <summary>Judges a static file: compression and how long browsers may cache it.</summary>
    public static IEnumerable<PerfProbeItem> AssetItems(int status, HttpResponseHeaders headers, HttpContentHeaders content)
    {
        if (status != 200)
        {
            yield return new PerfProbeItem("Static files", $"The stylesheet answered {status}", false, "A file the page refers to could not be loaded. Check the log of web.");
            yield break;
        }

        yield return Compression("Static file compression", content);
        var cache = headers.CacheControl;
        var maxAge = cache?.MaxAge?.TotalSeconds ?? 0;
        var immutable = cache?.Extensions.Any(e => e.Name.Equals("immutable", StringComparison.OrdinalIgnoreCase)) == true;
        var good = maxAge >= 86_400 || immutable;
        yield return new PerfProbeItem("Static file caching", cache is null ? "No Cache-Control" : cache.ToString(), good,
            good ? null : "Browsers load this file again on every page. Web marks fingerprinted files as cacheable for a year; check that the host Caddy keeps the header.");
    }

    private static PerfProbeItem Compression(string name, HttpContentHeaders content)
    {
        var encoding = content.ContentEncoding.FirstOrDefault();
        var length = content.ContentLength;
        if (encoding is null && length is < 1024)
        {
            return new PerfProbeItem(name, $"None, {PerformanceRules.Bytes(length.Value)} (too small to matter)", null);
        }

        return new PerfProbeItem(name, encoding is null ? $"None{(length is { } l ? $", {PerformanceRules.Bytes(l)}" : "")}" : encoding, encoding is not null,
            encoding is not null ? null : "Text files went out uncompressed. Web serves compressed static files when the browser asks; check that the host Caddy passes Accept-Encoding.");
    }

    public static long HstsAge(string header)
    {
        var match = MaxAge().Match(header);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    /// <summary>
    /// A stylesheet the page links to, or null: the first of Fleeto's own, which carry a fingerprint and may be cached for a year,
    /// before one of a package under <c>_content/</c>, which browsers revalidate.
    /// </summary>
    public static string? StylesheetOf(string html)
    {
        var sheets = Link().Matches(html)
            .Where(link => link.Value.Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
            .Select(link => Href().Match(link.Value))
            .Where(href => href.Success)
            .Select(href => WebUtility.HtmlDecode(href.Groups[1].Value))
            .ToList();
        return sheets.FirstOrDefault(s => !s.StartsWith("_content/", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase))
            ?? sheets.FirstOrDefault();
    }

    [GeneratedRegex(@"max-age\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxAge();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"\bhref\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
}
