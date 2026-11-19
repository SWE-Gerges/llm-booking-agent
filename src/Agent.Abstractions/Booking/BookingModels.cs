using Agent.Abstractions.Sessions;

namespace Agent.Abstractions.Booking;

public sealed record Money(decimal Amount, string Currency);

public sealed record PaymentMethod(string Id, string Type, string Label);

/// <summary>Results of the last search for one service. Later tools resolve items from here, never from model-supplied data.</summary>
/// <param name="PartySize">Travellers the search was for; the trip basket derives how many traveller records it needs from it.</param>
public sealed record SearchSnapshot(string Handle, DateTimeOffset CreatedAt, string ParamsJson, string ResultsJson, int PartySize = 1);

/// <summary>A price the server re-checked (flights) or pre-booked (hotels) with the supplier.</summary>
public sealed record PriceConfirmation(string QuoteId, Money Total, DateTimeOffset ConfirmedAt);

/// <summary>Traveller or guest details submitted through a form. <see cref="Masked"/> is the only copy safe for logs and prompts.</summary>
public sealed record FormRecord(
    IReadOnlyDictionary<string, string> Raw,
    IReadOnlyDictionary<string, string> Masked,
    DateTimeOffset SubmittedAt);

/// <summary>
/// Server-owned state of a booking in progress. The model can steer the conversation but never writes price,
/// passenger or payment fields; submit tools read exclusively from here.
/// </summary>
public sealed record BookingContext
{
    public required string SessionId { get; init; }

    public required ServiceType ServiceType { get; init; }

    public SearchSnapshot? Search { get; init; }

    public string? SelectedItemId { get; init; }

    public string? SelectedOptionId { get; init; }

    public PriceConfirmation? ConfirmedPrice { get; init; }

    public FormRecord? Form { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? BookingId { get; init; }

    /// <summary>For the trip context: the services currently in the basket. Written only by the server-side basket, never from model input.</summary>
    public IReadOnlyList<ServiceType> TripServices { get; init; } = [];

    /// <summary>Facts about the confirmed selection that later steps need, for example "passport_required".</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>Bookings this session completed earlier for the same service. Kept so status lookups can verify ownership.</summary>
    public IReadOnlyList<string> PastBookingIds { get; init; } = [];

    /// <summary>Optimistic concurrency token managed by the store. Mutations must not change it.</summary>
    public long Version { get; init; }

    public static BookingContext Empty(string sessionId, ServiceType serviceType) =>
        new() { SessionId = sessionId, ServiceType = serviceType };
}
