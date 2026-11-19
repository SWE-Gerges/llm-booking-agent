using System.Text.Json;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Options;
using Agent.Core.Tools;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared.Trips;

internal static class TripView
{
    public static object Present(TripState state) => new
    {
        services = state.Services.Select(s => s.ToString().ToLowerInvariant()),
        lines = state.Lines.Select(l => new
        {
            service = l.Service.ToString().ToLowerInvariant(),
            itemId = l.ItemId,
            total = l.Total,
            partySize = l.PartySize,
            status = l.Status == TripLineStatus.Ready ? "ready" : "needs_price_check",
        }),
        total = state.Total,
        travellers = state.Travellers,
        passportRequired = state.PassportRequired,
        formSubmitted = state.FormSubmitted,
        bookingId = state.BookingId,
        readyToSubmit = state.ReadyToSubmit,
    };

    public static ServiceType ParseService(string value) => Enum.Parse<ServiceType>(value, ignoreCase: true);
}

public sealed class AddToTripTool(TripBasket basket) : ChatToolBase(
    "add_to_trip",
    "Add a service whose price is already confirmed (flight, hotel or transfer) to the trip basket for one combined checkout. The server decides what may be added.",
    """{"type":"object","properties":{"service":{"type":"string","enum":["flight","hotel","transfer"]}},"required":["service"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var state = await basket.AddAsync(context.SessionId, TripView.ParseService(input.GetString("service")!), cancellationToken);
        return Result(TripView.Present(state), "trip");
    }
}

public sealed class ViewTripTool(TripBasket basket) : ChatToolBase(
    "view_trip",
    "Show the trip basket: services, confirmed prices, the total, how many travellers are needed and whether the traveller form was submitted.",
    """{"type":"object","properties":{},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken) =>
        Result(TripView.Present(await basket.ViewAsync(context.SessionId, cancellationToken)), "trip");
}

public sealed class RemoveFromTripTool(TripBasket basket) : ChatToolBase(
    "remove_from_trip",
    "Remove a service from the trip basket. The traveller form must then be filled in again.",
    """{"type":"object","properties":{"service":{"type":"string","enum":["flight","hotel","transfer"]}},"required":["service"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var state = await basket.RemoveAsync(context.SessionId, TripView.ParseService(input.GetString("service")!), cancellationToken);
        return Result(TripView.Present(state), "trip");
    }
}

public sealed class CollectTripDetailsTool(TripBasket basket) : ChatToolBase(
    "collect_trip_details",
    "Show the one combined traveller form for the whole trip. Never ask for traveller details in chat; call this instead.",
    """{"type":"object","properties":{},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var state = await basket.ViewAsync(context.SessionId, cancellationToken);
        if (state.BookingId is not null || state.Lines.Count == 0)
        {
            throw new ToolExecutionException("The trip is empty or already booked. Add a confirmed service to the trip first.");
        }

        if (state.NeedsPriceCheck.Count > 0)
        {
            throw new ToolExecutionException(
                $"Confirm the price again for: {string.Join(", ", state.NeedsPriceCheck.Select(s => s.ToString().ToLowerInvariant()))}, then show the form.");
        }

        return Result(
            TripFormSchema.Build(state.Travellers, state.PassportRequired),
            "trip_form",
            modelContent: """{"status":"form_shown","instruction":"The secure trip form is now displayed to the user. Do not ask for traveller details in chat. Wait for the user to submit it."}""");
    }
}

public sealed class SubmitTripBookingTool(TripSubmitter submitter, IBookingContextStore contexts) : ChatToolBase(
    "submit_trip_booking",
    "Create the combined booking for every service in the trip and move it to a single payment. Takes no arguments: prices, selections and travellers come from the server.",
    """{"type":"object","properties":{},"additionalProperties":false}"""), IFinalizingTool
{
    public ServiceType Service => ServiceType.Trip;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var submitted = await submitter.SubmitTripAsync(context.SessionId, cancellationToken);
        var confirmation = submitted.Confirmation;
        var trip = await contexts.GetAsync(context.SessionId, ServiceType.Trip, cancellationToken);

        var payload = new
        {
            bookingId = confirmation.BookingId,
            status = confirmation.Status.ToString(),
            total = confirmation.Total,
            paymentUrl = confirmation.PaymentUrl,
            replayed = submitted.Replayed,
            services = trip?.TripServices.Select(s => s.ToString().ToLowerInvariant()),
        };
        return ResultWithView(
            payload,
            "booking_created",
            new { bookingId = confirmation.BookingId, status = confirmation.Status.ToString(), total = confirmation.Total, replayed = submitted.Replayed, nextStep = "The user completes one payment on the secure payment page." });
    }
}

/// <summary>
/// The trip checkout is validated against the live per-service confirmations, not against a stored price: it is
/// refused unless every service in the basket has a fresh confirmed price. A trip that is already booked passes so a
/// replayed submit returns the same booking.
/// </summary>
public sealed class TripFinalizeRequiresConfirmedPricesPolicy(TripBasket basket) : IToolCallPolicy
{
    public async ValueTask<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken cancellationToken)
    {
        if (context.Tool is not IFinalizingTool { Service: ServiceType.Trip })
        {
            return PolicyDecision.Allow;
        }

        var state = await basket.ViewAsync(context.Session.SessionId, cancellationToken);
        if (state.BookingId is not null || state.ReadyToSubmit)
        {
            return PolicyDecision.Allow;
        }

        return state.Lines.Count == 0
            ? PolicyDecision.Deny("The trip is empty. Add confirmed services to the trip first.")
            : PolicyDecision.Deny(
                $"These trip services need a fresh price confirmation before checkout: {string.Join(", ", state.NeedsPriceCheck.Select(s => s.ToString().ToLowerInvariant()))}.");
    }
}
