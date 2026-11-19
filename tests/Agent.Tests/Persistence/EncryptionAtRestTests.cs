using System.Text.Json;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Persistence;
using Agent.Persistence.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Tests.Persistence;

public sealed class EncryptionAtRestTests : IDisposable
{
    private const string PlainName = "Zebediah Quillfeather";
    private const string PlainEmail = "zebediah.quillfeather@example.com";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-enc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<ServiceProvider> _providers = [];

    public EncryptionAtRestTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder cleanup is best effort.
        }
    }

    [Fact]
    public async Task Booking_contexts_round_trip_through_encryption()
    {
        await using var host = await NewHostAsync("ring-a");
        var sessionId = await host.CreateSessionAsync();

        await host.StoreContextAsync(sessionId);
        var loaded = await host.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("offer-1", loaded.SelectedItemId);
        Assert.Equal(PlainName, loaded.Form!.Raw["passengers[0].fullName"]);
        Assert.Equal(PlainEmail, loaded.Form.Raw["contact.email"]);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task The_database_column_holds_ciphertext_not_json_or_plaintext()
    {
        await using var host = await NewHostAsync("ring-a");
        var sessionId = await host.CreateSessionAsync();
        await host.StoreContextAsync(sessionId);

        var raw = host.ReadRawContextColumn();

        Assert.DoesNotContain("Zebediah", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("offer-1", raw, StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(raw));
    }

    [Fact]
    public async Task A_second_host_with_the_same_key_ring_reads_what_the_first_wrote()
    {
        await using var first = await NewHostAsync("ring-a");
        var sessionId = await first.CreateSessionAsync();
        await first.StoreContextAsync(sessionId);

        await using var restarted = await NewHostAsync("ring-a", first.DatabasePath);

        var loaded = await restarted.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None);
        Assert.Equal(PlainEmail, loaded!.Form!.Raw["contact.email"]);
    }

    [Fact]
    public async Task A_different_key_ring_cannot_read_the_data_and_does_not_disturb_the_first_host()
    {
        await using var first = await NewHostAsync("ring-a");
        var sessionId = await first.CreateSessionAsync();
        await first.StoreContextAsync(sessionId);

        await using var foreign = await NewHostAsync("ring-b", first.DatabasePath);

        await AssertFailsClosedAsync(() => foreign.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None));
        await AssertFailsClosedAsync(() => foreign.Contexts.ListAsync(sessionId, CancellationToken.None));
        await AssertFailsClosedAsync(() => foreign.Contexts.MutateAsync(sessionId, ServiceType.Flight, c => c, CancellationToken.None));

        // The converter of one container must not leak into the other through EF's model cache.
        var stillReadable = await first.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None);
        Assert.Equal(PlainName, stillReadable!.Form!.Raw["passengers[0].fullName"]);
    }

    [Fact]
    public async Task Tampered_ciphertext_fails_closed_on_every_read_path()
    {
        await using var host = await NewHostAsync("ring-a");
        var sessionId = await host.CreateSessionAsync();
        await host.StoreContextAsync(sessionId);

        var original = host.ReadRawContextColumn();
        var middle = original.Length / 2;
        var tampered = original[..middle] + (original[middle] == 'A' ? 'B' : 'A') + original[(middle + 1)..];
        host.WriteRawContextColumn(tampered);

        await AssertFailsClosedAsync(() => host.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None));
        await AssertFailsClosedAsync(() => host.Contexts.ListAsync(sessionId, CancellationToken.None));
        await AssertFailsClosedAsync(() => host.Contexts.MutateAsync(sessionId, ServiceType.Flight, c => c with { SelectedItemId = "other" }, CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_plaintext_rows_are_refused_instead_of_being_read_partially()
    {
        await using var host = await NewHostAsync("ring-a");
        var sessionId = await host.CreateSessionAsync();
        await host.StoreContextAsync(sessionId);
        host.WriteRawContextColumn("""{"sessionId":"x","serviceType":"Flight","selectedItemId":"offer-1"}""");

        await AssertFailsClosedAsync(() => host.Contexts.GetAsync(sessionId, ServiceType.Flight, CancellationToken.None));
    }

    [Fact]
    public async Task The_failure_message_reveals_neither_the_stored_value_nor_the_cause()
    {
        await using var host = await NewHostAsync("ring-a");
        var protector = host.Provider.GetRequiredService<IFormDataProtector>();

        var failure = Assert.Throws<FormDataProtectionException>(() => protector.Unprotect("definitely-not-protected-data"));

        Assert.Equal("Stored data could not be decrypted.", failure.Message);
        Assert.DoesNotContain("definitely-not-protected-data", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protecting_the_same_text_twice_gives_different_ciphertexts_that_both_decrypt()
    {
        await using var host = await NewHostAsync("ring-a");
        var protector = host.Provider.GetRequiredService<IFormDataProtector>();

        var one = protector.Protect("same text");
        var two = protector.Protect("same text");

        Assert.NotEqual(one, two);
        Assert.Equal("same text", protector.Unprotect(one));
        Assert.Equal("same text", protector.Unprotect(two));
    }

    [Fact]
    public void The_key_ring_path_is_required_in_production_but_optional_in_development()
    {
        Assert.True(Validate(Environments.Production, null).Failed);
        Assert.True(Validate(Environments.Production, "  ").Failed);
        Assert.True(Validate(Environments.Production, "/keys").Succeeded);
        Assert.True(Validate(Environments.Development, null).Succeeded);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string environment, string? path) =>
        new FormDataProtectionOptionsValidator(new FakeEnvironment(environment))
            .Validate(null, new FormDataProtectionOptions { KeyRingPath = path });

    private static async Task AssertFailsClosedAsync<T>(Func<Task<T>> read)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(read);
        Assert.True(HasInner<FormDataProtectionException>(failure), $"Expected a FormDataProtectionException but got {failure.GetType().Name}: {failure.Message}");
    }

    private static bool HasInner<TException>(Exception? exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<EncryptedHost> NewHostAsync(string keyRing, string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["ConnectionStrings:Sqlite"] = $"Data Source={path};Pooling=False;Default Timeout=30",
            ["DataProtection:KeyRingPath"] = Path.Combine(_directory, keyRing),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAgentPersistence(configuration);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        await provider.InitializeAgentDatabaseAsync();
        return new EncryptedHost(provider, path);
    }

    private sealed class EncryptedHost(ServiceProvider provider, string databasePath) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public string DatabasePath { get; } = databasePath;

        public IBookingContextStore Contexts => Provider.GetRequiredService<IBookingContextStore>();

        public async Task<string> CreateSessionAsync()
        {
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await Provider.GetRequiredService<ISessionStore>().CreateAsync(
                new SessionContext { SessionId = id, CreatedAt = now, LastActivityAt = now, WindowStartSequence = 1 },
                CancellationToken.None);
            return id;
        }

        public Task StoreContextAsync(string sessionId) =>
            Contexts.MutateAsync(
                sessionId,
                ServiceType.Flight,
                c => c with
                {
                    SelectedItemId = "offer-1",
                    Form = new FormRecord(
                        new Dictionary<string, string>
                        {
                            ["passengers[0].fullName"] = PlainName,
                            ["contact.email"] = PlainEmail,
                        },
                        new Dictionary<string, string> { ["passengers[0].fullName"] = "Z*** Q***", ["contact.email"] = "z***@example.com" },
                        DateTimeOffset.UtcNow),
                },
                CancellationToken.None);

        public string ReadRawContextColumn()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DataJson FROM booking_contexts LIMIT 1";
            return (string)command.ExecuteScalar()!;
        }

        public void WriteRawContextColumn(string value)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE booking_contexts SET DataJson = $value";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
