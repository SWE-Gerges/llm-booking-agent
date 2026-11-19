using Agent.Abstractions;
using Agent.Abstractions.Config;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Persistence.Caching;

/// <summary>
/// Single-process cache. Sessions are stored as JSON, exactly like the Redis cache, so a cached copy can never alias
/// live mutable state and both implementations behave identically.
/// </summary>
internal sealed class MemoryChatCache(IMemoryCache cache, IOptions<SessionOptions> options) : IChatCache
{
    public Task<SessionContext?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(cache.TryGetValue(Key(sessionId), out string? json) && json is not null
            ? AgentJson.Deserialize<SessionContext>(json)
            : null);

    public Task SetAsync(SessionContext session, CancellationToken cancellationToken)
    {
        cache.Set(Key(session.SessionId), AgentJson.Serialize(session), new MemoryCacheEntryOptions { SlidingExpiration = options.Value.IdleTtl });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cache.Remove(Key(sessionId));
        return Task.CompletedTask;
    }

    private static string Key(string sessionId) => $"chat:session:{sessionId}";
}

/// <summary>
/// Redis cache with a sliding idle TTL. A Redis outage degrades to cache misses (the session is rebuilt from the
/// database) instead of failing the request.
/// </summary>
internal sealed class RedisChatCache(IConnectionMultiplexer redis, IOptions<SessionOptions> options, ILogger<RedisChatCache> logger) : IChatCache
{
    public async Task<SessionContext?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetSetExpiryAsync(Key(sessionId), options.Value.IdleTtl).WaitAsync(cancellationToken);
            return value.IsNullOrEmpty ? null : AgentJson.Deserialize<SessionContext>(value.ToString());
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis read failed; treating as a cache miss");
            return null;
        }
    }

    public async Task SetAsync(SessionContext session, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(session.SessionId), AgentJson.Serialize(session), options.Value.IdleTtl).WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis write failed; the session will be rebuilt from the database on the next request");
        }
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Key(sessionId)).WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis delete failed");
        }
    }

    private static RedisKey Key(string sessionId) => $"chat:session:{sessionId}";
}
