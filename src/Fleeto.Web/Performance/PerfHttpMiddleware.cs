using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Web.Performance;

/// <summary>
/// Measures the HTTP requests of web outside the circuit (0.7.0, PERFORMANCE.md §4.1): the public API, sign-in and setup, the
/// sign-in pages, the Entra ID callback (under <c>/api</c>) and the agent download. Placed right after routing, before rate limiting and
/// authentication, so refused requests are measured too. Keyed by route template, method and status class; the response size is
/// counted on the way out. Not measured: static files, the circuit (<c>/_blazor</c>, measured per interaction), health checks and
/// pages, which are measured as interactions. Timing never goes back to the browser.
/// </summary>
public sealed class PerfHttpMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PerfRecorder _recorder;

    public PerfHttpMiddleware(RequestDelegate next, PerfRecorder recorder)
    {
        _next = next;
        _recorder = recorder;
    }

    public static bool IsMeasured(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/agent/download", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_recorder.Collects(PerfCollector.Http) || !IsMeasured(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var route = context.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { Length: > 0 } raw
            ? "/" + raw.TrimStart('/')
            : RouteTemplates.Unmatched;
        var scope = PerfContext.Begin(PerfKeys.ApiSource(route));
        var body = context.Response.Body;
        var counting = new CountingStream(body);
        context.Response.Body = counting;
        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            context.Response.Body = body;
            var status = failed && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
            _recorder.Complete(PerfCollector.Http, PerfKind.Http, route, $"{context.Request.Method} {PerfKeys.StatusClass(status)}", scope,
                failed || status >= 500, counting.Written);
        }
    }

    /// <summary>Passes everything through and counts the bytes written.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            Written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Written += buffer.Length;
        }
    }
}
