using Fleetify.Core.Entities;
using Fleetify.Workers.Email;
using Fleetify.Workers.Options;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees that the outbox sends due emails once, retries failures with the documented backoff, gives up after the
/// maximum number of attempts, does not retry permanent refusals, pauses behind the circuit breaker and leaves emails
/// pending while email is not configured.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class OutboxEmailTests
{
    private readonly WorkersFixture _fixture;

    public OutboxEmailTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FakeTransports : IEmailTransportFactory
    {
        public bool Configured { get; set; } = true;
        public Func<OutboxEmail, Exception?> Behaviour { get; set; } = _ => null;
        public List<Guid> Sent { get; } = [];

        public Task<IEmailSession?> CreateSessionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IEmailSession?>(Configured ? new Session(this) : null);

        private sealed class Session(FakeTransports owner) : IEmailSession
        {
            public Task SendAsync(OutboxEmail email, CancellationToken cancellationToken)
            {
                var failure = owner.Behaviour(email);
                if (failure is not null)
                {
                    throw failure;
                }

                owner.Sent.Add(email.Id);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private async Task<Guid> EnqueueAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var email = OutboxEmails.Create($"ops-{Guid.NewGuid():N}@test.example", EmailTemplates.TestEmail("rmm.test.example"), "test", _fixture.Now);
        db.OutboxEmails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    private async Task<OutboxEmail> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.OutboxEmails.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    [Fact]
    public async Task A_due_email_is_sent_once()
    {
        await _fixture.ClearOutboxAsync();
        var id = await EnqueueAsync();
        var transports = new FakeTransports();
        var outbox = _fixture.Outbox(transports);

        Assert.Equal(1, await outbox.DeliverPendingAsync(CancellationToken.None));
        Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));

        Assert.Equal([id], transports.Sent);
        var email = await ReadAsync(id);
        Assert.NotNull(email.SentAt);
        Assert.Equal(1, email.Attempts);
        Assert.Null(email.LastError);
    }

    [Fact]
    public async Task Failures_back_off_and_the_outbox_gives_up_after_ten_attempts()
    {
        await _fixture.ClearOutboxAsync();
        var id = await EnqueueAsync();
        var transports = new FakeTransports { Behaviour = _ => new EmailDeliveryException("Could not connect to the mail server smtp.test.example:587.", permanent: false) };
        var outbox = _fixture.Outbox(transports);
        TimeSpan[] expected = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
            TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromHours(6)];

        for (var attempt = 1; attempt <= OutboxEmailService.MaxAttempts; attempt++)
        {
            var before = _fixture.Now;
            Assert.Equal(1, await outbox.DeliverPendingAsync(CancellationToken.None));
            var email = await ReadAsync(id);
            Assert.Equal(attempt, email.Attempts);
            Assert.Equal("Could not connect to the mail server smtp.test.example:587.", email.LastError);
            Assert.Null(email.SentAt);
            Assert.InRange(email.NextAttemptAt - before, expected[attempt - 1] - TimeSpan.FromSeconds(1), expected[attempt - 1] + TimeSpan.FromSeconds(1));

            // Not due yet: nothing is attempted.
            Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));
            _fixture.Db.Time.Advance(expected[attempt - 1] + TimeSpan.FromSeconds(1));
        }

        _fixture.Db.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));
        Assert.Equal(OutboxEmailService.MaxAttempts, (await ReadAsync(id)).Attempts);
    }

    [Fact]
    public async Task A_refused_recipient_is_not_retried()
    {
        await _fixture.ClearOutboxAsync();
        var id = await EnqueueAsync();
        var outbox = _fixture.Outbox(new FakeTransports { Behaviour = _ => new EmailDeliveryException("The mail server refused the recipient (550).", permanent: true) });

        await outbox.DeliverPendingAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));

        var email = await ReadAsync(id);
        Assert.Equal(OutboxEmailService.MaxAttempts, email.Attempts);
        Assert.Null(email.SentAt);
    }

    [Fact]
    public async Task The_circuit_breaker_pauses_delivery_after_consecutive_failures()
    {
        await _fixture.ClearOutboxAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await EnqueueAsync());
        }

        var transports = new FakeTransports { Behaviour = _ => new EmailDeliveryException("The mail server did not respond in time.", permanent: false) };
        var outbox = _fixture.Outbox(transports, new EmailOptions { CircuitBreakerFailures = 2, CircuitBreakerPauseMinutes = 5 });

        Assert.Equal(2, await outbox.DeliverPendingAsync(CancellationToken.None));
        var untouched = await Task.WhenAll(ids.Select(ReadAsync));
        Assert.Equal(2, untouched.Count(e => e.Attempts == 0));

        // Open: nothing is attempted, even when due.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));

        transports.Behaviour = _ => null;
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(await outbox.DeliverPendingAsync(CancellationToken.None) > 0);
        Assert.NotEmpty(transports.Sent);
    }

    [Fact]
    public async Task Emails_stay_pending_while_email_is_not_configured()
    {
        await _fixture.ClearOutboxAsync();
        var id = await EnqueueAsync();
        var outbox = _fixture.Outbox(new FakeTransports { Configured = false });

        Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));

        var email = await ReadAsync(id);
        Assert.Equal(0, email.Attempts);
        Assert.Null(email.SentAt);
        Assert.True(email.NextAttemptAt <= _fixture.Now);
    }
}
