using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleetify.Web.Services;

/// <summary>Small helpers shared by the web services.</summary>
internal static class ServiceSupport
{
    /// <summary>Shown when an unexpected exception occurs; the exception itself is logged, never shown.</summary>
    public const string GenericProblem = "Something went wrong. Try again, and check the web log if it keeps happening.";

    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Validates a single email address with sane length limits.</summary>
    public static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320)
        {
            return false;
        }

        return MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() && value.Contains('.');
    }

    /// <summary>Escapes LIKE wildcards in user input; the value is still passed as a parameter.</summary>
    public static string LikePattern(string input) =>
        "%" + input.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
