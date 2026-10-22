namespace Fleeto.Core.Domain;

/// <summary>How long the security monitor keeps what it records (0.7.0).</summary>
public static class SecurityMonitorRules
{
    /// <summary>Signals of web and the gateway, per minute and address.</summary>
    public static readonly TimeSpan SignalRetention = TimeSpan.FromDays(90);

    /// <summary>A block that ended by itself stays listed this long, then retention removes it.</summary>
    public static readonly TimeSpan EndedBlockRetention = TimeSpan.FromDays(30);

    /// <summary>A network a user has not signed in from for this long is forgotten (an IPv4 /24 of a person is personal data).</summary>
    public static readonly TimeSpan SignInNetworkRetention = TimeSpan.FromDays(400);
}
