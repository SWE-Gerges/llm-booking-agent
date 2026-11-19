using Agent.Abstractions.Booking;

namespace Agent.Domains.Transfers;

public sealed record TransferSearchParams(string Pickup, string Dropoff, DateOnly Date, TimeOnly Time, int Passengers);

public sealed record TransferOffer(
    string Id,
    string Supplier,
    string VehicleType,
    int MaxPassengers,
    int DurationMinutes,
    Money Price,
    int FreeWaitingMinutes,
    bool FreeCancellation);

public sealed record TransferQuote(
    string QuoteId,
    string OfferId,
    Money Total,
    Money? PreviousTotal,
    bool PriceChanged,
    DateTimeOffset ExpiresAt);

/// <summary>Supplier-facing transfer operations. Offers are passed in by the server, never rebuilt from model input.</summary>
public interface ITransferBackend
{
    Task<IReadOnlyList<TransferOffer>> SearchAsync(TransferSearchParams search, CancellationToken cancellationToken);

    /// <summary>Live price of one offer. The returned total is authoritative and may differ from the listed price.</summary>
    Task<TransferQuote> GetQuoteAsync(TransferOffer offer, TransferSearchParams search, CancellationToken cancellationToken);
}
