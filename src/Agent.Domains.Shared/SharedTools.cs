using System.Text.Json;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Options;
using Agent.Core.Tools;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared;

public sealed class GetPaymentMethodsTool(IPaymentBackend payments, IOptions<AgentOptions> options) : ChatToolBase(
    "get_payment_methods",
    "List the payment methods available for the booking in progress. Payment itself happens on a secure page, never in chat.",
    """{"type":"object","properties":{"service":{"type":"string","enum":["flight","hotel","transfer","trip"],"description":"Which booking the payment is for. Use trip for a combined checkout. Defaults to the most recent service."}},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var service = input.GetString("service") is { } name
            ? Enum.Parse<ServiceType>(name, ignoreCase: true)
            : context.Session.RequestedServices.LastOrDefault();

        var methods = await payments.GetPaymentMethodsAsync(service, options.Value.Currency, cancellationToken);
        return Result(new { service = service.ToString().ToLowerInvariant(), currency = options.Value.Currency, methods }, "payment_methods");
    }
}

public sealed class GetBookingStatusTool(BookingStatusService statuses) : ChatToolBase(
    "get_booking_status",
    "Report the status of a booking made in this conversation. Omit bookingId to list every booking of this conversation.",
    """{"type":"object","properties":{"bookingId":{"type":"string","maxLength":64}},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var found = await statuses.GetAsync(context.SessionId, input.GetString("bookingId"), cancellationToken);
        if (found.Count == 0)
        {
            throw new ToolExecutionException("No matching booking exists in this conversation.");
        }

        return Result(new { bookings = found.Select(Present) }, "booking_status");
    }

    private static object Present(BookingStatusInfo s) => new
    {
        bookingId = s.BookingId,
        service = s.Service.ToString().ToLowerInvariant(),
        status = s.Status.ToString(),
        total = s.Total,
        createdAt = s.CreatedAt,
        confirmedAt = s.ConfirmedAt,
    };
}

public sealed class EscalateToHumanTool(IEscalationStore escalations, TimeProvider clock) : ChatToolBase(
    "escalate_to_human",
    "Hand the conversation to a human agent. Use when you cannot help, a tool keeps failing, or the user asks for a person.",
    """{"type":"object","properties":{"reason":{"type":"string","minLength":3,"maxLength":500},"summary":{"type":"string","maxLength":2000}},"required":["reason"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        await escalations.CreateAsync(
            new EscalationRequest(id, context.SessionId, input.GetString("reason")!, input.GetString("summary"), clock.GetUtcNow()),
            cancellationToken);

        context.Session.State = SessionState.Escalated;
        return Result(
            new { escalationId = id, status = "open" },
            "escalated",
            modelContent: """{"status":"escalated","instruction":"A human agent will follow up. Tell the user, and do not take further booking actions."}""");
    }
}
