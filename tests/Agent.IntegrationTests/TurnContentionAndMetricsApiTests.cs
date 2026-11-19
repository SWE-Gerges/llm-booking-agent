using System.Diagnostics.Metrics;
using System.Net;
using Agent.Abstractions.Llm;
using Agent.Core.Telemetry;
using Agent.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.IntegrationTests;

public sealed class TurnContentionAndMetricsApiTests
{
    [Fact]
    public async Task A_concurrent_message_gets_409_with_the_default_retry_after()
    {
        var gated = new GatedLlmClient();
        using var factory = new ApiFactory(configureServices: s => s.AddSingleton<ILlmClient>(gated));
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();

        var first = api.MessageAsync(token, "slow one");
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await api.MessageAsync(token, "impatient");
        gated.Release.SetResult();
        var firstResponse = await first;

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("2", second.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task The_retry_after_on_contention_is_configurable_and_form_posts_share_the_same_lock()
    {
        var gated = new GatedLlmClient();
        using var factory = new ApiFactory(
            overrides: new Dictionary<string, string?> { ["Turn:ContentionRetryAfterSeconds"] = "7" },
            configureServices: s => s.AddSingleton<ILlmClient>(gated));
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();

        var first = api.MessageAsync(token, "slow one");
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var form = await api.SendAsync(token, HttpMethod.Post, "/api/v1/chat/forms/passenger", Payloads.Passenger());
        gated.Release.SetResult();
        await first;

        Assert.Equal(HttpStatusCode.Conflict, form.StatusCode);
        Assert.Equal("7", form.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task The_lock_is_released_after_every_turn_so_a_sequence_of_messages_never_conflicts()
    {
        using var factory = new ApiFactory("""{ "steps": [ { "text": "a" }, { "text": "b" }, { "text": "c" } ] }""");
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();

        foreach (var message in new[] { "one", "two", "three" })
        {
            var response = await api.MessageAsync(token, message);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public void An_invalid_lease_length_fails_the_host_at_startup()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?> { ["Turn:LeaseSeconds"] = "1" });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("LeaseSeconds", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_ip_and_start_rate_limit_rejections_are_counted_by_source()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["RateLimiting:SessionStartsPerWindow"] = "1",
            ["RateLimiting:IpRequestsPerWindow"] = "6",
        });
        var telemetry = factory.Services.GetRequiredService<AgentTelemetry>();
        var rejections = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, telemetry.Meter) && instrument.Name == AgentTelemetry.RateLimitRejectionsInstrument)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "source")
                {
                    lock (rejections)
                    {
                        rejections.Add(tag.Value as string);
                    }
                }
            }
        });
        listener.Start();
        var api = new TestApi(factory.CreateClient());

        Assert.Equal(HttpStatusCode.Created, (await api.StartAsync()).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.StartAsync()).Response.StatusCode);
        // Every request, including rejected ones, counts against the per-IP window; keep calling until it trips.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10 && !statuses.Contains(HttpStatusCode.TooManyRequests); i++)
        {
            statuses.Add((await api.Http.GetAsync("/api/v1/lookups/countries")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        lock (rejections)
        {
            Assert.Equal(["start", "ip"], rejections);
        }
    }

    private sealed class GatedLlmClient : ILlmClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LlmChatResult([new TextBlock("done")], LlmStopReason.EndTurn, LlmUsage.None);
        }
    }
}
