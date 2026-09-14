using Fleetify.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Fleetify.Web.Tests;

/// <summary>
/// Every response carries the security headers; the Content Security Policy allows scripts only from this origin or with a
/// fresh per-response nonce, never 'unsafe-inline'.
/// </summary>
public class SecurityHeadersTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseFleetifySecurityHeaders();
        app.MapGet("/", (HttpContext context) => Results.Content(
            $"<html><body>{context.Items[SecurityHeadersMiddleware.NonceItemKey]}</body></html>", "text/html"));
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Responses_carry_security_headers_with_a_nonce_and_no_inline_script()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var first = await client.GetAsync("/");
        var second = await client.GetAsync("/");

        var csp = first.Headers.GetValues("Content-Security-Policy").Single();
        var scriptSrc = csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        Assert.Contains("'self'", scriptSrc);
        Assert.Matches("'nonce-[A-Za-z0-9+/=]{16,}'", scriptSrc);
        Assert.DoesNotContain("unsafe-inline", scriptSrc);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.DoesNotContain("fonts.googleapis.com", csp);

        // The nonce in the header is the one handed to the page, and a new one is generated per response.
        var nonce = System.Text.RegularExpressions.Regex.Match(scriptSrc, "'nonce-([^']+)'").Groups[1].Value;
        Assert.Contains(nonce, await first.Content.ReadAsStringAsync());
        Assert.NotEqual(csp, second.Headers.GetValues("Content-Security-Policy").Single());

        Assert.Equal("nosniff", first.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", first.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", first.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", first.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("/clients/1", "/clients/1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/account/login", "/")]
    [InlineData(null, "/")]
    public void Return_urls_are_local_paths_only(string? input, string expected) =>
        Assert.Equal(expected, EndpointExtensions.SafeReturnUrl(input));
}
