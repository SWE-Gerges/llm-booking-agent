using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Domains.Shared.Trips;
using Agent.Tests.Support;

namespace Agent.Tests.TripTests;

public class TripBasketTests
{
    private static JsonElement Trip(ToolResult result) => DomainHarness.Content(result);

    private static string[] Services(ToolResult result) =>
        Trip(result).GetProperty("services").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static Task<ToolResult> AddAsync(DomainHarness h, SessionContext s, string service) =>
        h.RunAsync(s, "add_to_trip", $$"""{"service":"{{service}}"}""");

    private static async Task SeedFormAsync(DomainHarness h, SessionContext s)
    {
        var raw = new Dictionary<string, string> { ["travellers[0].firstName"] = "Jane" };
        await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Trip,
            c => c with { Form = new FormRecord(raw, raw, h.Clock.GetUtcNow()) },
            CancellationToken.None);
    }

    [Theory]
    [InlineData("flight")]
    [InlineData("hotel")]
    [InlineData("transfer")]
    public async Task Adding_a_service_with_nothing_selected_is_refused_with_advice_for_the_model(string service)
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, service));

        Assert.Contains("confirm the price", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await TripScenarios.TripContextAsync(h, s));
    }

    [Fact]
    public async Task A_selection_without_a_confirmed_price_cannot_be_added()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, selected: true, priced: false, form: false);

        await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "flight"));

        Assert.Null(await TripScenarios.TripContextAsync(h, s));
    }

    [Fact]
    public async Task A_confirmed_price_without_a_selection_cannot_be_added()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel, selected: false, priced: true, form: false);

        await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "hotel"));
    }

    [Fact]
    public async Task A_price_older_than_the_confirmation_window_cannot_be_added()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        h.Clock.Advance(TimeSpan.FromMinutes(16));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "flight"));

        Assert.Contains("confirm the price", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adding_a_confirmed_service_puts_it_in_the_basket_without_storing_a_price_there()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);

        var added = await AddAsync(h, s, "flight");

        Assert.Equal("trip", added.State);
        Assert.Equal(["flight"], Services(added));
        var trip = (await TripScenarios.TripContextAsync(h, s))!;
        Assert.Equal([ServiceType.Flight], trip.TripServices);
        Assert.Null(trip.ConfirmedPrice);
        Assert.Null(trip.SelectedItemId);
    }

    [Fact]
    public async Task Adding_the_same_service_twice_is_idempotent()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);

        await AddAsync(h, s, "flight");
        var again = await AddAsync(h, s, "flight");

        Assert.Equal(["flight"], Services(again));
        Assert.Equal([ServiceType.Flight], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task Services_keep_the_order_in_which_they_were_added()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);

        await TripScenarios.AddToTripAsync(h, s, "transfer", "flight", "hotel");

        Assert.Equal([ServiceType.Transfer, ServiceType.Flight, ServiceType.Hotel], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task Services_outside_the_server_allow_list_are_refused_even_when_confirmed()
    {
        using var h = await TripScenarios.CreateAsync(new Dictionary<string, string?>
        {
            ["Trip:AllowedServices:0"] = "Flight",
            ["Trip:AllowedServices:1"] = "Hotel",
        });
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);

        var transfer = await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "transfer"));
        var trip = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<TripBasket>().AddAsync(s.SessionId, ServiceType.Trip, CancellationToken.None));
        var flight = await AddAsync(h, s, "flight");

        Assert.Contains("cannot be added", transfer.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be added", trip.Message, StringComparison.Ordinal);
        Assert.Equal(["flight"], Services(flight));
        Assert.DoesNotContain(ServiceType.Transfer, (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Theory]
    [InlineData("trip")]
    [InlineData("car")]
    [InlineData("FLIGHT")]
    [InlineData("")]
    public async Task The_tool_schemas_only_accept_the_three_bookable_service_names(string service)
    {
        using var h = await TripScenarios.CreateAsync();

        foreach (var tool in new[] { "add_to_trip", "remove_from_trip" })
        {
            Assert.NotNull(h.Validator.Validate(h.Tool(tool), AgentJson.Parse($$"""{"service":"{{service}}"}""")));
        }
    }

    [Fact]
    public async Task The_trip_tools_reject_prices_travellers_and_unknown_arguments()
    {
        using var h = await TripScenarios.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("add_to_trip"), AgentJson.Parse("""{"service":"flight","price":1}""")));
        Assert.NotNull(h.Validator.Validate(h.Tool("add_to_trip"), AgentJson.Parse("{}")));
        Assert.NotNull(h.Validator.Validate(h.Tool("remove_from_trip"), AgentJson.Parse("""{"service":"hotel","force":true}""")));
        foreach (var tool in new[] { "view_trip", "collect_trip_details", "submit_trip_booking" })
        {
            Assert.Null(h.Validator.Validate(h.Tool(tool), AgentJson.Parse("{}")));
            foreach (var extra in new[] { """{"price":1}""", """{"service":"flight"}""", """{"travellers":[{"firstName":"A"}]}""", """{"total":{"amount":1,"currency":"EUR"}}""" })
            {
                Assert.NotNull(h.Validator.Validate(h.Tool(tool), AgentJson.Parse(extra)));
            }
        }
    }

    [Fact]
    public void An_unset_or_empty_allow_list_means_flight_hotel_and_transfer_and_trip_is_never_allowed()
    {
        Assert.Equal([ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer], new TripOptions().Allowed);
        Assert.Equal([ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer], new TripOptions { AllowedServices = [] }.Allowed);
        Assert.Equal([ServiceType.Flight], new TripOptions { AllowedServices = [ServiceType.Trip, ServiceType.Flight, ServiceType.Flight] }.Allowed);
        Assert.Equal(3, new TripOptions().MaxServices);
    }

    [Fact]
    public async Task The_basket_cannot_exceed_the_configured_size()
    {
        using var h = await TripScenarios.CreateAsync(new Dictionary<string, string?> { ["Trip:MaxServices"] = "2" });
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel");

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "transfer"));

        Assert.Contains("at most 2", error.Message, StringComparison.Ordinal);
        Assert.Equal([ServiceType.Flight, ServiceType.Hotel], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task Re_adding_a_member_does_not_count_against_the_cap()
    {
        using var h = await TripScenarios.CreateAsync(new Dictionary<string, string?> { ["Trip:MaxServices"] = "1" });
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);

        await TripScenarios.AddToTripAsync(h, s, "flight", "flight");

        Assert.Equal([ServiceType.Flight], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task Removing_a_service_that_is_not_in_the_trip_is_an_error_and_creates_nothing()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var empty = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "remove_from_trip", """{"service":"hotel"}"""));
        Assert.Contains("not in the current trip", empty.Message, StringComparison.Ordinal);
        Assert.Null(await TripScenarios.TripContextAsync(h, s));

        await TripScenarios.ConfirmAllAsync(h, s);
        await AddAsync(h, s, "flight");
        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "remove_from_trip", """{"service":"transfer"}"""));
        Assert.Equal([ServiceType.Flight], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task Removing_a_service_clears_the_traveller_form_so_it_must_be_submitted_again()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        await SeedFormAsync(h, s);
        Assert.NotNull((await TripScenarios.TripContextAsync(h, s))!.Form);

        var removed = await h.RunAsync(s, "remove_from_trip", """{"service":"transfer"}""");

        Assert.Equal(["flight", "hotel"], Services(removed));
        Assert.False(Trip(removed).GetProperty("formSubmitted").GetBoolean());
        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task Adding_a_service_clears_the_traveller_form_too()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight");
        await SeedFormAsync(h, s);

        var added = await AddAsync(h, s, "hotel");

        Assert.False(Trip(added).GetProperty("formSubmitted").GetBoolean());
        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task Re_adding_a_member_changes_nothing_so_the_submitted_form_stays_valid()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight");
        await SeedFormAsync(h, s);

        await AddAsync(h, s, "flight");

        Assert.NotNull((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task A_new_search_for_a_service_in_the_basket_turns_its_line_into_needs_price_check()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        Assert.True(Trip(await h.RunAsync(s, "view_trip")).GetProperty("readyToSubmit").GetBoolean());

        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(40), adults: 1));

        var view = Trip(await h.RunAsync(s, "view_trip"));
        var lines = view.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("service").GetString()!, l => l.GetProperty("status").GetString());
        Assert.Equal("needs_price_check", lines["flight"]);
        Assert.Equal("ready", lines["hotel"]);
        Assert.Equal("ready", lines["transfer"]);
        Assert.False(view.GetProperty("readyToSubmit").GetBoolean());
        Assert.False(view.TryGetProperty("total", out _), "an incomplete basket has no total");

        var form = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "collect_trip_details"));
        Assert.Contains("flight", form.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Confirming_the_price_again_makes_the_line_ready_without_touching_the_basket()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var flight = await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        await AddAsync(h, s, "flight");
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(41), adults: 1));
        Assert.False(Trip(await h.RunAsync(s, "view_trip")).GetProperty("readyToSubmit").GetBoolean());

        var fresh = await h.ContextAsync(s, ServiceType.Flight);
        var offerId = Agent.Domains.Shared.SearchSnapshots.ReadResults<Agent.Domains.Flights.FlightOffer>(fresh!.Search!)[0].Id;
        await h.RunAsync(s, "recheck_flight_price", $$"""{"offerId":"{{offerId}}"}""");

        var view = Trip(await h.RunAsync(s, "view_trip"));
        Assert.True(view.GetProperty("readyToSubmit").GetBoolean());
        Assert.NotEqual(flight.Id, view.GetProperty("lines")[0].GetProperty("itemId").GetString());
    }

    [Fact]
    public async Task A_price_that_ages_past_the_window_blocks_the_checkout_until_it_is_confirmed_again()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        h.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(Trip(await h.RunAsync(s, "view_trip")).GetProperty("readyToSubmit").GetBoolean());

        h.Clock.Advance(TimeSpan.FromMinutes(2));

        var view = Trip(await h.RunAsync(s, "view_trip"));
        Assert.False(view.GetProperty("readyToSubmit").GetBoolean());
        Assert.All(view.GetProperty("lines").EnumerateArray(), l => Assert.Equal("needs_price_check", l.GetProperty("status").GetString()));
        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "collect_trip_details"));
        await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Viewing_an_empty_trip_shows_nothing_to_check_out()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var view = Trip(await h.RunAsync(s, "view_trip"));

        Assert.Equal(0, view.GetProperty("services").GetArrayLength());
        Assert.False(view.GetProperty("readyToSubmit").GetBoolean());
        Assert.Equal(1, view.GetProperty("travellers").GetInt32());
    }

    [Fact]
    public async Task View_trip_derives_travellers_total_and_passport_need_from_the_confirmed_services()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        // Flight for 1 adult (passport required), hotel for 2 adults (default), transfer for 3 passengers.
        await Scenarios.ConfirmFlightAsync(h, s, o => o.RequiresPassport, adults: 1);
        await Scenarios.PrebookFirstRoomAsync(h, s);
        await TripScenarios.SelectTransferAsync(h, s, passengers: 3);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");

        var result = await h.RunAsync(s, "view_trip");
        var view = Trip(result);

        Assert.Equal(3, view.GetProperty("travellers").GetInt32());
        Assert.True(view.GetProperty("passportRequired").GetBoolean());
        Assert.True(view.GetProperty("readyToSubmit").GetBoolean());
        var parties = view.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("service").GetString()!, l => l.GetProperty("partySize").GetInt32());
        Assert.Equal(1, parties["flight"]);
        Assert.Equal(2, parties["hotel"]);
        Assert.Equal(3, parties["transfer"]);

        var sum = 0m;
        foreach (var service in new[] { ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer })
        {
            sum += (await h.ContextAsync(s, service))!.ConfirmedPrice!.Total.Amount;
        }

        Assert.Equal(sum, view.GetProperty("total").GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", view.GetProperty("total").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task A_flight_that_needs_no_passport_leaves_the_requirement_off()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport, adults: 1);
        await AddAsync(h, s, "flight");

        var view = Trip(await h.RunAsync(s, "view_trip"));

        Assert.False(view.GetProperty("passportRequired").GetBoolean());
        Assert.Equal(1, view.GetProperty("travellers").GetInt32());
    }

    [Fact]
    public async Task Services_priced_in_different_currencies_cannot_share_a_trip_and_the_refused_add_changes_nothing()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        await AddAsync(h, s, "flight");
        await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Hotel,
            c => c with
            {
                SelectedItemId = "ht-usd",
                ConfirmedPrice = new PriceConfirmation("pb-usd", new Money(100m, "USD"), h.Clock.GetUtcNow()),
            },
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => AddAsync(h, s, "hotel"));

        Assert.Contains("different currencies", error.Message, StringComparison.Ordinal);
        Assert.Equal([ServiceType.Flight], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
        Assert.True(Trip(await h.RunAsync(s, "view_trip")).GetProperty("readyToSubmit").GetBoolean());
    }

    [Fact]
    public async Task Parallel_adds_of_different_services_are_each_applied_exactly_once()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);

        await Task.WhenAll(AddAsync(h, s, "flight"), AddAsync(h, s, "hotel"), AddAsync(h, s, "transfer"));

        var services = (await TripScenarios.TripContextAsync(h, s))!.TripServices;
        Assert.Equal(3, services.Count);
        Assert.Equal(3, services.Distinct().Count());
    }

    [Fact]
    public async Task Parallel_adds_of_the_same_service_leave_a_single_entry()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AddAsync(h, s, "flight")));

        Assert.Equal([ServiceType.Flight], (await TripScenarios.TripContextAsync(h, s))!.TripServices);
    }

    [Fact]
    public async Task The_size_cap_holds_when_more_services_than_allowed_are_added_in_parallel()
    {
        using var h = await TripScenarios.CreateAsync(new Dictionary<string, string?> { ["Trip:MaxServices"] = "2" });
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s);

        var outcomes = await Task.WhenAll(new[] { "flight", "hotel", "transfer", "transfer", "hotel", "flight" }.Select(async service =>
        {
            try
            {
                await AddAsync(h, s, service);
                return (Service: service, Refused: false);
            }
            catch (ToolExecutionException)
            {
                return (Service: service, Refused: true);
            }
        }));

        var services = (await TripScenarios.TripContextAsync(h, s))!.TripServices;
        Assert.Equal(2, services.Count);
        Assert.Equal(2, services.Distinct().Count());
        Assert.Contains(outcomes, o => o.Refused);
        Assert.All(
            outcomes.Where(o => !o.Refused),
            o => Assert.Contains(Enum.Parse<ServiceType>(o.Service, ignoreCase: true), services));
    }
}
