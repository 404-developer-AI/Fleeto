using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Fleetify.Web.Api;

/// <summary>
/// Errors of the public API as <c>application/problem+json</c>: <c>title</c> states the cause, <c>detail</c> the next step, and the
/// <c>code</c> extension is a stable machine-readable value (MD-Files/API.md, Errors).
/// </summary>
public static class ApiProblems
{
    public const string Unauthorized = "unauthorized";
    public const string InvalidParameter = "invalid_parameter";
    public const string NotFound = "not_found";
    public const string EndpointNotManaged = "endpoint_not_managed";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";

    public static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(Create(status, code, title, detail));

    public static ProblemDetails Create(int status, string code, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return problem;
    }

    public static IResult InvalidParameterResult(string parameter, string title, string detail) =>
        Problem(StatusCodes.Status400BadRequest, InvalidParameter, title, detail + $" Parameter: {parameter}.");

    public static IResult NotFoundResult(string what) =>
        Problem(StatusCodes.Status404NotFound, NotFound, $"The {what} does not exist, or this API key cannot read it.",
            "Check the id. A key limited to clients only sees the data of those clients.");

    public static IResult NotManagedResult(string feature) =>
        Problem(StatusCodes.Status409Conflict, EndpointNotManaged, $"{feature} are only available on managed endpoints.",
            "Switch the endpoint to managed in Fleeto, or load a new license when the license has expired.");

    /// <summary>Writes a problem outside an endpoint (rate limiter, unmatched routes).</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string title, string detail)
    {
        context.Response.StatusCode = status;
        return Results.Problem(Create(status, code, title, detail)).ExecuteAsync(context);
    }
}

/// <summary>
/// Opaque keyset cursors: base64url of <c>v1|&lt;sort value&gt;|&lt;id&gt;</c>. Clients treat them as opaque strings; the format may change
/// between releases, so a cursor is only valid for the list and filters it came from.
/// </summary>
internal static class ApiCursor
{
    private const int MaxLength = 800;

    public static string Encode(string sortValue, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"v1|{sortValue}|{id:N}"));

    public static string Encode(DateTime time, Guid id) => Encode(time.Ticks.ToString(CultureInfo.InvariantCulture), id);

    public static bool TryDecode(string? cursor, out string sortValue, out Guid id)
    {
        sortValue = string.Empty;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength || !Base64Url.IsValid(cursor))
        {
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // The sort value may itself contain '|' (a hostname cannot, a site name can): the version is first and the id last.
        var first = text.IndexOf('|', StringComparison.Ordinal);
        var last = text.LastIndexOf('|');
        if (first != 2 || !text.StartsWith("v1|", StringComparison.Ordinal) || last <= first ||
            !Guid.TryParseExact(text[(last + 1)..], "N", out id))
        {
            return false;
        }

        sortValue = text[(first + 1)..last];
        return true;
    }

    /// <summary>Decodes a cursor the caller sent, or throws <see cref="InvalidCursorException"/>: a bad cursor must never restart at page one.</summary>
    public static bool Require(string cursor, out string sortValue, out Guid id) =>
        TryDecode(cursor, out sortValue, out id) ? true : throw new InvalidCursorException();

    /// <inheritdoc cref="Require(string, out string, out Guid)"/>
    public static bool Require(string cursor, out DateTime time, out Guid id) =>
        TryDecode(cursor, out time, out id) ? true : throw new InvalidCursorException();

    public static bool TryDecode(string? cursor, out DateTime time, out Guid id)
    {
        time = default;
        if (!TryDecode(cursor, out string value, out id) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        time = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}

/// <summary>The <c>cursor</c> parameter is not a cursor this API returned. Answered with 400 invalid_parameter.</summary>
public sealed class InvalidCursorException : Exception
{
    public InvalidCursorException()
        : base("The cursor is not valid.")
    {
    }
}
