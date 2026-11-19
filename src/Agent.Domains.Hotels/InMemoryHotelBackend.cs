using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions.Booking;

namespace Agent.Domains.Hotels;

/// <summary>Deterministic hotel supplier stand-in: results depend only on the search, prebook totals may drift per rate.</summary>
internal sealed class InMemoryHotelBackend(TimeProvider clock) : IHotelBackend
{
    private static readonly string[] Suppliers = ["supplier-a", "supplier-b", "supplier-c"];
    private static readonly string[] Prefixes = ["Harbour", "Central", "Garden", "Riverside", "Old Town", "Skyline", "Park", "Lakeside"];
    private static readonly string[] Suffixes = ["Hotel", "Suites", "Inn", "Residence"];
    private static readonly string[] Areas = ["City Centre", "Waterfront", "Old Town", "Business District", "Near Station"];
    private static readonly string[] AllAmenities = ["wifi", "breakfast", "pool", "gym", "parking", "spa", "airport shuttle"];

    public Task<IReadOnlyList<Hotel>> SearchAsync(HotelSearchParams search, CancellationToken cancellationToken)
    {
        var seed = SeedFor($"{search.Destination.ToUpperInvariant()}|{search.CheckIn:O}|{search.CheckOut:O}|{search.Adults}|{search.Rooms}");
        var rng = new Random(seed);
        var count = 7 + rng.Next(0, 5);
        var hotels = new List<Hotel>(count);

        for (var i = 0; i < count; i++)
        {
            var stars = rng.Next(2, 6);
            var perNight = Math.Round((45 + (stars * 28) + rng.Next(0, 70)) * (decimal)search.Rooms, 2, MidpointRounding.AwayFromZero);
            hotels.Add(new Hotel(
                $"ht-{seed:x8}-{i:D2}",
                $"{Prefixes[rng.Next(Prefixes.Length)]} {Suffixes[rng.Next(Suffixes.Length)]}",
                stars,
                Areas[rng.Next(Areas.Length)],
                Suppliers[rng.Next(Suppliers.Length)],
                new Money(perNight, "EUR"),
                AllAmenities.OrderBy(_ => rng.Next()).Take(2 + rng.Next(0, 4)).Order().ToList(),
                Math.Round(6.5 + (rng.NextDouble() * 3.4), 1)));
        }

        return Task.FromResult<IReadOnlyList<Hotel>>(hotels);
    }

    public Task<IReadOnlyList<HotelRoom>> GetRoomsAsync(Hotel hotel, HotelSearchParams search, CancellationToken cancellationToken)
    {
        var nights = Math.Max(1, search.Nights);
        var rooms = new List<HotelRoom>();
        var types = new (string Name, string Beds, int Guests, decimal Factor)[]
        {
            ("Standard room", "1 double bed", 2, 1m),
            ("Superior room", "1 king bed", 2, 1.25m),
            ("Family room", "2 double beds", 4, 1.6m),
        };

        for (var r = 0; r < types.Length; r++)
        {
            var type = types[r];
            var rates = new List<HotelRate>();
            foreach (var (board, boardFactor, refundable) in new[] { ("room only", 1m, true), ("breakfast included", 1.12m, false) })
            {
                var perNight = Math.Round(hotel.FromPricePerNight.Amount * type.Factor * boardFactor, 2, MidpointRounding.AwayFromZero);
                var freeUntil = refundable ? new DateTimeOffset(search.CheckIn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-2) : (DateTimeOffset?)null;
                rates.Add(new HotelRate(
                    $"rt-{r}{(refundable ? "a" : "b")}",
                    board,
                    new Money(perNight, hotel.FromPricePerNight.Currency),
                    new Money(perNight * nights, hotel.FromPricePerNight.Currency),
                    new CancellationPolicy(
                        freeUntil,
                        refundable ? "Free cancellation until 2 days before check-in, then the first night is charged." : "Non-refundable.")));
            }

            rooms.Add(new HotelRoom($"rm-{r}", type.Name, type.Beds, type.Guests, rates));
        }

        return Task.FromResult<IReadOnlyList<HotelRoom>>(rooms);
    }

    public Task<HotelPrebook> PrebookAsync(Hotel hotel, HotelRoom room, HotelRate rate, HotelSearchParams search, CancellationToken cancellationToken)
    {
        var drift = (SeedFor($"{hotel.Id}|{room.RoomId}|{rate.RateId}") % 4) switch
        {
            1 => 0.04m,
            3 => -0.03m,
            _ => 0m,
        };
        var total = Math.Round(rate.Total.Amount * (1 + drift), 2, MidpointRounding.AwayFromZero);

        return Task.FromResult(new HotelPrebook(
            $"pb-{Guid.NewGuid():N}",
            hotel.Id,
            room.RoomId,
            rate.RateId,
            new Money(total, rate.Total.Currency),
            drift == 0 ? null : rate.Total,
            drift != 0,
            clock.GetUtcNow().AddMinutes(15)));
    }

    private static int SeedFor(string text) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) & 0x7FFFFFFF;
}
