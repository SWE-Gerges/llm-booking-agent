using Agent.Core;
using Agent.Domains.Transfers.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Domains.Transfers;

public static class DependencyInjection
{
    /// <summary>Registers the four transfer tools and the in-memory transfer backend. Transfers are booked through the trip basket.</summary>
    public static IServiceCollection AddTransferDomain(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITransferBackend, InMemoryTransferBackend>();

        services.AddChatTool<SearchTransfersTool>();
        services.AddChatTool<GetTransferDetailsTool>();
        services.AddChatTool<GetTransferPriceTool>();
        services.AddChatTool<SelectTransferTool>();
        return services;
    }
}
