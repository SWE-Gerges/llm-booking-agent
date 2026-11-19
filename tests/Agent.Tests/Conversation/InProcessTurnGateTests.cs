using Agent.Core.Conversation;

namespace Agent.Tests.ConversationTests;

public sealed class InProcessTurnGateTests
{
    [Fact]
    public async Task A_second_entry_for_the_same_session_is_refused_until_the_first_is_released()
    {
        var gate = new InProcessTurnGate();

        var first = await gate.TryEnterAsync("s1", CancellationToken.None);
        var second = await gate.TryEnterAsync("s1", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);

        await first.DisposeAsync();
        await using var third = await gate.TryEnterAsync("s1", CancellationToken.None);
        Assert.NotNull(third);
    }

    [Fact]
    public async Task Different_sessions_do_not_block_each_other()
    {
        var gate = new InProcessTurnGate();

        await using var a = await gate.TryEnterAsync("a", CancellationToken.None);
        await using var b = await gate.TryEnterAsync("b", CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_does_not_release_the_next_holder()
    {
        var gate = new InProcessTurnGate();
        var first = await gate.TryEnterAsync("s1", CancellationToken.None);
        await first!.DisposeAsync();
        await using var second = await gate.TryEnterAsync("s1", CancellationToken.None);

        await first.DisposeAsync();

        Assert.Null(await gate.TryEnterAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task Exactly_one_of_many_parallel_attempts_gets_in()
    {
        var gate = new InProcessTurnGate();

        var leases = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => gate.TryEnterAsync("s1", CancellationToken.None).AsTask())));

        Assert.Single(leases, l => l is not null);
    }
}
