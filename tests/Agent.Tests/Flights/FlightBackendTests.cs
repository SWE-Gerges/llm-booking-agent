using System.Text.RegularExpressions;
using Agent.Abstractions;
using Agent.Domains.Flights;
using Agent.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.FlightTests;

public class FlightBackendTests
{
    private static readonly FlightSearchParams Base = new("AMS", "LIS", new DateOnly(2026, 4, 1), null, 1, "economy");

    private readonly FakeTimeProvider _clock = new(DomainHarness.Start);

    private InMemoryFlightBackend Backend() => new(_clock);

    [Fact]
    public async Task Same_params_give_identical_offers()
    {
        var first = await Backend().SearchAsync(Base, CancellationToken.None);
        var second = await Backend().SearchAsync(Base, CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(first), AgentJson.Serialize(second));
        Assert.InRange(first.Count, 8, 12);
    }

    [Fact]
    public async Task Different_params_give_different_offers()
    {
        var baseline = await Backend().SearchAsync(Base, CancellationToken.None);

        var otherDate = await Backend().SearchAsync(Base with { DepartureDate = new DateOnly(2026, 4, 2) }, CancellationToken.None);
        var otherAdults = await Backend().SearchAsync(Base with { Adults = 3 }, CancellationToken.None);
        var otherCabin = await Backend().SearchAsync(Base with { Cabin = "business" }, CancellationToken.None);

        Assert.NotEqual(AgentJson.Serialize(baseline), AgentJson.Serialize(otherDate));
        Assert.NotEqual(baseline[0].Id, otherDate[0].Id);
        Assert.NotEqual(AgentJson.Serialize(baseline), AgentJson.Serialize(otherAdults));
        Assert.NotEqual(AgentJson.Serialize(baseline), AgentJson.Serialize(otherCabin));
    }

    [Fact]
    public async Task Offer_ids_are_stable_unique_and_well_formed()
    {
        var offers = await Backend().SearchAsync(Base, CancellationToken.None);

        Assert.All(offers, o => Assert.Matches(new Regex("^fl-[0-9a-f]{8}-\\d{2}$"), o.Id));
        Assert.Equal(offers.Count, offers.Select(o => o.Id).Distinct().Count());
        Assert.Equal(offers.Select(o => o.Id), (await Backend().SearchAsync(Base, CancellationToken.None)).Select(o => o.Id));
    }

    [Fact]
    public async Task Offers_have_realistic_shape()
    {
        var offers = await Backend().SearchAsync(Base, CancellationToken.None);

        Assert.All(offers, o =>
        {
            Assert.NotEmpty(o.Outbound);
            Assert.Empty(o.Inbound);
            Assert.Equal("AMS", o.Outbound[0].Origin);
            Assert.Equal("LIS", o.Outbound[^1].Destination);
            Assert.True(o.Price.Amount > 0);
            Assert.Equal("EUR", o.Price.Currency);
            Assert.StartsWith("supplier-", o.Supplier, StringComparison.Ordinal);
            Assert.Equal(o.Supplier == "supplier-b", o.RequiresPassport);
            Assert.Equal(o.FareBrand == "flex", o.Refundable);
            Assert.Equal(o.Outbound.Count - 1, o.Stops);
            Assert.All(o.Outbound, s => Assert.True(s.ArriveAt > s.DepartAt));
        });
        Assert.Contains(offers, o => o.Stops == 0);
    }

    [Fact]
    public async Task Return_trips_have_an_inbound_direction()
    {
        var round = await Backend().SearchAsync(Base with { ReturnDate = new DateOnly(2026, 4, 8) }, CancellationToken.None);

        Assert.All(round, o =>
        {
            Assert.NotEmpty(o.Inbound);
            Assert.Equal("LIS", o.Inbound[0].Origin);
            Assert.Equal("AMS", o.Inbound[^1].Destination);
        });
    }

    [Fact]
    public async Task Price_scales_with_the_number_of_travellers()
    {
        var one = await Backend().SearchAsync(Base, CancellationToken.None);
        var three = await Backend().SearchAsync(Base with { Adults = 3 }, CancellationToken.None);

        Assert.True(three.Sum(o => o.Price.Amount) > one.Sum(o => o.Price.Amount));
    }

    [Fact]
    public async Task Branded_fares_are_ordered_and_include_the_offers_own_price()
    {
        var backend = Backend();
        foreach (var offer in await backend.SearchAsync(Base, CancellationToken.None))
        {
            var fares = await backend.GetBrandedFaresAsync(offer, CancellationToken.None);

            Assert.Equal(["basic", "standard", "flex"], fares.Select(f => f.Brand));
            Assert.True(fares[0].Price.Amount < fares[1].Price.Amount);
            Assert.True(fares[1].Price.Amount < fares[2].Price.Amount);
            Assert.Equal(offer.Price.Amount, fares.Single(f => f.Brand == offer.FareBrand).Price.Amount);
            Assert.True(fares[2].Refundable);
            Assert.False(fares[0].Changeable);
            Assert.Equal([0, 1, 2], fares.Select(f => f.Baggage.CheckedPieces));
        }
    }

    [Fact]
    public async Task Recheck_drift_is_deterministic_per_offer_and_marks_changes()
    {
        var backend = Backend();
        var offers = (await backend.SearchAsync(Base, CancellationToken.None))
            .Concat(await backend.SearchAsync(Base with { DepartureDate = new DateOnly(2026, 4, 3) }, CancellationToken.None))
            .ToList();
        var drifted = 0;
        var stable = 0;

        foreach (var offer in offers)
        {
            var first = await backend.RecheckPriceAsync(offer, null, CancellationToken.None);
            var second = await backend.RecheckPriceAsync(offer, null, CancellationToken.None);

            Assert.Equal(first.Total, second.Total);
            Assert.Equal(first.PriceChanged, second.PriceChanged);
            Assert.Equal(first.PreviousTotal, second.PreviousTotal);
            Assert.NotEqual(first.QuoteId, second.QuoteId);
            Assert.Equal(offer.Id, first.OfferId);
            Assert.Equal(offer.FareBrand, first.FareBrand);
            Assert.Equal(first.PriceChanged, first.PreviousTotal is not null);

            if (first.PriceChanged)
            {
                drifted++;
                Assert.Equal(offer.Price.Amount, first.PreviousTotal!.Amount);
            }
            else
            {
                stable++;
                Assert.Equal(offer.Price.Amount, first.Total.Amount);
            }
        }

        Assert.True(drifted > 0, "expected at least one drifting offer");
        Assert.True(stable > 0, "expected at least one stable offer");
    }

    [Fact]
    public async Task Recheck_for_another_brand_reprices_from_the_same_baseline()
    {
        var backend = Backend();
        var offer = (await backend.SearchAsync(Base, CancellationToken.None))[0];
        var fares = await backend.GetBrandedFaresAsync(offer, CancellationToken.None);

        var flex = await backend.RecheckPriceAsync(offer, "flex", CancellationToken.None);

        Assert.Equal("flex", flex.FareBrand);
        var listed = fares.Single(f => f.Brand == "flex").Price.Amount;
        Assert.Equal(listed, flex.PriceChanged ? flex.PreviousTotal!.Amount : flex.Total.Amount);
    }

    [Fact]
    public async Task Quotes_expire_fifteen_minutes_after_the_clock()
    {
        var offer = (await Backend().SearchAsync(Base, CancellationToken.None))[0];

        var quote = await Backend().RecheckPriceAsync(offer, null, CancellationToken.None);

        Assert.Equal(DomainHarness.Start.AddMinutes(15), quote.ExpiresAt);
    }
}
