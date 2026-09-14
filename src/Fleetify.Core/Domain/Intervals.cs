namespace Fleetify.Core.Domain;

/// <summary>Plain-language intervals per branding §6: "every 30 seconds", "every 5 minutes", "once a month".</summary>
public static class Intervals
{
    public static string Describe(int seconds)
    {
        if (seconds >= 28 * 86400)
        {
            return "once a month";
        }

        if (seconds % 604800 == 0)
        {
            return Plural(seconds / 604800, "week", "once a week");
        }

        if (seconds % 86400 == 0)
        {
            return Plural(seconds / 86400, "day", "once a day");
        }

        if (seconds % 3600 == 0)
        {
            return Plural(seconds / 3600, "hour", "every hour");
        }

        if (seconds % 60 == 0)
        {
            return Plural(seconds / 60, "minute", "every minute");
        }

        return Plural(seconds, "second", "every second");
    }

    private static string Plural(int count, string unit, string single) =>
        count == 1 ? single : $"every {count} {unit}s";
}
