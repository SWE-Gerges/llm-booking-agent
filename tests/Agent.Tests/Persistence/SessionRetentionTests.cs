using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Persistence;
using Agent.Persistence.Retention;
using Agent.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.Persistence;

public sealed class SessionRetentionTests
{
    // Defaults: idle TTL 30 minutes plus 60 minutes of grace, so sessions idle for more than 90 minutes are expired.
    private const int Expired = 120;
    private const int InGrace = 45;
    private const int Active = 1;

    [Fact]
    public async Task Expired_sessions_are_removed_with_their_messages_and_contexts_while_the_rest_stay()
    {
        using var harness = await DomainHarness.CreateAsync();
        var old = await SeedAsync(harness, Expired);
        var grace = await SeedAsync(harness, InGrace);
        var active = await SeedAsync(harness, Active);

        var result = await harness.Get<ISessionRetention>().PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsDeleted);
        Assert.False(result.Truncated);
        Assert.False(await SessionExistsAsync(harness, old));
        Assert.Equal(0, await CountMessagesAsync(harness, old));
        Assert.Equal(0, await CountContextsAsync(harness, old));
        foreach (var kept in new[] { grace, active })
        {
            Assert.True(await SessionExistsAsync(harness, kept));
            Assert.Equal(2, await CountMessagesAsync(harness, kept));
            Assert.Equal(1, await CountContextsAsync(harness, kept));
        }
    }

    [Fact]
    public async Task A_session_becomes_eligible_only_after_the_idle_ttl_plus_the_grace_period()
    {
        using var harness = await DomainHarness.CreateAsync();
        var id = await SeedAsync(harness, Active);
        var retention = harness.Get<ISessionRetention>();

        harness.Clock.Advance(TimeSpan.FromMinutes(89));
        Assert.Equal(0, (await retention.PurgeExpiredAsync(CancellationToken.None)).SessionsDeleted);
        Assert.True(await SessionExistsAsync(harness, id));

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await retention.PurgeExpiredAsync(CancellationToken.None)).SessionsDeleted);
        Assert.False(await SessionExistsAsync(harness, id));
    }

    [Fact]
    public async Task Escalation_requests_and_the_audit_log_outlive_the_purged_session()
    {
        using var harness = await DomainHarness.CreateAsync();
        var id = await SeedAsync(harness, Expired);
        await harness.Get<IEscalationStore>().CreateAsync(new EscalationRequest("esc-1", id, "needs a person", null, harness.Clock.GetUtcNow()), CancellationToken.None);
        await harness.Get<IAuditLog>().WriteAsync(new AuditEntry("api", "session.deleted", id, null, harness.Clock.GetUtcNow()), CancellationToken.None);

        await harness.Get<ISessionRetention>().PurgeExpiredAsync(CancellationToken.None);

        await using var db = await harness.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.False(await SessionExistsAsync(harness, id));
        Assert.Equal(1, await db.Escalations.CountAsync());
        Assert.Equal(2, await db.AuditLog.CountAsync());
    }

    [Fact]
    public async Task Work_is_split_into_bounded_batches()
    {
        using var harness = await DomainHarness.CreateAsync(Settings(batchSize: 2, maxBatches: 50));
        for (var i = 0; i < 7; i++)
        {
            await SeedAsync(harness, Expired + i);
        }

        var result = await harness.Get<ISessionRetention>().PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(7, result.SessionsDeleted);
        Assert.Equal(4, result.Batches);
        Assert.False(result.Truncated);
        Assert.Equal(0, await CountSessionsAsync(harness));
    }

    [Fact]
    public async Task A_run_stops_at_its_batch_limit_and_the_next_run_continues()
    {
        using var harness = await DomainHarness.CreateAsync(Settings(batchSize: 2, maxBatches: 2));
        for (var i = 0; i < 7; i++)
        {
            await SeedAsync(harness, Expired + i);
        }

        var retention = harness.Get<ISessionRetention>();
        var first = await retention.PurgeExpiredAsync(CancellationToken.None);
        Assert.Equal(new RetentionResult(4, 2, true), first);
        Assert.Equal(3, await CountSessionsAsync(harness));

        var second = await retention.PurgeExpiredAsync(CancellationToken.None);
        Assert.Equal(new RetentionResult(3, 2, false), second);
        Assert.Equal(0, await CountSessionsAsync(harness));
    }

    [Fact]
    public async Task Running_again_is_a_no_op()
    {
        using var harness = await DomainHarness.CreateAsync();
        await SeedAsync(harness, Expired);
        var retention = harness.Get<ISessionRetention>();

        var first = await retention.PurgeExpiredAsync(CancellationToken.None);
        var second = await retention.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, first.SessionsDeleted);
        Assert.Equal(new RetentionResult(0, 0, false), second);
    }

    [Fact]
    public async Task The_purge_logs_counts_only_and_never_a_session_identifier()
    {
        using var harness = await DomainHarness.CreateAsync();
        var id = await SeedAsync(harness, Expired);

        await harness.Get<ISessionRetention>().PurgeExpiredAsync(CancellationToken.None);

        var line = Assert.Single(harness.Logs.Entries, e => e.Category.EndsWith("SessionRetention", StringComparison.Ordinal));
        Assert.Contains("1 expired sessions", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(id, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hosted_loop_purges_at_start_and_then_on_every_interval_using_the_fake_clock()
    {
        using var harness = await DomainHarness.CreateAsync(Settings(runIntervalMinutes: 10));
        var expiredAtStart = await SeedAsync(harness, Expired);

        var hosted = harness.Services.GetServices<IHostedService>().Single(s => s.GetType().Name == "SessionRetentionService");
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => !await SessionExistsAsync(harness, expiredAtStart));

            // A session that is active now expires as time passes; the next tick removes it.
            var later = await SeedAsync(harness, Active);
            harness.Clock.Advance(TimeSpan.FromMinutes(100));
            await WaitUntilAsync(async () => !await SessionExistsAsync(harness, later));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_loop_survives_a_failing_run_logs_it_and_tries_again_at_the_next_interval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-10T09:00:00Z", CultureInfo.InvariantCulture));
        var retention = new ScriptedRetention(failOnCall: 1);
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var service = new SessionRetentionService(retention, clock, Options.Create(new RetentionOptions { RunIntervalMinutes = 10 }), loggers.CreateLogger<SessionRetentionService>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await retention.WaitForCallsAsync(1);
            clock.Advance(TimeSpan.FromMinutes(10));
            await retention.WaitForCallsAsync(2);
            clock.Advance(TimeSpan.FromMinutes(10));
            await retention.WaitForCallsAsync(3);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task A_disabled_loop_never_purges()
    {
        var clock = new FakeTimeProvider();
        var retention = new ScriptedRetention(failOnCall: 0);
        using var loggers = LoggerFactory.Create(_ => { });
        var service = new SessionRetentionService(retention, clock, Options.Create(new RetentionOptions { Enabled = false }), loggers.CreateLogger<SessionRetentionService>());

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, retention.Calls);
    }

    [Fact]
    public async Task The_loop_stops_promptly_on_shutdown()
    {
        var clock = new FakeTimeProvider();
        var retention = new ScriptedRetention(failOnCall: 0);
        using var loggers = LoggerFactory.Create(_ => { });
        var service = new SessionRetentionService(retention, clock, Options.Create(new RetentionOptions()), loggers.CreateLogger<SessionRetentionService>());

        await service.StartAsync(CancellationToken.None);
        await retention.WaitForCallsAsync(1);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.ExecuteTask is null or { IsCompleted: true });
    }

    [Theory]
    [InlineData(0, 60, 100, 50)]
    [InlineData(15, -1, 100, 50)]
    [InlineData(15, 60, 0, 50)]
    [InlineData(15, 60, 100, 0)]
    public void Out_of_range_retention_settings_fail_validation(int interval, int grace, int batch, int maxBatches)
    {
        var options = new RetentionOptions { RunIntervalMinutes = interval, GraceMinutes = grace, BatchSize = batch, MaxBatchesPerRun = maxBatches };

        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
    }

    [Fact]
    public void Retention_is_enabled_by_default_with_documented_values()
    {
        var options = new RetentionOptions();

        Assert.True(options.Enabled);
        Assert.Equal(15, options.RunIntervalMinutes);
        Assert.Equal(60, options.GraceMinutes);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(50, options.MaxBatchesPerRun);
    }

    private static Dictionary<string, string?> Settings(int? batchSize = null, int? maxBatches = null, int? runIntervalMinutes = null)
    {
        var settings = new Dictionary<string, string?>();
        if (batchSize is { } size)
        {
            settings["Retention:BatchSize"] = size.ToString(CultureInfo.InvariantCulture);
        }

        if (maxBatches is { } max)
        {
            settings["Retention:MaxBatchesPerRun"] = max.ToString(CultureInfo.InvariantCulture);
        }

        if (runIntervalMinutes is { } interval)
        {
            settings["Retention:RunIntervalMinutes"] = interval.ToString(CultureInfo.InvariantCulture);
        }

        return settings;
    }

    /// <summary>Creates a session last active <paramref name="idleMinutes"/> ago with two messages and one booking context.</summary>
    private static async Task<string> SeedAsync(DomainHarness harness, int idleMinutes)
    {
        var id = Guid.NewGuid().ToString("N");
        var lastActivity = harness.Clock.GetUtcNow().AddMinutes(-idleMinutes);
        await harness.Get<ISessionStore>().CreateAsync(
            new SessionContext { SessionId = id, CreatedAt = lastActivity, LastActivityAt = lastActivity, WindowStartSequence = 1, LastSequence = 2 },
            CancellationToken.None);
        await harness.Get<IMessageStore>().AppendAsync(
            id,
            [
                new ConversationMessage(Guid.NewGuid().ToString("N"), StoredMessageRole.User, [new TextBlock("hi")], true, lastActivity) { Sequence = 1 },
                new ConversationMessage(Guid.NewGuid().ToString("N"), StoredMessageRole.Assistant, [new TextBlock("hello")], true, lastActivity) { Sequence = 2 },
            ],
            CancellationToken.None);
        await harness.Contexts.MutateAsync(id, ServiceType.Flight, c => c with { SelectedItemId = "offer-1" }, CancellationToken.None);
        return id;
    }

    private static async Task<bool> SessionExistsAsync(DomainHarness harness, string id) =>
        await harness.Get<ISessionStore>().FindAsync(id, CancellationToken.None) is not null;

    private static async Task<int> CountSessionsAsync(DomainHarness harness)
    {
        await using var db = await harness.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        return await db.Sessions.CountAsync();
    }

    private static async Task<int> CountMessagesAsync(DomainHarness harness, string id)
    {
        await using var db = await harness.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        return await db.Messages.CountAsync(m => m.Session!.SessionToken == id);
    }

    private static async Task<int> CountContextsAsync(DomainHarness harness, string id)
    {
        await using var db = await harness.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        return await db.BookingContexts.CountAsync(c => c.Session!.SessionToken == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(25);
        }
    }

    private sealed class ScriptedRetention(int failOnCall) : ISessionRetention
    {
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<RetentionResult> PurgeExpiredAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _called.Release();
            return call == failOnCall
                ? throw new InvalidOperationException("simulated failure")
                : Task.FromResult(new RetentionResult(0, 0, false));
        }

        public async Task WaitForCallsAsync(int expected)
        {
            while (Calls < expected)
            {
                Assert.True(await _called.WaitAsync(TimeSpan.FromSeconds(15)), $"Expected {expected} calls but saw {Calls}.");
            }
        }
    }
}
