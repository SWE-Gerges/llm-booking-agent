using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Domains.Transfers;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.TransferTests;

public class SearchTransfersToolTests
{
    private static async Task<(ToolResult Result, List<TransferOffer> Offers)> SearchAsync(DomainHarness h, SessionContext s, int? passengers = null, int day = 10)
    {
        var result = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(day), passengers: passengers));
        return (result, DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(result), "offers"));
    }

    [Fact]
    public async Task A_past_date_is_rejected()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(-1))));

        Assert.Contains("past", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:30")]
    [InlineData("10:60")]
    [InlineData("noon")]
    public async Task A_malformed_time_is_rejected_by_the_schema_before_the_tool_runs(string time)
    {
        using var h = await TripScenarios.CreateAsync();

        var problem = h.Validator.Validate(
            h.Tool("search_transfers"),
            AgentJson.Parse(TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), time)));

        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("""{"pickup":"LIS","dropoff":"Lisbon","date":"2026-04-01"}""")]
    [InlineData("""{"pickup":"L","dropoff":"Lisbon","date":"2026-04-01","time":"10:00"}""")]
    [InlineData("""{"pickup":"LIS","dropoff":"Lisbon","date":"2026-04-01","time":"10:00","passengers":13}""")]
    [InlineData("""{"pickup":"LIS","dropoff":"Lisbon","date":"2026-04-01","time":"10:00","passengers":0}""")]
    [InlineData("""{"pickup":"LIS","dropoff":"Lisbon","date":"2026-04-01","time":"10:00","price":1}""")]
    public async Task Missing_or_out_of_range_input_is_rejected_by_the_schema(string json)
    {
        using var h = await TripScenarios.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("search_transfers"), AgentJson.Parse(json)));
    }

    [Fact]
    public async Task An_impossible_calendar_date_is_rejected_by_the_tool()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson("2026-02-30")));
    }

    [Fact]
    public async Task When_no_vehicle_fits_the_model_gets_an_actionable_error_and_nothing_is_stored()
    {
        using var h = await TripScenarios.CreateAsync(configure: services => services.AddSingleton<ITransferBackend>(new EmptyTransferBackend()));
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 12)));

        Assert.Contains("No vehicle", error.Message, StringComparison.Ordinal);
        Assert.Null(await h.ContextAsync(s, ServiceType.Transfer));
        Assert.Empty(s.RequestedServices);
    }

    [Fact]
    public async Task The_snapshot_records_the_party_size_the_search_was_for()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var (result, offers) = await SearchAsync(h, s, passengers: 5);

        var snapshot = (await h.ContextAsync(s, ServiceType.Transfer))!.Search!;
        Assert.Equal(5, snapshot.PartySize);
        Assert.Equal(DomainHarness.Content(result).GetProperty("handle").GetString(), snapshot.Handle);
        Assert.Equal(offers.Select(o => o.Id), SearchSnapshots_ReadIds(snapshot));
        Assert.All(offers, o => Assert.True(o.MaxPassengers >= 5));
    }

    [Fact]
    public async Task Searching_registers_the_transfer_service_on_the_session()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        await SearchAsync(h, s, passengers: 2);

        Assert.Equal([ServiceType.Transfer], s.RequestedServices);
    }

    [Fact]
    public async Task Omitted_passengers_mean_one_passenger_and_a_disclosure_note_for_the_model()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var (result, _) = await SearchAsync(h, s);

        Assert.Equal(1, (await h.ContextAsync(s, ServiceType.Transfer))!.Search!.PartySize);
        Assert.NotNull(result.ModelNote);
        Assert.Contains("assumed", result.ModelNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_passengers_carry_no_assumption_note()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var (result, _) = await SearchAsync(h, s, passengers: 2);

        Assert.Null(result.ModelNote);
        Assert.Equal("transfer_results", result.State);
    }

    [Fact]
    public async Task The_model_view_hides_the_supplier_while_the_consumer_view_keeps_it()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var (result, _) = await SearchAsync(h, s, passengers: 2);

        Assert.Contains("\"supplier\"", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", result.ModelContent, StringComparison.OrdinalIgnoreCase);
        var view = DomainHarness.ModelView(result);
        Assert.True(view.GetProperty("count").GetInt32() > 0);
        Assert.Equal("EUR", view.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Offers_are_listed_cheapest_first()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var (_, offers) = await SearchAsync(h, s, passengers: 1);

        Assert.Equal(offers.OrderBy(o => o.Price.Amount).Select(o => o.Id), offers.Select(o => o.Id));
    }

    [Fact]
    public async Task The_tool_is_a_transfer_search_and_the_one_search_per_turn_policy_applies_to_it()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var tool = h.Tool("search_transfers");
        var policy = new OneSearchPerTurnPolicy();

        var search = Assert.IsAssignableFrom<ISearchTool>(tool);
        Assert.Equal(ServiceType.Transfer, search.Service);

        var turn = new TurnState();
        PolicyContext Pending() => new(s, turn, new ToolUseBlock("t", "search_transfers", AgentJson.EmptyObject), tool, h.Clock.GetUtcNow());
        Assert.True((await policy.EvaluateAsync(Pending(), CancellationToken.None)).IsAllowed);

        turn.RecordCall("search_flights", isSearch: true, carriedAssumption: false);
        var second = await policy.EvaluateAsync(Pending(), CancellationToken.None);
        Assert.False(second.IsAllowed);
        Assert.Contains("one search", second.MessageForModel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_corrected_research_is_allowed_once_when_the_first_search_carried_an_assumption()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var tool = h.Tool("search_transfers");
        var policy = new OneSearchPerTurnPolicy();
        var turn = new TurnState();
        PolicyContext Pending() => new(s, turn, new ToolUseBlock("t", "search_transfers", AgentJson.EmptyObject), tool, h.Clock.GetUtcNow());

        turn.RecordCall("search_transfers", isSearch: true, carriedAssumption: true);
        Assert.True((await policy.EvaluateAsync(Pending(), CancellationToken.None)).IsAllowed);

        turn.RecordCall("search_transfers", isSearch: true, carriedAssumption: false);
        Assert.False((await policy.EvaluateAsync(Pending(), CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public async Task A_new_search_replaces_the_snapshot_and_drops_an_earlier_selection()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await TripScenarios.SelectTransferAsync(h, s, passengers: 2);
        var before = (await h.ContextAsync(s, ServiceType.Transfer))!;
        Assert.NotNull(before.ConfirmedPrice);

        await SearchAsync(h, s, passengers: 2, day: 30);

        var after = (await h.ContextAsync(s, ServiceType.Transfer))!;
        Assert.NotEqual(before.Search!.Handle, after.Search!.Handle);
        Assert.Null(after.SelectedItemId);
        Assert.Null(after.ConfirmedPrice);
    }

    private static IEnumerable<string> SearchSnapshots_ReadIds(SearchSnapshot snapshot) =>
        Agent.Domains.Shared.SearchSnapshots.ReadResults<TransferOffer>(snapshot).Select(o => o.Id);
}

public class TransferDetailsAndPriceToolTests
{
    [Fact]
    public async Task Details_of_an_offer_outside_the_latest_search_are_refused()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 2));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_transfer_details", """{"offerId":"tr-unknown"}"""));

        Assert.Contains("not part of the latest search", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Details_without_any_search_say_to_search_first()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, "get_transfer_details", """{"offerId":"tr-1"}"""));

        Assert.Contains("search", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_search_results_are_refused_for_details_price_and_selection()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var search = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 2));
        var id = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers")[0].Id;
        h.Clock.Advance(TimeSpan.FromMinutes(31));

        foreach (var tool in new[] { "get_transfer_details", "get_transfer_price", "select_transfer" })
        {
            var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, tool, $$"""{"offerId":"{{id}}"}"""));
            Assert.Contains("out of date", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Details_show_the_offer_and_the_search_and_leave_the_context_untouched()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var search = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 2));
        var offer = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers")[0];
        var before = await h.ContextAsync(s, ServiceType.Transfer);

        var details = await h.RunAsync(s, "get_transfer_details", $$"""{"offerId":"{{offer.Id}}"}""");

        Assert.Equal("transfer_details", details.State);
        Assert.Equal(offer.Id, DomainHarness.Content(details).GetProperty("offer").GetProperty("id").GetString());
        Assert.DoesNotContain("supplier", details.ModelContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before!.Version, (await h.ContextAsync(s, ServiceType.Transfer))!.Version);
    }

    [Fact]
    public async Task Asking_for_the_price_is_read_only_and_reports_the_live_quote()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var search = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 2));
        var offer = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers")[0];
        var before = (await h.ContextAsync(s, ServiceType.Transfer))!;

        var priced = await h.RunAsync(s, "get_transfer_price", $$"""{"offerId":"{{offer.Id}}"}""");

        var after = (await h.ContextAsync(s, ServiceType.Transfer))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.ConfirmedPrice);
        Assert.Null(after.SelectedItemId);
        Assert.Equal("transfer_price", priced.State);
        var quote = await h.Get<ITransferBackend>().GetQuoteAsync(offer, TripScenarios.TransferParams(Scenarios.DateAfterStart(10), passengers: 2), CancellationToken.None);
        Assert.Equal(quote.Total.Amount, DomainHarness.Content(priced).GetProperty("total").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Price_and_select_reject_an_unknown_offer_id()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(10), passengers: 2));

        foreach (var tool in new[] { "get_transfer_price", "select_transfer" })
        {
            await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(s, tool, """{"offerId":"tr-unknown"}"""));
        }
    }
}

public class SelectTransferToolTests
{
    [Fact]
    public async Task Selecting_records_the_backend_quote_total_as_the_confirmed_price()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var date = Scenarios.DateAfterStart(12);
        var search = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(date, passengers: 2));
        var offer = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers")[0];
        var expected = await h.Get<ITransferBackend>().GetQuoteAsync(offer, TripScenarios.TransferParams(date, passengers: 2), CancellationToken.None);

        var selected = await h.RunAsync(s, "select_transfer", $$"""{"offerId":"{{offer.Id}}"}""");

        var context = (await h.ContextAsync(s, ServiceType.Transfer))!;
        Assert.Equal(expected.Total, context.ConfirmedPrice!.Total);
        Assert.StartsWith("tq-", context.ConfirmedPrice.QuoteId, StringComparison.Ordinal);
        Assert.Equal(h.Clock.GetUtcNow(), context.ConfirmedPrice.ConfirmedAt);
        Assert.Equal(offer.Id, context.SelectedItemId);
        Assert.Equal(offer.VehicleType, context.SelectedOptionId);
        Assert.Equal("transfer_selected", selected.State);
    }

    [Fact]
    public async Task A_price_supplied_by_the_model_is_rejected_by_the_schema_and_never_stored()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var tool = h.Tool("select_transfer");

        Assert.Null(h.Validator.Validate(tool, AgentJson.Parse("""{"offerId":"tr-1"}""")));
        foreach (var forbidden in new[] { """{"offerId":"tr-1","price":1}""", """{"offerId":"tr-1","total":{"amount":1,"currency":"EUR"}}""", """{"price":1}""", "{}" })
        {
            Assert.NotNull(h.Validator.Validate(tool, AgentJson.Parse(forbidden)));
        }

        Assert.Null(await h.ContextAsync(s, ServiceType.Transfer));
    }

    [Fact]
    public async Task A_drifting_quote_is_disclosed_with_the_previous_and_the_new_total()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var backend = h.Get<ITransferBackend>();

        // Find a deterministic search whose first offer reprices.
        for (var day = 5; day < 60; day++)
        {
            var date = Scenarios.DateAfterStart(day);
            var parameters = TripScenarios.TransferParams(date, passengers: 2);
            var offer = (await backend.SearchAsync(parameters, CancellationToken.None)).OrderBy(o => o.Price.Amount).First();
            var quote = await backend.GetQuoteAsync(offer, parameters, CancellationToken.None);
            if (!quote.PriceChanged)
            {
                continue;
            }

            await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(date, passengers: 2));
            var selected = await h.RunAsync(s, "select_transfer", $$"""{"offerId":"{{offer.Id}}"}""");

            Assert.NotNull(selected.ModelNote);
            Assert.Contains(quote.PreviousTotal!.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), selected.ModelNote, StringComparison.Ordinal);
            Assert.Contains(quote.Total.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), selected.ModelNote, StringComparison.Ordinal);
            Assert.Equal(quote.Total, (await h.ContextAsync(s, ServiceType.Transfer))!.ConfirmedPrice!.Total);
            return;
        }

        throw new InvalidOperationException("No drifting transfer quote found in 55 deterministic searches.");
    }

    [Fact]
    public async Task A_stable_quote_has_no_note()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var backend = h.Get<ITransferBackend>();

        for (var day = 5; day < 60; day++)
        {
            var date = Scenarios.DateAfterStart(day);
            var parameters = TripScenarios.TransferParams(date, passengers: 2);
            var offer = (await backend.SearchAsync(parameters, CancellationToken.None)).OrderBy(o => o.Price.Amount).First();
            if ((await backend.GetQuoteAsync(offer, parameters, CancellationToken.None)).PriceChanged)
            {
                continue;
            }

            await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(date, passengers: 2));
            var selected = await h.RunAsync(s, "select_transfer", $$"""{"offerId":"{{offer.Id}}"}""");

            Assert.Null(selected.ModelNote);
            Assert.Equal(offer.Price, (await h.ContextAsync(s, ServiceType.Transfer))!.ConfirmedPrice!.Total);
            return;
        }

        throw new InvalidOperationException("No stable transfer quote found in 55 deterministic searches.");
    }

    [Fact]
    public async Task Selecting_a_different_offer_replaces_the_earlier_selection()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();
        var search = await h.RunAsync(s, "search_transfers", TripScenarios.TransferSearchJson(Scenarios.DateAfterStart(14), passengers: 2));
        var offers = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers");

        await h.RunAsync(s, "select_transfer", $$"""{"offerId":"{{offers[0].Id}}"}""");
        await h.RunAsync(s, "select_transfer", $$"""{"offerId":"{{offers[^1].Id}}"}""");

        Assert.Equal(offers[^1].Id, (await h.ContextAsync(s, ServiceType.Transfer))!.SelectedItemId);
    }
}

internal sealed class EmptyTransferBackend : ITransferBackend
{
    public Task<IReadOnlyList<TransferOffer>> SearchAsync(TransferSearchParams search, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TransferOffer>>([]);

    public Task<TransferQuote> GetQuoteAsync(TransferOffer offer, TransferSearchParams search, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No offers exist, so nothing can be quoted.");
}
