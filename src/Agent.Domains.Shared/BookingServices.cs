using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Agent.Domains.Shared;

public sealed record SubmittedBooking(BookingConfirmation Confirmation, bool Replayed);

/// <summary>
/// The only path from conversation to a booking. Money, selection and personal details are read from the stored
/// booking context; nothing here accepts a price or a person from the caller. An idempotency key is minted once and
/// persisted, so replays (retries, double clicks, duplicate tool calls) resolve to the same booking.
/// </summary>
public sealed class BookingSubmitter(
    IBookingContextStore contexts,
    IBookingBackend backend,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<BookingSubmitter> logger)
{
    public async Task<SubmittedBooking> SubmitAsync(string sessionId, ServiceType service, CancellationToken cancellationToken)
    {
        var context = await contexts.GetAsync(sessionId, service, cancellationToken)
            ?? throw new ToolExecutionException("Nothing has been selected yet. Search and choose an option first.");

        if (context.BookingId is not null)
        {
            var existing = await backend.GetStatusAsync(context.BookingId, cancellationToken);
            if (existing is not null)
            {
                return new SubmittedBooking(
                    new BookingConfirmation(existing.BookingId, existing.Service, existing.Status, existing.Total, existing.PaymentUrl, existing.CreatedAt),
                    Replayed: true);
            }
        }

        if (context.SelectedItemId is null || context.ConfirmedPrice is not { } price)
        {
            throw new ToolExecutionException("The price has not been confirmed for a selected option. Re-check the price first.");
        }

        if (context.Form is not { } form)
        {
            throw new ToolExecutionException("The traveller or guest details have not been submitted through the form yet. Show the form first.");
        }

        // The key is minted inside the mutation so concurrent first calls converge on a single persisted value.
        context = await contexts.MutateAsync(
            sessionId,
            service,
            c => c.IdempotencyKey is null ? c with { IdempotencyKey = Guid.NewGuid().ToString("N") } : c,
            cancellationToken);

        var confirmation = await backend.SubmitAsync(
            new BookingSubmission(service, context.SelectedItemId!, context.SelectedOptionId, price.QuoteId, price.Total, form.Raw, context.IdempotencyKey!),
            cancellationToken);

        await contexts.MutateAsync(sessionId, service, c => c.BookingId is null ? c with { BookingId = confirmation.BookingId } : c, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("assistant", "booking.submitted", sessionId, $"{service} booking {confirmation.BookingId}", clock.GetUtcNow()),
            cancellationToken);

        logger.LogInformation("Booking {BookingId} submitted for a {Service} selection", confirmation.BookingId, service);
        return new SubmittedBooking(confirmation, Replayed: false);
    }
}

/// <summary>Looks up booking status, but only for bookings that belong to the session asking.</summary>
public sealed class BookingStatusService(IBookingContextStore contexts, IBookingBackend backend)
{
    /// <summary>Returns the session's bookings, or just <paramref name="bookingId"/> when given. Foreign ids yield an empty list.</summary>
    public async Task<IReadOnlyList<BookingStatusInfo>> GetAsync(string sessionId, string? bookingId, CancellationToken cancellationToken)
    {
        var owned = (await contexts.ListAsync(sessionId, cancellationToken))
            .SelectMany(c => c.PastBookingIds.Append(c.BookingId))
            .OfType<string>()
            .Where(id => bookingId is null || string.Equals(id, bookingId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var statuses = new List<BookingStatusInfo>(owned.Count);
        foreach (var id in owned)
        {
            if (await backend.GetStatusAsync(id, cancellationToken) is { } status)
            {
                statuses.Add(status);
            }
        }

        return statuses;
    }
}
