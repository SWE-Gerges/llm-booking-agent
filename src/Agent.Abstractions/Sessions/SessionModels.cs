using System.Text.Json.Serialization;
using Agent.Abstractions.Llm;

namespace Agent.Abstractions.Sessions;

public enum SessionState
{
    Active,
    Escalated,
}

public enum ServiceType
{
    Flight,
    Hotel,
    Transfer,

    /// <summary>The combined checkout of several services: one form, one payment, one booking.</summary>
    Trip,
}

/// <summary>How a stored message maps onto the provider conversation. Tool traffic is stored but never shown to the end user.</summary>
public enum StoredMessageRole
{
    User,
    Assistant,
    ToolCall,
    ToolResult,

    /// <summary>A server-authored message for the end user (for example a form acknowledgement). Never sent to the model.</summary>
    Notice,
}

public sealed record ConversationMessage(
    string MessageId,
    StoredMessageRole Role,
    IReadOnlyList<ContentBlock> Blocks,
    bool IsVisible,
    DateTimeOffset CreatedAt)
{
    /// <summary>Monotonic per-session position, assigned when the message is appended to a session.</summary>
    public long Sequence { get; init; }

    [JsonIgnore]
    public LlmRole LlmRole => Role is StoredMessageRole.User or StoredMessageRole.ToolResult ? LlmRole.User : LlmRole.Assistant;

    public LlmMessage ToLlmMessage() => new(LlmRole, Blocks);

    [JsonIgnore]
    public string VisibleText => string.Concat(Blocks.OfType<TextBlock>().Select(b => b.Text));
}

/// <summary>
/// The hydrated state of one conversation: what the orchestrator needs to run a turn. Everything that influences
/// behaviour is persisted with the session so that a cache miss rebuilds an identical object.
/// </summary>
public sealed class SessionContext
{
    public required string SessionId { get; init; }

    public SessionState State { get; set; } = SessionState.Active;

    /// <summary>Session default locale, used when a message carries no script signal (digits only, emoji).</summary>
    public string Locale { get; set; } = "en";

    public List<ServiceType> RequestedServices { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Highest message sequence assigned so far.</summary>
    public long LastSequence { get; set; }

    /// <summary>First sequence still inside the model window. Everything before it was trimmed from the prompt.</summary>
    public long WindowStartSequence { get; set; }

    public string? RepeatHash { get; set; }

    public int RepeatCount { get; set; }

    public List<ConversationMessage> History { get; set; } = [];

    public void AddRequestedService(ServiceType service)
    {
        if (!RequestedServices.Contains(service))
        {
            RequestedServices.Add(service);
        }
    }
}

/// <summary>Mutable bookkeeping for the turn that is currently running. Policies read it, the orchestrator writes it.</summary>
public sealed class TurnState
{
    private readonly List<string> _toolsCalled = [];

    public IReadOnlyList<string> ToolsCalled => _toolsCalled;

    public int SearchCount { get; private set; }

    public bool FirstSearchCarriedAssumption { get; private set; }

    public bool CorrectedSearchUsed { get; private set; }

    public void RecordCall(string toolName, bool isSearch, bool carriedAssumption)
    {
        _toolsCalled.Add(toolName);
        if (!isSearch)
        {
            return;
        }

        SearchCount++;
        if (SearchCount == 1)
        {
            FirstSearchCarriedAssumption = carriedAssumption;
        }
        else
        {
            CorrectedSearchUsed = true;
        }
    }
}
