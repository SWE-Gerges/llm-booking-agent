using System.ComponentModel.DataAnnotations;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;

namespace Agent.Domains.Shared;

public enum BookingStatus
{
    PendingPayment,
    Confirmed,
    Cancelled,
}

/// <summary>One priced component of a trip. Built by the server from the confirmed selection of each service.</summary>
public sealed record BookingLine(ServiceType Service, string ItemId, string? OptionId, string QuoteId, Money Total, int PartySize);

/// <summary>
/// Everything the booking backend needs. Built by the server from stored state, never from model input. A trip
/// submission carries one line per service and a total that must equal their sum.
/// </summary>
public sealed record BookingSubmission(
    ServiceType Service,
    string ItemId,
    string? OptionId,
    string QuoteId,
    Money Total,
    IReadOnlyDictionary<string, string> Details,
    string IdempotencyKey,
    IReadOnlyList<BookingLine>? Lines = null);

public sealed record BookingConfirmation(
    string BookingId,
    ServiceType Service,
    BookingStatus Status,
    Money Total,
    string PaymentUrl,
    DateTimeOffset CreatedAt);

public sealed record BookingStatusInfo(
    string BookingId,
    ServiceType Service,
    BookingStatus Status,
    Money Total,
    string PaymentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

public interface IPaymentBackend
{
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(ServiceType service, string currency, CancellationToken cancellationToken);
}

/// <summary>Creates and reads bookings. Implementations must treat the idempotency key as a dedupe key.</summary>
public interface IBookingBackend
{
    Task<BookingConfirmation> SubmitAsync(BookingSubmission submission, CancellationToken cancellationToken);

    Task<BookingStatusInfo?> GetStatusAsync(string bookingId, CancellationToken cancellationToken);
}

public sealed class FakeBackendOptions
{
    public const string SectionName = "Backends:Fake";

    /// <summary>The in-memory booking backend reports Confirmed once a booking is this old, so status polling shows progress.</summary>
    [Range(0, 86400)]
    public int AutoConfirmAfterSeconds { get; set; } = 30;
}
