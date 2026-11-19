using System.Net;
using System.Net.Http.Headers;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.LlmClients;

public sealed class LlmErrorClassificationTests
{
    [Theory]
    [InlineData(null, LlmErrorKind.Transient)]
    [InlineData(408, LlmErrorKind.Transient)]
    [InlineData(429, LlmErrorKind.Transient)]
    [InlineData(500, LlmErrorKind.Transient)]
    [InlineData(503, LlmErrorKind.Transient)]
    [InlineData(400, LlmErrorKind.Permanent)]
    [InlineData(401, LlmErrorKind.Permanent)]
    [InlineData(403, LlmErrorKind.Permanent)]
    [InlineData(404, LlmErrorKind.Permanent)]
    [InlineData(422, LlmErrorKind.Permanent)]
    public void Status_codes_are_classified(int? status, LlmErrorKind expected)
    {
        Assert.Equal(expected, LlmApiException.Classify(status));
        Assert.Equal(expected, new LlmApiException("x", status).Kind);
    }

    [Fact]
    public void An_explicit_kind_and_retry_after_override_the_default()
    {
        var error = new LlmApiException("x", 200, null, null, LlmErrorKind.Blocked, TimeSpan.FromSeconds(7));

        Assert.Equal(LlmErrorKind.Blocked, error.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Fact]
    public void The_original_constructor_shape_still_works()
    {
        var error = new LlmApiException("failed", 502, "excerpt", new InvalidOperationException("inner"));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("excerpt", error.BodyExcerpt);
        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Null(error.RetryAfter);
    }
}

public sealed class RetryAfterTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);

    private static HttpResponseMessage WithRetryAfter(RetryConditionHeaderValue? value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = value;
        return response;
    }

    [Fact]
    public void No_header_keeps_the_base_delay()
    {
        var clock = new FakeTimeProvider();

        Assert.Null(LlmResilience.RetryAfterDelay(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), clock, Cap));
        Assert.Null(LlmResilience.RetryAfterDelay(null, clock, Cap));
    }

    [Fact]
    public void A_delta_in_seconds_replaces_the_base_delay()
    {
        var delay = LlmResilience.RetryAfterDelay(WithRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(3))), new FakeTimeProvider(), Cap);

        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void A_very_long_delta_is_capped()
    {
        var delay = LlmResilience.RetryAfterDelay(WithRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromHours(1))), new FakeTimeProvider(), Cap);

        Assert.Equal(Cap, delay);
    }

    [Fact]
    public void An_http_date_is_converted_relative_to_the_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

        var delay = LlmResilience.RetryAfterDelay(WithRetryAfter(new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(4))), clock, Cap);

        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }

    [Fact]
    public void A_date_in_the_past_means_retry_now()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

        var delay = LlmResilience.RetryAfterDelay(WithRetryAfter(new RetryConditionHeaderValue(clock.GetUtcNow().AddMinutes(-5))), clock, Cap);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void A_zero_cap_disables_waiting_entirely()
    {
        var delay = LlmResilience.RetryAfterDelay(WithRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))), new FakeTimeProvider(), TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, delay);
    }
}

public sealed class SecretRedactionTests
{
    [Fact]
    public void The_key_is_removed_from_text_copied_out_of_a_response()
    {
        var text = LlmHttp.Redact("Invalid key abc-secret-123 supplied", "abc-secret-123");

        Assert.DoesNotContain("abc-secret-123", text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_unchanged_without_a_key()
    {
        Assert.Equal("plain", LlmHttp.Redact("plain", null));
        Assert.Equal("plain", LlmHttp.Redact("plain", string.Empty));
    }
}

/// <summary>Behaviour every HTTP provider shares, driven through the Anthropic client: Retry-After and error kinds.</summary>
public sealed class AnthropicErrorKindTests
{
    private static LlmChatRequest Request() => new()
    {
        SystemStable = "stable",
        SystemVolatile = "volatile",
        Messages = [LlmMessage.User("hi")],
    };

    [Fact]
    public async Task A_transient_failure_with_retry_after_zero_is_retried_immediately()
    {
        using var host = new ProviderTestHost(
            "Anthropic",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.TooManyRequests, AnthropicFixtures.Overloaded, ("Retry-After", "0"))
                .Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var result = await host.Client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_huge_retry_after_is_capped_so_the_retry_does_not_wait()
    {
        using var host = new ProviderTestHost(
            "Anthropic",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.ServiceUnavailable, AnthropicFixtures.Overloaded, ("Retry-After", "86400"))
                .Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply),
            new Dictionary<string, string?> { ["Anthropic:MaxRetryAfterSeconds"] = "0" });

        var result = await host.Client.SendAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task The_final_transient_error_reports_its_kind_and_the_requested_delay()
    {
        using var host = new ProviderTestHost(
            "Anthropic",
            new FakeHttpHandler()
                .RespondWith(HttpStatusCode.TooManyRequests, AnthropicFixtures.Overloaded, ("Retry-After", "0"))
                .RespondWith(HttpStatusCode.TooManyRequests, AnthropicFixtures.Overloaded, ("Retry-After", "42")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Equal(429, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), error.RetryAfter);
    }

    [Fact]
    public async Task A_client_error_is_permanent_and_not_retried()
    {
        using var host = new ProviderTestHost(
            "Anthropic",
            new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, AnthropicFixtures.AuthError).Respond(HttpStatusCode.OK, AnthropicFixtures.TextReply));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task A_connection_failure_is_transient_without_a_status()
    {
        using var host = new ProviderTestHost(
            "Anthropic",
            new FakeHttpHandler().Throw(new HttpRequestException("reset")).Throw(new HttpRequestException("reset again")));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Transient, error.Kind);
        Assert.Null(error.StatusCode);
    }

    [Fact]
    public async Task A_malformed_success_body_is_permanent()
    {
        using var host = new ProviderTestHost("Anthropic", new FakeHttpHandler().Respond(HttpStatusCode.OK, "not json"));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Permanent, error.Kind);
    }

    [Fact]
    public async Task An_error_body_that_echoes_the_key_is_redacted()
    {
        var body = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"bad key " + ProviderTestHost.ApiKey + " rejected\"}}";
        using var host = new ProviderTestHost("Anthropic", new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, body));

        var error = await Assert.ThrowsAsync<LlmApiException>(() => host.Client.SendAsync(Request(), CancellationToken.None));

        Assert.DoesNotContain(ProviderTestHost.ApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderTestHost.ApiKey, error.BodyExcerpt!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error.BodyExcerpt, StringComparison.Ordinal);
    }
}
