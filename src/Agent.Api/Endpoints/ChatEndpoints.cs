using System.Globalization;
using System.Security.Claims;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Api.Contracts;
using Agent.Api.Infrastructure;
using Agent.Api.Security;
using Agent.Core.Conversation;
using Agent.Core.Forms;
using Agent.Core.Language;
using Agent.Core.Options;
using Agent.Domains.Flights.Forms;
using Agent.Domains.Hotels.Forms;
using Agent.Domains.Shared.Trips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace Agent.Api.Endpoints;

public static class AuthPolicies
{
    public const string ClientKey = "ClientKey";
    public const string Session = "Session";
}

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/api/v1/chat").WithTags("Chat");

        chat.MapPost("/start", StartAsync)
            .RequireAuthorization(AuthPolicies.ClientKey)
            .RequireRateLimiting(RateLimitingSetup.StartPolicy)
            .WithName("StartChat")
            .WithSummary("Start a chat session and mint a short-lived session token.")
            .Produces<StartChatResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        chat.MapPost("/message", SendMessageAsync)
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("SendMessage")
            .WithSummary("Send a user message and receive the assistant reply with structured tool output.")
            .Produces<ChatMessageResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        chat.MapGet("/history", GetHistoryAsync)
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("GetHistory")
            .WithSummary("Return the visible conversation, oldest first.")
            .Produces<HistoryResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        chat.MapDelete("/session", DeleteSessionAsync)
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("DeleteSession")
            .WithSummary("End the session and erase its messages and booking data.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        chat.MapPost("/forms/passenger", (PassengerFormRequest request, ClaimsPrincipal user, FormSubmissionCoordinator forms, CancellationToken ct) =>
                SubmitFormAsync(request, user, forms, ct))
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("SubmitPassengerForm")
            .WithSummary("Submit the passenger details form (bypasses the model).")
            .Produces<FormAcceptedResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        chat.MapPost("/forms/trip", (TripFormRequest request, ClaimsPrincipal user, FormSubmissionCoordinator forms, CancellationToken ct) =>
                SubmitFormAsync(request, user, forms, ct))
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("SubmitTripForm")
            .WithSummary("Submit the one combined traveller form of a trip (bypasses the model).")
            .Produces<FormAcceptedResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        chat.MapPost("/forms/guest", (GuestFormRequest request, ClaimsPrincipal user, FormSubmissionCoordinator forms, CancellationToken ct) =>
                SubmitFormAsync(request, user, forms, ct))
            .RequireAuthorization(AuthPolicies.Session)
            .WithName("SubmitGuestForm")
            .WithSummary("Submit the hotel guest details form (bypasses the model).")
            .Produces<FormAcceptedResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    private static async Task<IResult> StartAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartChatRequest? request,
        ConversationService conversation,
        SessionTokenService tokens,
        Localizer localizer,
        IOptions<AgentOptions> options,
        CancellationToken cancellationToken)
    {
        var locale = request?.Locale?.Trim().ToLowerInvariant();
        if (locale is not null && locale is not ("en" or "ar"))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["locale"] = ["Locale must be 'en' or 'ar'."] },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var session = await conversation.CreateAsync(locale, cancellationToken);
        var (token, expiresAt) = tokens.Issue(session.SessionId);
        var greeting = localizer.Get(MessageKey.Greeting, session.Locale, options.Value.AssistantName);

        return Results.Created(
            "/api/v1/chat/history",
            new StartChatResponse(token, expiresAt, greeting, session.Locale, LanguageDetector.DirectionOf(session.Locale), options.Value.AssistantName));
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        IChatOrchestrator orchestrator,
        IOptions<AgentOptions> options,
        CancellationToken cancellationToken)
    {
        var message = request.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return Invalid("message", "The message must not be empty.");
        }

        if (message.Length > options.Value.MaxMessageLength)
        {
            return Invalid("message", $"The message must be at most {options.Value.MaxMessageLength} characters.");
        }

        var reply = await orchestrator.HandleMessageAsync(SessionId(user), message, cancellationToken);
        if (reply.Outcome == ChatOutcome.RateLimited)
        {
            var seconds = (int)Math.Ceiling((reply.RetryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds);
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                title: "Too many messages",
                detail: reply.Reply,
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?>
                {
                    ["locale"] = reply.Locale,
                    ["direction"] = reply.Direction,
                    ["retryAfterSeconds"] = seconds,
                });
        }

        return Results.Ok(new ChatMessageResponse(reply.Reply, reply.Locale, reply.Direction, reply.State, reply.StructuredData));
    }

    private static async Task<IResult> GetHistoryAsync(
        ClaimsPrincipal user,
        ConversationService conversation,
        IMessageStore messages,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 50)
    {
        if (limit is < 1 or > 200)
        {
            return Invalid("limit", "The limit must be between 1 and 200.");
        }

        var sessionId = SessionId(user);
        await conversation.LoadAsync(sessionId, cancellationToken);
        var visible = await messages.GetVisibleAsync(sessionId, limit, cancellationToken);
        return Results.Ok(new HistoryResponse(
            visible.Select(m => new HistoryItem(m.Role == StoredMessageRole.User ? "user" : "assistant", m.VisibleText, m.CreatedAt)).ToList()));
    }

    private static async Task<IResult> DeleteSessionAsync(
        ClaimsPrincipal user,
        ConversationService conversation,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionId(user);
        await conversation.DeleteAsync(sessionId, cancellationToken);
        await audit.WriteAsync(new AuditEntry("api", "session.deleted", sessionId, null, clock.GetUtcNow()), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SubmitFormAsync<TRequest>(
        TRequest request,
        ClaimsPrincipal user,
        FormSubmissionCoordinator forms,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        var outcome = await forms.SubmitAsync(SessionId(user), request, cancellationToken);
        if (!outcome.Result.Success)
        {
            return Results.ValidationProblem(
                outcome.Result.Errors.ToDictionary(e => e.Key, e => e.Value),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Ok(new FormAcceptedResponse(
            outcome.Message!,
            outcome.Locale,
            LanguageDetector.DirectionOf(outcome.Locale),
            outcome.Result.PaymentMethods,
            "choose_payment_method"));
    }

    private static string SessionId(ClaimsPrincipal user) => user.FindFirstValue(AuthSchemes.SessionClaim)!;

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, statusCode: StatusCodes.Status422UnprocessableEntity);
}
