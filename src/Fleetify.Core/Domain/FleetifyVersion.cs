using System.Reflection;

namespace Fleetify.Core.Domain;

/// <summary>The server version. Server and agent share one version number.</summary>
public static class FleetifyVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(FleetifyVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Strip the "+commit" suffix the SDK appends.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
