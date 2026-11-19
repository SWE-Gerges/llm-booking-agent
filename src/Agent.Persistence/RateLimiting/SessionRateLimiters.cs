using System.Collections.Concurrent;
using Agent.Abstractions.Config;
using Agent.Abstractions.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Persistence.RateLimiting;

/// <summary>Fixed-window counter per session held in process memory. Used when no Redis is configured, and as the Redis fallback.</summary>
internal sealed class InMemorySessionRateLimiter(IOptions<RateLimitOptions> options, TimeProvider clock) : ISessionRateLimiter
{
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public Task<RateLimitDecision> TryAcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Task.FromResult(RateLimitDecision.Allowed);
        }

        var now = clock.GetUtcNow();
        var bucket = WindowBucket(now, settings.Window);
        var current = _windows.AddOrUpdate(
            sessionId,
            _ => new Window(bucket, 1),
            (_, existing) => existing.Bucket == bucket ? existing with { Count = existing.Count + 1 } : new Window(bucket, 1));

        if (_windows.Count > PruneThreshold)
        {
            foreach (var stale in _windows.Where(w => w.Value.Bucket < bucket).Select(w => w.Key).ToList())
            {
                _windows.TryRemove(stale, out _);
            }
        }

        return Task.FromResult(
            current.Count <= settings.SessionMessagesPerWindow
                ? RateLimitDecision.Allowed
                : new RateLimitDecision(false, RetryAfter(now, settings.Window)));
    }

    internal static long WindowBucket(DateTimeOffset now, TimeSpan window) => now.ToUnixTimeSeconds() / (long)window.TotalSeconds;

    internal static TimeSpan RetryAfter(DateTimeOffset now, TimeSpan window)
    {
        var seconds = (long)window.TotalSeconds;
        var remaining = seconds - (now.ToUnixTimeSeconds() % seconds);
        return TimeSpan.FromSeconds(Math.Max(1, remaining));
    }

    private sealed record Window(long Bucket, int Count);
}

/// <summary>
/// Fixed-window counter in Redis. The increment and the expiry are sent in one transaction on a window-bucketed key,
/// so there is no read-modify-write and a crash cannot leave a counter that never expires.
/// </summary>
internal sealed class RedisSessionRateLimiter(
    IConnectionMultiplexer redis,
    InMemorySessionRateLimiter fallback,
    IOptions<RateLimitOptions> options,
    TimeProvider clock,
    ILogger<RedisSessionRateLimiter> logger) : ISessionRateLimiter
{
    public async Task<RateLimitDecision> TryAcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return RateLimitDecision.Allowed;
        }

        var now = clock.GetUtcNow();
        var bucket = InMemorySessionRateLimiter.WindowBucket(now, settings.Window);
        RedisKey key = $"ratelimit:session:{sessionId}:{bucket}";

        try
        {
            var transaction = redis.GetDatabase().CreateTransaction();
            var increment = transaction.StringIncrementAsync(key);
            _ = transaction.KeyExpireAsync(key, settings.Window * 2);
            if (!await transaction.ExecuteAsync().WaitAsync(cancellationToken))
            {
                throw new RedisException("The rate-limit transaction was not committed.");
            }

            var count = await increment;
            return count <= settings.SessionMessagesPerWindow
                ? RateLimitDecision.Allowed
                : new RateLimitDecision(false, InMemorySessionRateLimiter.RetryAfter(now, settings.Window));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis rate limiter unavailable; using the in-memory limiter for this request");
            return await fallback.TryAcquireAsync(sessionId, cancellationToken);
        }
    }
}
