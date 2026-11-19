using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Agent.Llm.Shared;

/// <summary>What the resilience pipeline of one provider needs from its options.</summary>
internal sealed record LlmHttpSettings(Uri BaseAddress, TimeSpan AttemptTimeout, TimeSpan RetryDelay, TimeSpan MaxRetryAfter);

/// <summary>
/// The HTTP pipeline shared by every model client: a per-attempt timeout and one retry for transient failures. A model
/// call has no side effects, so retrying it is safe; retries never span a tool execution because tools run between
/// model calls. A provider's Retry-After replaces the base delay, capped so a hostile or broken header cannot park a turn.
/// </summary>
internal static class LlmResilience
{
    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        LlmHttpSettings settings,
        TimeProvider clock)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            BackoffType = DelayBackoffType.Constant,
            Delay = settings.RetryDelay,
            UseJitter = true,
            ShouldHandle = args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
            DelayGenerator = args => ValueTask.FromResult(RetryAfterDelay(args.Outcome.Result, clock, settings.MaxRetryAfter)),
        });
        builder.AddTimeout(settings.AttemptTimeout);
    }

    /// <summary>The delay to use instead of the base delay, or null to keep it. Capped at <paramref name="max"/>.</summary>
    internal static TimeSpan? RetryAfterDelay(HttpResponseMessage? response, TimeProvider clock, TimeSpan max)
    {
        if (response is null || LlmHttp.ReadRetryAfter(response, clock) is not { } requested)
        {
            return null;
        }

        return requested > max ? max : requested;
    }

    /// <summary>Registers the typed client and its pipeline; <paramref name="settings"/> maps the provider options to transport settings.</summary>
    public static void AddLlmHttpClient<TClient, TOptions>(
        this IServiceCollection services,
        string pipelineName,
        Func<TOptions, LlmHttpSettings> settings,
        Action<HttpClient, TOptions> configureHeaders)
        where TClient : class, Agent.Abstractions.Llm.ILlmClient
        where TOptions : class
    {
        services.AddHttpClient<Agent.Abstractions.Llm.ILlmClient, TClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
                var transport = settings(options);
                client.BaseAddress = transport.BaseAddress;
                client.Timeout = (transport.AttemptTimeout * 2) + transport.MaxRetryAfter + TimeSpan.FromSeconds(10);
                configureHeaders(client, options);
            })
            .AddResilienceHandler(pipelineName, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<TOptions>>().Value;
                Configure(builder, settings(options), context.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System);
            });
    }
}
