using System.Text.Json.Nodes;
using Agent.Abstractions.Sessions;
using Agent.Domains.Flights;
using Agent.Domains.Hotels;

namespace Agent.Tests.Support;

internal static class Scenarios
{
    public static string FlightSearchJson(string date, int? adults = null, string origin = "AMS", string destination = "LIS", string? returnDate = null)
    {
        var input = new JsonObject { ["origin"] = origin, ["destination"] = destination, ["departureDate"] = date };
        if (adults is { } count)
        {
            input["adults"] = count;
        }

        if (returnDate is not null)
        {
            input["returnDate"] = returnDate;
        }

        return input.ToJsonString();
    }

    public static string DateAfterStart(int days) =>
        DateOnly.FromDateTime(DomainHarness.Start.UtcDateTime).AddDays(days).ToString("yyyy-MM-dd");

    public static Task<FlightOffer> ConfirmFlightAsync(
        DomainHarness harness,
        SessionContext session,
        Func<FlightOffer, bool>? pick = null,
        int? adults = 1,
        string? fareBrand = null) =>
        ConfirmFlightAsync(harness, session, pick is null ? null : o => Task.FromResult(pick(o)), adults, fareBrand);

    /// <summary>Searches (varying the date until an offer matches <paramref name="pick"/>) and re-checks that offer's price.</summary>
    public static async Task<FlightOffer> ConfirmFlightAsync(
        DomainHarness harness,
        SessionContext session,
        Func<FlightOffer, Task<bool>>? pick,
        int? adults = 1,
        string? fareBrand = null)
    {
        for (var day = 5; day < 60; day++)
        {
            var search = await harness.RunAsync(session, "search_flights", FlightSearchJson(DateAfterStart(day), adults));
            var offers = DomainHarness.ReadList<FlightOffer>(DomainHarness.Content(search), "offers");
            FlightOffer? offer = null;
            foreach (var candidate in offers)
            {
                if (pick is null || await pick(candidate))
                {
                    offer = candidate;
                    break;
                }
            }

            if (offer is null)
            {
                continue;
            }

            var input = new JsonObject { ["offerId"] = offer.Id };
            if (fareBrand is not null)
            {
                input["fareBrand"] = fareBrand;
            }

            await harness.RunAsync(session, "recheck_flight_price", input.ToJsonString());
            return offer;
        }

        throw new InvalidOperationException("No flight matching the predicate was found in 55 deterministic searches.");
    }

    public static string HotelSearchJson(string checkIn, string checkOut, int? adults = 2, int? rooms = 1, string destination = "Lisbon")
    {
        var input = new JsonObject { ["destination"] = destination, ["checkIn"] = checkIn, ["checkOut"] = checkOut };
        if (adults is { } a)
        {
            input["adults"] = a;
        }

        if (rooms is { } r)
        {
            input["rooms"] = r;
        }

        return input.ToJsonString();
    }

    /// <summary>Searches hotels, lists the first hotel's rooms and pre-books its first room and rate.</summary>
    public static async Task<(Hotel Hotel, HotelRoom Room, HotelRate Rate)> PrebookFirstRoomAsync(DomainHarness harness, SessionContext session)
    {
        var search = await harness.RunAsync(session, "search_hotels", HotelSearchJson(DateAfterStart(20), DateAfterStart(23)));
        var hotel = DomainHarness.ReadList<Hotel>(DomainHarness.Content(search), "hotels")[0];
        var rooms = await harness.RunAsync(session, "get_hotel_rooms", $$"""{"hotelId":"{{hotel.Id}}"}""");
        var room = DomainHarness.ReadList<HotelRoom>(DomainHarness.Content(rooms), "rooms")[0];
        var rate = room.Rates[0];
        await harness.RunAsync(session, "prebook_room", $$"""{"hotelId":"{{hotel.Id}}","roomId":"{{room.RoomId}}","rateId":"{{rate.RateId}}"}""");
        return (hotel, room, rate);
    }
}
