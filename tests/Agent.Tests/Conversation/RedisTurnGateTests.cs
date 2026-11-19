using Agent.Abstractions.Config;
using Agent.Persistence.Locking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;

namespace Agent.Tests.ConversationTests;

/// <summary>
/// The Redis turn gate against a mocked connection: how it behaves around the Redis calls, not how Redis behaves.
/// The Lua scripts and the real lease semantics are not exercised here.
/// </summary>
public sealed class RedisTurnGateTests
{
    private readonly IDatabase _db = Substitute.For<IDatabase>();
    private readonly FakeTimeProvider _clock = new();
    private readonly TurnOptions _options = new() { LeaseSeconds = 30 };
    private readonly RedisTurnGate _gate;
    private string? _tokenSet;

    public RedisTurnGateTests()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);
        _gate = new RedisTurnGate(redis, Options.Create(_options), _clock, NullLogger<RedisTurnGate>.Instance);
    }

    [Fact]
    public async Task When_the_key_is_free_the_turn_proceeds_and_the_lease_carries_a_random_token_and_expiry()
    {
        SetReturns(true);

        await using var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Single(SetCalls());
        var call = SetCalls().Single();
        Assert.Equal("turn:session:s1", (string?)(RedisKey)call.GetArguments()[0]!);
        Assert.False(string.IsNullOrEmpty(_tokenSet));
        Assert.Equal(_options.Lease, (TimeSpan?)call.GetArguments()[2]);
        Assert.Equal(When.NotExists, (When)call.GetArguments()[3]!);
    }

    [Fact]
    public async Task When_the_key_is_already_held_the_turn_is_refused_and_nothing_is_renewed_or_deleted()
    {
        SetReturns(false);

        var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(lease);
        Assert.Empty(ScriptCalls());
    }

    [Fact]
    public async Task Each_entry_uses_a_different_token()
    {
        SetReturns(true);

        await using (await _gate.TryEnterAsync("s1", CancellationToken.None))
        {
        }

        var first = _tokenSet;
        await using (await _gate.TryEnterAsync("s1", CancellationToken.None))
        {
        }

        Assert.NotEqual(first, _tokenSet);
    }

    [Fact]
    public async Task Releasing_runs_the_compare_and_delete_script_with_the_token_that_was_set()
    {
        SetReturns(true);
        var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);

        await lease!.DisposeAsync();

        var script = Assert.Single(ScriptCalls());
        Assert.Contains("'del'", (string)script.GetArguments()[0]!);
        Assert.Equal("turn:session:s1", (string?)((RedisKey[])script.GetArguments()[1]!).Single());
        Assert.Equal(_tokenSet, (string?)((RedisValue[])script.GetArguments()[2]!).Single());
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_once()
    {
        SetReturns(true);
        var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);

        await lease!.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Single(ScriptCalls());
    }

    [Fact]
    public async Task A_live_turn_renews_the_lease_with_its_token_every_third_of_the_lease()
    {
        SetReturns(true);
        _db.ScriptEvaluateAsync("", (RedisKey[]?)null, (RedisValue[]?)null).ReturnsForAnyArgs(Task.FromResult(RedisResult.Create((RedisValue)1L)));
        await using var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);

        _clock.Advance(_options.RenewInterval);
        await WaitForAsync(() => ScriptCalls().Count >= 1);

        var renew = ScriptCalls().First();
        Assert.Contains("'pexpire'", (string)renew.GetArguments()[0]!);
        var values = (RedisValue[])renew.GetArguments()[2]!;
        Assert.Equal(_tokenSet, (string?)values[0]);
        Assert.Equal((long)_options.Lease.TotalMilliseconds, (long)values[1]);
    }

    [Fact]
    public async Task A_failing_release_does_not_throw_into_the_caller()
    {
        SetReturns(true);
        _db.ScriptEvaluateAsync("", (RedisKey[]?)null, (RedisValue[]?)null)
            .ReturnsForAnyArgs(Task.FromException<RedisResult>(new RedisException("down")));
        var lease = await _gate.TryEnterAsync("s1", CancellationToken.None);

        await lease!.DisposeAsync();
    }

    [Fact]
    public async Task When_redis_is_unreachable_the_gate_falls_back_to_a_process_local_lock()
    {
        _db.StringSetAsync(default, default, (TimeSpan?)null, When.NotExists)
            .ReturnsForAnyArgs(Task.FromException<bool>(new RedisException("down")));

        var first = await _gate.TryEnterAsync("s1", CancellationToken.None);
        var sameSession = await _gate.TryEnterAsync("s1", CancellationToken.None);
        await using var otherSession = await _gate.TryEnterAsync("s2", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(sameSession);
        Assert.NotNull(otherSession);

        await first.DisposeAsync();
        await using var again = await _gate.TryEnterAsync("s1", CancellationToken.None);
        Assert.NotNull(again);
        Assert.Empty(ScriptCalls());
    }

    [Fact]
    public async Task A_caller_that_cancels_while_waiting_for_redis_gets_the_cancellation_not_a_lock()
    {
        _db.StringSetAsync(default, default, (TimeSpan?)null, When.NotExists)
            .ReturnsForAnyArgs(new TaskCompletionSource<bool>().Task);
        using var cts = new CancellationTokenSource();
        var pending = _gate.TryEnterAsync("s1", cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private void SetReturns(bool acquired)
    {
        _db.StringSetAsync(default, default, (TimeSpan?)null, When.NotExists)
            .ReturnsForAnyArgs(call =>
            {
                _tokenSet = (string?)call.ArgAt<RedisValue>(1);
                return Task.FromResult(acquired);
            });
    }

    private List<NSubstitute.Core.ICall> SetCalls() =>
        _db.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IDatabaseAsync.StringSetAsync)).ToList();

    private List<NSubstitute.Core.ICall> ScriptCalls() =>
        _db.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IDatabaseAsync.ScriptEvaluateAsync)).ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // The renewal loop runs on the thread pool once the fake clock ticks; give it a bounded moment to run.
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The renewal call did not happen.");
    }
}
