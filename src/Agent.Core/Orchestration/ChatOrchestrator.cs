using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Conversation;
using Agent.Core.Guards;
using Agent.Core.Language;
using Agent.Core.Options;
using Agent.Core.Prompting;
using Agent.Core.Telemetry;
using Agent.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Orchestration;

/// <summary>
/// Runs one user message through guards and the model/tool loop. The model never owns money or identity: tools read
/// those from server-side state, and every outcome (including failures) is persisted so a client retry cannot repeat
/// a side effect that already happened.
/// </summary>
public sealed class ChatOrchestrator(
    ConversationService conversation,
    GuardPipeline guards,
    ILlmClient llm,
    ToolRegistry registry,
    ToolInputValidator validator,
    IEnumerable<IToolCallPolicy> policies,
    SystemPromptBuilder prompts,
    ToolOutputSanitizer sanitizer,
    Localizer localizer,
    ITurnGate gate,
    AgentTelemetry telemetry,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    ILogger<ChatOrchestrator> logger) : IChatOrchestrator
{
    private readonly IToolCallPolicy[] _policies = policies.ToArray();

    public async Task<ChatReply> HandleMessageAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        await using var turnLease = await gate.TryEnterAsync(sessionId, cancellationToken) ?? throw new TurnInProgressException(sessionId);

        using var span = telemetry.StartTurn();
        try
        {
            var reply = await HandleCoreAsync(sessionId, message, cancellationToken);
            telemetry.CompleteTurn(span, reply.Outcome);
            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteAbortedTurn(span);
            throw;
        }
    }

    private async Task<ChatReply> HandleCoreAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        var session = await conversation.LoadAsync(sessionId, cancellationToken);
        var locale = LanguageDetector.Detect(message, session.Locale);

        var guard = await guards.EvaluateAsync(new GuardContext(session, message), cancellationToken);
        if (guard.IsBlocked)
        {
            return await AnswerGuardedAsync(session, message, locale, guard, cancellationToken);
        }

        if (session.State == SessionState.Escalated)
        {
            return await AnswerEscalatedAsync(session, message, locale, cancellationToken);
        }

        return await RunTurnAsync(session, message, locale, cancellationToken);
    }

    private async Task<ChatReply> AnswerGuardedAsync(SessionContext session, string message, string locale, GuardResult guard, CancellationToken ct)
    {
        var reply = localizer.Get(guard.Reply, locale);

        if (guard.Outcome != ChatOutcome.RateLimited)
        {
            if (guard.PersistExchange)
            {
                await conversation.AppendAsync(
                    session,
                    [
                        NewMessage(StoredMessageRole.User, [new TextBlock(guard.PersistedUserText ?? message)], isVisible: true),
                        NewMessage(StoredMessageRole.Assistant, [new TextBlock(reply)], isVisible: true),
                    ],
                    ct);
            }

            await conversation.SaveAsync(session, ct);
        }

        var state = guard.Outcome == ChatOutcome.RateLimited ? "rate_limited" : "guarded";
        return new ChatReply(reply, locale, LanguageDetector.DirectionOf(locale), state, [], guard.Outcome, guard.RetryAfter);
    }

    private async Task<ChatReply> AnswerEscalatedAsync(SessionContext session, string message, string locale, CancellationToken ct)
    {
        var reply = localizer.Get(MessageKey.Escalated, locale);
        await conversation.AppendAsync(
            session,
            [
                NewMessage(StoredMessageRole.User, [new TextBlock(message)], isVisible: true),
                NewMessage(StoredMessageRole.Assistant, [new TextBlock(reply)], isVisible: true),
            ],
            ct);
        await conversation.SaveAsync(session, ct);
        return new ChatReply(reply, locale, LanguageDetector.DirectionOf(locale), "escalated", [], ChatOutcome.Escalated);
    }

    private async Task<ChatReply> RunTurnAsync(SessionContext session, string message, string locale, CancellationToken callerToken)
    {
        var settings = options.Value;
        var turn = new TurnState();
        var structured = new List<StructuredDataBlock>();
        var pending = new List<ConversationMessage>();
        var working = session.History.Where(m => m.Role != StoredMessageRole.Notice).Select(m => m.ToLlmMessage()).ToList();

        var userMessage = NewMessage(StoredMessageRole.User, [new TextBlock(message)], isVisible: true);
        pending.Add(userMessage);
        working.Add(userMessage.ToLlmMessage());

        using var deadline = new CancellationTokenSource(settings.TurnTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);

        string? finalText = null;
        MessageKey? fallback = null;
        var callerAborted = false;

        try
        {
            for (var iteration = 0; iteration < settings.MaxToolIterations; iteration++)
            {
                var result = await SendToModelAsync(BuildRequest(session, locale, working), linked.Token);

                if (result.StopReason == LlmStopReason.MaxTokens)
                {
                    fallback = MessageKey.TruncatedReply;
                    break;
                }

                var toolUses = result.Blocks.OfType<ToolUseBlock>().ToList();
                if (toolUses.Count == 0)
                {
                    var text = string.Concat(result.Blocks.OfType<TextBlock>().Select(b => b.Text)).Trim();
                    if (text.Length == 0)
                    {
                        fallback = MessageKey.EmptyReply;
                    }
                    else
                    {
                        finalText = text;
                    }

                    break;
                }

                var toolResults = new List<ContentBlock>(toolUses.Count);
                foreach (var use in toolUses)
                {
                    toolResults.Add(await ExecuteToolAsync(session, turn, use, structured, iteration, linked.Token));
                }

                var callMessage = NewMessage(StoredMessageRole.ToolCall, result.Blocks, isVisible: false);
                var resultMessage = NewMessage(StoredMessageRole.ToolResult, toolResults, isVisible: false);
                pending.Add(callMessage);
                pending.Add(resultMessage);
                working.Add(callMessage.ToLlmMessage());
                working.Add(resultMessage.ToLlmMessage());
            }

            if (finalText is null && fallback is null)
            {
                fallback = MessageKey.TooManySteps;
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            callerAborted = true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning("Turn for session {Session} hit the {Seconds}s deadline", LogSafe.Short(session.SessionId), settings.TurnTimeoutSeconds);
            fallback = MessageKey.TurnTimeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Turn for session {Session} failed", LogSafe.Short(session.SessionId));
            fallback = MessageKey.LlmFailure;
        }

        var reply = finalText ?? (fallback is { } key ? localizer.Get(key, locale) : string.Empty);
        if (!callerAborted)
        {
            pending.Add(NewMessage(StoredMessageRole.Assistant, [new TextBlock(reply)], isVisible: true));
        }

        // Persist even when the caller aborted or the model failed: tool side effects already happened, and the
        // history must show them so a retry does not repeat them. Cancellation must not interrupt this write.
        await conversation.AppendAsync(session, pending, CancellationToken.None);
        await conversation.SaveAsync(session, CancellationToken.None);

        callerToken.ThrowIfCancellationRequested();

        var state = structured.Count > 0
            ? structured[^1].State
            : session.State == SessionState.Escalated ? "escalated" : "message";

        return new ChatReply(
            reply,
            locale,
            LanguageDetector.DirectionOf(locale),
            state,
            structured,
            fallback is null ? ChatOutcome.Completed : ChatOutcome.Failed);
    }

    private LlmChatRequest BuildRequest(SessionContext session, string locale, IReadOnlyList<LlmMessage> messages)
    {
        var prompt = prompts.Build(session, locale);
        return new LlmChatRequest
        {
            SystemStable = prompt.Stable,
            SystemVolatile = prompt.Volatile,
            Messages = messages.ToList(),
            Tools = registry.Definitions,
            MaxTokens = options.Value.MaxTokens,
            Temperature = options.Value.Temperature,
            ConversationId = session.SessionId,
        };
    }

    private async Task<ToolResultBlock> ExecuteToolAsync(
        SessionContext session,
        TurnState turn,
        ToolUseBlock use,
        List<StructuredDataBlock> structured,
        int iteration,
        CancellationToken ct)
    {
        ToolResult result;
        var executed = false;
        var tool = registry.Find(use.Name);
        var toolTag = tool?.Name ?? "unknown";
        using var span = telemetry.StartToolCall(toolTag, iteration);
        ToolOutcome outcome;

        if (tool is null)
        {
            outcome = ToolOutcome.Unknown;
            result = ToolResult.Error($"Unknown tool '{use.Name}'. Use only the tools provided.");
        }
        else if (await FindDenialAsync(session, turn, use, tool, ct) is { } denial)
        {
            logger.LogInformation("Tool call {Tool} denied by policy", use.Name);
            outcome = ToolOutcome.Denied;
            result = ToolResult.Error(denial);
        }
        else if (validator.Validate(tool, use.Input) is { } problem)
        {
            outcome = ToolOutcome.Invalid;
            result = ToolResult.Error(problem);
        }
        else
        {
            executed = true;
            result = await InvokeAsync(tool, new ToolContext(session, turn), use, ct);
            outcome = result.IsError ? ToolOutcome.Error : ToolOutcome.Ok;
        }

        telemetry.CompleteToolCall(span, toolTag, outcome);

        if (executed)
        {
            turn.RecordCall(use.Name, tool is ISearchTool && !result.IsError, result.ModelNote is not null);
        }

        if (!result.IsError && result.State is { } state)
        {
            structured.Add(new StructuredDataBlock(state, ParseOrString(result.Content)));
        }

        var body = result.ModelContent is { } modelContent
            ? ToolOutputSanitizer.Wrap(modelContent)
            : sanitizer.Sanitize(result.Content);
        var text = result.ModelNote is { } note ? $"{body}\n\nNote for you (written by the server, not tool data): {note}" : body;
        return new ToolResultBlock(use.Id, text, result.IsError);
    }

    private async Task<LlmChatResult> SendToModelAsync(LlmChatRequest request, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        try
        {
            var result = await llm.SendAsync(request, ct);
            telemetry.RecordLlmCall(clock.GetElapsedTime(started), succeeded: true, result.Usage);
            return result;
        }
        catch
        {
            telemetry.RecordLlmCall(clock.GetElapsedTime(started), succeeded: false, usage: null);
            throw;
        }
    }

    private async Task<string?> FindDenialAsync(SessionContext session, TurnState turn, ToolUseBlock use, IChatTool tool, CancellationToken ct)
    {
        var context = new PolicyContext(session, turn, use, tool, clock.GetUtcNow());
        foreach (var policy in _policies)
        {
            var decision = await policy.EvaluateAsync(context, ct);
            if (!decision.IsAllowed)
            {
                return decision.MessageForModel ?? "This tool call is not allowed right now.";
            }
        }

        return null;
    }

    private async Task<ToolResult> InvokeAsync(IChatTool tool, ToolContext context, ToolUseBlock use, CancellationToken ct)
    {
        try
        {
            return await tool.ExecuteAsync(context, use.Input, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolExecutionException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool {Tool} failed", tool.Name);
            return ToolResult.Error("The tool failed unexpectedly. Try a different approach, or offer to hand over with escalate_to_human.");
        }
    }

    private ConversationMessage NewMessage(StoredMessageRole role, IReadOnlyList<ContentBlock> blocks, bool isVisible) =>
        new(Guid.NewGuid().ToString("N"), role, blocks, isVisible, clock.GetUtcNow());

    private static JsonElement ParseOrString(string content)
    {
        try
        {
            return AgentJson.Parse(content);
        }
        catch (JsonException)
        {
            return AgentJson.ToElement(content);
        }
    }
}
