using Agent.Abstractions.Sessions;

namespace Agent.Abstractions.Ports;

/// <summary>Short-lived cache of hydrated sessions. A miss is always recoverable from the stores.</summary>
public interface IChatCache
{
    Task<SessionContext?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Stores the session and (re)starts its idle TTL.</summary>
    Task SetAsync(SessionContext session, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed record RateLimitDecision(bool IsAllowed, TimeSpan RetryAfter)
{
    public static readonly RateLimitDecision Allowed = new(true, TimeSpan.Zero);
}

public interface ISessionRateLimiter
{
    Task<RateLimitDecision> TryAcquireAsync(string sessionId, CancellationToken cancellationToken);
}
