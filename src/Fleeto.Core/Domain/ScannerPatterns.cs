namespace Fleeto.Core.Domain;

/// <summary>What a request looks like to the scanner detection of web and gateway (0.7.0, security monitor).</summary>
public enum ScannerVerdict
{
    None,
    /// <summary>A path Fleeto never serves but vulnerability scanners always try: <c>/.env</c>, <c>/wp-admin</c>, <c>*.php</c>.</summary>
    ScannerPath,
    /// <summary>The User-Agent of a known scanning tool.</summary>
    ScannerAgent,
    /// <summary>An exploit attempt in the path or the query: path traversal, a null byte, Log4Shell, SQL injection, script injection.</summary>
    AttackPattern
}

/// <summary>A verdict and whether it is certain enough to block the address at the first request.</summary>
public readonly record struct ScannerMatch(ScannerVerdict Verdict, bool HighConfidence)
{
    public static ScannerMatch None => default;
}

/// <summary>
/// Recognises vulnerability scanners and exploit attempts by the request alone (0.7.0, security monitor). Fleeto serves none of
/// these paths, so a request for one that matches no route is a probe by definition: web and the gateway answer it with a plain
/// 404 and count it as a security signal. Pure and allocation-light, because it runs on every request.
/// </summary>
public static class ScannerPatterns
{
    /// <summary>Longest path or query looked at; a longer one is judged by its start.</summary>
    public const int MaxInspectedLength = 2048;

    // Path prefixes, compared case-insensitively against the decoded path. Certain ones block at the first request.
    private static readonly string[] CertainPrefixes =
    [
        "/.env", "/.git/", "/.git", "/.svn/", "/.hg/", "/.aws/", "/.ssh/", "/.docker/", "/.kube/", "/.npmrc", "/.htpasswd", "/.htaccess",
        "/.ds_store", "/.vscode/", "/.idea/", "/wp-config", "/etc/passwd", "/proc/self/", "/vendor/phpunit", "/phpunit",
    ];

    private static readonly string[] ProbePrefixes =
    [
        "/wp-admin", "/wp-login", "/wp-content/", "/wp-includes/", "/wp-json/", "/xmlrpc.php", "/wordpress/", "/phpmyadmin", "/pma/",
        "/myadmin", "/mysql/", "/adminer", "/cgi-bin/", "/actuator", "/server-status", "/server-info", "/solr/", "/owa/", "/ecp/",
        "/autodiscover/", "/hnap1", "/boaform", "/manager/html", "/jmx-console", "/web-console", "/invoker/", "/console/", "/telescope/",
        "/_ignition/", "/druid/", "/geoserver/", "/jenkins/", "/remote/login", "/global-protect/", "/dana-na/", "/vpn/", "/sslvpn",
        "/config.json", "/config.yml", "/config.yaml", "/configuration.php", "/settings.php", "/sftp-config.json", "/credentials",
        "/backup", "/db.sql", "/dump.sql", "/database.sql", "/shell", "/cmd", "/eval-stdin.php", "/info.php", "/phpinfo",
        "/admin.php", "/login.php", "/index.php", "/setup.cgi", "/goform/", "/api/jsonws", "/struts/", "/axis2/", "/nifi/",
    ];

    // File extensions Fleeto never serves.
    private static readonly string[] ProbeExtensions =
    [
        ".php", ".php5", ".php7", ".phtml", ".asp", ".aspx", ".ashx", ".asmx", ".jsp", ".jspx", ".do", ".action", ".cgi", ".pl", ".cfm",
        ".env", ".bak", ".old", ".orig", ".swp", ".sql", ".sqlite", ".db", ".tar.gz", ".tgz", ".rar", ".7z", ".war", ".ini", ".log", ".yml",
        ".yaml", ".toml",
    ];

    // Found anywhere in the decoded path or query, also of a real route: nothing a person or an integration sends contains them.
    private static readonly string[] AttackFragments =
    [
        "../", "..\\", "\0", "${jndi:", "${env:", "${${", "<script", "<?php", "union select", "union all select", "/etc/passwd",
        "/etc/shadow", "' or '1'='1", "\" or \"1\"=\"1", "or 1=1--",
    ];

    // Found in the path or query of a request that matches no route. A search on a real route may contain them (a process
    // called cmd.exe), so they count only where Fleeto has nothing to serve.
    private static readonly string[] ProbeFragments =
    [
        "javascript:", "win.ini", "boot.ini", "/bin/sh", "/bin/bash", "cmd.exe", "powershell.exe", "wget http", "curl http",
        "base64_decode(", "sleep(", "benchmark(", "waitfor delay",
    ];

    // Raw (undecoded) markers of double encoding used to slip traversal past a decoder.
    private static readonly string[] EncodedAttackFragments = ["%2e%2e", "%252e", "%c0%ae", "%c0%af", "%00", "..%2f", "..%5c", "%2e%2e%2f"];

    // User-Agent fragments of scanning tools. A person never browses with these.
    private static readonly string[] ScannerAgents =
    [
        "sqlmap", "nikto", "nuclei", "zgrab", "masscan", "nmap", "wpscan", "gobuster", "dirbuster", "dirb/", "ffuf", "feroxbuster",
        "acunetix", "nessus", "openvas", "qualys", "wfuzz", "whatweb", "joomscan", "commix", "xsstrike", "arachni", "skipfish",
        "w3af", "burpcollaborator", "interactsh", "jaeles", "httpx - open-source", "zmeu", "morfeus", "censysinspect", "fuzz faster",
    ];

    /// <summary>
    /// Classifies one request. <paramref name="path"/> is the request path as received (still encoded), <paramref name="query"/>
    /// the query string with or without its leading <c>?</c>. <paramref name="routeExists"/> is whether routing found an endpoint
    /// for the request: a real route is judged only by exploit fragments and the User-Agent, never by its path, so no route of
    /// Fleeto can ever look like a probe.
    /// </summary>
    public static ScannerMatch Classify(string? path, string? query, string? userAgent, bool routeExists)
    {
        var rawPath = Limit(path);
        var rawQuery = Limit(query);

        if (ContainsAny(rawPath, EncodedAttackFragments) || ContainsAny(rawQuery, EncodedAttackFragments))
        {
            return new ScannerMatch(ScannerVerdict.AttackPattern, HighConfidence: true);
        }

        var decodedPath = Decode(rawPath);
        var decodedQuery = Decode(rawQuery);
        if (ContainsAny(decodedPath, AttackFragments) || ContainsAny(decodedQuery, AttackFragments))
        {
            return new ScannerMatch(ScannerVerdict.AttackPattern, HighConfidence: true);
        }

        if (!routeExists)
        {
            if (ContainsAny(decodedPath, ProbeFragments) || ContainsAny(decodedQuery, ProbeFragments))
            {
                return new ScannerMatch(ScannerVerdict.AttackPattern, HighConfidence: true);
            }

            if (StartsWithAny(decodedPath, CertainPrefixes))
            {
                return new ScannerMatch(ScannerVerdict.ScannerPath, HighConfidence: true);
            }

            if (StartsWithAny(decodedPath, ProbePrefixes) || EndsWithAny(decodedPath, ProbeExtensions))
            {
                return new ScannerMatch(ScannerVerdict.ScannerPath, HighConfidence: false);
            }
        }

        if (!string.IsNullOrEmpty(userAgent) && ContainsAny(userAgent.Length > 512 ? userAgent[..512] : userAgent, ScannerAgents))
        {
            return new ScannerMatch(ScannerVerdict.ScannerAgent, HighConfidence: true);
        }

        return ScannerMatch.None;
    }

    private static string Limit(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length > MaxInspectedLength ? value[..MaxInspectedLength] : value;

    /// <summary>Decodes percent-encoding twice (double encoding) and plus signs; never throws on a malformed escape.</summary>
    private static string Decode(string value)
    {
        if (value.Length == 0 || (value.IndexOf('%') < 0 && value.IndexOf('+') < 0))
        {
            return value;
        }

        var once = Uri.UnescapeDataString(value.Replace('+', ' '));
        return once.IndexOf('%') < 0 ? once : Uri.UnescapeDataString(once);
    }

    private static bool ContainsAny(string value, string[] fragments)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var fragment in fragments)
        {
            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAny(string path, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndsWithAny(string path, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
