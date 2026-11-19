namespace Agent.Persistence.Entities;

public sealed class ChatSession
{
    public Guid Id { get; set; }

    /// <summary>Public, unguessable session identifier that the signed API token is bound to.</summary>
    public string SessionToken { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Locale { get; set; } = "en";

    /// <summary>Comma-separated service names the user asked about. Part of the state a rebuild must restore.</summary>
    public string RequestedServices { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public long LastSequence { get; set; }

    public long WindowStartSequence { get; set; }

    public string? RepeatHash { get; set; }

    public int RepeatCount { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];

    public List<BookingContext> BookingContexts { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }

    public string MessageId { get; set; } = string.Empty;

    public Guid SessionId { get; set; }

    public long Sequence { get; set; }

    /// <summary>User, Assistant, ToolCall or ToolResult.</summary>
    public string Role { get; set; } = string.Empty;

    public bool IsVisible { get; set; }

    /// <summary>The complete content-block list as JSON, including tool_use and tool_result blocks.</summary>
    public string BlocksJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public ChatSession? Session { get; set; }
}

public sealed class BookingContext
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public string ServiceType { get; set; } = string.Empty;

    public string DataJson { get; set; } = "{}";

    /// <summary>Optimistic concurrency token; incremented by every write.</summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ChatSession? Session { get; set; }
}

public sealed class EscalationRequest
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Kept as a plain value rather than a foreign key so a request outlives the deleted session.</summary>
    public string SessionToken { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string Status { get; set; } = "open";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AdminAuditLog
{
    public long Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? SessionToken { get; set; }

    public string? Details { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
