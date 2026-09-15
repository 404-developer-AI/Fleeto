using System.Globalization;

namespace Fleeto.Web.Services;

/// <summary>
/// Formats UTC timestamps in the user's own time zone (branding §8): relative under 24 hours ("2 min ago"), absolute
/// after that. The zone comes from the browser once the circuit is connected; until then the server zone is used.
/// </summary>
public sealed class TimeDisplay
{
    private readonly TimeProvider _time;

    public TimeDisplay(TimeProvider time)
    {
        _time = time;
    }

    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;

    /// <summary>Raised when the browser time zone becomes known, so visible timestamps can re-render.</summary>
    public event Action? ZoneChanged;

    public void SetZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId) || ianaId.Length > 100)
        {
            return;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            if (zone.Id != Zone.Id)
            {
                Zone = zone;
                ZoneChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Unknown zone: keep the server zone.
        }
    }

    public string Absolute(DateTime? utc)
    {
        if (utc is null)
        {
            return "Never";
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc.Value), Zone);
        return local.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A point in time close by: "16:00" today, otherwise "3 Oct 16:00" ("3 Oct 2027 16:00" in another year).</summary>
    public string Short(DateTime? utc)
    {
        if (utc is null)
        {
            return "-";
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc.Value), Zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(_time.GetUtcNow().UtcDateTime, Zone).Date;
        var format = local.Date == today ? "HH:mm" : local.Year == today.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "in 20 min" within 24 hours; absolute otherwise.</summary>
    public string Relative(DateTime? utc, string never = "Never")
    {
        if (utc is null)
        {
            return never;
        }

        var difference = _time.GetUtcNow().UtcDateTime - AsUtc(utc.Value);
        var future = difference < TimeSpan.Zero;
        var span = future ? -difference : difference;
        if (span >= TimeSpan.FromHours(24))
        {
            return Absolute(utc);
        }

        string amount;
        if (span < TimeSpan.FromMinutes(1))
        {
            return future ? "in less than a minute" : "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            amount = $"{(int)span.TotalMinutes} min";
        }
        else
        {
            amount = $"{(int)span.TotalHours} h";
        }

        return future ? $"in {amount}" : $"{amount} ago";
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime()
    };
}
