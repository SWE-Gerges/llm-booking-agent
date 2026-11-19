using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Abstractions.Llm;
using Agent.Llm.Groq;

namespace Agent.Tests.LlmClients;

public sealed class GroqRequestTests
{
    private static void AssertSameJson(JsonNode expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:\n{expected.ToJsonString()}\nActual:\n{actual.ToJsonString()}");

    [Fact]
    public void A_basic_request_matches_the_golden_body()
    {
        AssertSameJson(LlmFixtures.Node("groq/request-basic.json"), GroqMapper.BuildRequest(LlmFixtures.BasicRequest(), "test-model"));
    }

    [Fact]
    public void Temperature_is_sent_only_when_set()
    {
        var with = GroqMapper.BuildRequest(LlmFixtures.BasicRequest(0.2), "test-model");
        var without = GroqMapper.BuildRequest(LlmFixtures.BasicRequest(), "test-model");

        AssertSameJson(LlmFixtures.Node("groq/request-basic-temperature.json"), with);
        Assert.Null(without["temperature"]);
    }

    [Fact]
    public void History_with_parallel_calls_and_tool_results_matches_the_golden_body()
    {
        AssertSameJson(LlmFixtures.Node("groq/request-history.json"), GroqMapper.BuildRequest(LlmFixtures.HistoryRequest(), "test-model"));
    }

    [Fact]
    public void Tool_arguments_are_sent_as_a_json_string_and_schemas_are_passed_through_unchanged()
    {
        var body = GroqMapper.BuildRequest(LlmFixtures.HistoryRequest(), "test-model");

        var call = body["messages"]![2]!["tool_calls"]![0]!;
        Assert.Equal(JsonValueKind.String, call["function"]!["arguments"]!.GetValueKind());
        Assert.Equal("""{"origin":"AMS"}""", call["function"]!["arguments"]!.GetValue<string>());

        var schema = body["tools"]![0]!["function"]!["parameters"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(LlmFixtures.SearchFlightsSchema), schema));
    }

    [Fact]
    public void An_assistant_message_with_only_tool_calls_has_null_content()
    {
        var request = new LlmChatRequest
        {
            SystemStable = "s",
            SystemVolatile = "v",
            Messages =
            [
                LlmMessage.User("go"),
                new LlmMessage(LlmRole.Assistant, [new ToolUseBlock("c1", "get_booking_status", Abstractions.AgentJson.Parse("{}"))]),
            ],
        };

        var assistant = GroqMapper.BuildRequest(request, "m")["messages"]![2]!;

        Assert.Null(assistant["content"]);
        Assert.NotNull(assistant["tool_calls"]);
    }

    [Fact]
    public void Empty_messages_get_a_placeholder_because_empty_content_is_rejected()
    {
        var request = new LlmChatRequest
        {
            SystemStable = "s",
            SystemVolatile = "v",
            Messages = [new LlmMessage(LlmRole.User, [new TextBlock(" ")]), new LlmMessage(LlmRole.Assistant, [new TextBlock(" ")])],
        };

        var messages = GroqMapper.BuildRequest(request, "m")["messages"]!.AsArray();

        Assert.Equal("(no content)", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("(no content)", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_client_posts_to_chat_completions_with_a_bearer_header_and_never_puts_the_key_in_the_url()
    {
        using var host = new ProviderTestHost("Groq", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        await host.Client.SendAsync(LlmFixtures.BasicRequest(), CancellationToken.None);

        var sent = Assert.Single(host.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/chat/completions", sent.Uri.AbsolutePath);
        Assert.Equal(string.Empty, sent.Uri.Query);
        Assert.Equal($"Bearer {ProviderTestHost.ApiKey}", sent.Headers["Authorization"]);
        Assert.DoesNotContain(ProviderTestHost.ApiKey, sent.Uri.ToString(), StringComparison.Ordinal);
        AssertSameJson(LlmFixtures.Node("groq/request-basic.json"), JsonNode.Parse(sent.Body!)!);
    }

    [Fact]
    public async Task A_base_address_without_a_trailing_slash_still_resolves_the_relative_path()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")),
            new Dictionary<string, string?> { ["Groq:BaseUrl"] = "http://localhost:1/openai/v1" });

        await host.Client.SendAsync(LlmFixtures.BasicRequest(), CancellationToken.None);

        Assert.Equal("/openai/v1/chat/completions", Assert.Single(host.Handler.Requests).Uri.AbsolutePath);
    }
}

public sealed class GroqResponseTests
{
    [Fact]
    public void Text_and_usage_including_cached_tokens_are_mapped()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-text.json"));

        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal(new LlmUsage(15, 7, 10, 0), result.Usage);
    }

    [Fact]
    public void Parallel_tool_calls_keep_the_provider_ids_and_parse_the_argument_strings()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-tool-calls.json"));

        Assert.Equal(LlmStopReason.ToolUse, result.StopReason);
        Assert.Equal("Let me check.", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        var first = Assert.IsType<ToolUseBlock>(result.Blocks[1]);
        var second = Assert.IsType<ToolUseBlock>(result.Blocks[2]);
        Assert.Equal(("call_a1", "search_flights"), (first.Id, first.Name));
        Assert.Equal(2, first.Input.GetProperty("adults").GetInt32());
        Assert.Equal(("call_a2", "get_booking_status"), (second.Id, second.Name));
        Assert.Equal(new LlmUsage(120, 30, 0, 0), result.Usage);
    }

    [Fact]
    public void Null_content_with_tool_calls_yields_only_tool_blocks()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-tool-calls-null-content.json"));

        Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal(LlmStopReason.ToolUse, result.StopReason);
    }

    [Fact]
    public void Length_maps_to_max_tokens()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-length.json"));

        Assert.Equal(LlmStopReason.MaxTokens, result.StopReason);
    }

    [Fact]
    public void A_content_filter_finish_is_classified_as_blocked()
    {
        var error = Assert.Throws<LlmApiException>(() => GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-content-filter.json")));

        Assert.Equal(LlmErrorKind.Blocked, error.Kind);
    }

    [Fact]
    public void Arguments_that_are_not_json_are_kept_as_a_string_so_schema_validation_can_reject_them()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-bad-arguments.json"));

        var use = Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal(JsonValueKind.String, use.Input.ValueKind);
        Assert.Equal("{not json", use.Input.GetString());
    }

    [Fact]
    public void Empty_arguments_become_an_empty_object()
    {
        var result = GroqMapper.ParseResponse(LlmFixtures.Read("groq/response-empty-arguments.json"));

        var use = Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal(JsonValueKind.Object, use.Input.ValueKind);
        Assert.Empty(use.Input.EnumerateObject());
    }

    [Theory]
    [InlineData("groq/response-no-choices.json")]
    [InlineData("[]")]
    public void A_response_without_choices_is_permanent(string bodyOrFixture)
    {
        var body = bodyOrFixture.EndsWith(".json", StringComparison.Ordinal) ? LlmFixtures.Read(bodyOrFixture) : bodyOrFixture;

        var error = Assert.Throws<LlmApiException>(() => GroqMapper.ParseResponse(body));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
    }
}

public sealed class GroqRoundTripTests
{
    [Fact]
    public async Task A_tool_call_answer_fed_back_as_history_produces_matching_tool_call_ids()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-tool-calls.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        var first = await host.Client.SendAsync(LlmFixtures.BasicRequest(), CancellationToken.None);
        var calls = first.Blocks.OfType<ToolUseBlock>().ToList();
        var history = new List<LlmMessage>(LlmFixtures.BasicRequest().Messages)
        {
            new(LlmRole.Assistant, first.Blocks),
            new(LlmRole.User, calls.Select(c => (ContentBlock)new ToolResultBlock(c.Id, "{\"ok\":true}")).ToList()),
        };
        await host.Client.SendAsync(
            new LlmChatRequest { SystemStable = "s", SystemVolatile = "v", Messages = history, Tools = LlmFixtures.Tools() },
            CancellationToken.None);

        var messages = JsonNode.Parse(host.Handler.Requests[1].Body!)!["messages"]!.AsArray();
        var roles = messages.Select(m => m!["role"]!.GetValue<string>()).ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], roles);

        var requestedIds = messages[2]!["tool_calls"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToList();
        var answeredIds = messages.Skip(3).Select(m => m!["tool_call_id"]!.GetValue<string>()).ToList();
        Assert.Equal(["call_a1", "call_a2"], requestedIds);
        Assert.Equal(requestedIds, answeredIds);
    }
}

public sealed class GroqErrorTests
{
    private static LlmChatRequest Request() => LlmFixtures.BasicRequest();

    [Fact]
    public async Task A_rate_limit_with_retry_after_is_retried_once_and_succeeds()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.TooManyRequests, LlmFixtures.Read("groq/error-rate-limited.json"), ("Retry-After", "0"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Two_rate_limits_surface_the_provider_message_and_the_requested_delay()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.TooManyRequests, LlmFixtures.Read("groq/error-rate-limited.json"), ("Retry-After", "0"))
                .RespondWith(HttpStatusCode.TooManyRequests, LlmFixtures.Read("groq/error-rate-limited.json"), ("Retry-After", "17")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Equal(429, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
        Assert.Contains("Rate limit reached", error.BodyExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_error_is_retried_once_then_reported_as_transient()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.ServiceUnavailable, LlmFixtures.Read("groq/error-unavailable.json"))
                .Respond(HttpStatusCode.ServiceUnavailable, LlmFixtures.Read("groq/error-unavailable.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task An_invalid_key_is_permanent_and_not_retried()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.Unauthorized, LlmFixtures.Read("groq/error-invalid-key.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
        Assert.Equal("Invalid API Key", error.BodyExcerpt);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task A_filtered_answer_is_not_retried_and_is_classified()
    {
        using var host = new ProviderTestHost(
            "Groq",
            new FakeHttpHandler()
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-content-filter.json"))
                .Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Blocked, error.Kind);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task A_malformed_success_body_is_permanent()
    {
        using var host = new ProviderTestHost("Groq", new FakeHttpHandler().Respond(HttpStatusCode.OK, "{\"choices\":\"nope\"}"));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
    }

    [Fact]
    public async Task A_provider_that_never_answers_times_out_as_a_transient_error_after_one_retry()
    {
        var handler = new HangingHandler();
        using var host = new ProviderTestHost("Groq", handler, new Dictionary<string, string?> { ["Groq:TimeoutSeconds"] = "1" });

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_api_key_never_appears_in_logs_or_exception_text()
    {
        using var ok = new ProviderTestHost("Groq", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));
        await ok.Client.SendAsync(Request(), CancellationToken.None);

        var echoed = "{\"error\":{\"message\":\"Invalid API Key " + ProviderTestHost.ApiKey + "\",\"type\":\"invalid_request_error\"}}";
        using var failing = new ProviderTestHost("Groq", new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, echoed));
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
        using var host = new ProviderTestHost("Groq", new FakeHttpHandler().Respond(HttpStatusCode.OK, LlmFixtures.Read("groq/response-text.json")));

        await host.Client.SendAsync(Request(), CancellationToken.None);

        var line = Assert.Single(host.Logs.Entries, e => e.Message.StartsWith("LLM call:", StringComparison.Ordinal));
        Assert.Contains("provider=groq", line.Message, StringComparison.Ordinal);
        Assert.Contains("cache_read=10", line.Message, StringComparison.Ordinal);
    }
}
