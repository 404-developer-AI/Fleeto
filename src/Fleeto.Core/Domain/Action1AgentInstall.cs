using System.Globalization;

namespace Fleeto.Core.Domain;

/// <summary>
/// The job that installs the Action1 agent on a Windows endpoint (0.4.0 step 3).
///
/// Patch management matches an endpoint on the id of the Action1 agent that runs next to the Fleeto agent, so an endpoint
/// without that agent has no patch state at all. Fleeto can put it there as an ordinary signed job, which makes this the
/// one job whose script nobody writes: <b>fleeto-signer composes the body itself</b> from the installer link of the
/// client's Action1 organization and signs that. Web cannot influence what runs, and there is no script in the library to
/// edit or approve.
/// </summary>
public static class Action1AgentInstall
{
    /// <summary>The name of the job in the job list and the audit log.</summary>
    public const string JobName = "Install Action1 agent";

    /// <summary>
    /// How long the installer may take on the endpoint. Downloading and installing an MSI over a slow line is minutes,
    /// not seconds, and a job that times out leaves a half-installed product behind.
    /// </summary>
    public const int TimeoutSeconds = 20 * 60;

    /// <summary>Output of the installer is a handful of lines; anything larger is the installer having a bad day.</summary>
    public const long MaxOutputBytes = 1024 * 1024;

    /// <summary>
    /// True when a link is safe to put in a job: https, on Action1's own domain, ending in an MSI, and free of anything
    /// that could end the quoted string it is placed in. An admin pastes this link by hand, so it is never trusted.
    /// </summary>
    public static bool IsValidInstallerUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500 ||
            url.Any(c => c is '\'' or '"' or '`' or '$' or ';' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttps &&
               (parsed.Host.EndsWith(".action1.com", StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.Equals("action1.com", StringComparison.OrdinalIgnoreCase)) &&
               parsed.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PowerShell that defines how both jobs find the Action1 agent on an endpoint (0.6.0): the key where it keeps its id,
    /// which is what the Fleeto agent reads, in both registry views, or its entry in the installed programs. Installing and
    /// removing judge "installed" the same way.
    /// </summary>
    internal const string FindAgent = """
        function Find-Action1Agent {
            $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
            @(Get-ChildItem -Path $roots -ErrorAction SilentlyContinue |
                Get-ItemProperty -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like 'Action1 Agent*' -and $_.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$' })
        }
        function Test-Action1Agent {
            foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Action1', 'HKLM:\SOFTWARE\Action1')) {
                if ((Get-ItemProperty -Path $key -Name 'agent.guid' -ErrorAction SilentlyContinue).'agent.guid') { return $true }
            }
            return (Find-Action1Agent).Count -gt 0
        }
        """;

    /// <summary>
    /// The PowerShell the agent runs as SYSTEM: stop without changing anything when an Action1 agent is already installed
    /// (0.6.0), otherwise download the installer of this organization, install it silently without restarting, and remove
    /// the file again. It never restarts the endpoint and never installs anything else.
    /// </summary>
    /// <exception cref="ArgumentException">The link is not one <see cref="IsValidInstallerUrl"/> accepts.</exception>
    public static string Body(string installerUrl)
    {
        if (!IsValidInstallerUrl(installerUrl))
        {
            throw new ArgumentException("The Action1 agent installer link is not a valid Action1 download link.", nameof(installerUrl));
        }

        return FindAgent + "\n" + string.Format(CultureInfo.InvariantCulture, """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Action1Agent) {{
                Write-Output 'An Action1 agent is already installed on this endpoint, so nothing was changed. It reports itself to Fleeto with the next inventory.'
                exit 0
            }}
            $installer = Join-Path $env:ProgramData ('fleeto-action1-' + [guid]::NewGuid().ToString('N') + '.msi')
            try {{
                Invoke-WebRequest -Uri '{0}' -OutFile $installer -UseBasicParsing
                $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', "`"$installer`"", '/quiet', '/qn', '/norestart' -Wait -PassThru
                if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {{
                    throw "The Action1 installer ended with exit code $($process.ExitCode)."
                }}
                Write-Output 'The Action1 agent was installed. It reports itself to Fleeto with the next inventory.'
            }}
            finally {{
                Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
            }}
            """, installerUrl);
    }
}

/// <summary>
/// The job that uninstalls the Action1 agent from a Windows endpoint (0.6.0). Only for an agent that Action1 does not
/// report in the organization of the endpoint's client: every other one is removed through the Action1 API, so Action1
/// also drops the endpoint from its console. Like the install, <b>fleeto-signer composes the body</b>, which here takes
/// no input at all.
/// </summary>
public static class Action1AgentRemoval
{
    /// <summary>The name of the job in the job list and the audit log.</summary>
    public const string JobName = "Remove Action1 agent";

    public const int TimeoutSeconds = 10 * 60;

    public const long MaxOutputBytes = Action1AgentInstall.MaxOutputBytes;

    /// <summary>
    /// The PowerShell the agent runs as SYSTEM: uninstall every installed Action1 agent by its product code, silently and
    /// without restarting. Exit code 3010 (restart needed) and 1605 (no longer installed) count as done.
    /// </summary>
    public static string Body() => Action1AgentInstall.FindAgent + "\n" + """
        $ErrorActionPreference = 'Stop'
        $agents = Find-Action1Agent
        if ($agents.Count -eq 0) {
            Write-Output 'No Action1 agent is installed on this endpoint, so nothing was changed.'
            exit 0
        }
        foreach ($agent in $agents) {
            $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/x', $agent.PSChildName, '/quiet', '/qn', '/norestart' -Wait -PassThru
            if ($process.ExitCode -notin @(0, 1605, 3010)) {
                throw "Uninstalling $($agent.DisplayName) ended with exit code $($process.ExitCode)."
            }
        }
        Write-Output 'The Action1 agent was removed. Fleeto sees it gone with the next inventory.'
        """;
}
