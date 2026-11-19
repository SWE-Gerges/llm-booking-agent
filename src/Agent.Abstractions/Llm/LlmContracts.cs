using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Abstractions.Llm;

public enum LlmRole
{
    User,
    Assistant,
}

public enum LlmStopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
}

/// <summary>A single block of message content. Serialised with a "type" discriminator so history can be stored as JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock;

public sealed record LlmMessage(LlmRole Role, IReadOnlyList<ContentBlock> Content)
{
    public static LlmMessage User(string text) => new(LlmRole.User, [new TextBlock(text)]);

    public static LlmMessage Assistant(string text) => new(LlmRole.Assistant, [new TextBlock(text)]);
}

public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record LlmChatRequest
{
    /// <summary>Persona, rules and tool policy. Must be byte-identical between turns so the provider can cache it.</summary>
    public required string SystemStable { get; init; }

    /// <summary>Per-turn context such as today's date. Sent after the cacheable prefix.</summary>
    public required string SystemVolatile { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];

    public int MaxTokens { get; init; } = 1024;

    public double? Temperature { get; init; }

    /// <summary>Opaque conversation id, available to clients that need to correlate calls. Never sent to a provider.</summary>
    public string? ConversationId { get; init; }
}

public sealed record LlmUsage(int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0)
{
    public static readonly LlmUsage None = new(0, 0);
}

public sealed record LlmChatResult(IReadOnlyList<ContentBlock> Blocks, LlmStopReason StopReason, LlmUsage Usage);

/// <summary>Provider-agnostic chat port. Implementations must be safe to call concurrently.</summary>
public interface ILlmClient
{
    Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken);
}

/// <summary>How a provider failure should be treated by whoever sees it.</summary>
public enum LlmErrorKind
{
    /// <summary>Worth retrying: connection errors, timeouts, 408, 429 and 5xx.</summary>
    Transient,

    /// <summary>Retrying the same request will not help: other 4xx, malformed responses.</summary>
    Permanent,

    /// <summary>The provider refused to answer because of a safety or content policy.</summary>
    Blocked,
}

/// <summary>Raised when a model provider returns a non-success response or an unusable body.</summary>
public sealed class LlmApiException : Exception
{
    public LlmApiException(
        string message,
        int? statusCode = null,
        string? bodyExcerpt = null,
        Exception? inner = null,
        LlmErrorKind? kind = null,
        TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        BodyExcerpt = bodyExcerpt;
        Kind = kind ?? Classify(statusCode);
        RetryAfter = retryAfter;
    }

    public int? StatusCode { get; }

    public string? BodyExcerpt { get; }

    public LlmErrorKind Kind { get; }

    /// <summary>The delay the provider asked for (Retry-After), when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>No status means the request never completed (connection, timeout): transient. 408, 429 and 5xx are transient; other statuses are not.</summary>
    public static LlmErrorKind Classify(int? statusCode) => statusCode switch
    {
        null => LlmErrorKind.Transient,
        408 or 429 or >= 500 => LlmErrorKind.Transient,
        _ => LlmErrorKind.Permanent,
    };
}
