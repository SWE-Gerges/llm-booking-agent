using System.Text.Json;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;

namespace Agent.Abstractions.Tools;

/// <summary>A capability the model may invoke. Inputs are validated against <see cref="InputSchema"/> before execution.</summary>
public interface IChatTool
{
    string Name { get; }

    string Description { get; }

    JsonElement InputSchema { get; }

    Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken);
}

/// <summary>Marks a tool that starts a search, so turn-level policies can reason about repeated searching.</summary>
public interface ISearchTool
{
    ServiceType Service { get; }
}

/// <summary>Marks a tool that commits a booking. Such tools must read money and identity only from server-side state.</summary>
public interface IFinalizingTool
{
    ServiceType Service { get; }
}

public sealed class ToolContext
{
    public ToolContext(SessionContext session, TurnState turn)
    {
        Session = session;
        Turn = turn;
    }

    /// <summary>Mutable session state for the running turn. It is persisted by the orchestrator when the turn ends.</summary>
    public SessionContext Session { get; }

    public TurnState Turn { get; }

    public string SessionId => Session.SessionId;
}

public sealed record ToolResult
{
    /// <summary>JSON returned to the API consumer. Never shown to the model unless <see cref="ModelContent"/> is null.</summary>
    public required string Content { get; init; }

    /// <summary>What the model sees. When null, the sanitised <see cref="Content"/> is used.</summary>
    public string? ModelContent { get; init; }

    /// <summary>Extra instruction or disclosure for the model, for example an assumption it must tell the user about.</summary>
    public string? ModelNote { get; init; }

    /// <summary>UI/state tag for the API consumer, for example "flight_results".</summary>
    public string? State { get; init; }

    public bool IsError { get; init; }

    public static ToolResult Ok(string contentJson, string? state = null, string? modelContent = null, string? modelNote = null) =>
        new() { Content = contentJson, State = state, ModelContent = modelContent, ModelNote = modelNote };

    public static ToolResult Error(string message) =>
        new() { Content = JsonSerializer.Serialize(new { error = message }), IsError = true };
}

public sealed record PolicyDecision(bool IsAllowed, string? MessageForModel)
{
    public static readonly PolicyDecision Allow = new(true, null);

    public static PolicyDecision Deny(string messageForModel) => new(false, messageForModel);
}

public sealed record PolicyContext(
    SessionContext Session,
    TurnState Turn,
    ToolUseBlock PendingCall,
    IChatTool Tool,
    DateTimeOffset Now);

/// <summary>A composable rule evaluated before every tool call. The first denial wins and is shown to the model.</summary>
public interface IToolCallPolicy
{
    ValueTask<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken cancellationToken);
}
