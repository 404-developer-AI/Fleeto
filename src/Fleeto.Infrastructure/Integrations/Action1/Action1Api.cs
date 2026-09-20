using System.Globalization;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Integrations.Action1;

/// <summary>
/// The shapes of the Action1 REST API that Fleeto depends on (0.4.0). Action1 publishes no downloadable OpenAPI document,
/// so what Fleeto relies on is written down here and covered by tests against a stand-in server.
/// </summary>
public static class Action1Api
{
    /// <summary>The API version in the path of every call.</summary>
    public const string Version = "3.0";

    /// <summary>Records a page of 50, the default Action1 uses in the URLs it returns.</summary>
    public const int PageSize = 50;

    /// <summary>Pages one call may walk, so a wrong answer can never keep Fleeto reading forever.</summary>
    public const int MaxPages = 40;

    /// <summary>
    /// The base address of a region. An Action1 account is registered in one region and answers only there; Europe keeps
    /// patch data in the EU.
    /// </summary>
    public static Uri BaseAddress(Action1Region region) => new(region switch
    {
        Action1Region.Europe => "https://app.eu.action1.com/api/3.0/",
        Action1Region.NorthAmerica => "https://app.action1.com/api/3.0/",
        Action1Region.NorthAmerica2 => "https://app.na-2.action1.com/api/3.0/",
        Action1Region.UnitedKingdom => "https://app.uk.action1.com/api/3.0/",
        Action1Region.Australia => "https://app.au.action1.com/api/3.0/",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown Action1 region.")
    });

    /// <summary>How a region is named in the UI.</summary>
    public static string RegionName(Action1Region region) => region switch
    {
        Action1Region.Europe => "Europe",
        Action1Region.NorthAmerica => "North America",
        Action1Region.NorthAmerica2 => "North America 2",
        Action1Region.UnitedKingdom => "United Kingdom",
        Action1Region.Australia => "Australia",
        _ => region.ToString()
    };

    /// <summary>
    /// Action1 stamps times as <c>2026-09-20_14-11-14</c> and documents no timezone; Fleeto reads them as UTC, which is
    /// what the account's own console shows for an endpoint that reported at a known moment. A value that does not parse
    /// gives null rather than a wrong time, so nothing is shown instead of something untrue.
    /// </summary>
    public static DateTime? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
