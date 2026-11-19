using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Tests.Support;

namespace Agent.Tests.Orchestration;

/// <summary>A cache miss must rebuild exactly what the cache held, including tool traffic and turn-relevant flags.</summary>
public sealed class OrchestratorRebuildTests
{
    private static readonly Dictionary<string, string?> SmallWindow = new()
    {
        ["Agent:HistoryWindow"] = "12",
        ["Agent:TrimChunk"] = "4",
    };

    private static OrchestratorHarness CreateHarness()
    {
        var search = new FakeSearchTool(
            "search_x",
            (ctx, _) =>
            {
                ctx.Session.AddRequestedService(ServiceType.Flight);
                return ToolResult.Ok("""{"items":[{"id":"F1"}]}""", state: "results");
            });
        var escalate = new FakeTool(
            "escalate_x",
            (ctx, _) =>
            {
                ctx.Session.State = SessionState.Escalated;
                return ToolResult.Ok("""{"escalated":true}""", state: "escalated");
            });
        return OrchestratorHarness.Create([search, escalate], SmallWindow);
    }

    private static async Task<string> RunTurnsAsync(OrchestratorHarness harness, bool escalateAtTheEnd)
    {
        var sessionId = await harness.StartAsync();
        harness.Llm
            .EnqueueTools(LlmResults.Use("s1", "search_x")).EnqueueText("found")
            .EnqueueTools(LlmResults.Use("s2", "search_x")).EnqueueText("found again")
            .EnqueueText("ok")
            .EnqueueText("ok again");
        await harness.SendAsync(sessionId, "find flights");
        await harness.SendAsync(sessionId, "find flights");
        await harness.SendAsync(sessionId, "something else");
        await harness.SendAsync(sessionId, "something else");

        if (escalateAtTheEnd)
        {
            harness.Llm.EnqueueTools(LlmResults.Use("e1", "escalate_x")).EnqueueText("handing over");
        }
        else
        {
            harness.Llm.EnqueueText("fine");
        }

        await harness.SendAsync(sessionId, "last one");
        return sessionId;
    }

    [Fact]
    public async Task Rebuilt_session_equals_the_cached_session_field_by_field_including_tool_blocks()
    {
        using var harness = CreateHarness();
        var sessionId = await RunTurnsAsync(harness, escalateAtTheEnd: true);

        var cached = await harness.Cache.GetAsync(sessionId, CancellationToken.None);
        harness.Cache.Clear();
        var rebuilt = await harness.Conversation.LoadAsync(sessionId, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.NotSame(cached, rebuilt);
        Assert.Equal(SessionState.Escalated, rebuilt.State);
        Assert.Equal(cached.State, rebuilt.State);
        Assert.Equal([ServiceType.Flight], rebuilt.RequestedServices);
        Assert.Equal(cached.RequestedServices, rebuilt.RequestedServices);
        Assert.Equal(cached.RepeatHash, rebuilt.RepeatHash);
        Assert.NotNull(rebuilt.RepeatHash);
        Assert.Equal(cached.RepeatCount, rebuilt.RepeatCount);
        Assert.Equal(1, rebuilt.RepeatCount);
        Assert.Equal(cached.LastSequence, rebuilt.LastSequence);
        Assert.Equal(cached.WindowStartSequence, rebuilt.WindowStartSequence);
        Assert.True(rebuilt.WindowStartSequence > 1, "the scenario should have trimmed the prompt window");
        Assert.Equal(cached.Locale, rebuilt.Locale);
        Assert.Equal(cached.LastActivityAt, rebuilt.LastActivityAt);
        Assert.Equal(cached.History.Count, rebuilt.History.Count);
        Assert.Equal(rebuilt.History[0].Sequence, rebuilt.WindowStartSequence);

        Assert.Contains(rebuilt.History, m => m.Role == StoredMessageRole.ToolCall && m.Blocks.OfType<ToolUseBlock>().Any());
        Assert.Contains(rebuilt.History, m => m.Role == StoredMessageRole.ToolResult && m.Blocks.OfType<ToolResultBlock>().Any());
        Assert.Equal(AgentJson.Serialize(cached.History), AgentJson.Serialize(rebuilt.History));
        Assert.Equal(AgentJson.Serialize(cached), AgentJson.Serialize(rebuilt));
    }

    [Fact]
    public async Task The_next_model_request_is_identical_whether_the_session_came_from_cache_or_from_the_stores()
    {
        using var warm = CreateHarness();
        using var cold = CreateHarness();
        var warmId = await RunTurnsAsync(warm, escalateAtTheEnd: false);
        var coldId = await RunTurnsAsync(cold, escalateAtTheEnd: false);
        cold.Cache.Clear();
        warm.Llm.EnqueueText("final");
        cold.Llm.EnqueueText("final");

        await warm.SendAsync(warmId, "one more question");
        await cold.SendAsync(coldId, "one more question");

        var warmRequest = warm.Llm.Requests[^1];
        var coldRequest = cold.Llm.Requests[^1];
        Assert.Equal(AgentJson.Serialize(warmRequest.Messages), AgentJson.Serialize(coldRequest.Messages));
        Assert.Equal(warmRequest.SystemStable, coldRequest.SystemStable);
        Assert.Equal(warmRequest.SystemVolatile, coldRequest.SystemVolatile);
        Assert.Contains(warmRequest.Messages, m => m.Content.OfType<ToolUseBlock>().Any());
        Assert.Contains("flight", coldRequest.SystemVolatile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_window_is_trimmed_in_chunks_and_never_starts_with_an_orphaned_tool_result()
    {
        using var harness = CreateHarness();
        var sessionId = await RunTurnsAsync(harness, escalateAtTheEnd: false);

        var session = await harness.Conversation.LoadAsync(sessionId, CancellationToken.None);

        Assert.True(session.History.Count <= 12);
        Assert.Equal(StoredMessageRole.User, session.History[0].Role);
        Assert.DoesNotContain(session.History[0].Blocks, b => b is ToolResultBlock);
        var all = await harness.StoredAsync(sessionId);
        Assert.True(all.Count > session.History.Count, "the full transcript stays in the message store");
    }
}
