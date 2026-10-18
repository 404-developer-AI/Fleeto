namespace Fleeto.Core.Performance;

/// <summary>
/// Rules of the keys the performance monitor keeps (PERFORMANCE.md §4, spec §2). A key is a template, a type name or a fixed
/// name, never a value: no endpoint id, client code, hostname, user, IP, path or payload. Keys come from code, so these rules
/// are the bound, not the filter: a key is cut to its maximum length, and once a kind holds <see cref="MaxKeysPerKind"/> keys in
/// one minute every new one is counted under <see cref="Other"/>.
/// </summary>
public static class PerfKeys
{
    public const string Other = "__other__";

    public const int MaxKeyLength = 200;
    public const int MaxSubKeyLength = 100;

    /// <summary>Distinct key and sub key pairs per kind and per minute in one process.</summary>
    public const int MaxKeysPerKind = 300;

    /// <summary>The source of database work that happened outside any measured scope.</summary>
    public const string OtherSource = "other";

    /// <summary>Work of the monitor itself, so its own queries never count against a page or a loop.</summary>
    public const string MonitorSource = "perf-monitor";

    /// <summary>The page of the performance monitor, attributed to itself (spec §5).</summary>
    public const string DashboardSource = "perf-dashboard";

    public static string InteractionSource(string route) => "interaction:" + route;
    public static string ApiSource(string route) => "api:" + route;
    public static string WorkerSource(string loop) => "worker:" + loop;
    public static string GatewaySource(string messageType) => "gateway:" + messageType;
    public static string SignerSource(string kind) => "signer:" + kind;

    public static string Key(string? key) => Cut(string.IsNullOrWhiteSpace(key) ? OtherSource : key, MaxKeyLength);

    public static string SubKey(string? subKey) => string.IsNullOrEmpty(subKey) ? string.Empty : Cut(subKey, MaxSubKeyLength);

    /// <summary>The class of an HTTP status: <c>2xx</c>, <c>4xx</c>. A 429 keeps its own sub key: rate limiting is a finding.</summary>
    public static string StatusClass(int status) => status switch
    {
        429 => "429",
        >= 100 and < 600 => $"{status / 100}xx",
        _ => "error"
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
