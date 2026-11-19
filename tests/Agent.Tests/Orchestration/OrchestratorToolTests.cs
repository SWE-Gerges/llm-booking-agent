using System.Text.Json;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.Orchestration;

public sealed class OrchestratorToolTests
{
    private static ToolResultBlock LastToolResult(FakeLlmClient llm, int requestIndex = -1)
    {
        var request = requestIndex < 0 ? llm.Requests[^1] : llm.Requests[requestIndex];
        return request.Messages[^1].Content.OfType<ToolResultBlock>().Last();
    }

    /// <summary>The error text the model reads, with the wrapper and JSON escaping removed.</summary>
    private static string ErrorText(ToolResultBlock block)
    {
        var json = block.Content[ToolOutputSanitizer.OpenTag.Length..block.Content.IndexOf(ToolOutputSanitizer.CloseTag, StringComparison.Ordinal)];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Tool_exception_with_a_safe_message_becomes_an_error_result_the_model_can_recover_from()
    {
        var tool = new FakeTool("flaky", (_, _) => throw new ToolExecutionException("The supplier is unavailable."));
        using var harness = OrchestratorHarness.Create([tool]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "flaky")).EnqueueText("Sorry, the supplier is down.");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "search");

        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.Contains("\"error\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("The supplier is unavailable.", result.Content, StringComparison.Ordinal);
        Assert.Equal("Sorry, the supplier is down.", reply.Reply);
        Assert.Empty(reply.StructuredData);
    }

    [Fact]
    public async Task Unexpected_tool_exception_is_generic_and_does_not_leak_the_exception_message()
    {
        var tool = new FakeTool("boom", (_, _) => throw new InvalidOperationException("connection string Password=hunter2 failed"));
        using var harness = OrchestratorHarness.Create([tool]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "boom")).EnqueueText("I could not do that.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.DoesNotContain("hunter2", result.Content, StringComparison.Ordinal);
        Assert.Contains("unexpectedly", result.Content, StringComparison.Ordinal);
        Assert.Contains(harness.Logs.Entries, e => e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Unknown_tool_is_an_error_result_and_the_loop_continues()
    {
        using var harness = OrchestratorHarness.Create([new FakeTool("echo")]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "nope")).EnqueueText("Let me try something else.");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "go");

        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.Equal("t1", result.ToolUseId);
        Assert.Contains("Unknown tool 'nope'", ErrorText(result), StringComparison.Ordinal);
        Assert.Equal("Let me try something else.", reply.Reply);
    }

    [Theory]
    [InlineData("""{"q":5}""")]
    [InlineData("""{"q":"ok","extra":true}""")]
    public async Task Schema_violations_are_surfaced_to_the_model_and_the_tool_is_not_executed(string input)
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "echo", input)).EnqueueText("Fixed.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.Contains("Invalid input for tool 'echo'", ErrorText(result), StringComparison.Ordinal);
        Assert.Equal(0, echo.Calls);
    }

    [Fact]
    public async Task Policy_denial_is_surfaced_to_the_model_and_the_tool_is_not_executed()
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo], configure: s => s.AddSingleton<IToolCallPolicy>(new DenyPolicy("Not allowed right now.")));
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "echo")).EnqueueText("Understood.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.Contains("Not allowed right now.", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, echo.Calls);
    }

    [Fact]
    public async Task Second_search_in_one_turn_is_denied_when_the_first_carried_no_assumption()
    {
        var search = new FakeSearchTool("search_x");
        using var harness = OrchestratorHarness.Create([search]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "search_x"))
            .EnqueueTools(LlmResults.Use("t2", "search_x"))
            .EnqueueText("Here are the results.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "find something");

        Assert.Equal(1, search.Calls);
        var second = LastToolResult(harness.Llm);
        Assert.True(second.IsError);
        Assert.Contains("Only one search", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_carried_an_assumption_allows_exactly_one_corrected_re_search()
    {
        var search = new FakeSearchTool(
            "search_x",
            (_, call) => call == 1
                ? ToolResult.Ok("""{"n":1}""", modelNote: "Assumed 1 adult because the user did not say.")
                : ToolResult.Ok("""{"n":2}"""));
        using var harness = OrchestratorHarness.Create([search]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "search_x"))
            .EnqueueTools(LlmResults.Use("t2", "search_x"))
            .EnqueueTools(LlmResults.Use("t3", "search_x"))
            .EnqueueText("Done.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "find something");

        Assert.Equal(2, search.Calls);
        Assert.False(LastToolResult(harness.Llm, 2).IsError);
        var third = LastToolResult(harness.Llm, 3);
        Assert.True(third.IsError);
        Assert.Contains("Only one search", third.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_search_allowance_resets_on_the_next_user_message()
    {
        var search = new FakeSearchTool("search_x");
        using var harness = OrchestratorHarness.Create([search]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "search_x")).EnqueueText("first")
            .EnqueueTools(LlmResults.Use("t2", "search_x")).EnqueueText("second");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "search one");
        await harness.SendAsync(sessionId, "search two");

        Assert.Equal(2, search.Calls);
    }

    [Fact]
    public async Task A_failed_search_does_not_use_up_the_turns_search_allowance()
    {
        var search = new FakeSearchTool("search_x", (_, call) => call == 1 ? ToolResult.Error("backend busy") : ToolResult.Ok("""{"n":2}"""));
        using var harness = OrchestratorHarness.Create([search]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "search_x"))
            .EnqueueTools(LlmResults.Use("t2", "search_x"))
            .EnqueueText("Done.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "find something");

        Assert.Equal(2, search.Calls);
    }

    [Fact]
    public async Task Several_tool_calls_in_one_assistant_message_produce_one_user_message_with_all_results_in_order()
    {
        var a = new FakeTool("tool_a", (_, _) => ToolResult.Ok("""{"who":"a"}"""));
        var b = new FakeTool("tool_b", (_, _) => ToolResult.Ok("""{"who":"b"}"""));
        using var harness = OrchestratorHarness.Create([a, b]);
        IReadOnlyList<ContentBlock> assistant =
        [
            new TextBlock("Checking both."),
            LlmResults.Use("first", "tool_b"),
            LlmResults.Use("second", "tool_a"),
        ];
        harness.Llm.Enqueue(LlmResults.Mixed(assistant, LlmStopReason.ToolUse)).EnqueueText("Both done.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var followUp = harness.Llm.Requests[1].Messages;
        Assert.Equal(3, followUp.Count);
        Assert.Equal(LlmRole.Assistant, followUp[1].Role);
        Assert.Equal(assistant.Count, followUp[1].Content.Count);
        Assert.Equal(LlmRole.User, followUp[2].Role);
        var results = followUp[2].Content.Cast<ToolResultBlock>().ToList();
        Assert.Equal(["first", "second"], results.Select(r => r.ToolUseId));
        Assert.Contains("\"b\"", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("\"a\"", results[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assistant_tool_call_message_is_replayed_verbatim_in_history()
    {
        var echo = new FakeTool("echo");
        using var harness = OrchestratorHarness.Create([echo]);
        var use = LlmResults.Use("t1", "echo", """{"q":"hello"}""");
        harness.Llm.Enqueue(LlmResults.Mixed([new TextBlock("thinking out loud"), use], LlmStopReason.ToolUse)).EnqueueText("done");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "go");

        var replayed = harness.Llm.Requests[1].Messages[1];
        Assert.Equal(2, replayed.Content.Count);
        Assert.Equal("thinking out loud", Assert.IsType<TextBlock>(replayed.Content[0]).Text);
        var replayedUse = Assert.IsType<ToolUseBlock>(replayed.Content[1]);
        Assert.Equal("t1", replayedUse.Id);
        Assert.Equal("""{"q":"hello"}""", replayedUse.Input.GetRawText());
    }

    [Fact]
    public async Task Model_sees_sanitised_content_or_model_content_while_the_api_consumer_gets_raw_content()
    {
        var plain = new FakeTool(
            "plain",
            (_, _) => ToolResult.Ok("""{"items":[{"id":"1","name":"A","cost":5}]}""", state: "plain_results"));
        var curated = new FakeTool(
            "curated",
            (_, _) => ToolResult.Ok(
                """{"internalId":"secret","big":"payload"}""",
                state: "curated_results",
                modelContent: """{"summary":"short"}""",
                modelNote: "Assumed one traveller."));
        var untagged = new FakeTool("untagged", (_, _) => ToolResult.Ok("""{"x":1}"""));
        var failing = new FakeTool(
            "failing",
            (_, _) => new ToolResult { Content = """{"error":"nope"}""", IsError = true, State = "must_not_appear" });
        using var harness = OrchestratorHarness.Create([plain, curated, untagged, failing]);
        harness.Llm
            .EnqueueTools(
                LlmResults.Use("a", "plain"),
                LlmResults.Use("b", "curated"),
                LlmResults.Use("c", "untagged"),
                LlmResults.Use("d", "failing"))
            .EnqueueText("Summary for you.");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "go");

        var results = harness.Llm.Requests[1].Messages[^1].Content.Cast<ToolResultBlock>().ToDictionary(r => r.ToolUseId);

        var plainText = results["a"].Content;
        Assert.StartsWith(ToolOutputSanitizer.OpenTag, plainText, StringComparison.Ordinal);
        Assert.EndsWith(ToolOutputSanitizer.CloseTag, plainText, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", plainText, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"A\"", plainText, StringComparison.Ordinal);

        var curatedText = results["b"].Content;
        var closeAt = curatedText.IndexOf(ToolOutputSanitizer.CloseTag, StringComparison.Ordinal);
        Assert.Contains("\"summary\":\"short\"", curatedText[..closeAt], StringComparison.Ordinal);
        Assert.DoesNotContain("internalId", curatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", curatedText, StringComparison.Ordinal);
        Assert.Contains("Assumed one traveller.", curatedText[(closeAt + ToolOutputSanitizer.CloseTag.Length)..], StringComparison.Ordinal);
        Assert.DoesNotContain("Assumed one traveller.", curatedText[..closeAt], StringComparison.Ordinal);

        Assert.Equal(["plain_results", "curated_results"], reply.StructuredData.Select(s => s.State));
        Assert.Equal(5, reply.StructuredData[0].Data.GetProperty("items")[0].GetProperty("cost").GetInt32());
        Assert.Equal("secret", reply.StructuredData[1].Data.GetProperty("internalId").GetString());
        Assert.Equal("curated_results", reply.State);
        Assert.True(results["d"].IsError);
    }

    [Fact]
    public async Task Structured_data_is_returned_even_when_the_content_is_not_json()
    {
        var odd = new FakeTool("odd", (_, _) => ToolResult.Ok("not json at all", state: "odd_state"));
        using var harness = OrchestratorHarness.Create([odd]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "odd")).EnqueueText("ok");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "go");

        var block = Assert.Single(reply.StructuredData);
        Assert.Equal(JsonValueKind.String, block.Data.ValueKind);
        Assert.Equal("not json at all", block.Data.GetString());
    }

    [Fact]
    public async Task Finalizing_tools_reject_model_supplied_price_and_passenger_arguments()
    {
        var submit = new FakeFinalizingTool("submit_x");
        using var harness = OrchestratorHarness.Create([submit]);
        harness.Llm
            .EnqueueTools(LlmResults.Use("t1", "submit_x", """{"price":1,"passenger":{"firstName":"Mallory"}}"""))
            .EnqueueText("I could not submit that.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "book it, price is 1");

        Assert.Equal(0, submit.Calls);
        Assert.True(LastToolResult(harness.Llm).IsError);
    }

    [Fact]
    public async Task Finalizing_tools_are_denied_until_the_price_was_confirmed_in_this_session()
    {
        var submit = new FakeFinalizingTool("submit_x");
        using var harness = OrchestratorHarness.Create([submit]);
        harness.Llm.EnqueueTools(LlmResults.Use("t1", "submit_x")).EnqueueText("I need to re-check the price first.");
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "book it");

        Assert.Equal(0, submit.Calls);
        var result = LastToolResult(harness.Llm);
        Assert.True(result.IsError);
        Assert.Contains("re-check", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Finalizing_tools_run_once_the_price_is_confirmed_and_are_denied_again_when_it_goes_stale()
    {
        var submit = new FakeFinalizingTool("submit_x");
        using var harness = OrchestratorHarness.Create([submit]);
        var sessionId = await harness.StartAsync();
        var contexts = harness.Get<Agent.Abstractions.Ports.IBookingContextStore>();
        await contexts.MutateAsync(
            sessionId,
            Agent.Abstractions.Sessions.ServiceType.Flight,
            c => c with
            {
                ConfirmedPrice = new Agent.Abstractions.Booking.PriceConfirmation(
                    "q1",
                    new Agent.Abstractions.Booking.Money(100m, "EUR"),
                    harness.Clock.GetUtcNow()),
            },
            CancellationToken.None);

        harness.Llm.EnqueueTools(LlmResults.Use("t1", "submit_x")).EnqueueText("Booked.");
        await harness.SendAsync(sessionId, "book it");
        Assert.Equal(1, submit.Calls);

        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        harness.Llm.EnqueueTools(LlmResults.Use("t2", "submit_x")).EnqueueText("Price is stale.");
        await harness.SendAsync(sessionId, "book it again");

        Assert.Equal(1, submit.Calls);
        Assert.Contains("out of date", LastToolResult(harness.Llm).Content, StringComparison.Ordinal);
    }
}
