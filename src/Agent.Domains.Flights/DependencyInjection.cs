using Agent.Core;
using Agent.Domains.Flights.Forms;
using Agent.Domains.Flights.Tools;
using Agent.Domains.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Agent.Domains.Flights;

public static class DependencyInjection
{
    /// <summary>Registers the seven flight tools, the passenger form handler and the selected flight backend.</summary>
    public static IServiceCollection AddFlightDomain(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        var options = new FlightBackendOptions();
        configuration.GetSection(FlightBackendOptions.SectionName).Bind(options);
        services.AddOptions<FlightBackendOptions>().Bind(configuration.GetSection(FlightBackendOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        if (options.Provider == FlightBackendProvider.Http)
        {
            services.AddHttpClient<IFlightBackend, HttpFlightBackend>((provider, client) =>
                {
                    var settings = provider.GetRequiredService<IOptions<FlightBackendOptions>>().Value;
                    client.BaseAddress = new Uri(settings.BaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                    if (!string.IsNullOrEmpty(settings.ApiKey))
                    {
                        client.DefaultRequestHeaders.Add("x-api-key", settings.ApiKey);
                    }
                })
                .AddResilienceHandler("flight-supplier", builder =>
                {
                    var retry = new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 2,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(300),
                        ShouldHandle = args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
                    };

                    // Only idempotent reads are retried; the POST price check must never be replayed automatically.
                    retry.DisableForUnsafeHttpMethods();
                    builder.AddRetry(retry);
                });
        }
        else
        {
            services.AddSingleton<IFlightBackend, InMemoryFlightBackend>();
        }

        services.AddChatTool<SearchFlightsTool>();
        services.AddChatTool<FilterFlightOffersTool>();
        services.AddChatTool<GetFlightDetailsTool>();
        services.AddChatTool<GetBrandedFaresTool>();
        services.AddChatTool<RecheckFlightPriceTool>();
        services.AddChatTool<CollectPassengerDetailsTool>();
        services.AddChatTool<SubmitFlightBookingTool>();
        services.AddFormHandler<PassengerFormRequest, PassengerFormHandler>();
        return services;
    }
}
