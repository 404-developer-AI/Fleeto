using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The notification listener (0.7.0): a notification proves the connection alive, so the listener pings only after a quiet
/// wait. The performance report of 0.7.0-alpha.8 showed a ping after every notification, a round trip per notification in
/// every component.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NotificationBusTests
{
    private readonly DatabaseFixture _fixture;

    public NotificationBusTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_listener_answers_notifications_without_a_ping_after_each()
    {
        var application = "fleeto-bus-test-" + Guid.NewGuid().ToString("N")[..8];
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Database.ConnectionString) { ApplicationName = application };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        using var bus = new PostgresNotificationBus(dataSource, NullLogger<PostgresNotificationBus>.Instance);

        const int sent = 10;
        var received = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe(NotificationChannels.Jobs, (_, _) =>
        {
            if (Interlocked.Increment(ref received) == sent)
            {
                all.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await bus.StartAsync(CancellationToken.None);
        try
        {
            // The listener is ready once its last statement is a LISTEN.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await LastStatementAsync(application) is not { } last || !last.StartsWith("LISTEN", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < deadline, "The listener did not start listening.");
                await Task.Delay(50);
            }

            for (var i = 0; i < sent; i++)
            {
                await using var notify = _fixture.Database.DataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
                notify.Parameters.AddWithValue("channel", NotificationChannels.Jobs);
                notify.Parameters.AddWithValue("payload", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await notify.ExecuteNonQueryAsync();
            }

            await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);

            Assert.StartsWith("LISTEN", await LastStatementAsync(application), StringComparison.Ordinal);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Connections_never_try_GSS_encryption()
    {
        var directory = Directory.CreateTempSubdirectory("fleeto-secrets-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, FleetoComponent.Signer.DatabasePasswordFile()), "test-password");
            var connectionString = new DatabaseOptions().BuildConnectionString(FleetoComponent.Signer, new SecretFiles(directory.FullName));

            Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task<string?> LastStatementAsync(string application)
    {
        await using var command = _fixture.Database.DataSource.CreateCommand(
            "SELECT query FROM pg_stat_activity WHERE application_name = @application AND state = 'idle'");
        command.Parameters.AddWithValue("application", application);
        return await command.ExecuteScalarAsync() as string;
    }
}
