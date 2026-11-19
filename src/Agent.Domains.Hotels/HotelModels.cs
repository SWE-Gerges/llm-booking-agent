using Agent.Abstractions.Booking;

namespace Agent.Domains.Hotels;

public sealed record HotelSearchParams(string Destination, DateOnly CheckIn, DateOnly CheckOut, int Adults, int Rooms)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}

public sealed record Hotel(
    string Id,
    string Name,
    int Stars,
    string Area,
    string Supplier,
    Money FromPricePerNight,
    IReadOnlyList<string> Amenities,
    double GuestRating);

public sealed record CancellationPolicy(DateTimeOffset? FreeUntil, string Summary);

public sealed record HotelRate(string RateId, string BoardType, Money PricePerNight, Money Total, CancellationPolicy Cancellation);

public sealed record HotelRoom(string RoomId, string Name, string Beds, int MaxGuests, IReadOnlyList<HotelRate> Rates);

public sealed record HotelPrebook(
    string QuoteId,
    string HotelId,
    string RoomId,
    string RateId,
    Money Total,
    Money? PreviousTotal,
    bool PriceChanged,
    DateTimeOffset ExpiresAt);

public interface IHotelBackend
{
    Task<IReadOnlyList<Hotel>> SearchAsync(HotelSearchParams search, CancellationToken cancellationToken);

    Task<IReadOnlyList<HotelRoom>> GetRoomsAsync(Hotel hotel, HotelSearchParams search, CancellationToken cancellationToken);

    /// <summary>Locks the rate with the supplier. The returned total is authoritative and may differ from the quoted rate.</summary>
    Task<HotelPrebook> PrebookAsync(Hotel hotel, HotelRoom room, HotelRate rate, HotelSearchParams search, CancellationToken cancellationToken);
}
