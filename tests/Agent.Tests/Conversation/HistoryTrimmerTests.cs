using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;
using Agent.Core.Conversation;

namespace Agent.Tests.ConversationTests;

public sealed class HistoryTrimmerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private static ConversationMessage Msg(long seq, StoredMessageRole role, params ContentBlock[] blocks) =>
        new($"m{seq}", role, blocks, role is StoredMessageRole.User or StoredMessageRole.Assistant, Now) { Sequence = seq };

    private static ConversationMessage User(long seq) => Msg(seq, StoredMessageRole.User, new TextBlock($"u{seq}"));

    private static ConversationMessage Assistant(long seq) => Msg(seq, StoredMessageRole.Assistant, new TextBlock($"a{seq}"));

    private static ConversationMessage ToolCall(long seq, string id) =>
        Msg(seq, StoredMessageRole.ToolCall, new ToolUseBlock(id, "echo", AgentJson.EmptyObject));

    private static ConversationMessage ToolResult(long seq, string id) =>
        Msg(seq, StoredMessageRole.ToolResult, new ToolResultBlock(id, "{}"));

    /// <summary>Alternating user/assistant text messages numbered from <paramref name="first"/>.</summary>
    private static List<ConversationMessage> Alternating(int count, long first = 1) =>
        Enumerable.Range(0, count).Select(i => i % 2 == 0 ? User(first + i) : Assistant(first + i)).ToList();

    private static bool IsRealUser(ConversationMessage m) =>
        m.Role == StoredMessageRole.User && !m.Blocks.OfType<ToolResultBlock>().Any();

    private static void AssertWellFormed(IReadOnlyList<ConversationMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        Assert.True(IsRealUser(messages[0]), "the window must start with a real user message");
        var seenToolUses = new HashSet<string>();
        foreach (var message in messages)
        {
            foreach (var use in message.Blocks.OfType<ToolUseBlock>())
            {
                seenToolUses.Add(use.Id);
            }

            foreach (var result in message.Blocks.OfType<ToolResultBlock>())
            {
                Assert.Contains(result.ToolUseId, seenToolUses);
            }
        }
    }

    [Fact]
    public void Histories_within_the_window_are_returned_untouched()
    {
        var messages = Alternating(20);

        var trimmed = HistoryTrimmer.Trim(messages, window: 20, chunk: 8);

        Assert.Same(messages, trimmed);
    }

    [Fact]
    public void Overflow_drops_exactly_one_chunk()
    {
        var messages = Alternating(21);

        var trimmed = HistoryTrimmer.Trim(messages, window: 20, chunk: 8);

        Assert.Equal(13, trimmed.Count);
        Assert.Equal(9, trimmed[0].Sequence);
        AssertWellFormed(trimmed);
    }

    [Fact]
    public void The_start_of_the_window_only_moves_when_a_chunk_boundary_is_crossed()
    {
        var kept = Alternating(1);
        var starts = new List<long>();
        long next = 2;
        for (var i = 0; i < 60; i++)
        {
            kept.Add(next % 2 == 1 ? User(next) : Assistant(next));
            next++;
            kept = HistoryTrimmer.Trim(kept, window: 20, chunk: 8).ToList();
            starts.Add(kept[0].Sequence);
        }

        var changes = starts.Zip(starts.Skip(1), (a, b) => (a, b)).Count(p => p.a != p.b);
        Assert.InRange(changes, 1, 7);
        Assert.True(starts.Distinct().Count() <= 8, "a prefix that changes every message would defeat prompt caching");
        Assert.All(starts.Zip(starts.Skip(1), (a, b) => b - a), step => Assert.True(step == 0 || step % 8 == 0));
    }

    [Fact]
    public void A_window_that_would_start_inside_a_tool_pair_drops_the_whole_pair()
    {
        // U1 TC2 TR3 A4 U5 A6: the first chunk boundary lands on the tool result.
        List<ConversationMessage> messages = [User(1), ToolCall(2, "t1"), ToolResult(3, "t1"), Assistant(4), User(5), Assistant(6)];

        var trimmed = HistoryTrimmer.Trim(messages, window: 5, chunk: 2);

        Assert.Equal([5L, 6L], trimmed.Select(m => m.Sequence));
        AssertWellFormed(trimmed);
    }

    [Fact]
    public void Complete_tool_pairs_inside_the_window_are_kept_together()
    {
        List<ConversationMessage> messages =
        [
            User(1), Assistant(2),
            User(3), ToolCall(4, "t1"), ToolResult(5, "t1"), Assistant(6),
            User(7), ToolCall(8, "t2"), ToolResult(9, "t2"), Assistant(10),
        ];

        var trimmed = HistoryTrimmer.Trim(messages, window: 8, chunk: 2);

        Assert.Equal(3, trimmed[0].Sequence);
        Assert.Equal(8, trimmed.Count);
        AssertWellFormed(trimmed);
    }

    [Fact]
    public void An_orphaned_leading_tool_result_is_dropped_even_below_the_window()
    {
        List<ConversationMessage> messages = [ToolResult(7, "gone"), Assistant(8), User(9), Assistant(10)];

        var trimmed = HistoryTrimmer.Trim(messages, window: 20, chunk: 8);

        Assert.Equal([9L, 10L], trimmed.Select(m => m.Sequence));
        AssertWellFormed(trimmed);
    }

    [Fact]
    public void Leading_assistant_messages_are_dropped_so_the_window_starts_with_the_user()
    {
        List<ConversationMessage> messages = [Assistant(1), Assistant(2), User(3), Assistant(4)];

        var trimmed = HistoryTrimmer.Trim(messages, window: 20, chunk: 8);

        Assert.Equal(3, trimmed[0].Sequence);
    }

    [Fact]
    public void The_window_is_never_larger_than_requested_plus_what_is_needed_to_reach_a_user_message()
    {
        var messages = Alternating(100);

        var trimmed = HistoryTrimmer.Trim(messages, window: 20, chunk: 8);

        Assert.InRange(trimmed.Count, 1, 20);
        Assert.Equal(messages[^1].Sequence, trimmed[^1].Sequence);
    }

    [Theory]
    [InlineData(6, 2)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    [InlineData(20, 8)]
    public void Every_trim_of_a_history_full_of_tool_calls_is_well_formed(int window, int chunk)
    {
        var messages = new List<ConversationMessage>();
        long seq = 1;
        for (var turn = 0; turn < 40; turn++)
        {
            messages.Add(User(seq++));
            for (var step = 0; step <= turn % 3; step++)
            {
                var id = $"t{turn}-{step}";
                messages.Add(ToolCall(seq++, id));
                messages.Add(ToolResult(seq++, id));
            }

            messages.Add(Assistant(seq++));
            AssertWellFormed(HistoryTrimmer.Trim(messages, window, chunk));
        }
    }

    [Fact]
    public void Empty_history_stays_empty()
    {
        Assert.Empty(HistoryTrimmer.Trim([], window: 20, chunk: 8));
    }
}
