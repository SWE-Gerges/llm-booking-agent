using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Agent.IntegrationTests.Support;

/// <summary>
/// Hosts the real API in memory with the scripted model and a throw-away SQLite database. Every setting is applied
/// through <c>UseSetting</c> so that configuration read eagerly during service registration sees it.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string ClientKey = "test-client-key-0123456789";
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";

    private readonly Dictionary<string, string?> _settings = [];
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "agent-it-" + Guid.NewGuid().ToString("N"));

    public ApiFactory(string? scriptJson = null, IDictionary<string, string?>? overrides = null, Action<IServiceCollection>? configureServices = null)
    {
        _configureServices = configureServices;
        Directory.CreateDirectory(_workDir);

        var scriptPath = Path.Combine(_workDir, "script.json");
        File.WriteAllText(scriptPath, scriptJson ?? """{ "steps": [ { "text": "ok" } ] }""");

        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _settings["Llm:Provider"] = "Scripted";
        _settings["Llm:Scripted:ScriptPath"] = scriptPath;
        _settings["Persistence:Provider"] = "Sqlite";
        _settings["ConnectionStrings:Sqlite"] = $"Data Source={Path.Combine(_workDir, "agent.db")};Default Timeout=30";
        _settings["DataProtection:KeyRingPath"] = Path.Combine(_workDir, "keys");
        _settings["Auth:SessionTokenSigningKey"] = SigningKey;
        _settings["Auth:ClientKeys:0:Name"] = "test";
        _settings["Auth:ClientKeys:0:Key"] = ClientKey;
        _settings["Agent:HistoryWindow"] = "100";
        _settings["RateLimiting:SessionMessagesPerWindow"] = "1000";
        _settings["RateLimiting:IpRequestsPerWindow"] = "100000";
        _settings["RateLimiting:SessionStartsPerWindow"] = "1000";

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    public FakeTimeProvider Clock { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            _configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // A lingering file handle must not fail the test run; the OS temp folder is cleaned up eventually.
        }
    }
}
