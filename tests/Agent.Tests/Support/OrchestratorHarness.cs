using Agent.Abstractions.Chat;
using Agent.Abstractions.Sessions;
using Agent.Core;
using Agent.Core.Conversation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.Support;

internal static class TestOptions
{
    public static IOptions<T> Of<T>(T value)
        where T : class => Microsoft.Extensions.Options.Options.Create(value);
}

/// <summary>Real Core services (via <c>AddAgentCore</c>) wired to in-memory stores and a fake model.</summary>
internal sealed class OrchestratorHarness : IDisposable
{
    private readonly ServiceProvider _services;

    private OrchestratorHarness(ServiceProvider services)
    {
        _services = services;
        Clock = (FakeTimeProvider)services.GetRequiredService<TimeProvider>();
        Llm = services.GetRequiredService<FakeLlmClient>();
        Sessions = services.GetRequiredService<InMemorySessionStore>();
        Messages = services.GetRequiredService<InMemoryMessageStore>();
        Cache = services.GetRequiredService<InMemoryChatCache>();
        RateLimiter = services.GetRequiredService<FakeRateLimiter>();
        Logs = services.GetRequiredService<CapturingLoggerProvider>();
    }

    public FakeTimeProvider Clock { get; }

    public FakeLlmClient Llm { get; }

    public InMemorySessionStore Sessions { get; }

    public InMemoryMessageStore Messages { get; }

    public InMemoryChatCache Cache { get; }

    public FakeRateLimiter RateLimiter { get; }

    public CapturingLoggerProvider Logs { get; }

    public IChatOrchestrator Orchestrator => _services.GetRequiredService<IChatOrchestrator>();

    public ConversationService Conversation => _services.GetRequiredService<ConversationService>();

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public static OrchestratorHarness Create(
        IEnumerable<FakeTool>? tools = null,
        IDictionary<string, string?>? config = null,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();

        var logs = new CapturingLoggerProvider();
        services.AddSingleton(logs);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<InMemorySessionStore>();
        services.AddSingleton<Agent.Abstractions.Ports.ISessionStore>(p => p.GetRequiredService<InMemorySessionStore>());
        services.AddSingleton<InMemoryMessageStore>();
        services.AddSingleton<Agent.Abstractions.Ports.IMessageStore>(p => p.GetRequiredService<InMemoryMessageStore>());
        services.AddSingleton<InMemoryChatCache>();
        services.AddSingleton<Agent.Abstractions.Ports.IChatCache>(p => p.GetRequiredService<InMemoryChatCache>());
        services.AddSingleton<FakeRateLimiter>();
        services.AddSingleton<Agent.Abstractions.Ports.ISessionRateLimiter>(p => p.GetRequiredService<FakeRateLimiter>());
        services.AddSingleton<Agent.Abstractions.Ports.IBookingContextStore, InMemoryBookingContextStore>();
        services.AddSingleton<FakeLlmClient>();
        services.AddSingleton<Agent.Abstractions.Llm.ILlmClient>(p => p.GetRequiredService<FakeLlmClient>());

        foreach (var tool in tools ?? [])
        {
            services.AddSingleton<Agent.Abstractions.Tools.IChatTool>(tool);
        }

        services.AddAgentCore(configuration);
        configure?.Invoke(services);
        return new OrchestratorHarness(services.BuildServiceProvider());
    }

    public async Task<string> StartAsync(string? locale = "en")
    {
        var session = await Conversation.CreateAsync(locale, CancellationToken.None);
        return session.SessionId;
    }

    public Task<ChatReply> SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        Orchestrator.HandleMessageAsync(sessionId, text, cancellationToken);

    public Task<IReadOnlyList<ConversationMessage>> StoredAsync(string sessionId) =>
        Messages.LoadFromAsync(sessionId, 0, CancellationToken.None);

    public void Dispose() => _services.Dispose();
}
