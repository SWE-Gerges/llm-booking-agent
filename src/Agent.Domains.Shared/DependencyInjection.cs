using Agent.Core;
using Agent.Domains.Shared.Trips;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Domains.Shared;

public static class DependencyInjection
{
    /// <summary>Registers payment methods, booking submission/status and the escalation tool backed by in-memory fakes.</summary>
    public static IServiceCollection AddSharedDomain(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FakeBackendOptions>()
            .Bind(configuration.GetSection(FakeBackendOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IPaymentBackend, InMemoryPaymentBackend>();
        services.AddSingleton<InMemoryBookingBackend>();
        services.AddSingleton<IBookingBackend>(sp => sp.GetRequiredService<InMemoryBookingBackend>());
        services.AddSingleton<SearchSnapshots>();
        services.AddSingleton<BookingSubmitter>();
        services.AddSingleton<BookingStatusService>();

        services.AddOptions<TripOptions>()
            .Bind(configuration.GetSection(TripOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<TripBasket>();
        services.AddSingleton<TripSubmitter>();
        services.AddToolCallPolicy<TripFinalizeRequiresConfirmedPricesPolicy>();
        services.AddChatTool<AddToTripTool>();
        services.AddChatTool<ViewTripTool>();
        services.AddChatTool<RemoveFromTripTool>();
        services.AddChatTool<CollectTripDetailsTool>();
        services.AddChatTool<SubmitTripBookingTool>();
        services.AddFormHandler<TripFormRequest, TripFormHandler>();

        services.AddChatTool<GetPaymentMethodsTool>();
        services.AddChatTool<GetBookingStatusTool>();
        services.AddChatTool<EscalateToHumanTool>();
        return services;
    }
}
