using System.Text.Json;

namespace Agent.Abstractions.Chat;

public enum ChatOutcome
{
    /// <summary>The model produced the reply.</summary>
    Completed,

    /// <summary>A guard answered without calling the model.</summary>
    Guarded,

    /// <summary>The per-session rate limit was hit. <see cref="ChatReply.RetryAfter"/> is set.</summary>
    RateLimited,

    /// <summary>The conversation was handed to a human; the model is no longer consulted.</summary>
    Escalated,

    /// <summary>The model or a deadline failed and the reply is a localized apology. History was still persisted.</summary>
    Failed,
}

/// <summary>Raw tool output for the API consumer, tagged with a UI/state label.</summary>
public sealed record StructuredDataBlock(string State, JsonElement Data);

public sealed record ChatReply(
    string Reply,
    string Locale,
    string Direction,
    string State,
    IReadOnlyList<StructuredDataBlock> StructuredData,
    ChatOutcome Outcome = ChatOutcome.Completed,
    TimeSpan? RetryAfter = null);

public interface IChatOrchestrator
{
    Task<ChatReply> HandleMessageAsync(string sessionId, string message, CancellationToken cancellationToken);
}
