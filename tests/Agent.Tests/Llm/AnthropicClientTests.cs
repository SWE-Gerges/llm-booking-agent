using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Llm;
using Agent.Llm.Anthropic;
using Agent.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent.Tests.LlmClients;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string? Body);

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responders = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string body)
    {
        _responders.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public FakeHttpHandler RespondWith(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        _responders.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responders.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body));
        return _responders.Count > 0
            ? _responders.Dequeue()()
            : throw new InvalidOperationException("The fake handler has no response queued.");
    }
}

/// <summary>The real <c>AddAgentLlm</c> registration (typed client + resilience pipeline) over a fake transport.</summary>
internal sealed class AnthropicTestHost : IDisposable
{
    public const string ApiKey = "test-key-do-not-leak-123";

    private readonly ServiceProvider _provider;

    public AnthropicTestHost(FakeHttpHandler handler)
    {
        Handler = handler;
        Logs = new CapturingLoggerProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "Anthropic",
                ["Anthropic:ApiKey"] = ApiKey,
                ["Anthropic:BaseUrl"] = "http://localhost:1/",
                ["Anthropic:Model"] = "test-model",
                ["Anthropic:RetryDelayMilliseconds"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddAgentLlm(configuration);
        services.AddHttpClient(nameof(ILlmClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
        _provider = services.BuildServiceProvider();
        Client = _provider.GetRequiredService<ILlmClient>();
    }

    public FakeHttpHandler Handler { get; }

    public CapturingLoggerProvider Logs { get; }

    public ILlmClient Client { get; }

    public void Dispose() => _provider.Dispose();
}

internal static class AnthropicFixtures
{
    public const string TextReply = """
        {"id":"msg_01TextReply","type":"message","role":"assistant","model":"test-model",
         "content":[{"type":"text","text":"Hello there"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":25,"output_tokens":7}}
        """;

    public const string ToolUseReply = """
        {"id":"msg_01ToolUse","type":"message","role":"assistant","model":"test-model",
         "content":[
           {"type":"text","text":"Let me search."},
           {"type":"tool_use","id":"toolu_01A","name":"search_flights","input":{"origin":"AMS","destination":"LIS","adults":2}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":120,"cache_creation_input_tokens":30,"cache_read_input_tokens":500,"output_tokens":42}}
        """;

    public const string ThinkingReply = """
        {"id":"msg_01Thinking","type":"message","role":"assistant","model":"test-model",
         "content":[
           {"type":"thinking","thinking":"hmm","signature":"abc"},
           {"type":"redacted_thinking","data":"zzz"},
           {"type":"text","text":"Visible answer"}],
         "stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
        """;

    public const string AuthError = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";

    public const string Overloaded = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";

    public static string WithStopReason(string? reason) =>
        "{\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"stop_reason\":"
        + (reason is null ? "null" : $"\"{reason}\"")
        + ",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
}

public sealed class AnthropicClientRequestTests
{
    private static LlmChatRequest Request() => new()
    {
        SystemStable = "stable",
        SystemVolatile = "volatile",
        Messages = [LlmMessage.User("hi")],
        Tools = [new LlmToolDefinition("search_flights", "Search", AgentJson.Parse("""{"type":"object","properties":{"origin":{"type":"string"}},"required":["origin"]}"""))],
    };

    [Fact]
    public async Task Posts_to_the_messages_endpoint_with_the_expected_headers_and_body()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        await host.Client.SendAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(host.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://localhost:1/v1/messages", sent.Uri.ToString());
        Assert.Equal(AnthropicTestHost.ApiKey, sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Equal("application/json", sent.ContentType);

        var body = JsonNode.Parse(sent.Body!)!;
        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        Assert.Equal(1024, body["max_tokens"]!.GetValue<int>());
        var tool = body["tools"]![0]!;
        Assert.Equal("search_flights", tool["name"]!.GetValue<string>());
        Assert.Equal("Search", tool["description"]!.GetValue<string>());
        Assert.Equal("object", tool["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("origin", tool["input_schema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_use_and_tool_result_blocks_are_sent_in_the_provider_shape()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));
        var request = Request() with
        {
            Messages =
            [
                LlmMessage.User("find flights"),
                new LlmMessage(LlmRole.Assistant, [new ToolUseBlock("toolu_1", "search_flights", AgentJson.Parse("""{"origin":"AMS"}"""))]),
                new LlmMessage(LlmRole.User, [new ToolResultBlock("toolu_1", "ok", false), new ToolResultBlock("toolu_2", "bad", true)]),
            ],
        };

        await host.Client.SendAsync(request, CancellationToken.None);

        var messages = JsonNode.Parse(host.Handler.Requests[0].Body!)!["messages"]!.AsArray();
        var use = messages[1]!["content"]![0]!;
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("tool_use", use["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", use["id"]!.GetValue<string>());
        Assert.Equal("AMS", use["input"]!["origin"]!.GetValue<string>());

        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", results[0]!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", results[0]!["tool_use_id"]!.GetValue<string>());
        Assert.Null(results[0]!["is_error"]);
        Assert.True(results[1]!["is_error"]!.GetValue<bool>());
    }
}

public sealed class AnthropicClientResponseTests
{
    private static LlmChatRequest Request() => new()
    {
        SystemStable = "stable",
        SystemVolatile = "volatile",
        Messages = [LlmMessage.User("hi")],
    };

    [Fact]
    public async Task Text_reply_is_mapped_with_usage()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(new LlmUsage(25, 7, 0, 0), result.Usage);
    }

    [Fact]
    public async Task Tool_use_reply_is_mapped_including_cache_read_and_write_tokens()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.ToolUseReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmStopReason.ToolUse, result.StopReason);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Let me search.", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        var use = Assert.IsType<ToolUseBlock>(result.Blocks[1]);
        Assert.Equal("toolu_01A", use.Id);
        Assert.Equal("search_flights", use.Name);
        Assert.Equal(2, use.Input.GetProperty("adults").GetInt32());
        Assert.Equal(new LlmUsage(120, 42, CacheReadTokens: 500, CacheWriteTokens: 30), result.Usage);
    }

    [Fact]
    public async Task Each_call_writes_a_cache_read_log_line_that_never_contains_the_key()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.ToolUseReply));

        await host.Client.SendAsync(Request(), CancellationToken.None);

        var line = Assert.Single(host.Logs.Entries, e => e.Category.EndsWith(nameof(AnthropicChatClient), StringComparison.Ordinal) && e.Level == LogLevel.Information);
        Assert.Contains("cache_read=500", line.Message, StringComparison.Ordinal);
        Assert.Contains("cache_write=30", line.Message, StringComparison.Ordinal);
        Assert.Contains("model=test-model", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains(AnthropicTestHost.ApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_content_block_types_are_ignored()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.ThinkingReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("Visible answer", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
    }

    [Theory]
    [InlineData("end_turn", LlmStopReason.EndTurn)]
    [InlineData("tool_use", LlmStopReason.ToolUse)]
    [InlineData("max_tokens", LlmStopReason.MaxTokens)]
    [InlineData("stop_sequence", LlmStopReason.EndTurn)]
    [InlineData("pause_turn", LlmStopReason.EndTurn)]
    [InlineData(null, LlmStopReason.EndTurn)]
    public void Stop_reasons_are_mapped(string? reason, LlmStopReason expected)
    {
        var result = AnthropicMapper.ParseResponse(AnthropicFixtures.WithStopReason(reason));

        Assert.Equal(expected, result.StopReason);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"id":"msg_1"}""")]
    [InlineData("""{"content":"not an array"}""")]
    [InlineData("""{"content":[{"text":"no type"}]}""")]
    [InlineData("[]")]
    public async Task Malformed_success_bodies_raise_a_typed_exception(string body)
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Single(host.Handler.Requests);
        Assert.DoesNotContain(AnthropicTestHost.ApiKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_responses_carry_the_status_and_the_provider_message_but_never_the_key()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, AnthropicFixtures.AuthError));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("invalid x-api-key", error.BodyExcerpt);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicTestHost.ApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains(AnthropicTestHost.ApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_json_error_bodies_are_excerpted()
    {
        var body = new string('x', 2000);
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, body));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.True(error.BodyExcerpt!.Length < 400);
    }
}

public sealed class AnthropicClientRetryTests
{
    private static LlmChatRequest Request() => new()
    {
        SystemStable = "stable",
        SystemVolatile = "volatile",
        Messages = [LlmMessage.User("hi")],
    };

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_transient_status_is_retried_once_and_the_second_attempt_succeeds(HttpStatusCode transient)
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(transient, AnthropicFixtures.Overloaded).Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("Hello there", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(2, host.Handler.Requests.Count);
        Assert.Equal(host.Handler.Requests[0].Body, host.Handler.Requests[1].Body);
    }

    [Fact]
    public async Task A_connection_error_is_retried_once()
    {
        using var host = new AnthropicTestHost(
            new FakeHttpHandler().Throw(new HttpRequestException("connection reset")).Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Two_consecutive_transient_failures_surface_the_status_and_body_excerpt()
    {
        using var host = new AnthropicTestHost(
            new FakeHttpHandler().Respond(HttpStatusCode.ServiceUnavailable, AnthropicFixtures.Overloaded).Respond(HttpStatusCode.ServiceUnavailable, AnthropicFixtures.Overloaded));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("Overloaded", error.BodyExcerpt);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Two_consecutive_connection_errors_raise_a_typed_exception_with_the_inner_cause()
    {
        using var host = new AnthropicTestHost(
            new FakeHttpHandler().Throw(new HttpRequestException("reset 1")).Throw(new HttpRequestException("reset 2")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Null(error.StatusCode);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_errors_are_not_retried(HttpStatusCode status)
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(status, AnthropicFixtures.AuthError).Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_a_provider_error()
    {
        using var host = new AnthropicTestHost(new FakeHttpHandler().Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.SendAsync(Request(), cancelled.Token));
    }
}
