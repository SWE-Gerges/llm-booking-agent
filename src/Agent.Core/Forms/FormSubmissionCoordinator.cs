using Agent.Abstractions.Forms;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Conversation;
using Agent.Core.Language;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Forms;

public sealed record FormSubmissionOutcome(FormSubmissionResult Result, string? Message, string Locale);

/// <summary>
/// Runs a form submission outside the model: the registered handler validates and stores the data, and the
/// conversation gets a hidden marker plus a visible next-step message so the model knows the form was filled in
/// without ever seeing the values.
/// </summary>
public sealed class FormSubmissionCoordinator(
    IServiceProvider services,
    ConversationService conversation,
    ITurnGate gate,
    Localizer localizer,
    TimeProvider clock)
{
    public async Task<FormSubmissionOutcome> SubmitAsync<TRequest>(string sessionId, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
    {
        var handler = services.GetRequiredService<IFormSubmissionHandler<TRequest>>();
        await using var turnLease = await gate.TryEnterAsync(sessionId, cancellationToken) ?? throw new TurnInProgressException(sessionId);

        var session = await conversation.LoadAsync(sessionId, cancellationToken);
        var result = await handler.HandleAsync(sessionId, request, cancellationToken);
        if (!result.Success)
        {
            return new FormSubmissionOutcome(result, null, session.Locale);
        }

        var message = localizer.Get(MessageKey.FormAccepted, session.Locale);
        var now = clock.GetUtcNow();
        await conversation.AppendAsync(
            session,
            [
                new ConversationMessage(
                    Guid.NewGuid().ToString("N"),
                    StoredMessageRole.User,
                    [new TextBlock($"[The user submitted the {handler.FormType} details form. The details are stored securely and are not shown to you.]")],
                    false,
                    now),
                new ConversationMessage(Guid.NewGuid().ToString("N"), StoredMessageRole.Notice, [new TextBlock(message)], true, now),
            ],
            cancellationToken);
        await conversation.SaveAsync(session, cancellationToken);
        return new FormSubmissionOutcome(result, message, session.Locale);
    }
}
