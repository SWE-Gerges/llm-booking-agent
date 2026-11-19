using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Domains.Shared;
using Agent.Persistence;
using Agent.Tests.Support;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.SharedTests;

public class SubmitToolSchemaTests
{
    [Theory]
    [InlineData("submit_flight_booking")]
    [InlineData("submit_hotel_booking")]
    public async Task Submit_tools_accept_only_an_empty_object(string toolName)
    {
        using var h = await DomainHarness.CreateAsync();
        var tool = h.Tool(toolName);

        Assert.Null(h.Validator.Validate(tool, AgentJson.Parse("{}")));
        foreach (var forbidden in new[]
                 {
                     """{"price":1}""",
                     """{"amount":10,"currency":"EUR"}""",
                     """{"total":{"amount":1,"currency":"EUR"}}""",
                     """{"passenger":"x"}""",
                     """{"passengers":[{"firstName":"A"}]}""",
                     """{"guest":{"firstName":"A"}}""",
                     """{"bookingId":"BK-1"}""",
                     """{"idempotencyKey":"k"}""",
                     """{"paymentMethod":"card"}""",
                 })
        {
            var problem = h.Validator.Validate(tool, AgentJson.Parse(forbidden));
            Assert.NotNull(problem);
            Assert.Contains(toolName, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_the_submit_tools_are_finalizing_and_only_the_search_tools_are_searches()
    {
        using var h = await DomainHarness.CreateAsync();

        var finalizing = h.Registry.Tools.OfType<IFinalizingTool>().Select(t => ((IChatTool)t).Name).Order().ToList();
        var searches = h.Registry.Tools.OfType<ISearchTool>().Select(t => ((IChatTool)t).Name).Order().ToList();

        Assert.Equal(["submit_flight_booking", "submit_hotel_booking", "submit_trip_booking"], finalizing);
        Assert.Equal(["search_flights", "search_hotels"], searches);
        Assert.Equal(ServiceType.Flight, ((IFinalizingTool)h.Tool("submit_flight_booking")).Service);
        Assert.Equal(ServiceType.Hotel, ((IFinalizingTool)h.Tool("submit_hotel_booking")).Service);
        Assert.Equal(ServiceType.Trip, ((IFinalizingTool)h.Tool("submit_trip_booking")).Service);
    }

    [Fact]
    public async Task Flight_hotel_shared_and_trip_tools_are_registered_in_stable_order()
    {
        using var h = await DomainHarness.CreateAsync();

        var names = h.Registry.Tools.Select(t => t.Name).ToList();

        Assert.Equal(
            [
                "add_to_trip", "collect_guest_details", "collect_passenger_details", "collect_trip_details", "escalate_to_human",
                "filter_flight_offers", "filter_hotels", "get_booking_status", "get_branded_fares", "get_flight_details",
                "get_hotel_rooms", "get_payment_methods", "prebook_room", "recheck_flight_price", "remove_from_trip",
                "search_flights", "search_hotels", "submit_flight_booking", "submit_hotel_booking", "submit_trip_booking",
                "view_trip",
            ],
            names);
    }

    [Fact]
    public async Task Submit_tools_book_from_server_state_and_show_only_masked_identity()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, total: 321.00m);

        var first = await h.RunAsync(s, "submit_flight_booking");
        var second = await h.RunAsync(s, "submit_flight_booking");

        Assert.Equal("booking_created", first.State);
        var content = DomainHarness.Content(first);
        Assert.Equal(321.00m, content.GetProperty("total").GetProperty("amount").GetDecimal());
        Assert.False(content.GetProperty("replayed").GetBoolean());
        Assert.True(DomainHarness.Content(second).GetProperty("replayed").GetBoolean());
        Assert.Equal(content.GetProperty("bookingId").GetString(), DomainHarness.Content(second).GetProperty("bookingId").GetString());
        Assert.Equal("J*** D***", content.GetProperty("leadPassenger").GetString());
        Assert.DoesNotContain("Jane", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("leadPassenger", first.ModelContent!, StringComparison.Ordinal);
        Assert.DoesNotContain("paymentUrl", first.ModelContent!, StringComparison.Ordinal);
    }
}

public class BookingStatusTests
{
    private static async Task<string> BookAsync(DomainHarness h, SessionContext s, ServiceType service = ServiceType.Flight)
    {
        await BookingSeed.SeedAsync(h, s, service);
        return (await h.Get<BookingSubmitter>().SubmitAsync(s.SessionId, service, CancellationToken.None)).Confirmation.BookingId;
    }

    [Fact]
    public async Task A_session_only_sees_its_own_bookings()
    {
        using var h = await DomainHarness.CreateAsync();
        var owner = await h.NewSessionAsync();
        var other = await h.NewSessionAsync();
        var id = await BookAsync(h, owner);
        var statuses = h.Get<BookingStatusService>();

        var ownerView = await statuses.GetAsync(owner.SessionId, id, CancellationToken.None);
        var otherView = await statuses.GetAsync(other.SessionId, id, CancellationToken.None);
        var otherAll = await statuses.GetAsync(other.SessionId, null, CancellationToken.None);

        Assert.Equal(id, Assert.Single(ownerView).BookingId);
        Assert.Empty(otherView);
        Assert.Empty(otherAll);
    }

    [Fact]
    public async Task Listing_without_an_id_returns_flight_and_hotel_bookings()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var flight = await BookAsync(h, s, ServiceType.Flight);
        var hotel = await BookAsync(h, s, ServiceType.Hotel);

        var all = await h.Get<BookingStatusService>().GetAsync(s.SessionId, null, CancellationToken.None);

        Assert.Equal(new[] { flight, hotel }.Order(StringComparer.Ordinal), all.Select(b => b.BookingId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Earlier_bookings_stay_visible_after_a_new_search_archives_them()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var first = await BookAsync(h, s);
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));
        var second = await BookAsync(h, s);
        var other = await h.NewSessionAsync();

        var statuses = h.Get<BookingStatusService>();
        var all = await statuses.GetAsync(s.SessionId, null, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), all.Select(b => b.BookingId).Order(StringComparer.Ordinal));
        Assert.Single(await statuses.GetAsync(s.SessionId, first, CancellationToken.None));
        Assert.Empty(await statuses.GetAsync(other.SessionId, first, CancellationToken.None));
    }

    [Fact]
    public async Task An_owned_id_the_backend_does_not_know_is_skipped()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { BookingId = "BK-GHOST" }, CancellationToken.None);

        Assert.Empty(await h.Get<BookingStatusService>().GetAsync(s.SessionId, null, CancellationToken.None));
    }

    [Fact]
    public async Task The_status_tool_reports_progress_over_time()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var id = await BookAsync(h, s);

        var pending = await h.RunAsync(s, "get_booking_status", $$"""{"bookingId":"{{id}}"}""");
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        var confirmed = await h.RunAsync(s, "get_booking_status");

        Assert.Equal("booking_status", pending.State);
        Assert.Equal("PendingPayment", DomainHarness.Content(pending).GetProperty("bookings")[0].GetProperty("status").GetString());
        Assert.Equal("Confirmed", DomainHarness.Content(confirmed).GetProperty("bookings")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_status_tool_refuses_bookings_of_other_sessions_and_empty_sessions()
    {
        using var h = await DomainHarness.CreateAsync();
        var owner = await h.NewSessionAsync();
        var stranger = await h.NewSessionAsync();
        var id = await BookAsync(h, owner);

        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(stranger, "get_booking_status", $$"""{"bookingId":"{{id}}"}"""));
        await Assert.ThrowsAsync<ToolExecutionException>(() => h.RunAsync(stranger, "get_booking_status"));
    }
}

public class EscalateAndPaymentToolTests
{
    [Fact]
    public async Task Escalation_writes_the_request_and_an_audit_row_and_hands_the_session_over()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await h.RunAsync(s, "escalate_to_human", """{"reason":"Customer asked for a person","summary":"Wants to change the dates"}""");

        Assert.Equal("escalated", result.State);
        Assert.Equal(SessionState.Escalated, s.State);
        var id = DomainHarness.Content(result).GetProperty("escalationId").GetString();
        Assert.Contains("human agent", result.ModelContent!, StringComparison.Ordinal);
        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        var request = await db.Escalations.AsNoTracking().SingleAsync();
        Assert.Equal(id, request.Id);
        Assert.Equal(s.SessionId, request.SessionToken);
        Assert.Equal("Customer asked for a person", request.Reason);
        Assert.Equal("Wants to change the dates", request.Summary);
        Assert.Equal("open", request.Status);
        Assert.Equal(h.Clock.GetUtcNow(), request.CreatedAt);
        var audit = Assert.Single(await BookingSeed.AuditRowsAsync(h, "escalation.created"));
        Assert.Equal("assistant", audit.Actor);
        Assert.Equal(s.SessionId, audit.SessionToken);
        Assert.Contains(id!, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_is_optional()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await h.RunAsync(s, "escalate_to_human", """{"reason":"Cannot help"}""");

        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.Null((await db.Escalations.AsNoTracking().SingleAsync()).Summary);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"reason":"x"}""")]
    [InlineData("""{"reason":"fine reason","priority":"high"}""")]
    public async Task Escalation_schema_rejects_malformed_input(string json)
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("escalate_to_human"), AgentJson.Parse(json)));
    }

    [Fact]
    public async Task An_escalation_that_cannot_be_stored_leaves_the_session_active()
    {
        var failing = Substitute.For<IEscalationStore>();
        failing.CreateAsync(Arg.Any<EscalationRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("db down")));
        using var h = await DomainHarness.CreateAsync(configure: services => services.AddSingleton(failing));
        var s = await h.NewSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.RunAsync(s, "escalate_to_human", """{"reason":"help me"}"""));

        Assert.Equal(SessionState.Active, s.State);
    }

    [Fact]
    public async Task Payment_methods_follow_the_requested_service()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var hotel = await h.RunAsync(s, "get_payment_methods", """{"service":"hotel"}""");
        var flight = await h.RunAsync(s, "get_payment_methods", """{"service":"flight"}""");

        Assert.Equal("payment_methods", hotel.State);
        var hotelIds = DomainHarness.ReadList<PaymentMethod>(DomainHarness.Content(hotel), "methods").Select(m => m.Id).ToList();
        var flightIds = DomainHarness.ReadList<PaymentMethod>(DomainHarness.Content(flight), "methods").Select(m => m.Id).ToList();
        Assert.Contains("pay_at_property", hotelIds);
        Assert.DoesNotContain("pay_at_property", flightIds);
        Assert.Equal("EUR", DomainHarness.Content(flight).GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Without_a_service_the_most_recently_requested_one_is_used()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        s.AddRequestedService(ServiceType.Flight);
        s.AddRequestedService(ServiceType.Hotel);

        var result = await h.RunAsync(s, "get_payment_methods");

        Assert.Equal("hotel", DomainHarness.Content(result).GetProperty("service").GetString());
    }

    [Fact]
    public async Task Payment_schema_rejects_an_unknown_service()
    {
        using var h = await DomainHarness.CreateAsync();

        Assert.NotNull(h.Validator.Validate(h.Tool("get_payment_methods"), AgentJson.Parse("""{"service":"car"}""")));
    }
}

public class FinalizeRequiresConfirmedPricePolicyTests
{
    private static FinalizeRequiresConfirmedPricePolicy Policy(DomainHarness h) =>
        h.Services.GetServices<IToolCallPolicy>().OfType<FinalizeRequiresConfirmedPricePolicy>().Single();

    private static ValueTask<PolicyDecision> EvaluateAsync(DomainHarness h, SessionContext s, string toolName) =>
        Policy(h).EvaluateAsync(
            new PolicyContext(s, new TurnState(), new ToolUseBlock("t1", toolName, AgentJson.EmptyObject), h.Tool(toolName), h.Clock.GetUtcNow()),
            CancellationToken.None);

    [Fact]
    public async Task Submitting_without_any_confirmed_price_is_denied_with_a_reason_for_the_model()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var decision = await EvaluateAsync(h, s, "submit_flight_booking");

        Assert.False(decision.IsAllowed);
        Assert.Contains("re-checked", decision.MessageForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_without_confirming_the_price_is_still_denied()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, priced: false);

        Assert.False((await EvaluateAsync(h, s, "submit_flight_booking")).IsAllowed);
    }

    [Fact]
    public async Task A_fresh_confirmed_price_allows_the_submit()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight);
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        var decision = await EvaluateAsync(h, s, "submit_flight_booking");

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.MessageForModel);
    }

    [Fact]
    public async Task The_price_expires_after_the_configured_age()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight);

        h.Clock.Advance(TimeSpan.FromMinutes(15));
        var atLimit = await EvaluateAsync(h, s, "submit_flight_booking");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var stale = await EvaluateAsync(h, s, "submit_flight_booking");

        Assert.True(atLimit.IsAllowed);
        Assert.False(stale.IsAllowed);
        Assert.Contains("out of date", stale.MessageForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_maximum_age_is_configurable()
    {
        using var h = await DomainHarness.CreateAsync(new Dictionary<string, string?> { ["Agent:PriceConfirmationMaxAgeMinutes"] = "5" });
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel);

        h.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False((await EvaluateAsync(h, s, "submit_hotel_booking")).IsAllowed);
    }

    [Fact]
    public async Task A_confirmed_price_unlocks_only_its_own_service()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight);

        Assert.True((await EvaluateAsync(h, s, "submit_flight_booking")).IsAllowed);
        Assert.False((await EvaluateAsync(h, s, "submit_hotel_booking")).IsAllowed);

        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel);
        Assert.True((await EvaluateAsync(h, s, "submit_hotel_booking")).IsAllowed);
    }

    [Fact]
    public async Task Confirmations_do_not_leak_between_sessions()
    {
        using var h = await DomainHarness.CreateAsync();
        var confirmed = await h.NewSessionAsync();
        var other = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, confirmed, ServiceType.Flight);

        Assert.False((await EvaluateAsync(h, other, "submit_flight_booking")).IsAllowed);
    }

    [Theory]
    [InlineData("search_flights")]
    [InlineData("get_booking_status")]
    [InlineData("escalate_to_human")]
    public async Task Tools_that_do_not_finalize_are_not_gated(string toolName)
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        Assert.True((await EvaluateAsync(h, s, toolName)).IsAllowed);
    }
}
