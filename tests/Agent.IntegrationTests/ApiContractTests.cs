using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Llm;
using Agent.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.IntegrationTests;

public sealed class DefaultApiFactory : ApiFactory
{
    public DefaultApiFactory()
        : base("""{ "steps": [ { "text": "Hello from the script." }, { "text": "Second." }, { "text": "Third." }, { "text": "Fourth." }, { "text": "Fifth." } ] }""")
    {
    }
}

public sealed class ApiContractTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private TestApi Api => new(factory.CreateClient());

    [Fact]
    public async Task Start_returns_201_with_token_greeting_locale_and_direction()
    {
        var (response, token) = await Api.StartAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await TestApi.ReadJsonAsync(response);
        Assert.StartsWith("v2.k1.", token, StringComparison.Ordinal);
        Assert.Equal("en", json.RootElement.GetProperty("locale").GetString());
        Assert.Equal("ltr", json.RootElement.GetProperty("direction").GetString());
        Assert.Contains("Aria", json.RootElement.GetProperty("greeting").GetString(), StringComparison.Ordinal);
        Assert.True(json.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Start_with_arabic_locale_returns_rtl_and_arabic_greeting()
    {
        var (response, _) = await Api.StartAsync(new { locale = "ar" });

        using var json = await TestApi.ReadJsonAsync(response);
        Assert.Equal("rtl", json.RootElement.GetProperty("direction").GetString());
        Assert.Matches("[؀-ۿ]", json.RootElement.GetProperty("greeting").GetString()!);
    }

    [Fact]
    public async Task Start_without_a_client_key_is_401_problem_json()
    {
        var (response, _) = await Api.StartAsync(clientKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Start_with_a_wrong_client_key_is_401()
    {
        var (response, _) = await Api.StartAsync(clientKey: "wrong-client-key-0123456789");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Start_with_an_unsupported_locale_is_422()
    {
        var (response, _) = await Api.StartAsync(new { locale = "fr" });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        using var json = await TestApi.ReadJsonAsync(response);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("locale", out _));
    }

    [Fact]
    public async Task Chat_endpoints_reject_missing_tampered_and_foreign_credentials()
    {
        var api = Api;
        var token = await api.StartSessionAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.MessageAsync(string.Empty, "hi")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.MessageAsync(token + "x", "hi")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.MessageAsync(ApiFactory.ClientKey, "hi")).StatusCode);

        // Swap the payload for another session id while keeping the old signature.
        var parts = token.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String("""{"sid":"abc","iat":1,"exp":99999999999}"""u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[3]}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.MessageAsync(forged, "hi")).StatusCode);
    }

    [Fact]
    public async Task Tools_endpoint_needs_a_client_key_and_lists_every_tool_in_stable_order()
    {
        var api = Api;
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.GetAsync("/api/v1/tools")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tools");
        request.Headers.Add("X-Client-Key", ApiFactory.ClientKey);
        var response = await api.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await TestApi.ReadJsonAsync(response);
        var names = json.RootElement.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Equal(25, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal).ToList(), names);
        Assert.All(json.RootElement.EnumerateArray(), t => Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString()));
        Assert.Contains("submit_flight_booking", names);
        Assert.Contains("escalate_to_human", names);
        foreach (var tool in new[] { "search_transfers", "get_transfer_price", "select_transfer", "get_transfer_details", "add_to_trip", "view_trip", "remove_from_trip", "collect_trip_details", "submit_trip_booking" })
        {
            Assert.Contains(tool, names);
        }
    }

    [Fact]
    public async Task Countries_are_listed_sorted_by_code()
    {
        var response = await Api.Http.GetAsync("/api/v1/lookups/countries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await TestApi.ReadJsonAsync(response);
        var codes = json.RootElement.EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToList();
        Assert.True(codes.Count > 100);
        Assert.Contains("NL", codes);
        Assert.Equal(codes.Order(StringComparer.Ordinal).ToList(), codes);
    }

    [Fact]
    public async Task Health_endpoints_report_live_and_ready()
    {
        var api = Api;

        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync("/health/live")).StatusCode);
        var ready = await api.Http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correlation_id_is_generated_echoed_and_sanitised()
    {
        var api = Api;

        var generated = await api.Http.GetAsync("/health/live");
        Assert.Matches("^[0-9a-f]{32}$", generated.Headers.GetValues("X-Correlation-Id").Single());

        using var echoed = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        echoed.Headers.Add("X-Correlation-Id", "trace-123.abc");
        Assert.Equal("trace-123.abc", (await api.Http.SendAsync(echoed)).Headers.GetValues("X-Correlation-Id").Single());

        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        hostile.Headers.Add("X-Correlation-Id", "bad value with spaces");
        Assert.NotEqual("bad value with spaces", (await api.Http.SendAsync(hostile)).Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Responses_carry_nosniff()
    {
        var response = await Api.Http.GetAsync("/health/live");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Message_reply_has_locale_direction_state_and_structured_data_fields()
    {
        var api = Api;
        var token = await api.StartSessionAsync();

        using var json = await api.ChatAsync(token, "hello");

        Assert.Equal("Hello from the script.", TestApi.Reply(json));
        Assert.Equal("en", json.RootElement.GetProperty("locale").GetString());
        Assert.Equal("ltr", json.RootElement.GetProperty("direction").GetString());
        Assert.Equal("message", TestApi.State(json));
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("structuredData").ValueKind);
    }

    [Fact]
    public async Task Message_validation_rejects_blank_and_oversized_text_with_422()
    {
        var api = Api;
        var token = await api.StartSessionAsync();

        var blank = await api.MessageAsync(token, "   ");
        var oversized = await api.MessageAsync(token, new string('a', 2001));
        var maximum = await api.MessageAsync(token, new string('b', 2000));

        Assert.Equal((HttpStatusCode)422, blank.StatusCode);
        Assert.Equal((HttpStatusCode)422, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
        Assert.Equal("application/problem+json", oversized.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task History_returns_only_visible_messages_oldest_first()
    {
        var api = Api;
        var token = await api.StartSessionAsync();
        (await api.ChatAsync(token, "first")).Dispose();
        (await api.ChatAsync(token, "second")).Dispose();

        var response = await api.SendAsync(token, HttpMethod.Get, "/api/v1/chat/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await TestApi.ReadJsonAsync(response);
        var items = json.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user", "assistant"], items.Select(i => i.GetProperty("role").GetString()));
        Assert.Equal("first", items[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task History_limit_is_validated()
    {
        var api = Api;
        var token = await api.StartSessionAsync();

        var response = await api.SendAsync(token, HttpMethod.Get, "/api/v1/chat/history?limit=500");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_session_erases_it_and_later_calls_are_404()
    {
        var api = Api;
        var token = await api.StartSessionAsync();
        (await api.ChatAsync(token, "hi")).Dispose();

        var deleted = await api.SendAsync(token, HttpMethod.Delete, "/api/v1/chat/session");
        var after = await api.MessageAsync(token, "still there?");
        var deletedAgain = await api.SendAsync(token, HttpMethod.Delete, "/api/v1/chat/session");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task An_idle_session_is_410_and_an_old_token_is_401()
    {
        using var local = new ApiFactory();
        var api = new TestApi(local.CreateClient());
        var token = await api.StartSessionAsync();

        local.Clock.Advance(TimeSpan.FromMinutes(31));
        var idle = await api.MessageAsync(token, "hello again");
        local.Clock.Advance(TimeSpan.FromMinutes(100));
        var expiredToken = await api.MessageAsync(token, "hello again");

        Assert.Equal(HttpStatusCode.Gone, idle.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredToken.StatusCode);
    }

    [Fact]
    public async Task Unhandled_failures_return_a_generic_problem_without_internals()
    {
        using var local = new ApiFactory(configureServices: s =>
            s.AddSingleton<IChatOrchestrator, ThrowingOrchestrator>());
        var api = new TestApi(local.CreateClient());
        var token = await api.StartSessionAsync();

        var response = await api.MessageAsync(token, "boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-internal-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", json.RootElement.GetProperty("correlationId").GetString()!);
    }

    [Fact]
    public async Task Per_session_rate_limit_answers_429_with_retry_after_and_a_localized_reply()
    {
        using var local = new ApiFactory(overrides: new Dictionary<string, string?> { ["RateLimiting:SessionMessagesPerWindow"] = "2" });
        var api = new TestApi(local.CreateClient());
        var token = await api.StartSessionAsync();

        (await api.ChatAsync(token, "one")).Dispose();
        (await api.ChatAsync(token, "two")).Dispose();
        var limited = await api.MessageAsync(token, "three");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        using var json = await TestApi.ReadJsonAsync(limited);
        Assert.Contains("too quickly", json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("ltr", json.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Session_starts_are_rate_limited_per_client_key()
    {
        using var local = new ApiFactory(overrides: new Dictionary<string, string?> { ["RateLimiting:SessionStartsPerWindow"] = "2" });
        var api = new TestApi(local.CreateClient());

        Assert.Equal(HttpStatusCode.Created, (await api.StartAsync()).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.StartAsync()).Response.StatusCode);
        var limited = (await api.StartAsync()).Response;

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_ip_backstop_limits_every_endpoint()
    {
        using var local = new ApiFactory(overrides: new Dictionary<string, string?> { ["RateLimiting:IpRequestsPerWindow"] = "3" });
        var http = local.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await http.GetAsync("/api/v1/lookups/countries")).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_second_message_while_a_turn_is_running_is_409()
    {
        var gated = new GatedLlmClient();
        using var local = new ApiFactory(configureServices: s => s.AddSingleton<ILlmClient>(gated));
        var api = new TestApi(local.CreateClient());
        var token = await api.StartSessionAsync();

        var first = api.MessageAsync(token, "slow one");
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await api.MessageAsync(token, "impatient");
        gated.Release.SetResult();
        var firstResponse = await first;

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task Sensitive_and_repeated_messages_are_answered_without_the_model()
    {
        var gated = new GatedLlmClient();
        gated.Release.SetResult();
        using var local = new ApiFactory(configureServices: s => s.AddSingleton<ILlmClient>(gated));
        var api = new TestApi(local.CreateClient());
        var token = await api.StartSessionAsync();

        using var card = await api.ChatAsync(token, "my card is 4111 1111 1111 1111");
        for (var i = 0; i < 3; i++)
        {
            (await api.ChatAsync(token, "same again")).Dispose();
        }

        using var fourth = await api.ChatAsync(token, "same again");

        Assert.Equal("guarded", TestApi.State(card));
        Assert.Contains("secure forms", TestApi.Reply(card), StringComparison.Ordinal);
        Assert.Equal("guarded", TestApi.State(fourth));
        Assert.Equal(3, gated.Calls);

        var history = await api.SendAsync(token, HttpMethod.Get, "/api/v1/chat/history");
        var text = await history.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4111", text, StringComparison.Ordinal);
        Assert.Contains("[redacted: card number]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arabic_messages_get_an_rtl_reply_envelope()
    {
        var api = Api;
        var token = await api.StartSessionAsync();

        using var json = await api.ChatAsync(token, "مرحبا كيف حالك");

        Assert.Equal("ar", json.RootElement.GetProperty("locale").GetString());
        Assert.Equal("rtl", json.RootElement.GetProperty("direction").GetString());
    }

    private sealed class ThrowingOrchestrator : IChatOrchestrator
    {
        public Task<ChatReply> HandleMessageAsync(string sessionId, string message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret-internal-detail at Some.Internal.Type.Method()");
    }

    private sealed class GatedLlmClient : ILlmClient
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public async Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LlmChatResult([new TextBlock("done")], LlmStopReason.EndTurn, LlmUsage.None);
        }
    }
}
