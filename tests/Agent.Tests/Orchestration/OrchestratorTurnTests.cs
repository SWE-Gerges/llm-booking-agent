using Agent.Abstractions.Chat;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;
using Agent.Core.Language;
using Agent.Tests.Support;

namespace Agent.Tests.Orchestration;

public sealed class OrchestratorTurnTests
{
    private static readonly Localizer Text = new();

    [Fact]
    public async Task Iteration_cap_ends_the_turn_with_a_localized_fallback_and_keeps_tool_pairs_intact()
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo]);
        var iteration = 0;
        harness.Llm.Default = (_, _) => Task.FromResult(LlmResults.Tools(LlmResults.Use($"t{iteration++}", "echo")));
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "keep going");

        Assert.Equal(8, harness.Llm.CallCount);
        Assert.Equal(8, echo.Calls);
        Assert.Equal(Text.Get(MessageKey.TooManySteps, "en"), reply.Reply);
        Assert.Equal(ChatOutcome.Failed, reply.Outcome);

        var stored = await harness.StoredAsync(sessionId);
        Assert.Equal(1 + (8 * 2) + 1, stored.Count);
        Assert.Equal(StoredMessageRole.Assistant, stored[^1].Role);
        Assert.True(stored[^1].IsVisible);
        Assert.Equal(reply.Reply, stored[^1].VisibleText);
        for (var i = 0; i < 8; i++)
        {
            var call = stored[1 + (i * 2)];
            var result = stored[2 + (i * 2)];
            Assert.Equal(StoredMessageRole.ToolCall, call.Role);
            Assert.Equal(StoredMessageRole.ToolResult, result.Role);
            var use = Assert.Single(call.Blocks.OfType<ToolUseBlock>());
            Assert.Equal(use.Id, Assert.Single(result.Blocks.OfType<ToolResultBlock>()).ToolUseId);
        }
    }

    [Fact]
    public async Task Turn_deadline_stops_a_stuck_model_call_with_a_timeout_apology_and_persists_history()
    {
        using var harness = OrchestratorHarness.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Llm.Enqueue(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return LlmResults.Text("never");
        });
        var sessionId = await harness.StartAsync();

        var turn = harness.SendAsync(sessionId, "hello");
        await started.Task;
        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        var reply = await turn;

        Assert.Equal(Text.Get(MessageKey.TurnTimeout, "en"), reply.Reply);
        Assert.Equal(ChatOutcome.Failed, reply.Outcome);
        var stored = await harness.StoredAsync(sessionId);
        Assert.Equal([StoredMessageRole.User, StoredMessageRole.Assistant], stored.Select(m => m.Role));
    }

    [Fact]
    public async Task Caller_abort_propagates_and_still_persists_the_completed_work()
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo]);
        using var caller = new CancellationTokenSource();
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "echo"))
            .Enqueue(async (_, ct) =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                return LlmResults.Text("never");
            });
        var sessionId = await harness.StartAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SendAsync(sessionId, "go", caller.Token));

        var stored = await harness.StoredAsync(sessionId);
        Assert.Equal([StoredMessageRole.User, StoredMessageRole.ToolCall, StoredMessageRole.ToolResult], stored.Select(m => m.Role));
        Assert.Equal(1, echo.Calls);
    }

    [Fact]
    public async Task Cancellation_that_the_caller_did_not_request_is_an_llm_failure_not_an_abort()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueThrow(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "hello");

        Assert.Equal(Text.Get(MessageKey.LlmFailure, "en"), reply.Reply);
        Assert.Equal(ChatOutcome.Failed, reply.Outcome);
    }

    [Fact]
    public async Task A_truncated_reply_gets_the_fallback_message_instead_of_the_partial_text()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.Enqueue(LlmResults.Mixed([new TextBlock("The answer is par")], LlmStopReason.MaxTokens));
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "hello");

        Assert.Equal(Text.Get(MessageKey.TruncatedReply, "en"), reply.Reply);
        var stored = await harness.StoredAsync(sessionId);
        Assert.DoesNotContain("The answer is par", stored[^1].VisibleText, StringComparison.Ordinal);
        Assert.Equal(reply.Reply, stored[^1].VisibleText);
    }

    [Fact]
    public async Task A_blank_reply_gets_the_fallback_message()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("   \n ");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "hello");

        Assert.Equal(Text.Get(MessageKey.EmptyReply, "en"), reply.Reply);
        Assert.Equal(ChatOutcome.Failed, reply.Outcome);
    }

    [Fact]
    public async Task Llm_failure_keeps_history_and_a_client_retry_does_not_repeat_the_tool()
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "echo"))
            .EnqueueThrow(new LlmApiException("provider down", 503))
            .EnqueueText("All good now.");
        var sessionId = await harness.StartAsync();

        var failed = await harness.SendAsync(sessionId, "do the thing");
        var stored = await harness.StoredAsync(sessionId);

        Assert.Equal(Text.Get(MessageKey.LlmFailure, "en"), failed.Reply);
        Assert.Equal(
            [StoredMessageRole.User, StoredMessageRole.ToolCall, StoredMessageRole.ToolResult, StoredMessageRole.Assistant],
            stored.Select(m => m.Role));

        var retried = await harness.SendAsync(sessionId, "do the thing");

        Assert.Equal("All good now.", retried.Reply);
        Assert.Equal(1, echo.Calls);
        var retryRequest = harness.Llm.Requests[^1];
        Assert.Contains(retryRequest.Messages, m => m.Content.OfType<ToolUseBlock>().Any(b => b.Id == "t1"));
        Assert.Contains(retryRequest.Messages, m => m.Content.OfType<ToolResultBlock>().Any(b => b.ToolUseId == "t1"));
    }

    [Fact]
    public async Task Concurrent_message_in_the_same_session_is_refused_while_a_turn_runs()
    {
        using var harness = OrchestratorHarness.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Llm.Enqueue(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return LlmResults.Text("first done");
        });
        var sessionId = await harness.StartAsync();

        var first = harness.SendAsync(sessionId, "first");
        await started.Task;

        await Assert.ThrowsAsync<TurnInProgressException>(() => harness.SendAsync(sessionId, "second"));

        release.SetResult();
        Assert.Equal("first done", (await first).Reply);
        Assert.Equal(1, harness.Llm.CallCount);
    }

    [Fact]
    public async Task Escalated_session_answers_with_the_canned_message_and_never_calls_the_model()
    {
        using var harness = OrchestratorHarness.Create();
        var sessionId = await harness.StartAsync();
        var session = await harness.Conversation.LoadAsync(sessionId, CancellationToken.None);
        session.State = SessionState.Escalated;
        await harness.Conversation.SaveAsync(session, CancellationToken.None);

        var reply = await harness.SendAsync(sessionId, "are you there?");

        Assert.Equal(Text.Get(MessageKey.Escalated, "en"), reply.Reply);
        Assert.Equal(ChatOutcome.Escalated, reply.Outcome);
        Assert.Equal(0, harness.Llm.CallCount);
        var stored = await harness.StoredAsync(sessionId);
        Assert.Equal([StoredMessageRole.User, StoredMessageRole.Assistant], stored.Select(m => m.Role));
    }

    [Fact]
    public async Task Arabic_message_gets_an_arabic_locale_rtl_direction_and_arabic_canned_text()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("أهلاً بك").EnqueueThrow(new LlmApiException("down", 500));
        var sessionId = await harness.StartAsync("en");

        var ok = await harness.SendAsync(sessionId, "مرحبا أريد حجز رحلة");
        var failed = await harness.SendAsync(sessionId, "وماذا عن الفنادق");

        Assert.Equal("ar", ok.Locale);
        Assert.Equal("rtl", ok.Direction);
        Assert.Equal("ar", failed.Locale);
        Assert.Equal(Text.Get(MessageKey.LlmFailure, "ar"), failed.Reply);
        Assert.Contains(failed.Reply, c => c is >= '؀' and <= 'ۿ');
    }

    [Fact]
    public async Task English_message_gets_ltr_direction()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("Hello!");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "hello there");

        Assert.Equal("en", reply.Locale);
        Assert.Equal("ltr", reply.Direction);
    }

    [Fact]
    public async Task Idle_session_expires_using_the_clock()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("hi");
        var sessionId = await harness.StartAsync();
        await harness.SendAsync(sessionId, "hello");

        harness.Clock.Advance(TimeSpan.FromMinutes(31));

        await Assert.ThrowsAsync<SessionExpiredException>(() => harness.SendAsync(sessionId, "still there?"));
    }

    [Fact]
    public async Task Idle_expiry_is_also_enforced_after_a_cache_miss()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("hi");
        var sessionId = await harness.StartAsync();
        await harness.SendAsync(sessionId, "hello");

        harness.Cache.Clear();
        harness.Clock.Advance(TimeSpan.FromMinutes(31));

        await Assert.ThrowsAsync<SessionExpiredException>(() => harness.SendAsync(sessionId, "still there?"));
    }

    [Fact]
    public async Task Activity_inside_the_idle_window_keeps_the_session_alive()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.Default = (_, _) => Task.FromResult(LlmResults.Text("ok"));
        var sessionId = await harness.StartAsync();

        for (var i = 0; i < 4; i++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(20));
            await harness.SendAsync(sessionId, $"message {i}");
        }

        Assert.Equal(4, harness.Llm.CallCount);
    }

    [Fact]
    public async Task Unknown_session_is_reported_as_not_found()
    {
        using var harness = OrchestratorHarness.Create();

        await Assert.ThrowsAsync<SessionNotFoundException>(() => harness.SendAsync("does-not-exist", "hi"));
    }

    [Fact]
    public async Task The_request_sent_to_the_model_carries_stable_system_tools_and_history()
    {
        using var harness = OrchestratorHarness.Create([new FakeTool("zeta"), new FakeTool("alpha")]);
        harness.Llm.EnqueueText("hi");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "hello");

        var request = harness.Llm.Requests.Single();
        Assert.Equal(["alpha", "zeta"], request.Tools.Select(t => t.Name));
        Assert.Equal(sessionId, request.ConversationId);
        Assert.Equal(1024, request.MaxTokens);
        Assert.Null(request.Temperature);
        var only = Assert.Single(request.Messages);
        Assert.Equal(LlmRole.User, only.Role);
        Assert.Equal("hello", Assert.IsType<TextBlock>(Assert.Single(only.Content)).Text);
        Assert.Contains("untrusted data", request.SystemStable, StringComparison.Ordinal);
    }
}
