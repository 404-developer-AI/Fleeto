using System.Reflection;

namespace Fleeto.Core.Domain;

/// <summary>The server version. Server and agent share one version number.</summary>
public static class FleetoVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(FleetoVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Strip the "+commit" suffix the SDK appends.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
