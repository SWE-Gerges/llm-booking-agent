using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions.Booking;

namespace Agent.Domains.Transfers;

/// <summary>Deterministic transfer supplier stand-in: offers depend only on the search, quotes drift deterministically per offer.</summary>
internal sealed class InMemoryTransferBackend(TimeProvider clock) : ITransferBackend
{
    private static readonly string[] Suppliers = ["supplier-a", "supplier-b", "supplier-c"];

    private static readonly (string Vehicle, int Capacity, decimal Factor)[] Vehicles =
    [
        ("sedan", 3, 1m),
        ("van", 6, 1.5m),
        ("minibus", 12, 2.4m),
    ];

    public Task<IReadOnlyList<TransferOffer>> SearchAsync(TransferSearchParams search, CancellationToken cancellationToken)
    {
        var seed = SeedFor($"{search.Pickup.ToUpperInvariant()}|{search.Dropoff.ToUpperInvariant()}|{search.Date:O}|{search.Time:O}|{search.Passengers}");
        var rng = new Random(seed);
        var duration = 20 + rng.Next(0, 45);
        var basePrice = 25 + rng.Next(0, 30);
        var offers = new List<TransferOffer>();

        foreach (var (vehicle, capacity, factor) in Vehicles.Where(v => v.Capacity >= search.Passengers))
        {
            for (var i = 0; i < 2; i++)
            {
                var price = Math.Round((basePrice + (duration * 0.6m) + rng.Next(0, 12)) * factor, 2, MidpointRounding.AwayFromZero);
                offers.Add(new TransferOffer(
                    $"tr-{seed:x8}-{offers.Count:D2}",
                    Suppliers[rng.Next(Suppliers.Length)],
                    vehicle,
                    capacity,
                    duration,
                    new Money(price, "EUR"),
                    15 + (i * 30),
                    i == 1));
            }
        }

        return Task.FromResult<IReadOnlyList<TransferOffer>>(offers);
    }

    public Task<TransferQuote> GetQuoteAsync(TransferOffer offer, TransferSearchParams search, CancellationToken cancellationToken)
    {
        var drift = (SeedFor(offer.Id) % 4) switch
        {
            1 => 0.05m,
            3 => -0.04m,
            _ => 0m,
        };
        var total = Math.Round(offer.Price.Amount * (1 + drift), 2, MidpointRounding.AwayFromZero);

        return Task.FromResult(new TransferQuote(
            $"tq-{Guid.NewGuid():N}",
            offer.Id,
            new Money(total, offer.Price.Currency),
            drift == 0 ? null : offer.Price,
            drift != 0,
            clock.GetUtcNow().AddMinutes(15)));
    }

    private static int SeedFor(string text) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) & 0x7FFFFFFF;
}
