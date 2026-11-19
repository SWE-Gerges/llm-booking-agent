using System.ComponentModel.DataAnnotations;
using Agent.Abstractions.Config;
using Agent.Abstractions.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Persistence.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the background purge runs.</summary>
    [Range(1, 1440)]
    public int RunIntervalMinutes { get; set; } = 15;

    /// <summary>Extra time kept on top of <c>Session:IdleTtlMinutes</c> before a session's data is deleted.</summary>
    [Range(0, 525600)]
    public int GraceMinutes { get; set; } = 60;

    /// <summary>Sessions deleted per statement. Keeps transactions short and lock times bounded.</summary>
    [Range(1, 5000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>Upper bound of batches per run, so one run cannot monopolise the database after a long outage.</summary>
    [Range(1, 1000)]
    public int MaxBatchesPerRun { get; set; } = 50;

    public TimeSpan RunInterval => TimeSpan.FromMinutes(RunIntervalMinutes);
}

/// <summary>
/// Deletes sessions idle for longer than the session TTL plus a grace period. Messages and booking contexts (which hold
/// the stored form data) are removed by the database cascade. Escalation requests and the audit log are deliberately
/// kept: they are an operational record that must outlive the conversation. Each batch is a single statement, so the
/// purge is idempotent, and on Postgres concurrent purgers skip rows the other one is deleting.
/// </summary>
internal sealed class SessionRetention(
    IDbContextFactory<AgentDbContext> factory,
    TimeProvider clock,
    IOptions<SessionOptions> sessionOptions,
    IOptions<RetentionOptions> retentionOptions,
    ILogger<SessionRetention> logger) : ISessionRetention
{
    public async Task<RetentionResult> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var settings = retentionOptions.Value;
        var cutoff = clock.GetUtcNow() - sessionOptions.Value.IdleTtl - TimeSpan.FromMinutes(settings.GraceMinutes);

        var deleted = 0;
        var batches = 0;
        var truncated = false;

        while (true)
        {
            if (batches == settings.MaxBatchesPerRun)
            {
                truncated = true;
                break;
            }

            var removed = await DeleteBatchAsync(cutoff, settings.BatchSize, cancellationToken);
            if (removed == 0)
            {
                break;
            }

            batches++;
            deleted += removed;
            if (removed < settings.BatchSize)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Retention removed {Sessions} expired sessions in {Batches} batches (truncated: {Truncated})", deleted, batches, truncated);
        }

        return new RetentionResult(deleted, batches, truncated);
    }

    private async Task<int> DeleteBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if (db.Database.IsNpgsql())
        {
            // SKIP LOCKED lets two purgers work through disjoint rows instead of waiting on or deadlocking with each other.
            return await db.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM chat_sessions
                WHERE "Id" IN (
                    SELECT "Id" FROM chat_sessions
                    WHERE "LastActivityAt" < {cutoff}
                    ORDER BY "LastActivityAt"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED)
                """,
                cancellationToken);
        }

        var ids = await db.Sessions
            .Where(s => s.LastActivityAt < cutoff)
            .OrderBy(s => s.LastActivityAt)
            .Take(batchSize)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        return ids.Count == 0
            ? 0
            : await db.Sessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>Runs the purge on a timer driven by <see cref="TimeProvider"/>: once at start, then every interval.</summary>
internal sealed class SessionRetentionService(
    ISessionRetention retention,
    TimeProvider clock,
    IOptions<RetentionOptions> options,
    ILogger<SessionRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Session retention is disabled");
            return;
        }

        using var timer = new PeriodicTimer(options.Value.RunInterval, clock);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await retention.PurgeExpiredAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Session retention run failed; it will be retried at the next interval");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal static class RetentionSetup
{
    public static IServiceCollection AddSessionRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ISessionRetention, SessionRetention>();
        services.AddHostedService<SessionRetentionService>();
        return services;
    }
}
