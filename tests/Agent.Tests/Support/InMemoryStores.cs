using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;

namespace Agent.Tests.Support;

/// <summary>Stores session metadata as JSON so that, like a real database, it never hands out a live object.</summary>
internal sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, string> _rows = new(StringComparer.Ordinal);

    public Task CreateAsync(SessionContext session, CancellationToken cancellationToken)
    {
        Store(session);
        return Task.CompletedTask;
    }

    public Task<SessionContext?> FindAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(sessionId, out var json) ? AgentJson.Deserialize<SessionContext>(json) : null);

    public Task SaveAsync(SessionContext session, CancellationToken cancellationToken)
    {
        Store(session);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Remove(sessionId));

    private void Store(SessionContext session)
    {
        var copy = AgentJson.Deserialize<SessionContext>(AgentJson.Serialize(session))!;
        copy.History = [];
        _rows[session.SessionId] = AgentJson.Serialize(copy);
    }
}

internal sealed class InMemoryMessageStore : IMessageStore
{
    private readonly Dictionary<string, List<ConversationMessage>> _messages = new(StringComparer.Ordinal);

    public Task AppendAsync(string sessionId, IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken)
    {
        if (!_messages.TryGetValue(sessionId, out var list))
        {
            list = [];
            _messages[sessionId] = list;
        }

        list.AddRange(messages.Select(m => AgentJson.Deserialize<ConversationMessage>(AgentJson.Serialize(m))!));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationMessage>> LoadFromAsync(string sessionId, long fromSequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationMessage> result = _messages.TryGetValue(sessionId, out var list)
            ? list.Where(m => m.Sequence >= fromSequence).OrderBy(m => m.Sequence).ToList()
            : [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ConversationMessage>> GetVisibleAsync(string sessionId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationMessage> result = _messages.TryGetValue(sessionId, out var list)
            ? list.Where(m => m.IsVisible).OrderBy(m => m.Sequence).TakeLast(limit).ToList()
            : [];
        return Task.FromResult(result);
    }
}

/// <summary>JSON-backed cache with a <see cref="Clear"/> switch to simulate eviction or a restart.</summary>
internal sealed class InMemoryChatCache : IChatCache
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public Task<SessionContext?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(sessionId, out var json) ? AgentJson.Deserialize<SessionContext>(json) : null);

    public Task SetAsync(SessionContext session, CancellationToken cancellationToken)
    {
        _entries[session.SessionId] = AgentJson.Serialize(session);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        _entries.Remove(sessionId);
        return Task.CompletedTask;
    }

    public void Clear() => _entries.Clear();
}

internal sealed class FakeRateLimiter : ISessionRateLimiter
{
    public RateLimitDecision Decision { get; set; } = RateLimitDecision.Allowed;

    public int Calls { get; private set; }

    public Task<RateLimitDecision> TryAcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Decision);
    }
}

internal sealed class InMemoryBookingContextStore : IBookingContextStore
{
    private readonly Dictionary<(string, ServiceType), BookingContext> _rows = [];

    public Task<BookingContext?> GetAsync(string sessionId, ServiceType serviceType, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.GetValueOrDefault((sessionId, serviceType)));

    public Task<IReadOnlyList<BookingContext>> ListAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookingContext>>(_rows.Where(r => r.Key.Item1 == sessionId).Select(r => r.Value).ToList());

    public Task<BookingContext> MutateAsync(
        string sessionId,
        ServiceType serviceType,
        Func<BookingContext, BookingContext> mutation,
        CancellationToken cancellationToken)
    {
        var current = _rows.GetValueOrDefault((sessionId, serviceType)) ?? BookingContext.Empty(sessionId, serviceType);
        var next = mutation(current) with { Version = current.Version + 1 };
        _rows[(sessionId, serviceType)] = next;
        return Task.FromResult(next);
    }
}
