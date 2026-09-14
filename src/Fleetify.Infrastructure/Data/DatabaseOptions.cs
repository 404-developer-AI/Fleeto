using Fleetify.Infrastructure.Hosting;
using Npgsql;

namespace Fleetify.Infrastructure.Data;

/// <summary>
/// Database connection settings. Non-secret values come from configuration; the password always comes from
/// a secret file, so no connection string with a password ever exists in a configuration file.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Fleetify:Database";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "fleetify";

    /// <summary>Role to connect as. Empty means the default role of the component.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password file in the secrets directory. Empty means the default file of the component.</summary>
    public string PasswordFile { get; set; } = string.Empty;

    /// <summary>Disable inside the instance's private Docker network; Require when the database is remote.</summary>
    public SslMode SslMode { get; set; } = SslMode.Disable;

    public int MaxPoolSize { get; set; } = 50;

    public string BuildConnectionString(FleetifyComponent component, SecretFiles secrets)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Name,
            Username = string.IsNullOrWhiteSpace(Username) ? component.DatabaseRole() : Username,
            Password = secrets.ReadText(string.IsNullOrWhiteSpace(PasswordFile) ? component.DatabasePasswordFile() : PasswordFile),
            SslMode = SslMode,
            MaxPoolSize = MaxPoolSize,
            ApplicationName = "fleetify-" + component.ToString().ToLowerInvariant(),
            // Keep-alive so LISTEN connections notice a dead peer.
            KeepAlive = 30
        };

        return builder.ConnectionString;
    }
}
