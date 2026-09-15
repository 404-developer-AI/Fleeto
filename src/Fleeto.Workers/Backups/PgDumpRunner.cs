using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Workers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleeto.Workers.Backups;

/// <summary>A backup failure whose message is safe to store and email: cause and next step, never a secret.</summary>
public sealed class BackupFailureException : Exception
{
    public BackupFailureException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Connection pg_dump uses. The password is handed to the child process only, through its environment.</summary>
public sealed record PgDumpTarget(string Host, int Port, string Database, string Username, string Password, SslMode SslMode);

public interface IPgDumpTargetProvider
{
    PgDumpTarget GetTarget();
}

/// <summary>The instance database as the read-only backup role (member of pg_read_all_data).</summary>
public sealed class PgDumpTargetProvider : IPgDumpTargetProvider
{
    public const string PasswordFile = "db-backup.password";

    private readonly DatabaseOptions _database;
    private readonly SecretFiles _secrets;

    public PgDumpTargetProvider(IOptions<DatabaseOptions> database, SecretFiles secrets)
    {
        _database = database.Value;
        _secrets = secrets;
    }

    public PgDumpTarget GetTarget() =>
        new(_database.Host, _database.Port, _database.Name, DatabaseRoles.Backup, _secrets.ReadText(PasswordFile), _database.SslMode);
}

/// <summary>Finds pg_dump.</summary>
public static class PgDumpLocator
{
    /// <summary>
    /// "auto" (or empty) searches <c>C:\Program Files\PostgreSQL\*\bin\pg_dump.exe</c> on Windows, newest major version
    /// first, and otherwise uses "pg_dump" from the PATH. Any other value is used as is.
    /// </summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
            if (Directory.Exists(root))
            {
                var found = Directory.GetDirectories(root)
                    .Select(d => (Directory: d, Version: int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) ? v : 0))
                    .OrderByDescending(d => d.Version)
                    .Select(d => Path.Combine(d.Directory, "bin", "pg_dump.exe"))
                    .FirstOrDefault(File.Exists);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return "pg_dump";
    }

    /// <summary>The resolved path when the executable exists (on disk or on the PATH), otherwise null.</summary>
    public static string? FindExecutable(string? configured)
    {
        var path = Resolve(configured);
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        var names = OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { path + ".exe", path } : [path];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>Runs <c>pg_dump --format=custom</c> into a file, with a timeout, without a shell.</summary>
public sealed class PgDumpRunner
{
    private const int MaxStderrLength = 4000;

    private readonly BackupOptions _options;
    private readonly IPgDumpTargetProvider _targets;
    private readonly ILogger<PgDumpRunner> _logger;

    public PgDumpRunner(IOptions<BackupOptions> options, IPgDumpTargetProvider targets, ILogger<PgDumpRunner> logger)
    {
        _options = options.Value;
        _targets = targets;
        _logger = logger;
    }

    public async Task DumpAsync(string outputFile, CancellationToken cancellationToken)
    {
        var executable = PgDumpLocator.Resolve(_options.PgDumpPath);
        PgDumpTarget target;
        try
        {
            target = _targets.GetTarget();
        }
        catch (InvalidOperationException ex)
        {
            throw new BackupFailureException("The password file of the backup database role is missing. Run install.sh for this instance again.", ex);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "--format=custom", "--no-password",
                     "--host", target.Host, "--port", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--username", target.Username, "--dbname", target.Database, "--file", outputFile
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Only the child process sees the password; it is never on the command line or in a file.
        startInfo.Environment["PGPASSWORD"] = target.Password;
        startInfo.Environment["PGSSLMODE"] = LibpqSslMode(target.SslMode);
        startInfo.Environment["PGCONNECT_TIMEOUT"] = "30";
        startInfo.Environment["PGAPPNAME"] = "fleeto-backup";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new BackupFailureException(
                $"pg_dump could not be started from '{executable}'. Install the PostgreSQL client tools or set Backup:PgDumpPath.", ex);
        }

        var stderr = ReadCappedAsync(process.StandardError);
        var stdout = ReadCappedAsync(process.StandardOutput);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.PgDumpTimeoutMinutes)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new BackupFailureException($"pg_dump did not finish within {_options.PgDumpTimeoutMinutes} minutes. Check the database load and disk speed.");
        }

        var errorText = (await stderr).Trim();
        await stdout;
        if (process.ExitCode != 0)
        {
            var firstLines = string.Join(" ", errorText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
            throw new BackupFailureException($"pg_dump exited with code {process.ExitCode}: {Cap(firstLines, 500)}");
        }

        if (!File.Exists(outputFile) || new FileInfo(outputFile).Length == 0)
        {
            throw new BackupFailureException("pg_dump finished without writing a dump file. Check free disk space in the temp directory.");
        }

        if (errorText.Length > 0)
        {
            _logger.LogInformation("pg_dump reported: {Output}", Cap(errorText, 500));
        }
    }

    private static string LibpqSslMode(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => "prefer"
    };

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (builder.Length < MaxStderrLength)
            {
                builder.Append(buffer, 0, Math.Min(read, MaxStderrLength - builder.Length));
            }
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
}
