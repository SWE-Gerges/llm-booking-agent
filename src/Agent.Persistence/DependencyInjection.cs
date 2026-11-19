using System.ComponentModel.DataAnnotations;
using Agent.Abstractions.Config;
using Agent.Abstractions.Ports;
using Agent.Persistence.Caching;
using Agent.Persistence.Locking;
using Agent.Persistence.RateLimiting;
using Agent.Persistence.Retention;
using Agent.Persistence.Security;
using Agent.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Agent.Persistence;

public enum DatabaseProvider
{
    Postgres,
    Sqlite,
}

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Postgres;

    /// <summary>Applies the committed migrations (Postgres) or creates the schema (SQLite) when the host starts.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Connection string for the selected provider; read from ConnectionStrings:Postgres or ConnectionStrings:Sqlite.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "A connection string for the selected Persistence:Provider is required.")]
    public string ConnectionString { get; set; } = string.Empty;
}

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    /// <summary>Registers EF Core stores, the chat cache (Redis when configured, else memory) and the session rate limiter.</summary>
    public static IServiceCollection AddAgentPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetValue($"{PersistenceOptions.SectionName}:Provider", DatabaseProvider.Postgres);
        var connectionString = configuration.GetConnectionString(provider.ToString()) ?? string.Empty;

        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .Configure(o => o.ConnectionString = connectionString)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddAgentDataProtection(configuration);
        services.AddSessionRetention(configuration);
        services.AddDbContextFactory<AgentDbContext>((_, builder) =>
        {
            if (provider == DatabaseProvider.Sqlite)
            {
                builder.UseSqlite(connectionString);
            }
            else
            {
                builder.UseNpgsql(connectionString);
            }

            builder.ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>();
        });

        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IMessageStore, MessageStore>();
        services.AddSingleton<IBookingContextStore, BookingContextStore>();
        services.AddSingleton<EscalationStore>();
        services.AddSingleton<IEscalationStore>(sp => sp.GetRequiredService<EscalationStore>());
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<EscalationStore>());

        var health = services.AddHealthChecks();
        health.AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]);

        var redisConnection = configuration.GetConnectionString("Redis");
        services.AddSingleton<InMemorySessionRateLimiter>();
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddMemoryCache();
            services.AddSingleton<IChatCache, MemoryChatCache>();
            services.AddSingleton<ISessionRateLimiter>(sp => sp.GetRequiredService<InMemorySessionRateLimiter>());
        }
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redisConnection);
                options.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddSingleton<IChatCache, RedisChatCache>();
            services.AddSingleton<ISessionRateLimiter, RedisSessionRateLimiter>();
            services.AddRedisTurnGate(configuration);
            health.AddCheck<RedisHealthCheck>("redis", tags: [ReadyTag]);
        }

        return services;
    }

    /// <summary>Prepares the schema. Postgres applies committed migrations (with a short wait for the server); SQLite creates the schema.</summary>
    public static async Task InitializeAgentDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var settings = services.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        if (!settings.MigrateOnStartup)
        {
            return;
        }

        var factory = services.GetRequiredService<IDbContextFactory<AgentDbContext>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Persistence.Startup");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if (settings.Provider == DatabaseProvider.Sqlite)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await PrepareMigrationHistoryAsync(db, cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    /// <summary>
    /// EF reads the migration history inside Migrate and, on an empty database, logs the expected failure of that
    /// read as an error. Creating the database and an empty history table first makes Migrate see a normal, empty
    /// history, so a first start logs nothing at warning level or above and real SQL failures stay visible.
    /// </summary>
    private static async Task PrepareMigrationHistoryAsync(AgentDbContext db, CancellationToken cancellationToken)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
        {
            await creator.CreateAsync(cancellationToken);
        }

        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken);
    }
}

internal sealed class DatabaseHealthCheck(IDbContextFactory<AgentDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database is not reachable.");
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (RedisException ex)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
