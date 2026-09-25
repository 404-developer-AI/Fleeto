using System.Text.Json;

namespace Fleeto.Infrastructure.Integrations;

/// <summary>An automation in the patch management product that Fleeto does not manage, as the workers last read it (0.6.0).</summary>
public sealed record OtherAutomation(string Name, string Schedule)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<OtherAutomation> automations) => JsonSerializer.Serialize(automations, Json);

    public static IReadOnlyList<OtherAutomation> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<OtherAutomation>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
