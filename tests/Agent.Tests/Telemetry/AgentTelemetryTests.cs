using Agent.Abstractions.Chat;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Guards;
using Agent.Core.Telemetry;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.Telemetry;

public sealed class AgentTelemetryTests
{
    private static (OrchestratorHarness Harness, AgentTelemetry Telemetry, MetricsCollector Metrics, SpanCollector Spans) Start(
        IEnumerable<FakeTool>? tools = null,
        Action<IServiceCollection>? configure = null)
    {
        var harness = OrchestratorHarness.Create(tools, configure: configure);
        var telemetry = harness.Get<AgentTelemetry>();
        return (harness, telemetry, new MetricsCollector(telemetry), new SpanCollector(telemetry));
    }

    private static IEnumerable<(string Tool, string Outcome)> ToolCalls(MetricsCollector metrics) =>
        metrics.For(AgentTelemetry.ToolCallsInstrument).Select(m => (MetricsCollector.Tag(m, "tool")!, MetricsCollector.Tag(m, "outcome")!));

    [Fact]
    public async Task Tool_calls_are_counted_by_tool_and_outcome_for_ok_error_invalid_and_unknown()
    {
        var ok = new FakeTool("fine");
        var failing = new FakeTool("fails", (_, _) => ToolResult.Error("nope"));
        var (harness, _, metrics, _) = Start([ok, failing]);
        using var _ = harness;
        using var __ = metrics;
        harness.Llm
            .EnqueueTools(
                LlmResults.Use("t1", "fine", """{"q":"x"}"""),
                LlmResults.Use("t2", "fails"),
                LlmResults.Use("t3", "fine", """{"q":5}"""),
                LlmResults.Use("t4", "model-invented-name"))
            .EnqueueText("done");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        Assert.Equal(
            [("fine", "ok"), ("fails", "error"), ("fine", "invalid"), ("unknown", "unknown")],
            ToolCalls(metrics));
    }

    [Fact]
    public async Task A_policy_denial_is_counted_as_denied()
    {
        var tool = new FakeTool("guarded_tool");
        var (harness, _, metrics, _) = Start([tool], services => services.AddSingleton<IToolCallPolicy>(new DenyPolicy("not now")));
        using var _ = harness;
        using var __ = metrics;
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "guarded_tool")).EnqueueText("ok");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        Assert.Equal([("guarded_tool", "denied")], ToolCalls(metrics));
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task The_unknown_tool_tag_never_carries_the_model_supplied_name()
    {
        var (harness, _, metrics, spans) = Start();
        using var _ = harness;
        using var __ = metrics;
        using var ___ = spans;
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "jane.doe@example.com")).EnqueueText("done");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        Assert.All(metrics.All, m => Assert.DoesNotContain("jane.doe", string.Join(",", m.Tags.Values.Select(v => v?.ToString())), StringComparison.Ordinal));
        Assert.All(spans.All, s => Assert.DoesNotContain("jane.doe", string.Join(",", s.TagObjects.Select(t => t.Value?.ToString())), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turns_are_counted_by_outcome()
    {
        var escalate = new FakeTool("escalate", (ctx, _) =>
        {
            ctx.Session.State = SessionState.Escalated;
            return ToolResult.Ok("""{"ok":true}""");
        });
        var (harness, _, metrics, _) = Start([escalate]);
        using var _ = harness;
        using var __ = metrics;

        var completed = await harness.StartAsync();
        harness.Llm.EnqueueText("hello");
        await harness.SendAsync(completed, "hi");

        var failed = await harness.StartAsync();
        harness.Llm.EnqueueThrow(new InvalidOperationException("model down"));
        await harness.SendAsync(failed, "hi");

        var guarded = await harness.StartAsync();
        await harness.SendAsync(guarded, "my card is 4111 1111 1111 1111");

        var limited = await harness.StartAsync();
        harness.RateLimiter.Decision = new RateLimitDecision(false, TimeSpan.FromSeconds(5));
        await harness.SendAsync(limited, "hi");
        harness.RateLimiter.Decision = RateLimitDecision.Allowed;

        var escalated = await harness.StartAsync();
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "escalate")).EnqueueText("handing over");
        await harness.SendAsync(escalated, "need a person");
        await harness.SendAsync(escalated, "anyone?");

        var aborted = await harness.StartAsync();
        using var caller = new CancellationTokenSource();
        harness.Llm.Enqueue(async (_, ct) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return LlmResults.Text("never");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SendAsync(aborted, "hi", caller.Token));

        var outcomes = metrics.For(AgentTelemetry.TurnsInstrument).Select(m => MetricsCollector.Tag(m, "outcome")).Order().ToList();
        Assert.Equal(
            ["aborted", "completed", "completed", "escalated", "failed", "guarded", "rate_limited"],
            outcomes);
    }

    [Fact]
    public async Task Guard_hits_and_session_rate_limit_rejections_are_counted_by_guard_and_source()
    {
        var (harness, _, metrics, _) = Start();
        using var _ = harness;
        using var __ = metrics;
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "iban DE89 3704 0044 0532 0130 00");
        harness.RateLimiter.Decision = new RateLimitDecision(false, TimeSpan.FromSeconds(1));
        await harness.SendAsync(sessionId, "hello");
        harness.RateLimiter.Decision = RateLimitDecision.Allowed;
        for (var i = 0; i < 4; i++)
        {
            harness.Llm.EnqueueText("again");
            await harness.SendAsync(sessionId, "same message");
        }

        var guards = metrics.For(AgentTelemetry.GuardHitsInstrument).Select(m => MetricsCollector.Tag(m, "guard")).ToList();
        Assert.Equal(["sensitive_data", "session_rate_limit", "repeated_message"], guards);
        var rejections = metrics.For(AgentTelemetry.RateLimitRejectionsInstrument);
        var rejection = Assert.Single(rejections);
        Assert.Equal("session", MetricsCollector.Tag(rejection, "source"));
    }

    [Fact]
    public async Task Llm_latency_and_token_counters_come_from_the_provider_usage()
    {
        var (harness, _, metrics, _) = Start();
        using var _ = harness;
        using var __ = metrics;
        harness.Llm
            .Enqueue(async (_, _) =>
            {
                harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
                await Task.Yield();
                return new LlmChatResult([new TextBlock("hello")], LlmStopReason.EndTurn, new LlmUsage(100, 20, 70, 30));
            })
            .Enqueue((_, _) =>
            {
                harness.Clock.Advance(TimeSpan.FromMilliseconds(40));
                throw new InvalidOperationException("provider failure");
            });
        var first = await harness.StartAsync();
        var second = await harness.StartAsync();

        await harness.SendAsync(first, "hi");
        await harness.SendAsync(second, "hi");

        var latency = metrics.For(AgentTelemetry.LlmLatencyInstrument);
        Assert.Equal(2, latency.Count);
        Assert.Equal(250, latency[0].Value, 3);
        Assert.Equal("ok", MetricsCollector.Tag(latency[0], "outcome"));
        Assert.Equal(40, latency[1].Value, 3);
        Assert.Equal("error", MetricsCollector.Tag(latency[1], "outcome"));
        Assert.Equal(100, Assert.Single(metrics.For(AgentTelemetry.LlmInputTokensInstrument)).Value);
        Assert.Equal(20, Assert.Single(metrics.For(AgentTelemetry.LlmOutputTokensInstrument)).Value);
        Assert.Equal(70, Assert.Single(metrics.For(AgentTelemetry.LlmCacheReadTokensInstrument)).Value);
        Assert.Equal(30, Assert.Single(metrics.For(AgentTelemetry.LlmCacheWriteTokensInstrument)).Value);
    }

    [Fact]
    public async Task Tool_call_spans_are_children_of_the_turn_span_and_carry_tool_outcome_and_iteration()
    {
        var tool = new FakeTool("echo");
        var (harness, _, metrics, spans) = Start([tool]);
        using var _ = harness;
        using var __ = metrics;
        using var ___ = spans;
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "echo"))
            .EnqueueTools(LlmResults.Use("t2", "echo", """{"q":1}"""))
            .EnqueueText("done");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var turn = Assert.Single(spans.All, s => s.OperationName == AgentTelemetry.TurnSpan);
        var tools = spans.All.Where(s => s.OperationName == AgentTelemetry.ToolCallSpan).ToList();
        Assert.Equal(2, tools.Count);
        Assert.All(tools, s => Assert.Equal(turn.Id, s.ParentId));
        Assert.Equal(["0", "1"], tools.Select(s => SpanCollector.Tag(s, "iteration")));
        Assert.Equal(["ok", "invalid"], tools.Select(s => SpanCollector.Tag(s, "outcome")));
        Assert.All(tools, s => Assert.Equal("echo", SpanCollector.Tag(s, "tool")));
        Assert.Equal("completed", SpanCollector.Tag(turn, "outcome"));
    }

    [Fact]
    public async Task No_message_text_tool_input_or_personal_data_appears_in_any_tag()
    {
        var tool = new FakeTool("echo", (_, _) => ToolResult.Ok("""{"email":"hidden.person@example.com"}"""));
        var (harness, _, metrics, spans) = Start([tool]);
        using var _ = harness;
        using var __ = metrics;
        using var ___ = spans;
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "echo", """{"q":"tool-input-secret-value"}"""))
            .EnqueueText("reply containing hidden.person@example.com");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "my name is Zebediah Quillfeather and my phone is +15555550100");

        var allowedKeys = new HashSet<string> { "outcome", "tool", "guard", "source", "iteration" };
        var tagKeys = metrics.All.SelectMany(m => m.Tags.Keys).Concat(spans.All.SelectMany(s => s.TagObjects.Select(t => t.Key))).ToList();
        Assert.All(tagKeys, key => Assert.Contains(key, allowedKeys));

        var tagValues = metrics.All.SelectMany(m => m.Tags.Values).Concat(spans.All.SelectMany(s => s.TagObjects.Select(t => t.Value)))
            .Select(v => v?.ToString() ?? string.Empty)
            .ToList();
        foreach (var forbidden in new[] { "Zebediah", "Quillfeather", "5555550100", "tool-input-secret-value", "hidden.person", "reply containing" })
        {
            Assert.DoesNotContain(tagValues, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(typeof(SensitiveDataGuard), "sensitive_data")]
    [InlineData(typeof(RepeatedMessageGuard), "repeated_message")]
    [InlineData(typeof(SessionRateLimitGuard), "session_rate_limit")]
    public void Guard_names_are_derived_from_the_registered_guard_types(Type guardType, string expected) =>
        Assert.Equal(expected, AgentTelemetry.GuardName(guardType));

    [Fact]
    public void Telemetry_exposes_one_meter_and_one_activity_source_with_the_documented_name()
    {
        using var telemetry = new AgentTelemetry();

        Assert.Equal("LlmBookingAgent", telemetry.Meter.Name);
        Assert.Equal("LlmBookingAgent", telemetry.ActivitySource.Name);
    }

    [Fact]
    public async Task A_form_submission_style_turn_conflict_still_throws_and_records_no_turn()
    {
        var (harness, _, metrics, _) = Start();
        using var _ = harness;
        using var __ = metrics;
        var sessionId = await harness.StartAsync();
        var gate = harness.Get<ITurnGate>();
        await using var held = await gate.TryEnterAsync(sessionId, CancellationToken.None);

        await Assert.ThrowsAsync<TurnInProgressException>(() => harness.SendAsync(sessionId, "second"));

        Assert.Empty(metrics.For(AgentTelemetry.TurnsInstrument));
    }
}
