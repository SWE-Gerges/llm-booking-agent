using Agent.Abstractions;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Microsoft.EntityFrameworkCore;
using Entities = Agent.Persistence.Entities;

namespace Agent.Persistence.Stores;

internal sealed class SessionStore(IDbContextFactory<AgentDbContext> factory) : ISessionStore
{
    public async Task CreateAsync(SessionContext session, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Sessions.Add(new Entities.ChatSession { Id = Guid.CreateVersion7() }.Apply(session));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SessionContext?> FindAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.SessionToken == sessionId, cancellationToken);
        return entity is null ? null : ToContext(entity);
    }

    public async Task SaveAsync(SessionContext session, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Sessions.SingleOrDefaultAsync(s => s.SessionToken == session.SessionId, cancellationToken)
            ?? throw new SessionNotFoundException(session.SessionId);
        entity.Apply(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.Where(s => s.SessionToken == sessionId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static SessionContext ToContext(Entities.ChatSession entity) => new()
    {
        SessionId = entity.SessionToken,
        State = Enum.Parse<SessionState>(entity.State, ignoreCase: true),
        Locale = entity.Locale,
        RequestedServices = entity.RequestedServices
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Enum.Parse<ServiceType>(s, ignoreCase: true))
            .ToList(),
        CreatedAt = entity.CreatedAt,
        LastActivityAt = entity.LastActivityAt,
        LastSequence = entity.LastSequence,
        WindowStartSequence = entity.WindowStartSequence,
        RepeatHash = entity.RepeatHash,
        RepeatCount = entity.RepeatCount,
    };
}

internal static class SessionEntityMapping
{
    public static Entities.ChatSession Apply(this Entities.ChatSession entity, SessionContext session)
    {
        entity.SessionToken = session.SessionId;
        entity.State = session.State.ToString();
        entity.Locale = session.Locale;
        entity.RequestedServices = string.Join(',', session.RequestedServices);
        entity.CreatedAt = session.CreatedAt;
        entity.LastActivityAt = session.LastActivityAt;
        entity.LastSequence = session.LastSequence;
        entity.WindowStartSequence = session.WindowStartSequence;
        entity.RepeatHash = session.RepeatHash;
        entity.RepeatCount = session.RepeatCount;
        return entity;
    }
}

internal sealed class MessageStore(IDbContextFactory<AgentDbContext> factory) : IMessageStore
{
    public async Task AppendAsync(string sessionId, IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var internalId = await db.Sessions
            .Where(s => s.SessionToken == sessionId)
            .Select(s => (Guid?)s.Id)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new SessionNotFoundException(sessionId);

        db.Messages.AddRange(messages.Select(m => new Entities.ChatMessage
        {
            Id = Guid.CreateVersion7(),
            MessageId = m.MessageId,
            SessionId = internalId,
            Sequence = m.Sequence,
            Role = m.Role.ToString(),
            IsVisible = m.IsVisible,
            BlocksJson = AgentJson.Serialize<IReadOnlyList<ContentBlock>>(m.Blocks),
            CreatedAt = m.CreatedAt,
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> LoadFromAsync(string sessionId, long fromSequence, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.Session!.SessionToken == sessionId && m.Sequence >= fromSequence)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);
        return rows.Select(ToMessage).ToList();
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetVisibleAsync(string sessionId, int limit, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.Session!.SessionToken == sessionId && m.IsVisible)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows.Select(ToMessage).ToList();
    }

    private static ConversationMessage ToMessage(Entities.ChatMessage row) => new(
        row.MessageId,
        Enum.Parse<StoredMessageRole>(row.Role, ignoreCase: true),
        AgentJson.Deserialize<List<ContentBlock>>(row.BlocksJson) ?? [],
        row.IsVisible,
        row.CreatedAt)
    {
        Sequence = row.Sequence,
    };
}
