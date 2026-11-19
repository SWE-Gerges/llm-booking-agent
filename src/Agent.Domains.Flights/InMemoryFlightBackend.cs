using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions.Booking;

namespace Agent.Domains.Flights;

/// <summary>
/// Deterministic flight supplier stand-in. Results depend only on the search parameters (seeded RNG), so the same
/// request always yields the same offers; repricing drifts deterministically per offer.
/// </summary>
internal sealed class InMemoryFlightBackend(TimeProvider clock) : IFlightBackend
{
    private static readonly string[] Suppliers = ["supplier-a", "supplier-b", "supplier-c"];
    private static readonly string[] Carriers = ["XA", "XB", "XC"];
    private static readonly string[] Hubs = ["HBA", "HBB", "HBC"];
    private static readonly string[] Brands = ["basic", "standard", "flex"];

    public Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchParams search, CancellationToken cancellationToken)
    {
        var seed = SeedFor($"{search.Origin}|{search.Destination}|{search.DepartureDate:O}|{search.ReturnDate:O}|{search.Adults}|{search.Cabin}");
        var rng = new Random(seed);
        var count = 8 + rng.Next(0, 5);
        var offers = new List<FlightOffer>(count);

        for (var i = 0; i < count; i++)
        {
            var carrierIndex = rng.Next(Carriers.Length);
            var outbound = BuildDirection(rng, search.Origin, search.Destination, search.DepartureDate, Carriers[carrierIndex]);
            var inbound = search.ReturnDate is { } returnDate
                ? BuildDirection(rng, search.Destination, search.Origin, returnDate, Carriers[carrierIndex])
                : [];

            var brandIndex = rng.Next(Brands.Length);
            var brand = Brands[brandIndex];
            var perPerson = 60 + rng.Next(0, 380) - ((outbound.Count - 1) * 15);
            var directions = inbound.Count > 0 ? 2 : 1;
            var amount = Math.Round(perPerson * search.Adults * directions * CabinFactor(search.Cabin) * BrandFactor(brand), 2, MidpointRounding.AwayFromZero);

            offers.Add(new FlightOffer(
                $"fl-{seed:x8}-{i:D2}",
                Suppliers[carrierIndex],
                outbound,
                inbound,
                new Money(amount, "EUR"),
                brand,
                new BaggageAllowance(1, brandIndex),
                brand == "flex",
                Suppliers[carrierIndex] == "supplier-b"));
        }

        return Task.FromResult<IReadOnlyList<FlightOffer>>(offers);
    }

    public Task<IReadOnlyList<BrandedFare>> GetBrandedFaresAsync(FlightOffer offer, CancellationToken cancellationToken)
    {
        var baseline = offer.Price.Amount / BrandFactor(offer.FareBrand);
        var fares = Brands
            .Select((brand, index) => new BrandedFare(
                brand,
                new Money(Math.Round(baseline * BrandFactor(brand), 2, MidpointRounding.AwayFromZero), offer.Price.Currency),
                new BaggageAllowance(1, index),
                brand == "flex",
                brand != "basic"))
            .ToList();
        return Task.FromResult<IReadOnlyList<BrandedFare>>(fares);
    }

    public Task<FlightQuote> RecheckPriceAsync(FlightOffer offer, string? fareBrand, CancellationToken cancellationToken)
    {
        var brand = fareBrand ?? offer.FareBrand;
        var baseline = offer.Price.Amount / BrandFactor(offer.FareBrand);
        var previous = Math.Round(baseline * BrandFactor(brand), 2, MidpointRounding.AwayFromZero);

        var drift = (SeedFor(offer.Id) % 5) switch
        {
            1 => 0.03m,
            2 => -0.02m,
            4 => 0.05m,
            _ => 0m,
        };
        var total = Math.Round(previous * (1 + drift), 2, MidpointRounding.AwayFromZero);

        return Task.FromResult(new FlightQuote(
            $"qt-{Guid.NewGuid():N}",
            offer.Id,
            brand,
            new Money(total, offer.Price.Currency),
            drift == 0 ? null : new Money(previous, offer.Price.Currency),
            drift != 0,
            clock.GetUtcNow().AddMinutes(15)));
    }

    private static List<FlightSegment> BuildDirection(Random rng, string origin, string destination, DateOnly date, string carrier)
    {
        var stops = rng.Next(0, 10) switch { < 5 => 0, < 8 => 1, _ => 2 };
        var points = new List<string> { origin };
        points.AddRange(Hubs.OrderBy(_ => rng.Next()).Take(stops));
        points.Add(destination);

        var departure = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .AddHours(5 + rng.Next(0, 16))
            .AddMinutes(rng.Next(0, 4) * 15);
        var segments = new List<FlightSegment>(points.Count - 1);
        for (var leg = 0; leg < points.Count - 1; leg++)
        {
            var minutes = 70 + rng.Next(0, 170);
            var arrival = departure.AddMinutes(minutes);
            segments.Add(new FlightSegment(points[leg], points[leg + 1], departure, arrival, carrier, $"{carrier}{100 + rng.Next(0, 900)}", minutes));
            departure = arrival.AddMinutes(60 + rng.Next(0, 90));
        }

        return segments;
    }

    private static decimal CabinFactor(string cabin) => cabin switch
    {
        "premium_economy" => 1.6m,
        "business" => 2.8m,
        "first" => 4m,
        _ => 1m,
    };

    private static decimal BrandFactor(string brand) => brand switch
    {
        "standard" => 1.15m,
        "flex" => 1.4m,
        _ => 1m,
    };

    private static int SeedFor(string text) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) & 0x7FFFFFFF;
}
