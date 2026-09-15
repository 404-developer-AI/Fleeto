using System.Text.Json;

namespace Fleetify.Web.Services;

/// <summary>Reads the services of an inventory. Agent data is untrusted: unreadable entries are skipped, never shown raw.</summary>
internal static class ServiceOptions
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class Item
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? StartType { get; init; }
        public string? State { get; init; }
    }

    public static IReadOnlyList<ServiceOption> Parse(string? json, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<Item?>>(json, Json) ?? [])
                .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
                .Select(i => new ServiceOption(i!.Name!, i.DisplayName ?? string.Empty, i.StartType ?? string.Empty, i.State ?? string.Empty))
                .OrderBy(s => string.IsNullOrEmpty(s.DisplayName) ? s.Name : s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The services in an inventory could not be read");
            return [];
        }
    }
}
