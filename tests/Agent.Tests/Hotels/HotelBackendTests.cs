using System.Text.RegularExpressions;
using Agent.Abstractions;
using Agent.Domains.Hotels;
using Agent.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.HotelTests;

public class HotelBackendTests
{
    private static readonly HotelSearchParams Base = new("Lisbon", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 4), 2, 1);

    private readonly FakeTimeProvider _clock = new(DomainHarness.Start);

    private InMemoryHotelBackend Backend() => new(_clock);

    [Fact]
    public async Task Same_search_gives_identical_hotels_and_different_searches_differ()
    {
        var first = await Backend().SearchAsync(Base, CancellationToken.None);
        var second = await Backend().SearchAsync(Base, CancellationToken.None);
        var otherDates = await Backend().SearchAsync(Base with { CheckOut = new DateOnly(2026, 4, 5) }, CancellationToken.None);
        var otherPlace = await Backend().SearchAsync(Base with { Destination = "Porto" }, CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(first), AgentJson.Serialize(second));
        Assert.NotEqual(AgentJson.Serialize(first), AgentJson.Serialize(otherDates));
        Assert.NotEqual(AgentJson.Serialize(first), AgentJson.Serialize(otherPlace));
        Assert.InRange(first.Count, 7, 11);
    }

    [Fact]
    public async Task The_destination_is_case_insensitive_for_seeding()
    {
        var lower = await Backend().SearchAsync(Base with { Destination = "lisbon" }, CancellationToken.None);
        var upper = await Backend().SearchAsync(Base with { Destination = "LISBON" }, CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(lower), AgentJson.Serialize(upper));
    }

    [Fact]
    public async Task Hotels_have_stable_unique_ids_and_a_realistic_shape()
    {
        var hotels = await Backend().SearchAsync(Base, CancellationToken.None);

        Assert.Equal(hotels.Count, hotels.Select(h => h.Id).Distinct().Count());
        Assert.All(hotels, h =>
        {
            Assert.Matches(new Regex("^ht-[0-9a-f]{8}-\\d{2}$"), h.Id);
            Assert.InRange(h.Stars, 2, 5);
            Assert.InRange(h.GuestRating, 6.5, 9.9);
            Assert.True(h.FromPricePerNight.Amount > 0);
            Assert.Equal("EUR", h.FromPricePerNight.Currency);
            Assert.InRange(h.Amenities.Count, 2, 5);
            Assert.Equal(h.Amenities.Order(StringComparer.Ordinal), h.Amenities);
            Assert.StartsWith("supplier-", h.Supplier, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(h.Name));
        });
    }

    [Fact]
    public async Task Prices_scale_with_the_number_of_rooms()
    {
        var one = await Backend().SearchAsync(Base, CancellationToken.None);
        var three = await Backend().SearchAsync(Base with { Rooms = 3 }, CancellationToken.None);

        Assert.True(three.Sum(h => h.FromPricePerNight.Amount) > one.Sum(h => h.FromPricePerNight.Amount));
    }

    [Fact]
    public async Task Rooms_are_deterministic_with_two_rates_each_and_consistent_totals()
    {
        var backend = Backend();
        var hotel = (await backend.SearchAsync(Base, CancellationToken.None))[0];

        var rooms = await backend.GetRoomsAsync(hotel, Base, CancellationToken.None);
        var again = await backend.GetRoomsAsync(hotel, Base, CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(rooms), AgentJson.Serialize(again));
        Assert.Equal(["rm-0", "rm-1", "rm-2"], rooms.Select(r => r.RoomId));
        Assert.All(rooms, room =>
        {
            Assert.Equal(2, room.Rates.Count);
            Assert.All(room.Rates, rate => Assert.Equal(rate.PricePerNight.Amount * Base.Nights, rate.Total.Amount));
            Assert.True(room.Rates[1].PricePerNight.Amount > room.Rates[0].PricePerNight.Amount);
        });
        Assert.True(rooms[2].MaxGuests > rooms[0].MaxGuests);
        Assert.True(rooms[2].Rates[0].PricePerNight.Amount > rooms[0].Rates[0].PricePerNight.Amount);
    }

    [Fact]
    public async Task Cancellation_policies_distinguish_refundable_and_non_refundable_rates()
    {
        var backend = Backend();
        var hotel = (await backend.SearchAsync(Base, CancellationToken.None))[0];

        var room = (await backend.GetRoomsAsync(hotel, Base, CancellationToken.None))[0];

        var refundable = room.Rates[0];
        var strict = room.Rates[1];
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero), refundable.Cancellation.FreeUntil);
        Assert.Contains("Free cancellation", refundable.Cancellation.Summary, StringComparison.Ordinal);
        Assert.Null(strict.Cancellation.FreeUntil);
        Assert.Contains("Non-refundable", strict.Cancellation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prebook_drift_is_deterministic_per_rate_and_marks_changes()
    {
        var backend = Backend();
        var drifted = 0;
        var stable = 0;

        foreach (var hotel in await backend.SearchAsync(Base, CancellationToken.None))
        {
            foreach (var room in await backend.GetRoomsAsync(hotel, Base, CancellationToken.None))
            {
                foreach (var rate in room.Rates)
                {
                    var first = await backend.PrebookAsync(hotel, room, rate, Base, CancellationToken.None);
                    var second = await backend.PrebookAsync(hotel, room, rate, Base, CancellationToken.None);

                    Assert.Equal(first.Total, second.Total);
                    Assert.Equal(first.PriceChanged, second.PriceChanged);
                    Assert.NotEqual(first.QuoteId, second.QuoteId);
                    Assert.Equal((hotel.Id, room.RoomId, rate.RateId), (first.HotelId, first.RoomId, first.RateId));
                    Assert.Equal(first.PriceChanged, first.PreviousTotal is not null);
                    Assert.Equal(DomainHarness.Start.AddMinutes(15), first.ExpiresAt);

                    if (first.PriceChanged)
                    {
                        drifted++;
                        Assert.Equal(rate.Total, first.PreviousTotal);
                        Assert.NotEqual(rate.Total.Amount, first.Total.Amount);
                    }
                    else
                    {
                        stable++;
                        Assert.Equal(rate.Total, first.Total);
                    }
                }
            }
        }

        Assert.True(drifted > 0, "expected at least one drifting rate");
        Assert.True(stable > 0, "expected at least one stable rate");
    }
}
