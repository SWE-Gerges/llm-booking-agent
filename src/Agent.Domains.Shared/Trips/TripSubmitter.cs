using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Agent.Domains.Shared.Trips;

/// <summary>
/// The only path from the trip basket to a booking. Lines are recomputed from the confirmed selection of each
/// service, travellers come from the stored form, and one persisted idempotency key makes replays resolve to the same
/// booking. Nothing here accepts a price, a service or a person from the caller.
/// </summary>
public sealed class TripSubmitter(
    IBookingContextStore contexts,
    TripBasket basket,
    IBookingBackend backend,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<TripSubmitter> logger)
{
    public async Task<SubmittedBooking> SubmitTripAsync(string sessionId, CancellationToken cancellationToken)
    {
        var trip = await contexts.GetAsync(sessionId, ServiceType.Trip, cancellationToken)
            ?? throw new ToolExecutionException("The trip is empty. Add confirmed services to the trip first.");

        if (trip.BookingId is not null)
        {
            var existing = await backend.GetStatusAsync(trip.BookingId, cancellationToken);
            if (existing is not null)
            {
                return new SubmittedBooking(
                    new BookingConfirmation(existing.BookingId, existing.Service, existing.Status, existing.Total, existing.PaymentUrl, existing.CreatedAt),
                    Replayed: true);
            }
        }

        var state = await basket.ViewAsync(sessionId, cancellationToken);
        if (state.Lines.Count == 0)
        {
            throw new ToolExecutionException("The trip is empty. Add confirmed services to the trip first.");
        }

        if (!state.ReadyToSubmit || state.Total is not { } total)
        {
            throw new ToolExecutionException(
                $"These trip services need a fresh price confirmation: {string.Join(", ", state.NeedsPriceCheck.Select(s => s.ToString().ToLowerInvariant()))}.");
        }

        if (trip.Form is not { } form)
        {
            throw new ToolExecutionException("The traveller details have not been submitted through the trip form yet. Show the form first.");
        }

        trip = await contexts.MutateAsync(
            sessionId,
            ServiceType.Trip,
            c => c.IdempotencyKey is null ? c with { IdempotencyKey = Guid.NewGuid().ToString("N") } : c,
            cancellationToken);

        var lines = state.Lines
            .Select(l => new BookingLine(l.Service, l.ItemId!, l.OptionId, l.QuoteId!, l.Total!, l.PartySize))
            .ToList();
        var confirmation = await backend.SubmitAsync(
            new BookingSubmission(ServiceType.Trip, "trip", null, $"trip-{trip.IdempotencyKey![..8]}", total, form.Raw, trip.IdempotencyKey!, lines),
            cancellationToken);

        await contexts.MutateAsync(sessionId, ServiceType.Trip, c => c.BookingId is null ? c with { BookingId = confirmation.BookingId } : c, cancellationToken);

        // The selections are consumed by this booking: booking them again needs a new, explicit confirmation.
        foreach (var line in state.Lines)
        {
            await contexts.MutateAsync(
                sessionId,
                line.Service,
                c => c with { SelectedItemId = null, SelectedOptionId = null, ConfirmedPrice = null },
                cancellationToken);
        }

        await audit.WriteAsync(
            new AuditEntry("assistant", "booking.submitted", sessionId, $"Trip booking {confirmation.BookingId} with {lines.Count} service(s)", clock.GetUtcNow()),
            cancellationToken);
        logger.LogInformation("Trip booking {BookingId} submitted with {Count} services", confirmation.BookingId, lines.Count);
        return new SubmittedBooking(confirmation, Replayed: false);
    }
}
