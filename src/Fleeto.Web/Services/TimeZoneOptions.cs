namespace Fleeto.Web.Services;

/// <summary>A time zone to choose, by IANA id with its current offset.</summary>
public sealed record TimeZoneOption(string Id, string Label);

/// <summary>IANA time zones for pickers. On Windows the system zones are converted from their Windows ids.</summary>
public static class TimeZoneOptions
{
    private static readonly Lazy<IReadOnlyList<TimeZoneOption>> Cached = new(Build);

    public static IReadOnlyList<TimeZoneOption> All => Cached.Value;

    /// <summary>The IANA id of a zone, or UTC when it has none.</summary>
    public static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";

    private static IReadOnlyList<TimeZoneOption> Build()
    {
        var now = DateTime.UtcNow;
        var options = new Dictionary<string, TimeZoneOption>(StringComparer.Ordinal)
        {
            ["UTC"] = new("UTC", "(UTC+00:00) UTC")
        };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = IanaId(zone);
            if (!id.Contains('/') || id.StartsWith("Etc/", StringComparison.Ordinal) || options.ContainsKey(id))
            {
                continue;
            }

            var offset = zone.GetUtcOffset(now);
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            options[id] = new TimeZoneOption(id, $"(UTC{sign}{offset.Duration():hh\\:mm}) {id}");
        }

        return options.Values.OrderBy(o => o.Id == "UTC" ? 0 : 1).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
    }
}
