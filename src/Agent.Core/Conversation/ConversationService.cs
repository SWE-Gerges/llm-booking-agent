using System.Security.Cryptography;
using Agent.Abstractions.Config;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Conversation;

/// <summary>
/// Owns the session life cycle: cache first, rebuild from the durable stores on a miss, and write-through on save.
/// The rebuilt session is identical to the cached one because every field that influences behaviour is persisted.
/// </summary>
public sealed class ConversationService(
    ISessionStore sessions,
    IMessageStore messages,
    IChatCache cache,
    TimeProvider clock,
    IOptions<SessionOptions> sessionOptions,
    IOptions<AgentOptions> agentOptions,
    ILogger<ConversationService> logger)
{
    public async Task<SessionContext> CreateAsync(string? locale, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var session = new SessionContext
        {
            SessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Locale = locale is "en" or "ar" ? locale : agentOptions.Value.DefaultLocale,
            CreatedAt = now,
            LastActivityAt = now,
            LastSequence = 0,
            WindowStartSequence = 1,
        };

        await sessions.CreateAsync(session, cancellationToken);
        await cache.SetAsync(session, cancellationToken);
        return session;
    }

    /// <exception cref="SessionNotFoundException">The id is unknown.</exception>
    /// <exception cref="SessionExpiredException">The session was idle for longer than the configured TTL.</exception>
    public async Task<SessionContext> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await cache.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            session = await sessions.FindAsync(sessionId, cancellationToken) ?? throw new SessionNotFoundException(sessionId);
            session.History = (await messages.LoadFromAsync(sessionId, session.WindowStartSequence, cancellationToken)).ToList();
            logger.LogDebug("Session {SessionId} rebuilt from the database with {Count} messages", sessionId[..Math.Min(8, sessionId.Length)], session.History.Count);
        }

        if (clock.GetUtcNow() - session.LastActivityAt > sessionOptions.Value.IdleTtl)
        {
            await cache.RemoveAsync(sessionId, cancellationToken);
            throw new SessionExpiredException(sessionId);
        }

        return session;
    }

    /// <summary>Assigns sequence numbers, stores the messages and adds them to the in-memory history.</summary>
    public async Task AppendAsync(SessionContext session, IReadOnlyList<ConversationMessage> newMessages, CancellationToken cancellationToken)
    {
        if (newMessages.Count == 0)
        {
            return;
        }

        var numbered = new List<ConversationMessage>(newMessages.Count);
        foreach (var message in newMessages)
        {
            session.LastSequence++;
            numbered.Add(message with { Sequence = session.LastSequence });
        }

        await messages.AppendAsync(session.SessionId, numbered, cancellationToken);
        session.History.AddRange(numbered);
    }

    /// <summary>Trims the prompt window, touches the session and writes it to the cache and the database.</summary>
    public async Task SaveAsync(SessionContext session, CancellationToken cancellationToken)
    {
        var trimmed = HistoryTrimmer.Trim(session.History, agentOptions.Value.HistoryWindow, agentOptions.Value.TrimChunk);
        session.History = trimmed.ToList();
        session.WindowStartSequence = session.History.Count > 0 ? session.History[0].Sequence : session.LastSequence + 1;
        session.LastActivityAt = clock.GetUtcNow();

        await sessions.SaveAsync(session, cancellationToken);
        await cache.SetAsync(session, cancellationToken);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(sessionId, cancellationToken);
        if (!await sessions.DeleteAsync(sessionId, cancellationToken))
        {
            throw new SessionNotFoundException(sessionId);
        }
    }
}
