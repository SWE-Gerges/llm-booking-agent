using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core;
using Agent.Core.Conversation;
using Agent.Core.Tools;
using Agent.Domains.Flights;
using Agent.Domains.Hotels;
using Agent.Domains.Shared;
using Agent.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.Support;

/// <summary>
/// The real Core, persistence (temp-file SQLite) and domain registrations over a fake clock and a fake model.
/// Each instance owns its own database file, which is removed on dispose.
/// </summary>
internal sealed class DomainHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory;

    private DomainHarness(ServiceProvider services, string directory)
    {
        Services = services;
        _directory = directory;
        Clock = (FakeTimeProvider)services.GetRequiredService<TimeProvider>();
        Llm = services.GetRequiredService<FakeLlmClient>();
        Logs = services.GetRequiredService<CapturingLoggerProvider>();
    }

    public ServiceProvider Services { get; }

    public FakeTimeProvider Clock { get; }

    public FakeLlmClient Llm { get; }

    public CapturingLoggerProvider Logs { get; }

    public IBookingContextStore Contexts => Get<IBookingContextStore>();

    public ToolRegistry Registry => Get<ToolRegistry>();

    public ToolInputValidator Validator => Get<ToolInputValidator>();

    public string SqliteConnectionString { get; private init; } = string.Empty;

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public static async Task<DomainHarness> CreateAsync(IDictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "agent.db")};Pooling=False;Default Timeout=30";

        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["ConnectionStrings:Sqlite"] = connectionString,
            ["DataProtection:KeyRingPath"] = Path.Combine(directory, "keys"),
        };
        foreach (var (key, value) in config ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton(logs);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
        services.AddSingleton<FakeLlmClient>();
        services.AddSingleton<ILlmClient>(p => p.GetRequiredService<FakeLlmClient>());

        services
            .AddAgentCore(configuration)
            .AddAgentPersistence(configuration)
            .AddSharedDomain(configuration)
            .AddFlightDomain(configuration)
            .AddHotelDomain();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await provider.InitializeAgentDatabaseAsync();
        return new DomainHarness(provider, directory) { SqliteConnectionString = connectionString };
    }

    public Task<SessionContext> NewSessionAsync(string locale = "en") =>
        Get<ConversationService>().CreateAsync(locale, CancellationToken.None);

    public ToolContext ContextFor(SessionContext session, TurnState? turn = null) => new(session, turn ?? new TurnState());

    public IChatTool Tool(string name) =>
        Registry.Find(name) ?? throw new InvalidOperationException($"Tool '{name}' is not registered.");

    /// <summary>Runs a tool after checking its input against the schema, exactly as the orchestrator does.</summary>
    public async Task<ToolResult> RunAsync(SessionContext session, string toolName, string inputJson = "{}", TurnState? turn = null)
    {
        var tool = Tool(toolName);
        var input = AgentJson.Parse(inputJson);
        Assert.Null(Validator.Validate(tool, input));
        return await tool.ExecuteAsync(ContextFor(session, turn), input, CancellationToken.None);
    }

    public Task<BookingContext?> ContextAsync(SessionContext session, ServiceType service) =>
        Contexts.GetAsync(session.SessionId, service, CancellationToken.None);

    public static JsonElement Content(ToolResult result) => AgentJson.Parse(result.Content);

    public static JsonElement ModelView(ToolResult result) => AgentJson.Parse(result.ModelContent!);

    public static List<T> ReadList<T>(JsonElement element, string property) =>
        element.GetProperty(property).Deserialize<List<T>>(AgentJson.Options)!;

    public void Dispose()
    {
        Services.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
