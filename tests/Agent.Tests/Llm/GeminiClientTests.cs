using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Llm.Gemini;

namespace Agent.Tests.LlmClients;

public sealed class GeminiRequestTests
{
    private static void AssertSameJson(JsonNode expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:\n{expected.ToJsonString()}\nActual:\n{actual.ToJsonString()}");

    [Fact]
    public void A_basic_request_matches_the_golden_body()
    {
        var body = GeminiMapper.BuildRequest(LlmFixtures.BasicRequest());

        AssertSameJson(LlmFixtures.Node("gemini/request-basic.json"), body);
    }

    [Fact]
    public void Temperature_is_sent_only_when_set()
    {
        var with = GeminiMapper.BuildRequest(LlmFixtures.BasicRequest(0.2));
        var without = GeminiMapper.BuildRequest(LlmFixtures.BasicRequest());

        AssertSameJson(LlmFixtures.Node("gemini/request-basic-temperature.json"), with);
        Assert.Null(without["generationConfig"]!["temperature"]);
    }

    [Fact]
    public void History_with_parallel_calls_results_and_consecutive_user_messages_matches_the_golden_body()
    {
        var body = GeminiMapper.BuildRequest(LlmFixtures.HistoryRequest());

        AssertSameJson(LlmFixtures.Node("gemini/request-history.json"), body);
    }

    [Fact]
    public void System_text_is_the_stable_block_followed_by_the_volatile_block()
    {
        var body = GeminiMapper.BuildRequest(LlmFixtures.BasicRequest());

        Assert.Equal(
            "You are a booking assistant.\n\nToday is 2030-01-02.",
            body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void No_system_instruction_or_tools_are_sent_when_there_are_none()
    {
        var request = new LlmChatRequest { SystemStable = " ", SystemVolatile = string.Empty, Messages = [LlmMessage.User("hi")] };

        var body = GeminiMapper.BuildRequest(request);

        Assert.Null(body["systemInstruction"]);
        Assert.Null(body["tools"]);
    }

    [Fact]
    public void A_tool_without_parameters_is_declared_without_a_parameters_object()
    {
        var body = GeminiMapper.BuildRequest(LlmFixtures.BasicRequest());

        var declarations = body["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.Null(declarations[1]!["parameters"]);
        Assert.NotNull(declarations[0]!["parameters"]);
    }

    [Fact]
    public void An_empty_message_gets_a_placeholder_part_because_empty_parts_are_rejected()
    {
        var request = new LlmChatRequest
        {
            SystemStable = "s",
            SystemVolatile = "v",
            Messages = [new LlmMessage(LlmRole.User, [new TextBlock("  ")])],
        };

        var body = GeminiMapper.BuildRequest(request);

        Assert.Equal("(no content)", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void An_unmatched_tool_result_falls_back_to_a_placeholder_function_name()
    {
        var request = new LlmChatRequest
        {
            SystemStable = "s",
            SystemVolatile = "v",
            Messages = [new LlmMessage(LlmRole.User, [new ToolResultBlock("missing", "{}")])],
        };

        var body = GeminiMapper.BuildRequest(request);

        Assert.Equal("unknown_tool", body["contents"]![0]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_client_posts_to_the_model_endpoint_with_the_key_in_a_header_and_never_in_the_url()
    {
        using var host = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));

        await host.Client.SendAsync(LlmFixtures.BasicRequest(), CancellationToken.None);

        var sent = Assert.Single(host.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/v1beta/models/test-model:generateContent", sent.Uri.AbsolutePath);
        Assert.Equal(string.Empty, sent.Uri.Query);
        Assert.Equal(ProviderTestHost.ApiKey, sent.Headers["x-goog-api-key"]);
        Assert.DoesNotContain(ProviderTestHost.ApiKey, sent.Uri.ToString(), StringComparison.Ordinal);
        Assert.Equal("application/json", sent.ContentType);
        AssertSameJson(LlmFixtures.Node("gemini/request-basic.json"), JsonNode.Parse(sent.Body!)!);
    }
}

public sealed class GeminiSchemaTests
{
    [Fact]
    public void Json_schema_is_reduced_to_the_accepted_subset()
    {
        var converted = GeminiMapper.ConvertSchema(AgentJson.Parse(LlmFixtures.SearchFlightsSchema));

        var expected = LlmFixtures.Node("gemini/request-basic.json")["tools"]![0]!["functionDeclarations"]![0]!["parameters"]!;
        Assert.True(JsonNode.DeepEquals(expected, converted), converted.ToJsonString());
    }

    [Fact]
    public void Unsupported_keywords_are_dropped()
    {
        var converted = GeminiMapper.ConvertSchema(AgentJson.Parse("""
            {"$schema":"x","$id":"y","title":"t","type":"object","additionalProperties":false,
             "properties":{"a":{"type":"integer","exclusiveMinimum":0,"exclusiveMaximum":10,"default":5,"examples":[1]},
                           "b":{"anyOf":[{"type":"string"},{"type":"integer"}]}}}
            """)).ToJsonString();

        foreach (var keyword in new[] { "$schema", "$id", "title", "additionalProperties", "exclusiveMinimum", "exclusiveMaximum", "default", "examples", "anyOf" })
        {
            Assert.DoesNotContain(keyword, converted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_const_becomes_a_single_value_enum()
    {
        var converted = GeminiMapper.ConvertSchema(AgentJson.Parse("""{"type":"string","const":"fixed"}"""));

        Assert.Equal("fixed", converted["enum"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_nullable_union_keeps_the_concrete_type_and_marks_it_nullable()
    {
        var converted = GeminiMapper.ConvertSchema(AgentJson.Parse("""{"type":["null","integer"]}"""));

        Assert.Equal("INTEGER", converted["type"]!.GetValue<string>());
        Assert.True(converted["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Only_formats_the_api_knows_are_kept()
    {
        var kept = GeminiMapper.ConvertSchema(AgentJson.Parse("""{"type":"string","format":"date-time"}"""));
        var dropped = GeminiMapper.ConvertSchema(AgentJson.Parse("""{"type":"string","format":"email"}"""));

        Assert.Equal("date-time", kept["format"]!.GetValue<string>());
        Assert.Null(dropped["format"]);
    }
}

public sealed class GeminiResponseTests
{
    [Fact]
    public void Text_parts_are_joined_and_usage_is_mapped()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-text.json"));

        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal(new LlmUsage(25, 7, 0, 0), result.Usage);
    }

    [Fact]
    public void Parallel_function_calls_become_tool_use_blocks_in_order_with_text_first()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-parallel-calls.json"));

        Assert.Equal(LlmStopReason.ToolUse, result.StopReason);
        Assert.Equal("Let me check.", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        var first = Assert.IsType<ToolUseBlock>(result.Blocks[1]);
        var second = Assert.IsType<ToolUseBlock>(result.Blocks[2]);
        Assert.Equal("search_flights", first.Name);
        Assert.Equal(2, first.Input.GetProperty("adults").GetInt32());
        Assert.Equal("get_booking_status", second.Name);
        Assert.Equal(JsonValueKind.Object, second.Input.ValueKind);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Cached_and_thinking_tokens_are_reflected_in_usage()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-parallel-calls.json"));

        // prompt 120 includes 80 cached tokens; output counts the candidate tokens plus the 12 thinking tokens.
        Assert.Equal(new LlmUsage(40, 42, 80, 0), result.Usage);
    }

    [Fact]
    public void Synthesised_tool_ids_are_stable_across_parses_and_distinct_between_responses()
    {
        var body = LlmFixtures.Read("gemini/response-parallel-calls.json");

        var a = GeminiMapper.ParseResponse(body).Blocks.OfType<ToolUseBlock>().Select(b => b.Id).ToList();
        var b = GeminiMapper.ParseResponse(body).Blocks.OfType<ToolUseBlock>().Select(x => x.Id).ToList();
        var other = GeminiMapper.ParseResponse(body.Replace("resp-calls-1", "resp-calls-2", StringComparison.Ordinal)).Blocks.OfType<ToolUseBlock>().Select(x => x.Id).ToList();

        Assert.Equal(a, b);
        Assert.All(a, id => Assert.StartsWith("gemini-", id, StringComparison.Ordinal));
        Assert.Empty(a.Intersect(other));
    }

    [Fact]
    public void An_id_supplied_by_the_provider_is_used_as_is()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-provider-ids.json"));

        Assert.Equal("fc-77", Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks)).Id);
    }

    [Fact]
    public void A_truncated_answer_maps_to_max_tokens()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-max-tokens.json"));

        Assert.Equal(LlmStopReason.MaxTokens, result.StopReason);
        Assert.Equal("A partial ans", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
    }

    [Fact]
    public void Thought_parts_are_not_shown_to_the_user()
    {
        var result = GeminiMapper.ParseResponse(LlmFixtures.Read("gemini/response-thought-part.json"));

        Assert.Equal("Visible answer", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
    }

    [Theory]
    [InlineData("gemini/response-blocked-prompt.json")]
    [InlineData("gemini/response-safety-finish.json")]
    [InlineData("gemini/response-recitation-finish.json")]
    public void Blocked_prompts_and_safety_finishes_are_classified_as_blocked(string fixture)
    {
        var error = Assert.Throws<LlmApiException>(() => GeminiMapper.ParseResponse(LlmFixtures.Read(fixture)));

        Assert.Equal(LlmErrorKind.Blocked, error.Kind);
    }

    [Theory]
    [InlineData("gemini/response-no-candidates.json")]
    [InlineData("[]")]
    public void A_response_without_candidates_is_permanent(string bodyOrFixture)
    {
        var body = bodyOrFixture.EndsWith(".json", StringComparison.Ordinal) ? LlmFixtures.Read(bodyOrFixture) : bodyOrFixture;

        var error = Assert.Throws<LlmApiException>(() => GeminiMapper.ParseResponse(body));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
    }
}

public sealed class GeminiRoundTripTests
{
    [Fact]
    public async Task A_tool_call_answer_fed_back_as_history_produces_matching_function_response_names()
    {
        using var host = new ProviderTestHost(
            "Gemini",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-parallel-calls.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));

        var first = await host.Client.SendAsync(LlmFixtures.BasicRequest(), CancellationToken.None);
        var calls = first.Blocks.OfType<ToolUseBlock>().ToList();
        var history = new List<LlmMessage>(LlmFixtures.BasicRequest().Messages)
        {
            new(LlmRole.Assistant, first.Blocks),
            new(LlmRole.User, calls.Select(c => (ContentBlock)new ToolResultBlock(c.Id, $"{{\"ok\":\"{c.Name}\"}}")).ToList()),
        };
        await host.Client.SendAsync(
            new LlmChatRequest { SystemStable = "s", SystemVolatile = "v", Messages = history, Tools = LlmFixtures.Tools() },
            CancellationToken.None);

        var next = JsonNode.Parse(host.Handler.Requests[1].Body!)!;
        var contents = next["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => c!["role"]!.GetValue<string>()));

        var calledNames = contents[1]!["parts"]!.AsArray().Select(p => p!["functionCall"]?["name"]?.GetValue<string>()).OfType<string>().ToList();
        var respondedNames = contents[2]!["parts"]!.AsArray().Select(p => p!["functionResponse"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["search_flights", "get_booking_status"], calledNames);
        Assert.Equal(calledNames, respondedNames);
    }
}

public sealed class GeminiErrorTests
{
    private static LlmChatRequest Request() => LlmFixtures.BasicRequest();

    [Fact]
    public async Task A_rate_limit_with_retry_after_is_retried_once_and_succeeds()
    {
        using var host = new ProviderTestHost(
            "Gemini",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.TooManyRequests, LlmFixtures.Read("gemini/error-rate-limited.json"), ("Retry-After", "0"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Two_server_errors_surface_a_transient_exception_with_the_provider_message()
    {
        using var host = new ProviderTestHost(
            "Gemini",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.ServiceUnavailable, LlmFixtures.Read("gemini/error-unavailable.json"))
                .Respond(HttpStatusCode.ServiceUnavailable, LlmFixtures.Read("gemini/error-unavailable.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Equal("The model is overloaded. Please try again later.", error.BodyExcerpt);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task An_invalid_key_is_permanent_and_not_retried()
    {
        using var host = new ProviderTestHost(
            "Gemini",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.BadRequest, LlmFixtures.Read("gemini/error-invalid-key.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
        Assert.Equal(400, error.StatusCode);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task A_blocked_answer_is_not_retried_and_is_classified()
    {
        using var host = new ProviderTestHost(
            "Gemini",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-blocked-prompt.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Blocked, error.Kind);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task A_malformed_success_body_is_permanent()
    {
        using var host = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.OK, "this is not json"));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
    }

    [Fact]
    public async Task A_provider_that_never_answers_times_out_as_a_transient_error_after_one_retry()
    {
        var handler = new HangingHandler();
        using var host = new ProviderTestHost("Gemini", handler, new Dictionary<string, string?> { ["Gemini:TimeoutSeconds"] = "1" });

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Null(error.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_api_key_never_appears_in_logs_or_exception_text()
    {
        using var ok = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));
        await ok.Client.SendAsync(Request(), CancellationToken.None);

        var echoed = "{\"error\":{\"code\":400,\"message\":\"key " + ProviderTestHost.ApiKey + " is not valid\"}}";
        using var failing = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, echoed));
        var error = await Assert.ThrowsAsync<LlmApiException>(() => failing.Client.SendAsync(Request(), CancellationToken.None));

        Assert.DoesNotContain(ProviderTestHost.ApiKey, error.ToString(), StringComparison.Ordinal);
        foreach (var logs in new[] { ok.Logs, failing.Logs })
        {
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(ProviderTestHost.ApiKey, StringComparison.Ordinal) || (e.Exception?.ToString().Contains(ProviderTestHost.ApiKey, StringComparison.Ordinal) ?? false));
        }
    }

    [Fact]
    public async Task One_usage_line_is_logged_per_call()
    {
        using var host = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-parallel-calls.json")));

        await host.Client.SendAsync(Request(), CancellationToken.None);

        var line = Assert.Single(host.Logs.Entries, e => e.Message.StartsWith("LLM call:", StringComparison.Ordinal));
        Assert.Contains("provider=gemini", line.Message, StringComparison.Ordinal);
        Assert.Contains("cache_read=80", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_a_provider_error()
    {
        using var host = new ProviderTestHost("Gemini", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("gemini/response-text.json")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.SendAsync(Request(), cancelled.Token));
    }
}

/// <summary>A transport that accepts the request and never answers, to exercise the per-attempt timeout.</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}
