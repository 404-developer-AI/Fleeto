using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace Fleeto.Web.Security;

/// <summary>
/// Refuses a blocked address before anything else runs (0.7.0, security monitor): right after the forwarded headers, so the
/// address is the caller's and not the proxy's. A refusal is a plain 403 that names the address, so the person can tell an
/// admin which one to allow, and counts as a signal. An address on the allowlist is never refused.
/// </summary>
public sealed class IpBlockMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IpBlockList _list;
    private readonly SecuritySignalRecorder _signals;

    public IpBlockMiddleware(RequestDelegate next, IpBlockList list, SecuritySignalRecorder signals)
    {
        _next = next;
        _list = list;
        _signals = signals;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (_list.Check(address) is not (IpVerdict.BlockedManual or IpVerdict.BlockedAutomatic or IpVerdict.BlockedFeed))
        {
            return _next(context);
        }

        _signals.Record(SecuritySignalKind.Blocked, address);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            $"Access from {context.RemoteIp()} to this Fleeto instance is blocked. If that is a mistake, ask an administrator of the instance " +
            "to allow the address in Settings, Security.", context.RequestAborted);
    }
}

/// <summary>
/// Recognises vulnerability scanners (0.7.0, security monitor), after routing so it knows whether a route exists. A probe for a
/// path Fleeto never serves, an exploit attempt or a scanning tool gets a bare 404 without the not-found page and counts as a
/// signal; any other 404 for an anonymous caller on a path without a route counts too, to recognise directory enumeration.
/// The verdict is <see cref="ScannerPatterns"/>; a real route is never judged by its path.
/// </summary>
public sealed class ScannerDetectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecuritySignalRecorder _signals;

    public ScannerDetectionMiddleware(RequestDelegate next, SecuritySignalRecorder signals)
    {
        _next = next;
        _signals = signals;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The not-found page re-executes the pipeline for the original request, which was counted already.
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is not null)
        {
            await _next(context);
            return;
        }

        var routeExists = context.GetEndpoint() is not null;
        var (path, query) = RawTarget(context);
        var match = ScannerPatterns.Classify(path, query, context.Request.Headers.UserAgent.ToString(), routeExists);
        if (match.Verdict != ScannerVerdict.None)
        {
            _signals.Record(Kind(match), context.Connection.RemoteIpAddress);
            if (context.Features.Get<IStatusCodePagesFeature>() is { } pages)
            {
                pages.Enabled = false;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
        if (!routeExists && context.Response.StatusCode == StatusCodes.Status404NotFound && context.User.Identity?.IsAuthenticated != true)
        {
            _signals.Record(SecuritySignalKind.NotFound, context.Connection.RemoteIpAddress);
        }
    }

    /// <summary>The signal of a scanner verdict; a certain scanner path (<c>/.env</c>, <c>/.git/</c>) is a sensitive path.</summary>
    public static SecuritySignalKind Kind(ScannerMatch match) => match.Verdict switch
    {
        ScannerVerdict.ScannerAgent => SecuritySignalKind.ScannerAgent,
        ScannerVerdict.AttackPattern => SecuritySignalKind.AttackPattern,
        _ when match.HighConfidence => SecuritySignalKind.SensitivePath,
        _ => SecuritySignalKind.ScannerPath
    };

    /// <summary>The path and query as the client sent them, still encoded, so double encoding stays visible.</summary>
    private static (string Path, string Query) RawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            return (context.Request.Path.Value ?? string.Empty, context.Request.QueryString.Value ?? string.Empty);
        }

        var question = raw.IndexOf('?');
        return question < 0 ? (raw, string.Empty) : (raw[..question], raw[(question + 1)..]);
    }
}
