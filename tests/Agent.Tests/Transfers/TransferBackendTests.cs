using Agent.Domains.Transfers;
using Agent.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.TransferTests;

public class TransferBackendTests
{
    private static readonly FakeTimeProvider Clock = new(DomainHarness.Start);

    private static TransferSearchParams Search(int passengers = 1, int day = 10, string pickup = "LIS") =>
        TripScenarios.TransferParams(Scenarios.DateAfterStart(day), passengers: passengers, pickup: pickup);

    [Fact]
    public async Task The_same_search_always_yields_identical_offers_and_ids()
    {
        var backend = new InMemoryTransferBackend(Clock);

        var first = await backend.SearchAsync(Search(), CancellationToken.None);
        var second = await new InMemoryTransferBackend(Clock).SearchAsync(Search(), CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Equal(TripScenarios.Serialize(first), TripScenarios.Serialize(second));
        Assert.Equal(first.Count, first.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public async Task Different_searches_yield_different_offer_ids()
    {
        var backend = new InMemoryTransferBackend(Clock);

        var a = await backend.SearchAsync(Search(day: 10), CancellationToken.None);
        var b = await backend.SearchAsync(Search(day: 11), CancellationToken.None);
        var c = await backend.SearchAsync(Search(pickup: "OPO"), CancellationToken.None);

        Assert.Empty(a.Select(o => o.Id).Intersect(b.Select(o => o.Id)));
        Assert.Empty(a.Select(o => o.Id).Intersect(c.Select(o => o.Id)));
    }

    [Theory]
    [InlineData(1, new[] { "sedan", "van", "minibus" })]
    [InlineData(3, new[] { "sedan", "van", "minibus" })]
    [InlineData(4, new[] { "van", "minibus" })]
    [InlineData(6, new[] { "van", "minibus" })]
    [InlineData(7, new[] { "minibus" })]
    [InlineData(12, new[] { "minibus" })]
    public async Task Only_vehicles_that_fit_the_party_are_offered(int passengers, string[] expectedVehicles)
    {
        var offers = await new InMemoryTransferBackend(Clock).SearchAsync(Search(passengers), CancellationToken.None);

        Assert.All(offers, o => Assert.True(o.MaxPassengers >= passengers, $"{o.VehicleType} cannot carry {passengers}"));
        Assert.Equal(expectedVehicles.Order(), offers.Select(o => o.VehicleType).Distinct().Order());
    }

    [Fact]
    public async Task A_party_larger_than_every_vehicle_gets_no_offers()
    {
        var offers = await new InMemoryTransferBackend(Clock).SearchAsync(Search(13), CancellationToken.None);

        Assert.Empty(offers);
    }

    [Fact]
    public async Task Offers_are_priced_in_euro_with_neutral_supplier_names()
    {
        var offers = await new InMemoryTransferBackend(Clock).SearchAsync(Search(2), CancellationToken.None);

        Assert.All(offers, o =>
        {
            Assert.Equal("EUR", o.Price.Currency);
            Assert.True(o.Price.Amount > 0);
            Assert.Matches("^supplier-[abc]$", o.Supplier);
        });
    }

    [Fact]
    public async Task Quotes_drift_deterministically_per_offer_and_report_the_change_consistently()
    {
        var backend = new InMemoryTransferBackend(Clock);
        var changed = 0;
        var unchanged = 0;

        for (var day = 1; day < 25; day++)
        {
            var search = Search(day: day);
            foreach (var offer in await backend.SearchAsync(search, CancellationToken.None))
            {
                var one = await backend.GetQuoteAsync(offer, search, CancellationToken.None);
                var two = await backend.GetQuoteAsync(offer, search, CancellationToken.None);

                Assert.Equal(one.Total, two.Total);
                Assert.NotEqual(one.QuoteId, two.QuoteId);
                Assert.Equal(offer.Id, one.OfferId);
                Assert.Equal(one.Total != offer.Price, one.PriceChanged);
                Assert.Equal(one.PriceChanged, one.PreviousTotal is not null);
                if (one.PriceChanged)
                {
                    Assert.Equal(offer.Price, one.PreviousTotal);
                    changed++;
                }
                else
                {
                    unchanged++;
                }
            }
        }

        Assert.True(changed > 0 && unchanged > 0, "the fake should exercise both a stable and a drifting price");
    }

    [Fact]
    public async Task A_quote_expires_fifteen_minutes_after_it_was_issued()
    {
        var clock = new FakeTimeProvider(DomainHarness.Start);
        var backend = new InMemoryTransferBackend(clock);
        var search = Search();
        var offer = (await backend.SearchAsync(search, CancellationToken.None))[0];

        var quote = await backend.GetQuoteAsync(offer, search, CancellationToken.None);

        Assert.Equal(DomainHarness.Start.AddMinutes(15), quote.ExpiresAt);
    }
}
