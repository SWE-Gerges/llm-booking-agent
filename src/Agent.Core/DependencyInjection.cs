using Agent.Abstractions.Chat;
using Agent.Abstractions.Config;
using Agent.Abstractions.Forms;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Tools;
using Agent.Core.Conversation;
using Agent.Core.Forms;
using Agent.Core.Guards;
using Agent.Core.Language;
using Agent.Core.Lookups;
using Agent.Core.Options;
using Agent.Core.Orchestration;
using Agent.Core.Prompting;
using Agent.Core.Telemetry;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agent.Core;

public static class DependencyInjection
{
    /// <summary>Registers the orchestrator, guards (in their fixed order), policies and supporting services.</summary>
    public static IServiceCollection AddAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Localizer>();
        services.AddSingleton<CountryCatalog>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ToolInputValidator>();
        services.AddSingleton<ToolOutputSanitizer>();
        services.AddSingleton<SystemPromptBuilder>();
        services.TryAddSingleton<ITurnGate, InProcessTurnGate>();
        services.AddSingleton<AgentTelemetry>();
        services.AddOptions<TurnOptions>()
            .Bind(configuration.GetSection(TurnOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<FormSubmissionCoordinator>();

        // Guard order is a security property: throttle first, then abuse, then content inspection.
        services.AddSingleton<IInboundGuard, SessionRateLimitGuard>();
        services.AddSingleton<IInboundGuard, RepeatedMessageGuard>();
        services.AddSingleton<IInboundGuard, SensitiveDataGuard>();
        services.AddSingleton<GuardPipeline>();

        services.AddToolCallPolicy<OneSearchPerTurnPolicy>();
        services.AddToolCallPolicy<FinalizeRequiresConfirmedPricePolicy>();

        services.AddSingleton<IChatOrchestrator, ChatOrchestrator>();
        return services;
    }

    public static IServiceCollection AddChatTool<TTool>(this IServiceCollection services)
        where TTool : class, IChatTool =>
        services.AddSingleton<IChatTool, TTool>();

    public static IServiceCollection AddToolCallPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, IToolCallPolicy =>
        services.AddSingleton<IToolCallPolicy, TPolicy>();

    public static IServiceCollection AddFormHandler<TRequest, THandler>(this IServiceCollection services)
        where TRequest : class
        where THandler : class, IFormSubmissionHandler<TRequest> =>
        services.AddSingleton<IFormSubmissionHandler<TRequest>, THandler>();
}
