using Agent.Abstractions.Booking;

namespace Agent.Domains.Flights;

public sealed record FlightSearchParams(string Origin, string Destination, DateOnly DepartureDate, DateOnly? ReturnDate, int Adults, string Cabin);

public sealed record FlightSegment(
    string Origin,
    string Destination,
    DateTimeOffset DepartAt,
    DateTimeOffset ArriveAt,
    string Carrier,
    string FlightNumber,
    int DurationMinutes);

public sealed record BaggageAllowance(int CabinPieces, int CheckedPieces);

/// <param name="Price">Total for all travellers on the search, both directions when a return date was given.</param>
/// <param name="RequiresPassport">Whether the supplier needs passport details at booking time.</param>
public sealed record FlightOffer(
    string Id,
    string Supplier,
    IReadOnlyList<FlightSegment> Outbound,
    IReadOnlyList<FlightSegment> Inbound,
    Money Price,
    string FareBrand,
    BaggageAllowance Baggage,
    bool Refundable,
    bool RequiresPassport)
{
    public int Stops => Math.Max(0, Outbound.Count - 1);

    public int DurationMinutes => (int)(Outbound[^1].ArriveAt - Outbound[0].DepartAt).TotalMinutes;
}

public sealed record BrandedFare(string Brand, Money Price, BaggageAllowance Baggage, bool Refundable, bool Changeable);

public sealed record FlightQuote(
    string QuoteId,
    string OfferId,
    string FareBrand,
    Money Total,
    Money? PreviousTotal,
    bool PriceChanged,
    DateTimeOffset ExpiresAt);

/// <summary>Supplier-facing flight operations. Offers are passed in by the server, never rebuilt from model input.</summary>
public interface IFlightBackend
{
    Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchParams search, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrandedFare>> GetBrandedFaresAsync(FlightOffer offer, CancellationToken cancellationToken);

    Task<FlightQuote> RecheckPriceAsync(FlightOffer offer, string? fareBrand, CancellationToken cancellationToken);
}

/// <summary>Raised when a supplier answers with an error. The message is for logs, not for the model or the user.</summary>
public sealed class SupplierException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
