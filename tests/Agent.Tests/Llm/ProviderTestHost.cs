using Agent.Abstractions.Llm;
using Agent.Llm;
using Agent.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent.Tests.LlmClients;

/// <summary>
/// The real <c>AddAgentLlm</c> registration (typed client, headers, resilience pipeline) for any provider, over a fake
/// transport. Retry delays are zero so the retry path runs instantly.
/// </summary>
internal sealed class ProviderTestHost : IDisposable
{
    public const string ApiKey = "test-key-do-not-leak-123";

    private readonly ServiceProvider _provider;

    public ProviderTestHost(string provider, HttpMessageHandler handler, IDictionary<string, string?>? overrides = null)
    {
        PrimaryHandler = handler;
        Logs = new CapturingLoggerProvider();

        var settings = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = provider,
            [$"{provider}:ApiKey"] = ApiKey,
            [$"{provider}:BaseUrl"] = "http://localhost:1/",
            [$"{provider}:Model"] = "test-model",
            [$"{provider}:RetryDelayMilliseconds"] = "0",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddAgentLlm(configuration);
        services.AddHttpClient(nameof(ILlmClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
        _provider = services.BuildServiceProvider();
        Client = _provider.GetRequiredService<ILlmClient>();
    }

    public HttpMessageHandler PrimaryHandler { get; }

    public FakeHttpHandler Handler => (FakeHttpHandler)PrimaryHandler;

    public CapturingLoggerProvider Logs { get; }

    public ILlmClient Client { get; }

    public IServiceProvider Services => _provider;

    public void Dispose() => _provider.Dispose();
}
