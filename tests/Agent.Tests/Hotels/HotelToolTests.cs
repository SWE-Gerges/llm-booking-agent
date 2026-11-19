using System.Globalization;
using Agent.Abstractions;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Forms;
using Agent.Abstractions.Sessions;
using Agent.Domains.Hotels;
using Agent.Domains.Hotels.Forms;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.HotelTests;

public class SearchHotelsToolTests
{
    private static string Search(int? adults = 2, int? rooms = 1, string destination = "Lisbon") =>
        Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23), adults, rooms, destination);

    [Fact]
    public async Task A_past_check_in_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(-2), Scenarios.DateAfterStart(2))));

        Assert.Contains("past", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Check_out_must_be_after_check_in(int offset)
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(20 + offset))));
    }

    [Theory]
    [InlineData("""{"destination":"L","checkIn":"2026-04-01","checkOut":"2026-04-02"}""")]
    [InlineData("""{"destination":"Lisbon","checkIn":"2026-04-01"}""")]
    [InlineData("""{"destination":"Lisbon","checkIn":"soon","checkOut":"2026-04-02"}""")]
    [InlineData("""{"destination":"Lisbon","checkIn":"2026-04-01","checkOut":"2026-04-02","adults":0}""")]
    [InlineData("""{"destination":"Lisbon","checkIn":"2026-04-01","checkOut":"2026-04-02","rooms":6}""")]
    [InlineData("""{"destination":"Lisbon","checkIn":"2026-04-01","checkOut":"2026-04-02","price":10}""")]
    public async Task Schema_rejects_malformed_input(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("search_hotels"), AgentJson.Parse(json)));
    }

    [Fact]
    public async Task Search_stores_a_snapshot_marks_the_service_and_sorts_by_price()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_hotels", Search());

        Assert.Equal("hotel_results", result.State);
        Assert.Contains(ServiceType.Hotel, s.RequestedServices);
        var content = DomainHarness.Content(result);
        Assert.Equal(3, content.GetProperty("nights").GetInt32());
        var hotels = DomainHarness.ReadList<Hotel>(content, "hotels");
        Assert.InRange(hotels.Count, 1, 10);
        Assert.Equal(hotels.OrderBy(x => x.FromPricePerNight.Amount).Select(x => x.Id), hotels.Select(x => x.Id));
        var context = await h.ContextAsync(s, ServiceType.Hotel);
        Assert.Equal(content.GetProperty("handle").GetString(), context!.Search!.Handle);
        var stored = AgentJson.Deserialize<HotelSearchParams>(context.Search.ParamsJson)!;
        Assert.Equal(("Lisbon", 2, 1), (stored.Destination, stored.Adults, stored.Rooms));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(2, null)]
    [InlineData(null, null)]
    public async Task Omitting_guests_or_rooms_adds_an_assumption_note(int? adults, int? rooms)
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_hotels", Search(adults, rooms));

        Assert.NotNull(result.ModelNote);
        Assert.Contains("2 adult(s) in 1 room(s)", result.ModelNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fully_specified_searches_carry_no_note_and_keep_the_numbers()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_hotels", Search(3, 2));

        Assert.Null(result.ModelNote);
        var search = DomainHarness.Content(result).GetProperty("search");
        Assert.Equal(3, search.GetProperty("adults").GetInt32());
        Assert.Equal(2, search.GetProperty("rooms").GetInt32());
    }

    [Fact]
    public async Task Model_view_hides_the_supplier_while_the_api_payload_keeps_it()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_hotels", Search());

        Assert.Contains("supplier-", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", result.ModelContent!, StringComparison.OrdinalIgnoreCase);
        var view = DomainHarness.ModelView(result);
        Assert.Equal(view.GetProperty("count").GetInt32(), view.GetProperty("hotels").GetArrayLength());
    }

    [Fact]
    public async Task A_new_hotel_search_resets_the_selection()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);

        await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(30), Scenarios.DateAfterStart(31)));

        var context = await h.ContextAsync(s, ServiceType.Hotel);
        Assert.Null(context!.SelectedItemId);
        Assert.Null(context.ConfirmedPrice);
    }
}

public class FilterHotelsToolTests
{
    private static async Task<(DomainHarness H, SessionContext S, string Handle, List<Hotel> Hotels)> ArrangeAsync()
    {
        var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var result = await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23)));
        var content = DomainHarness.Content(result);
        return (h, s, content.GetProperty("handle").GetString()!, DomainHarness.ReadList<Hotel>(content, "hotels"));
    }

    private static async Task<List<string>> FilterIdsAsync(DomainHarness h, SessionContext s, string json)
    {
        var result = await h.RunAsync(s, "filter_hotels", json);
        Assert.Equal("hotel_results", result.State);
        return DomainHarness.ReadList<Hotel>(DomainHarness.Content(result), "hotels").Select(x => x.Id).ToList();
    }

    [Fact]
    public async Task Max_price_per_night_keeps_cheaper_hotels()
    {
        var (h, s, handle, hotels) = await ArrangeAsync();
        using var disposeHarness = h;
        var limit = hotels.OrderBy(x => x.FromPricePerNight.Amount).ElementAt(hotels.Count / 2).FromPricePerNight.Amount;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","maxPricePerNight":{{limit.ToString(CultureInfo.InvariantCulture)}}}""");

        Assert.Equal(hotels.Where(x => x.FromPricePerNight.Amount <= limit).Select(x => x.Id), ids);
        Assert.True(ids.Count < hotels.Count);
    }

    [Fact]
    public async Task Minimum_stars_and_rating_are_applied()
    {
        var (h, s, handle, hotels) = await ArrangeAsync();
        using var disposeHarness = h;

        var byStars = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","minStars":4}""");
        var byRating = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","minRating":8.5}""");

        Assert.Equal(hotels.Where(x => x.Stars >= 4).Select(x => x.Id), byStars);
        Assert.Equal(hotels.Where(x => x.GuestRating >= 8.5).Select(x => x.Id), byRating);
    }

    [Fact]
    public async Task Amenities_must_all_be_present()
    {
        var (h, s, handle, hotels) = await ArrangeAsync();
        using var disposeHarness = h;
        var wanted = hotels.SelectMany(x => x.Amenities).GroupBy(a => a).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToList();

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","amenities":["{{wanted[0].ToUpperInvariant()}}","{{wanted[1]}}"]}""");

        Assert.Equal(hotels.Where(x => wanted.All(w => x.Amenities.Contains(w))).Select(x => x.Id), ids);
    }

    [Theory]
    [InlineData("rating")]
    [InlineData("stars")]
    [InlineData("price")]
    public async Task Sorting_orders_the_result(string sortBy)
    {
        var (h, s, handle, hotels) = await ArrangeAsync();
        using var disposeHarness = h;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","sortBy":"{{sortBy}}"}""");

        IEnumerable<Hotel> expected = sortBy switch
        {
            "rating" => hotels.OrderByDescending(x => x.GuestRating),
            "stars" => hotels.OrderByDescending(x => x.Stars).ThenBy(x => x.FromPricePerNight.Amount),
            _ => hotels.OrderBy(x => x.FromPricePerNight.Amount),
        };
        Assert.Equal(expected.Select(x => x.Id), ids);
    }

    [Fact]
    public async Task A_stale_handle_and_an_expired_snapshot_are_rejected()
    {
        var (h, s, handle, _) = await ArrangeAsync();
        using var disposeHarness = h;

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "filter_hotels", """{"handle":"old"}"""));
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "filter_hotels", $$"""{"handle":"{{handle}}"}"""));
        Assert.Contains("out of date", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"handle":"h","minStars":6}""")]
    [InlineData("""{"handle":"h","minRating":11}""")]
    [InlineData("""{"handle":"h","sortBy":"cheapest"}""")]
    public async Task Schema_rejects_malformed_filters(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("filter_hotels"), AgentJson.Parse(json)));
    }
}

public class HotelRoomAndPrebookToolTests
{
    [Fact]
    public async Task Rooms_of_an_unknown_hotel_are_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23)));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_hotel_rooms", """{"hotelId":"ht-nope"}"""));

        Assert.Contains("ht-nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rooms_come_back_with_rates_and_cancellation_policies()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var hotel = DomainHarness.ReadList<Hotel>(
            DomainHarness.Content(await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23)))),
            "hotels")[0];

        var result = await h.RunAsync(s, "get_hotel_rooms", $$"""{"hotelId":"{{hotel.Id}}"}""");

        Assert.Equal("hotel_rooms", result.State);
        var content = DomainHarness.Content(result);
        Assert.Equal(3, content.GetProperty("nights").GetInt32());
        var rooms = DomainHarness.ReadList<HotelRoom>(content, "rooms");
        Assert.Equal(3, rooms.Count);
        Assert.All(rooms, r => Assert.All(r.Rates, rate => Assert.False(string.IsNullOrEmpty(rate.Cancellation.Summary))));
    }

    [Fact]
    public async Task Rooms_without_a_search_are_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_hotel_rooms", """{"hotelId":"x"}"""));
    }

    [Fact]
    public async Task Prebook_takes_the_confirmed_price_from_the_backend()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var (hotel, room, rate) = await Scenarios.PrebookFirstRoomAsync(h, s);

        var context = await h.ContextAsync(s, ServiceType.Hotel);
        var search = new HotelSearchParams("Lisbon", DateOnly.Parse(Scenarios.DateAfterStart(20), CultureInfo.InvariantCulture), DateOnly.Parse(Scenarios.DateAfterStart(23), CultureInfo.InvariantCulture), 2, 1);
        var expected = await new InMemoryHotelBackend(h.Clock).PrebookAsync(hotel, room, rate, search, CancellationToken.None);
        Assert.Equal(hotel.Id, context!.SelectedItemId);
        Assert.Equal($"{room.RoomId}:{rate.RateId}", context.SelectedOptionId);
        Assert.Equal(expected.Total, context.ConfirmedPrice!.Total);
        Assert.Equal(h.Clock.GetUtcNow(), context.ConfirmedPrice.ConfirmedAt);
        Assert.StartsWith("pb-", context.ConfirmedPrice.QuoteId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_rooms_and_rates_are_rejected_and_nothing_is_selected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var hotel = DomainHarness.ReadList<Hotel>(
            DomainHarness.Content(await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23)))),
            "hotels")[0];

        var room = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "prebook_room", $$"""{"hotelId":"{{hotel.Id}}","roomId":"rm-99","rateId":"rt-0a"}"""));
        var rate = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "prebook_room", $$"""{"hotelId":"{{hotel.Id}}","roomId":"rm-0","rateId":"rt-9z"}"""));
        var unknownHotel = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "prebook_room", """{"hotelId":"ht-nope","roomId":"rm-0","rateId":"rt-0a"}"""));

        Assert.Contains("room", room.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rate", rate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ht-nope", unknownHotel.Message, StringComparison.Ordinal);
        var context = await h.ContextAsync(s, ServiceType.Hotel);
        Assert.Null(context!.SelectedItemId);
        Assert.Null(context.ConfirmedPrice);
    }

    [Fact]
    public async Task Price_drift_is_disclosed_to_the_model_and_stable_prices_are_not()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var checkIn = Scenarios.DateAfterStart(20);
        var checkOut = Scenarios.DateAfterStart(23);
        var hotels = DomainHarness.ReadList<Hotel>(DomainHarness.Content(await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(checkIn, checkOut))), "hotels");
        var search = new HotelSearchParams("Lisbon", DateOnly.Parse(checkIn, CultureInfo.InvariantCulture), DateOnly.Parse(checkOut, CultureInfo.InvariantCulture), 2, 1);
        var backend = new InMemoryHotelBackend(h.Clock);
        (Hotel Hotel, HotelRoom Room, HotelRate Rate)? drifting = null;
        (Hotel Hotel, HotelRoom Room, HotelRate Rate)? stable = null;
        foreach (var hotel in hotels)
        {
            foreach (var room in await backend.GetRoomsAsync(hotel, search, CancellationToken.None))
            {
                foreach (var rate in room.Rates)
                {
                    var quote = await backend.PrebookAsync(hotel, room, rate, search, CancellationToken.None);
                    if (quote.PriceChanged)
                    {
                        drifting ??= (hotel, room, rate);
                    }
                    else
                    {
                        stable ??= (hotel, room, rate);
                    }
                }
            }
        }

        Assert.NotNull(drifting);
        Assert.NotNull(stable);
        var changed = await h.RunAsync(s, "prebook_room", $$"""{"hotelId":"{{drifting!.Value.Hotel.Id}}","roomId":"{{drifting.Value.Room.RoomId}}","rateId":"{{drifting.Value.Rate.RateId}}"}""");
        var same = await h.RunAsync(s, "prebook_room", $$"""{"hotelId":"{{stable!.Value.Hotel.Id}}","roomId":"{{stable.Value.Room.RoomId}}","rateId":"{{stable.Value.Rate.RateId}}"}""");

        Assert.Equal("hotel_price", changed.State);
        Assert.NotNull(changed.ModelNote);
        Assert.Contains("changed", changed.ModelNote, StringComparison.Ordinal);
        Assert.True(DomainHarness.Content(changed).GetProperty("priceChanged").GetBoolean());
        Assert.Null(same.ModelNote);
        Assert.False(DomainHarness.Content(same).GetProperty("priceChanged").GetBoolean());
    }

    [Theory]
    [InlineData("""{"hotelId":"h","roomId":"r","rateId":"x","price":1}""")]
    [InlineData("""{"hotelId":"h","roomId":"r"}""")]
    [InlineData("""{"hotelId":"h","roomId":"r","rateId":"x","total":{"amount":1}}""")]
    public async Task The_model_cannot_supply_a_price_through_the_schema(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("prebook_room"), AgentJson.Parse(json)));
    }
}

public class CollectGuestDetailsToolTests
{
    [Fact]
    public async Task A_prebooked_room_is_required()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_hotels", Scenarios.HotelSearchJson(Scenarios.DateAfterStart(20), Scenarios.DateAfterStart(23)));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "collect_guest_details"));

        Assert.Contains("Pre-book", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_form_goes_to_the_consumer_and_the_model_only_gets_an_acknowledgement()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);

        var result = await h.RunAsync(s, "collect_guest_details");

        Assert.Equal("guest_form", result.State);
        var schema = AgentJson.Deserialize<FormSchema>(result.Content)!;
        Assert.Equal("guest", schema.FormType);
        Assert.Equal(["leadGuest", "contact", "extras"], schema.Groups.Select(g => g.Id));
        Assert.Contains(schema.Groups[0].Fields, f => f.Name == "leadGuest.nationality" && f.Type == FormFieldType.Country && f.Required);
        Assert.Contains(schema.Groups[2].Fields, f => f.Name == "specialRequests" && !f.Required);
        var ack = result.ModelContent!;
        Assert.True(ack.Length < 300);
        Assert.Contains("form_shown", ack, StringComparison.Ordinal);
        foreach (var leak in new[] { "firstName", "lastName", "nationality", "email", "phone", "specialRequests" })
        {
            Assert.DoesNotContain(leak, ack, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public class GuestFormHandlerTests
{
    private static GuestFormRequest Valid() => new()
    {
        LeadGuest = new LeadGuestDetails { FirstName = "Jane", LastName = "Doe", Nationality = "NL" },
        Contact = new Agent.Domains.Shared.ContactDetails { Email = "user@example.com", Phone = "+15555550100" },
        SpecialRequests = "Quiet room please",
    };

    private static Task<FormSubmissionResult> SubmitAsync(DomainHarness h, SessionContext s, GuestFormRequest request) =>
        h.Get<IFormSubmissionHandler<GuestFormRequest>>().HandleAsync(s.SessionId, request, CancellationToken.None);

    [Fact]
    public async Task Without_a_prebooked_room_the_form_is_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await SubmitAsync(h, s, Valid());

        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("form"));
    }

    [Fact]
    public async Task A_valid_form_is_stored_and_offers_hotel_payment_methods()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);

        var result = await SubmitAsync(h, s, Valid());

        Assert.True(result.Success);
        Assert.Equal("FormAccepted", result.NextStepMessageKey);
        Assert.Contains(result.PaymentMethods, m => m.Id == "pay_at_property");
        Assert.Contains(result.PaymentMethods, m => m.Id == "card");
        var form = (await h.ContextAsync(s, ServiceType.Hotel))!.Form!;
        Assert.Equal("Jane", form.Raw["leadGuest.firstName"]);
        Assert.Equal("Quiet room please", form.Raw["specialRequests"]);
        Assert.Equal(h.Clock.GetUtcNow(), form.SubmittedAt);
    }

    [Fact]
    public async Task The_masked_copy_hides_personal_values_and_free_text()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);

        await SubmitAsync(h, s, Valid());

        var form = (await h.ContextAsync(s, ServiceType.Hotel))!.Form!;
        Assert.Equal("J***", form.Masked["leadGuest.firstName"]);
        Assert.Equal("D***", form.Masked["leadGuest.lastName"]);
        Assert.StartsWith("u***@", form.Masked["contact.email"], StringComparison.Ordinal);
        Assert.Equal("***", form.Masked["specialRequests"]);
        Assert.Equal("NL", form.Masked["leadGuest.nationality"]);
        foreach (var secret in new[] { "Jane", "Doe", "user@example.com", "+15555550100", "Quiet room" })
        {
            Assert.DoesNotContain(form.Masked.Values, v => v.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Special_requests_are_optional()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);
        var request = Valid();
        request.SpecialRequests = null;

        var result = await SubmitAsync(h, s, request);

        Assert.True(result.Success);
        var form = (await h.ContextAsync(s, ServiceType.Hotel))!.Form!;
        Assert.False(form.Raw.ContainsKey("specialRequests"));
        Assert.False(form.Masked.ContainsKey("specialRequests"));
    }

    [Fact]
    public async Task Invalid_fields_are_reported_and_nothing_is_stored()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.PrebookFirstRoomAsync(h, s);
        var request = new GuestFormRequest
        {
            LeadGuest = new LeadGuestDetails { FirstName = "", LastName = new string('x', 61), Nationality = "ZZZ" },
            Contact = new Agent.Domains.Shared.ContactDetails { Email = "nope", Phone = "12" },
            SpecialRequests = new string('y', 501),
        };

        var result = await SubmitAsync(h, s, request);

        Assert.False(result.Success);
        foreach (var field in new[]
                 {
                     "leadGuest.firstName", "leadGuest.lastName", "leadGuest.nationality",
                     "contact.email", "contact.phone", "specialRequests",
                 })
        {
            Assert.True(result.Errors.ContainsKey(field), $"expected an error for {field}");
        }

        Assert.Null((await h.ContextAsync(s, ServiceType.Hotel))!.Form);
    }
}
