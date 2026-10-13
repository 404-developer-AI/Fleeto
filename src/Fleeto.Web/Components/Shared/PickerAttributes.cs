namespace Fleeto.Web.Components.Shared;

/// <summary>The HTML attributes of the date and time fields (0.6.0): their range and step, in the format of the input.</summary>
internal static class PickerAttributes
{
    public static Dictionary<string, object?> Range(string? min, string? max, int? step = null)
    {
        var attributes = new Dictionary<string, object?>();
        if (min is not null)
        {
            attributes["min"] = min;
        }

        if (max is not null)
        {
            attributes["max"] = max;
        }

        if (step is not null)
        {
            attributes["step"] = step;
        }

        return attributes;
    }
}
