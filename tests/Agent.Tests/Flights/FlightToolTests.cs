using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Forms;
using Agent.Abstractions.Sessions;
using Agent.Domains.Flights;
using Agent.Tests.Support;

namespace Agent.Tests.FlightTests;

public class SearchFlightsToolTests
{
    private static async Task<List<FlightOffer>> SearchAsync(DomainHarness h, SessionContext s, int? adults = null, int day = 10) =>
        DomainHarness.ReadList<FlightOffer>(
            DomainHarness.Content(await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(day), adults))),
            "offers");

    [Fact]
    public async Task Same_origin_and_destination_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), origin: "AMS", destination: "ams")));

        Assert.Contains("different", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Past_departure_date_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(-1))));

        Assert.Contains("past", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Return_before_departure_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), returnDate: Scenarios.DateAfterStart(9))));

        Assert.Contains("return", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Impossible_calendar_date_is_rejected_even_though_it_matches_the_schema_pattern()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson("2026-13-45")));
    }

    [Theory]
    [InlineData("""{"origin":"A1","destination":"LIS","departureDate":"2026-04-01"}""")]
    [InlineData("""{"origin":"AMS","destination":"LISB","departureDate":"2026-04-01"}""")]
    [InlineData("""{"origin":"AMS","destination":"LIS","departureDate":"tomorrow"}""")]
    [InlineData("""{"origin":"AMS","destination":"LIS","departureDate":"2026-04-01","adults":0}""")]
    [InlineData("""{"origin":"AMS","destination":"LIS","departureDate":"2026-04-01","adults":10}""")]
    [InlineData("""{"origin":"AMS","destination":"LIS","departureDate":"2026-04-01","cabin":"coach"}""")]
    [InlineData("""{"origin":"AMS","destination":"LIS","departureDate":"2026-04-01","price":1}""")]
    [InlineData("""{"origin":"AMS","departureDate":"2026-04-01"}""")]
    public async Task Schema_rejects_malformed_input(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        var problem = h.Validator.Validate(h.Tool("search_flights"), AgentJson.Parse(json));

        Assert.NotNull(problem);
        Assert.Contains("search_flights", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_stores_a_snapshot_under_the_returned_handle_and_marks_the_service_as_requested()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 2));

        Assert.False(result.IsError);
        Assert.Equal("flight_results", result.State);
        Assert.Contains(ServiceType.Flight, s.RequestedServices);
        var content = DomainHarness.Content(result);
        var context = await h.ContextAsync(s, ServiceType.Flight);
        Assert.NotNull(context?.Search);
        Assert.Equal(content.GetProperty("handle").GetString(), context!.Search!.Handle);
        Assert.Equal(h.Clock.GetUtcNow(), context.Search.CreatedAt);
        var stored = AgentJson.Deserialize<List<FlightOffer>>(context.Search.ResultsJson)!;
        Assert.Equal(DomainHarness.ReadList<FlightOffer>(content, "offers").Select(o => o.Id), stored.Select(o => o.Id));
        var parameters = AgentJson.Deserialize<FlightSearchParams>(context.Search.ParamsJson)!;
        Assert.Equal(("AMS", "LIS", 2, "economy"), (parameters.Origin, parameters.Destination, parameters.Adults, parameters.Cabin));
    }

    [Fact]
    public async Task Results_are_capped_at_ten_and_sorted_by_price()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var offers = await SearchAsync(h, s, 1);

        Assert.InRange(offers.Count, 1, 10);
        Assert.Equal(offers.OrderBy(o => o.Price.Amount).Select(o => o.Id), offers.Select(o => o.Id));
    }

    [Fact]
    public async Task A_return_date_produces_round_trip_offers()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1, returnDate: Scenarios.DateAfterStart(17)));

        Assert.All(DomainHarness.ReadList<FlightOffer>(DomainHarness.Content(result), "offers"), o => Assert.NotEmpty(o.Inbound));
    }

    [Fact]
    public async Task Omitted_adults_assumes_one_adult_and_tells_the_model_to_disclose_it()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10)));

        Assert.NotNull(result.ModelNote);
        Assert.Contains("1 adult", result.ModelNote, StringComparison.Ordinal);
        Assert.Equal(1, DomainHarness.Content(result).GetProperty("search").GetProperty("adults").GetInt32());
    }

    [Fact]
    public async Task Explicit_adults_carry_no_assumption_note()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 3));

        Assert.Null(result.ModelNote);
        Assert.Equal(3, DomainHarness.Content(result).GetProperty("search").GetProperty("adults").GetInt32());
    }

    [Fact]
    public async Task Model_view_hides_the_supplier_while_the_api_payload_keeps_it()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        Assert.Contains("supplier-", result.Content, StringComparison.Ordinal);
        Assert.NotNull(result.ModelContent);
        Assert.DoesNotContain("supplier", result.ModelContent, StringComparison.OrdinalIgnoreCase);
        var view = DomainHarness.ModelView(result);
        Assert.Equal(DomainHarness.Content(result).GetProperty("handle").GetString(), view.GetProperty("handle").GetString());
        Assert.Equal(view.GetProperty("count").GetInt32(), view.GetProperty("offers").GetArrayLength());
    }

    [Fact]
    public async Task A_new_search_resets_the_selection_and_the_confirmed_price()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s);
        var confirmed = await h.ContextAsync(s, ServiceType.Flight);
        Assert.NotNull(confirmed!.SelectedItemId);
        Assert.NotNull(confirmed.ConfirmedPrice);

        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(40), 1));

        var after = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Null(after!.SelectedItemId);
        Assert.Null(after.SelectedOptionId);
        Assert.Null(after.ConfirmedPrice);
        Assert.NotEqual(confirmed.Search!.Handle, after.Search!.Handle);
    }

    [Fact]
    public async Task Before_a_booking_the_form_survives_a_new_search()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var form = new FormRecord(new Dictionary<string, string> { ["contact.email"] = "x@example.com" }, new Dictionary<string, string>(), h.Clock.GetUtcNow());
        await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { Form = form }, CancellationToken.None);

        await SearchAsync(h, s, 1);

        var after = await h.ContextAsync(s, ServiceType.Flight);
        Assert.NotNull(after!.Form);
        Assert.Empty(after.PastBookingIds);
    }

    [Fact]
    public async Task After_a_booking_a_new_search_archives_the_booking_id_and_starts_clean()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var form = new FormRecord(new Dictionary<string, string>(), new Dictionary<string, string>(), h.Clock.GetUtcNow());
        await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Flight,
            c => c with { BookingId = "BK-OLD", IdempotencyKey = "key-1", Form = form, PastBookingIds = ["BK-OLDER"] },
            CancellationToken.None);

        await SearchAsync(h, s, 1);

        var after = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Null(after!.BookingId);
        Assert.Null(after.IdempotencyKey);
        Assert.Null(after.Form);
        Assert.Equal(["BK-OLDER", "BK-OLD"], after.PastBookingIds);
    }
}

public class FilterFlightOffersToolTests
{
    private static async Task<(DomainHarness H, SessionContext S, string Handle, List<FlightOffer> Offers)> ArrangeAsync()
    {
        var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var result = await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));
        var content = DomainHarness.Content(result);
        return (h, s, content.GetProperty("handle").GetString()!, DomainHarness.ReadList<FlightOffer>(content, "offers"));
    }

    private static async Task<List<string>> FilterIdsAsync(DomainHarness h, SessionContext s, string json)
    {
        var result = await h.RunAsync(s, "filter_flight_offers", json);
        Assert.Equal("flight_results", result.State);
        return DomainHarness.ReadList<FlightOffer>(DomainHarness.Content(result), "offers").Select(o => o.Id).ToList();
    }

    [Fact]
    public async Task Max_price_keeps_only_cheaper_offers()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;
        var limit = offers.OrderBy(o => o.Price.Amount).ElementAt(offers.Count / 2).Price.Amount;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","maxPrice":{{limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");

        var expected = offers.Where(o => o.Price.Amount <= limit).Select(o => o.Id).ToList();
        Assert.Equal(expected, ids);
        Assert.True(ids.Count < offers.Count);
    }

    [Fact]
    public async Task Max_stops_zero_keeps_only_direct_flights()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","maxStops":0}""");

        Assert.Equal(offers.Where(o => o.Stops == 0).Select(o => o.Id), ids);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public async Task Carrier_filter_is_case_insensitive()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;
        var carrier = offers[0].Outbound[0].Carrier;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","carriers":["{{carrier.ToLowerInvariant()}}"]}""");

        Assert.Equal(offers.Where(o => o.Outbound[0].Carrier == carrier).Select(o => o.Id), ids);
    }

    [Fact]
    public async Task Refundable_only_keeps_refundable_offers()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","refundableOnly":true}""");

        Assert.Equal(offers.Where(o => o.Refundable).Select(o => o.Id), ids);
    }

    [Fact]
    public async Task Depart_after_filters_on_the_outbound_departure_time()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","departAfter":"12:00"}""");

        var expected = offers.Where(o => TimeOnly.FromDateTime(o.Outbound[0].DepartAt.UtcDateTime) >= new TimeOnly(12, 0)).Select(o => o.Id).ToList();
        Assert.Equal(expected, ids);
        Assert.True(ids.Count < offers.Count);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("departure")]
    [InlineData("price")]
    public async Task Sorting_orders_the_result(string sortBy)
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;

        var ids = await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","sortBy":"{{sortBy}}"}""");

        IEnumerable<FlightOffer> expected = sortBy switch
        {
            "duration" => offers.OrderBy(o => o.DurationMinutes),
            "departure" => offers.OrderBy(o => o.Outbound[0].DepartAt),
            _ => offers.OrderBy(o => o.Price.Amount),
        };
        Assert.Equal(expected.Select(o => o.Id), ids);
    }

    [Fact]
    public async Task Filtering_does_not_change_the_stored_snapshot()
    {
        var (h, s, handle, offers) = await ArrangeAsync();
        using var disposeHarness = h;

        await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}","maxStops":0}""");

        var context = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Equal(handle, context!.Search!.Handle);
        Assert.Equal(offers.Count, AgentJson.Deserialize<List<FlightOffer>>(context.Search.ResultsJson)!.Count);
    }

    [Fact]
    public async Task A_stale_handle_is_rejected_with_a_message_for_the_model()
    {
        var (h, s, _, _) = await ArrangeAsync();
        using var disposeHarness = h;

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "filter_flight_offers", """{"handle":"not-current"}"""));

        Assert.Contains("handle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_snapshot_is_rejected_after_thirty_minutes()
    {
        var (h, s, handle, _) = await ArrangeAsync();
        using var disposeHarness = h;

        h.Clock.Advance(TimeSpan.FromMinutes(29));
        await FilterIdsAsync(h, s, $$"""{"handle":"{{handle}}"}""");

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "filter_flight_offers", $$"""{"handle":"{{handle}}"}"""));
        Assert.Contains("out of date", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filtering_before_any_search_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "filter_flight_offers", """{"handle":"abc"}"""));

        Assert.Contains("search", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"handle":"h","maxPrice":0}""")]
    [InlineData("""{"handle":"h","maxStops":4}""")]
    [InlineData("""{"handle":"h","departAfter":"25:00"}""")]
    [InlineData("""{"handle":"h","sortBy":"cheapest"}""")]
    public async Task Schema_rejects_malformed_filters(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("filter_flight_offers"), AgentJson.Parse(json)));
    }
}

public class FlightDetailAndFareToolTests
{
    [Fact]
    public async Task Details_of_an_unknown_offer_are_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_flight_details", """{"offerId":"fl-nope"}"""));

        Assert.Contains("fl-nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Details_show_segments_to_the_model_without_the_supplier()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var offers = DomainHarness.ReadList<FlightOffer>(
            DomainHarness.Content(await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1))),
            "offers");
        var offer = offers[0];

        var result = await h.RunAsync(s, "get_flight_details", $$"""{"offerId":"{{offer.Id}}"}""");

        Assert.Equal("flight_details", result.State);
        Assert.Contains("supplier-", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", result.ModelContent!, StringComparison.OrdinalIgnoreCase);
        var view = DomainHarness.ModelView(result);
        Assert.Equal(offer.Id, view.GetProperty("id").GetString());
        Assert.Equal(offer.Outbound.Count, view.GetProperty("outbound").GetArrayLength());
        Assert.Equal(offer.Outbound[0].FlightNumber, view.GetProperty("outbound")[0].GetProperty("flight").GetString());
    }

    [Fact]
    public async Task Details_without_a_search_are_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_flight_details", """{"offerId":"x"}"""));
    }

    [Fact]
    public async Task Branded_fares_of_an_unknown_offer_are_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_branded_fares", """{"offerId":"fl-nope"}"""));
    }

    [Fact]
    public async Task Branded_fares_list_every_brand_for_a_known_offer()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var offer = DomainHarness.ReadList<FlightOffer>(
            DomainHarness.Content(await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1))),
            "offers")[0];

        var result = await h.RunAsync(s, "get_branded_fares", $$"""{"offerId":"{{offer.Id}}"}""");

        Assert.Equal("flight_fares", result.State);
        var fares = DomainHarness.ReadList<BrandedFare>(DomainHarness.Content(result), "fares");
        Assert.Equal(["basic", "standard", "flex"], fares.Select(f => f.Brand));
    }
}

public class RecheckFlightPriceToolTests
{
    [Fact]
    public async Task Selection_and_price_come_from_the_backend_quote()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var offer = await Scenarios.ConfirmFlightAsync(h, s, fareBrand: "flex");

        var context = await h.ContextAsync(s, ServiceType.Flight);
        var expected = await new InMemoryFlightBackend(h.Clock).RecheckPriceAsync(offer, "flex", CancellationToken.None);
        Assert.Equal(offer.Id, context!.SelectedItemId);
        Assert.Equal("flex", context.SelectedOptionId);
        Assert.Equal(expected.Total, context.ConfirmedPrice!.Total);
        Assert.Equal(h.Clock.GetUtcNow(), context.ConfirmedPrice.ConfirmedAt);
        Assert.StartsWith("qt-", context.ConfirmedPrice.QuoteId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_fare_brand_the_offers_own_brand_is_used()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var offer = await Scenarios.ConfirmFlightAsync(h, s);

        Assert.Equal(offer.FareBrand, (await h.ContextAsync(s, ServiceType.Flight))!.SelectedOptionId);
    }

    [Fact]
    public async Task A_changed_price_tells_the_model_to_disclose_it()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var backend = new InMemoryFlightBackend(h.Clock);

        var offer = await Scenarios.ConfirmFlightAsync(
            h,
            s,
            async o => (await backend.RecheckPriceAsync(o, null, CancellationToken.None)).PriceChanged);

        var result = await h.RunAsync(s, "recheck_flight_price", $$"""{"offerId":"{{offer.Id}}"}""");

        Assert.Equal("flight_price", result.State);
        Assert.NotNull(result.ModelNote);
        Assert.Contains("changed", result.ModelNote, StringComparison.Ordinal);
        var content = DomainHarness.Content(result);
        Assert.True(content.GetProperty("priceChanged").GetBoolean());
        Assert.Equal(offer.Price.Amount, content.GetProperty("previousTotal").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task An_unchanged_price_carries_no_note()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var backend = new InMemoryFlightBackend(h.Clock);
        var offer = await Scenarios.ConfirmFlightAsync(
            h,
            s,
            async o => !(await backend.RecheckPriceAsync(o, null, CancellationToken.None)).PriceChanged);

        var result = await h.RunAsync(s, "recheck_flight_price", $$"""{"offerId":"{{offer.Id}}"}""");

        Assert.Null(result.ModelNote);
        Assert.False(DomainHarness.Content(result).GetProperty("priceChanged").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_offer_is_rejected_and_leaves_the_context_untouched()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "recheck_flight_price", """{"offerId":"fl-nope"}"""));

        var context = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Null(context!.SelectedItemId);
        Assert.Null(context.ConfirmedPrice);
    }

    [Theory]
    [InlineData("""{"offerId":"x","price":1}""")]
    [InlineData("""{"offerId":"x","total":{"amount":1,"currency":"EUR"}}""")]
    [InlineData("""{"offerId":"x","fareBrand":"gold"}""")]
    [InlineData("""{}""")]
    public async Task The_model_cannot_supply_a_price_through_the_schema(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("recheck_flight_price"), AgentJson.Parse(json)));
    }
}

public class CollectPassengerDetailsToolTests
{
    [Fact]
    public async Task A_confirmed_price_is_required()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "collect_passenger_details"));

        Assert.Contains("Re-check", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_search_the_form_is_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "collect_passenger_details"));
    }

    [Fact]
    public async Task The_form_schema_goes_to_the_consumer_and_the_model_gets_only_a_short_acknowledgement()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, adults: 2);

        var result = await h.RunAsync(s, "collect_passenger_details");

        Assert.Equal("passenger_form", result.State);
        var schema = AgentJson.Deserialize<FormSchema>(result.Content)!;
        Assert.Equal("passenger", schema.FormType);
        Assert.Equal(["passenger1", "passenger2", "contact"], schema.Groups.Select(g => g.Id));
        Assert.Contains(schema.Groups[0].Fields, f => f.Name == "passengers[0].firstName" && f.Type == FormFieldType.Text && f.Required);
        Assert.Contains(schema.Groups[2].Fields, f => f.Name == "contact.email" && f.Type == FormFieldType.Email);
        Assert.Contains(schema.Groups[0].Fields, f => f.Type == FormFieldType.Country);
        Assert.Contains(schema.Groups[0].Fields, f => f.Type == FormFieldType.Select && f.Options is { Count: 3 });

        var ack = result.ModelContent!;
        Assert.True(ack.Length < 300);
        Assert.Contains("form_shown", ack, StringComparison.Ordinal);
        foreach (var leak in new[] { "firstName", "lastName", "passport", "email", "phone", "dateOfBirth", "passengers[" })
        {
            Assert.DoesNotContain(leak, ack, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Passport_fields_are_mandatory_only_when_the_supplier_requires_them()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var strict = await Scenarios.ConfirmFlightAsync(h, s, o => o.RequiresPassport);
        var strictForm = AgentJson.Deserialize<FormSchema>((await h.RunAsync(s, "collect_passenger_details")).Content)!;
        var relaxed = await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        var relaxedForm = AgentJson.Deserialize<FormSchema>((await h.RunAsync(s, "collect_passenger_details")).Content)!;

        var strictFields = strictForm.Groups[0].Fields.Where(f => f.Name.Contains("passport", StringComparison.Ordinal)).ToList();
        var relaxedFields = relaxedForm.Groups[0].Fields.Where(f => f.Name.Contains("passport", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, strictFields.Count);
        Assert.All(strictFields, f =>
        {
            Assert.True(f.Required);
            Assert.Equal(strict.Supplier, f.RequiredBy);
        });
        Assert.Equal(2, relaxedFields.Count);
        Assert.All(relaxedFields, f =>
        {
            Assert.False(f.Required);
            Assert.Null(f.RequiredBy);
        });
        Assert.NotEqual(strict.Supplier, relaxed.Supplier);
    }

    [Fact]
    public async Task Schema_accepts_only_an_empty_object()
    {
        using var h = await DomainHarness.CreateAsync();
        var tool = h.Tool("collect_passenger_details");

        Assert.Null(h.Validator.Validate(tool, AgentJson.Parse("{}")));
        Assert.NotNull(h.Validator.Validate(tool, AgentJson.Parse("""{"name":"x"}""")));
    }

    [Fact]
    public void Form_schema_serialises_with_camel_case_names_and_lower_case_field_types()
    {
        var json = JsonSerializer.Serialize(Agent.Domains.Flights.Forms.PassengerFormSchema.Build(1, true, "supplier-b"), AgentJson.Options);

        Assert.Contains("\"formType\":\"passenger\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"country\"", json, StringComparison.Ordinal);
        Assert.Contains("\"requiredBy\":\"supplier-b\"", json, StringComparison.Ordinal);
    }
}
