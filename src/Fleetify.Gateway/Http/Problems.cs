using Microsoft.AspNetCore.Mvc;

namespace Fleetify.Gateway.Http;

/// <summary>problem+json responses with a cause and a next step (branding §8). Never reveal which security check failed.</summary>
internal static class Problems
{
    public static Task WriteAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = statusCode, Title = title, Detail = detail },
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
