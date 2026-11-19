using Agent.Abstractions;
using Agent.Abstractions.Config;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Persistence.Caching;
using Agent.Persistence.RateLimiting;
using Agent.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.PersistenceTests;

internal sealed class FakeCacheClock(TimeProvider time) : ISystemClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();
}

public class MemoryChatCacheTests
{
    private readonly FakeTimeProvider _time = new(DomainHarness.Start);

    private static SessionContext Sample(string id = "sess-1") => new()
    {
        SessionId = id,
        State = SessionState.Active,
        Locale = "ar",
        RequestedServices = [ServiceType.Flight],
        CreatedAt = DomainHarness.Start,
        LastActivityAt = DomainHarness.Start,
        LastSequence = 3,
        WindowStartSequence = 1,
        RepeatHash = "H",
        RepeatCount = 1,
        History =
        [
            new ConversationMessage("m1", StoredMessageRole.User, [new TextBlock("hello")], true, DomainHarness.Start) { Sequence = 1 },
            new ConversationMessage("m2", StoredMessageRole.ToolCall, [new ToolUseBlock("t1", "search_flights", AgentJson.Parse("""{"origin":"AMS"}"""))], false, DomainHarness.Start) { Sequence = 2 },
            new ConversationMessage("m3", StoredMessageRole.ToolResult, [new ToolResultBlock("t1", "{}", false)], false, DomainHarness.Start) { Sequence = 3 },
        ],
    };

    private MemoryChatCache Cache(MemoryCache memory, int idleMinutes = 30) =>
        new(memory, TestOptions.Of(new SessionOptions { IdleTtlMinutes = idleMinutes }));

    private MemoryCache NewMemory() => new(new MemoryCacheOptions { Clock = new FakeCacheClock(_time) });

    [Fact]
    public async Task A_session_round_trips_as_json_including_tool_blocks()
    {
        using var memory = NewMemory();
        var cache = Cache(memory);
        var session = Sample();

        await cache.SetAsync(session, CancellationToken.None);
        var loaded = await cache.GetAsync("sess-1", CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(session), AgentJson.Serialize(loaded));
        Assert.IsType<ToolUseBlock>(loaded!.History[1].Blocks[0]);
    }

    [Fact]
    public async Task A_miss_is_null_and_remove_evicts_only_the_named_session()
    {
        using var memory = NewMemory();
        var cache = Cache(memory);
        await cache.SetAsync(Sample("a"), CancellationToken.None);
        await cache.SetAsync(Sample("b"), CancellationToken.None);

        Assert.Null(await cache.GetAsync("missing", CancellationToken.None));
        await cache.RemoveAsync("a", CancellationToken.None);

        Assert.Null(await cache.GetAsync("a", CancellationToken.None));
        Assert.NotNull(await cache.GetAsync("b", CancellationToken.None));
        await cache.RemoveAsync("never-there", CancellationToken.None);
    }

    [Fact]
    public async Task Cached_entries_are_independent_copies_of_the_session()
    {
        using var memory = NewMemory();
        var cache = Cache(memory);
        var session = Sample();
        await cache.SetAsync(session, CancellationToken.None);

        session.State = SessionState.Escalated;
        session.History.Clear();
        var first = await cache.GetAsync("sess-1", CancellationToken.None);
        first!.RequestedServices.Add(ServiceType.Hotel);
        first.History.RemoveAt(0);
        first.RepeatCount = 99;
        var second = await cache.GetAsync("sess-1", CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(SessionState.Active, second!.State);
        Assert.Equal(3, second.History.Count);
        Assert.Equal([ServiceType.Flight], second.RequestedServices);
        Assert.Equal(1, second.RepeatCount);
    }

    [Fact]
    public async Task The_idle_ttl_slides_on_every_read_and_expires_after_inactivity()
    {
        using var memory = NewMemory();
        var cache = Cache(memory, idleMinutes: 30);
        await cache.SetAsync(Sample(), CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(await cache.GetAsync("sess-1", CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(await cache.GetAsync("sess-1", CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(await cache.GetAsync("sess-1", CancellationToken.None));
    }

    [Fact]
    public async Task Setting_again_restarts_the_ttl()
    {
        using var memory = NewMemory();
        var cache = Cache(memory, idleMinutes: 10);
        await cache.SetAsync(Sample(), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(9));

        await cache.SetAsync(Sample(), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(9));

        Assert.NotNull(await cache.GetAsync("sess-1", CancellationToken.None));
    }

    [Fact]
    public void The_ttl_comes_from_the_single_session_option_with_a_thirty_minute_default()
    {
        Assert.Equal(30, new SessionOptions().IdleTtlMinutes);
        Assert.Equal(TimeSpan.FromMinutes(30), new SessionOptions().IdleTtl);
    }
}

public class InMemorySessionRateLimiterTests
{
    private readonly FakeTimeProvider _time = new(DomainHarness.Start);

    private InMemorySessionRateLimiter Limiter(int limit = 3, bool enabled = true, int windowSeconds = 60) =>
        new(TestOptions.Of(new RateLimitOptions { Enabled = enabled, SessionMessagesPerWindow = limit, WindowSeconds = windowSeconds }), _time);

    [Fact]
    public async Task The_limit_is_enforced_per_window_with_a_retry_after()
    {
        var limiter = Limiter(limit: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
        }

        _time.Advance(TimeSpan.FromSeconds(25));
        var blocked = await limiter.TryAcquireAsync("s1", CancellationToken.None);

        Assert.False(blocked.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(35), blocked.RetryAfter);
    }

    [Fact]
    public async Task A_new_window_starts_counting_again()
    {
        var limiter = Limiter(limit: 2);
        await limiter.TryAcquireAsync("s1", CancellationToken.None);
        await limiter.TryAcquireAsync("s1", CancellationToken.None);
        Assert.False((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);

        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.True((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
        Assert.True((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
        Assert.False((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public async Task Sessions_are_limited_independently()
    {
        var limiter = Limiter(limit: 1);

        Assert.True((await limiter.TryAcquireAsync("a", CancellationToken.None)).IsAllowed);
        Assert.False((await limiter.TryAcquireAsync("a", CancellationToken.None)).IsAllowed);
        Assert.True((await limiter.TryAcquireAsync("b", CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public async Task A_disabled_limiter_always_allows()
    {
        var limiter = Limiter(limit: 1, enabled: false);

        for (var i = 0; i < 100; i++)
        {
            Assert.True((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
        }
    }

    [Fact]
    public async Task Concurrent_callers_are_counted_atomically()
    {
        var limiter = Limiter(limit: 10);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => limiter.TryAcquireAsync("busy", CancellationToken.None))));

        Assert.Equal(10, decisions.Count(d => d.IsAllowed));
        Assert.Equal(40, decisions.Count(d => !d.IsAllowed));
    }

    [Fact]
    public async Task Pruning_stale_windows_does_not_disturb_counting()
    {
        var limiter = Limiter(limit: 3);
        for (var i = 0; i <= 10_000; i++)
        {
            Assert.True((await limiter.TryAcquireAsync($"old-{i}", CancellationToken.None)).IsAllowed);
        }

        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.True((await limiter.TryAcquireAsync("live", CancellationToken.None)).IsAllowed);
        Assert.True((await limiter.TryAcquireAsync("live", CancellationToken.None)).IsAllowed);
        Assert.True((await limiter.TryAcquireAsync("live", CancellationToken.None)).IsAllowed);
        Assert.False((await limiter.TryAcquireAsync("live", CancellationToken.None)).IsAllowed);
        Assert.True((await limiter.TryAcquireAsync("old-1", CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public void Retry_after_is_the_time_left_in_the_current_window()
    {
        var start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(60), InMemorySessionRateLimiter.RetryAfter(start, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(1), InMemorySessionRateLimiter.RetryAfter(start.AddSeconds(59), TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(10), InMemorySessionRateLimiter.RetryAfter(start.AddSeconds(20), TimeSpan.FromSeconds(30)));
    }
}

public class RateLimitDefaultsTests
{
    [Fact]
    public void Enforcement_is_on_by_default_with_the_documented_limits()
    {
        var options = new RateLimitOptions();

        Assert.True(options.Enabled);
        Assert.Equal(30, options.SessionMessagesPerWindow);
        Assert.Equal(120, options.IpRequestsPerWindow);
        Assert.Equal(60, options.SessionStartsPerWindow);
        Assert.Equal(60, options.WindowSeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Window);
    }

    [Fact]
    public async Task The_registered_limiter_blocks_the_thirty_first_message_in_a_minute_without_any_configuration()
    {
        using var h = await DomainHarness.CreateAsync();
        var limiter = h.Get<ISessionRateLimiter>();

        for (var i = 0; i < 30; i++)
        {
            Assert.True((await limiter.TryAcquireAsync("s1", CancellationToken.None)).IsAllowed);
        }

        var blocked = await limiter.TryAcquireAsync("s1", CancellationToken.None);
        Assert.False(blocked.IsAllowed);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
        Assert.True((await limiter.TryAcquireAsync("another-session", CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public async Task Configuration_can_raise_or_disable_the_limits()
    {
        using var raised = await DomainHarness.CreateAsync(new Dictionary<string, string?> { ["RateLimiting:SessionMessagesPerWindow"] = "2" });
        var strict = raised.Get<ISessionRateLimiter>();
        await strict.TryAcquireAsync("s", CancellationToken.None);
        await strict.TryAcquireAsync("s", CancellationToken.None);
        Assert.False((await strict.TryAcquireAsync("s", CancellationToken.None)).IsAllowed);

        using var off = await DomainHarness.CreateAsync(new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "false" });
        var open = off.Get<ISessionRateLimiter>();
        for (var i = 0; i < 200; i++)
        {
            Assert.True((await open.TryAcquireAsync("s", CancellationToken.None)).IsAllowed);
        }
    }
}
