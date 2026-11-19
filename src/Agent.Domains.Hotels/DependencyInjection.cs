using Agent.Core;
using Agent.Domains.Hotels.Forms;
using Agent.Domains.Hotels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Domains.Hotels;

public static class DependencyInjection
{
    /// <summary>Registers the six hotel tools, the guest form handler and the in-memory hotel backend.</summary>
    public static IServiceCollection AddHotelDomain(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHotelBackend, InMemoryHotelBackend>();

        services.AddChatTool<SearchHotelsTool>();
        services.AddChatTool<FilterHotelsTool>();
        services.AddChatTool<GetHotelRoomsTool>();
        services.AddChatTool<PrebookRoomTool>();
        services.AddChatTool<CollectGuestDetailsTool>();
        services.AddChatTool<SubmitHotelBookingTool>();
        services.AddFormHandler<GuestFormRequest, GuestFormHandler>();
        return services;
    }
}
