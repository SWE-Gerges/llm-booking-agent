using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;

namespace Agent.Abstractions.Ports;

/// <summary>Durable session metadata. History lives in <see cref="IMessageStore"/>; sessions returned here carry an empty history.</summary>
public interface ISessionStore
{
    Task CreateAsync(SessionContext session, CancellationToken cancellationToken);

    Task<SessionContext?> FindAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveAsync(SessionContext session, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IMessageStore
{
    /// <summary>Appends messages that already carry their sequence numbers.</summary>
    Task AppendAsync(string sessionId, IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken);

    /// <summary>Returns every message with a sequence at or after <paramref name="fromSequence"/>, oldest first.</summary>
    Task<IReadOnlyList<ConversationMessage>> LoadFromAsync(string sessionId, long fromSequence, CancellationToken cancellationToken);

    /// <summary>Returns the most recent visible messages, oldest first.</summary>
    Task<IReadOnlyList<ConversationMessage>> GetVisibleAsync(string sessionId, int limit, CancellationToken cancellationToken);
}

public interface IBookingContextStore
{
    Task<BookingContext?> GetAsync(string sessionId, ServiceType serviceType, CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingContext>> ListAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="mutation"/> to the current context (or an empty one) and persists the result.
    /// Concurrent callers are serialised by the store; the mutation may run more than once and must be pure.
    /// </summary>
    Task<BookingContext> MutateAsync(
        string sessionId,
        ServiceType serviceType,
        Func<BookingContext, BookingContext> mutation,
        CancellationToken cancellationToken);
}

public sealed record EscalationRequest(string Id, string SessionId, string Reason, string? Summary, DateTimeOffset CreatedAt);

public sealed record AuditEntry(string Actor, string Action, string? SessionId, string? Details, DateTimeOffset At);

public interface IEscalationStore
{
    /// <summary>Persists the request and a matching audit row atomically.</summary>
    Task CreateAsync(EscalationRequest request, CancellationToken cancellationToken);
}

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}
