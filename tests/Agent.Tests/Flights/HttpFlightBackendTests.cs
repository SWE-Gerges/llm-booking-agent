using System.Globalization;
using System.Net;
using System.Text.Json;
using Agent.Abstractions.Booking;
using Agent.Domains.Flights;
using Agent.Tests.LlmClients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.FlightTests;

/// <summary>The real <c>AddFlightDomain</c> registration with Provider=Http (typed client + retry pipeline) over a fake transport.</summary>
internal sealed class FlightSupplierHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public FlightSupplierHost(FakeHttpHandler handler, string apiKey = "")
    {
        Handler = handler;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backends:Flights:Provider"] = "Http",
                ["Backends:Flights:BaseUrl"] = "https://flights.example.com/",
                ["Backends:Flights:ApiKey"] = apiKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlightDomain(configuration);
        services.AddHttpClient(nameof(IFlightBackend)).ConfigurePrimaryHttpMessageHandler(() => handler);
        _provider = services.BuildServiceProvider();
        Backend = _provider.GetRequiredService<IFlightBackend>();
    }

    public FakeHttpHandler Handler { get; }

    public IFlightBackend Backend { get; }

    public void Dispose() => _provider.Dispose();
}

public class HttpFlightBackendTests
{
    private static readonly FlightSearchParams Search = new("AMS", "LIS", new DateOnly(2026, 4, 1), null, 1, "economy");

    private const string Itinerary = """
        {"ref":"S-100","carrier":"XA","supplier_code":"supplier-b","needs_passport":true,
         "legs":[
           {"from":"AMS","to":"HBA","dep":"2026-04-01T08:00:00+00:00","arr":"2026-04-01T09:30:00+00:00","flight":"XA101","duration_min":90,"direction":"outbound"},
           {"from":"HBA","to":"LIS","dep":"2026-04-01T10:30:00+00:00","arr":"2026-04-01T13:00:00+00:00","flight":"XA102","duration_min":150},
           {"from":"LIS","to":"AMS","dep":"2026-04-08T09:00:00+00:00","arr":"2026-04-08T12:30:00+00:00","flight":"XA201","duration_min":210,"direction":"inbound"}],
         "fare":{"brand":"standard","total":"1234.50","currency":"EUR","refundable":false},
         "baggage":{"cabin":1,"checked":1}}
        """;

    private static string Wrapped(string payload) => $$"""{"success":true,"payload":{{payload}}}""";

    private static FlightOffer Offer(decimal amount = 300m, string brand = "standard") => new(
        "S-100",
        "supplier-a",
        [new FlightSegment("AMS", "LIS", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(3), "XA", "XA1", 180)],
        [],
        new Money(amount, "EUR"),
        brand,
        new BaggageAllowance(1, 1),
        false,
        false);

    [Fact]
    public async Task A_search_maps_the_supplier_dto_to_domain_offers()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Wrapped($$"""{"itineraries":[{{Itinerary}}]}"""));
        using var host = new FlightSupplierHost(handler);

        var offer = Assert.Single(await host.Backend.SearchAsync(Search, CancellationToken.None));

        Assert.Equal("S-100", offer.Id);
        Assert.Equal("supplier-b", offer.Supplier);
        Assert.True(offer.RequiresPassport);
        Assert.Equal(["AMS-HBA", "HBA-LIS"], offer.Outbound.Select(s => $"{s.Origin}-{s.Destination}"));
        Assert.Equal(["LIS-AMS"], offer.Inbound.Select(s => $"{s.Origin}-{s.Destination}"));
        Assert.Equal("XA", offer.Outbound[0].Carrier);
        Assert.Equal("XA102", offer.Outbound[1].FlightNumber);
        Assert.Equal(150, offer.Outbound[1].DurationMinutes);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero), offer.Outbound[0].DepartAt);
        Assert.Equal(new Money(1234.50m, "EUR"), offer.Price);
        Assert.Equal("standard", offer.FareBrand);
        Assert.False(offer.Refundable);
        Assert.Equal(new BaggageAllowance(1, 1), offer.Baggage);
        Assert.Equal(1, offer.Stops);
    }

    [Fact]
    public async Task Prices_are_parsed_with_the_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Wrapped($$"""{"itineraries":[{{Itinerary}}]}"""));
            using var host = new FlightSupplierHost(handler);

            var offer = Assert.Single(await host.Backend.SearchAsync(Search, CancellationToken.None));

            Assert.Equal(1234.50m, offer.Price.Amount);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task A_search_sends_a_get_with_the_encoded_query_and_the_api_key()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Wrapped("""{"itineraries":[]}"""));
        using var host = new FlightSupplierHost(handler, apiKey: "supplier-key-123");

        await host.Backend.SearchAsync(Search with { ReturnDate = new DateOnly(2026, 4, 8), Adults = 2, Cabin = "premium_economy" }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "/v1/flights/search?from=AMS&to=LIS&depart=2026-04-01&adults=2&cabin=premium_economy&return=2026-04-08",
            request.Uri.PathAndQuery);
        Assert.Equal("flights.example.com", request.Uri.Host);
        Assert.Equal("supplier-key-123", request.Headers["x-api-key"]);
    }

    [Fact]
    public async Task A_single_object_payload_is_normalised_to_a_list()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, Wrapped(Itinerary))
            .Respond(HttpStatusCode.OK, Wrapped($$"""{"itineraries":{{Itinerary}}}"""));
        using var host = new FlightSupplierHost(handler);

        var bare = await host.Backend.SearchAsync(Search, CancellationToken.None);
        var nested = await host.Backend.SearchAsync(Search, CancellationToken.None);

        Assert.Equal("S-100", Assert.Single(bare).Id);
        Assert.Equal("S-100", Assert.Single(nested).Id);
    }

    [Fact]
    public async Task A_bare_array_payload_is_normalised_to_a_list()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Wrapped($"[{Itinerary},{Itinerary.Replace("S-100", "S-200", StringComparison.Ordinal)}]"));
        using var host = new FlightSupplierHost(handler);

        var offers = await host.Backend.SearchAsync(Search, CancellationToken.None);

        Assert.Equal(["S-100", "S-200"], offers.Select(o => o.Id));
    }

    [Fact]
    public async Task A_get_is_retried_after_a_transient_failure()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, "{}")
            .Respond(HttpStatusCode.OK, Wrapped($$"""{"itineraries":[{{Itinerary}}]}"""));
        using var host = new FlightSupplierHost(handler);

        var offers = await host.Backend.SearchAsync(Search, CancellationToken.None);

        Assert.Single(offers);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_get_is_retried_after_a_connection_error()
    {
        var handler = new FakeHttpHandler()
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, Wrapped($$"""{"itineraries":[{{Itinerary}}]}"""));
        using var host = new FlightSupplierHost(handler);

        Assert.Single(await host.Backend.SearchAsync(Search, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_get_gives_up_after_two_retries()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, "{}")
            .Respond(HttpStatusCode.ServiceUnavailable, "{}")
            .Respond(HttpStatusCode.ServiceUnavailable, "{}");
        using var host = new FlightSupplierHost(handler);

        var error = await Assert.ThrowsAsync<SupplierException>(() => host.Backend.SearchAsync(Search, CancellationToken.None));

        Assert.Equal("http_503", error.Code);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, """{"success":false,"error":{"code":"BAD_REQUEST","message":"unknown airport"}}""");
        using var host = new FlightSupplierHost(handler);

        var error = await Assert.ThrowsAsync<SupplierException>(() => host.Backend.SearchAsync(Search, CancellationToken.None));

        Assert.Equal("BAD_REQUEST", error.Code);
        Assert.Equal("unknown airport", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_price_check_is_a_post_and_is_never_retried()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, """{"success":false,"error":{"code":"UNAVAILABLE","message":"try later"}}""")
            .Respond(HttpStatusCode.OK, Wrapped("""{"quote_ref":"Q-1","fare_brand":"standard","total":"300","currency":"EUR"}"""));
        using var host = new FlightSupplierHost(handler);

        var error = await Assert.ThrowsAsync<SupplierException>(() => host.Backend.RecheckPriceAsync(Offer(), null, CancellationToken.None));

        Assert.Equal("UNAVAILABLE", error.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Fact]
    public async Task The_price_check_maps_the_quote_and_detects_a_changed_total()
    {
        var handler = new FakeHttpHandler().Respond(
            HttpStatusCode.OK,
            Wrapped("""{"quote_ref":"Q-9","fare_brand":"standard","total":"310.50","currency":"EUR","expires_at":"2026-04-01T10:00:00+00:00"}"""));
        using var host = new FlightSupplierHost(handler);

        var quote = await host.Backend.RecheckPriceAsync(Offer(300m), null, CancellationToken.None);

        Assert.Equal("Q-9", quote.QuoteId);
        Assert.Equal("S-100", quote.OfferId);
        Assert.Equal(new Money(310.50m, "EUR"), quote.Total);
        Assert.True(quote.PriceChanged);
        Assert.Equal(new Money(300m, "EUR"), quote.PreviousTotal);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero), quote.ExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("S-100", body.RootElement.GetProperty("offer_ref").GetString());
        Assert.Equal("standard", body.RootElement.GetProperty("fare_brand").GetString());
    }

    [Fact]
    public async Task A_quote_for_another_brand_is_not_reported_as_a_price_change()
    {
        var handler = new FakeHttpHandler().Respond(
            HttpStatusCode.OK,
            Wrapped("""{"quote_ref":"Q-2","fare_brand":"flex","total":"420","currency":"EUR"}"""));
        using var host = new FlightSupplierHost(handler);

        var quote = await host.Backend.RecheckPriceAsync(Offer(300m), "flex", CancellationToken.None);

        Assert.Equal("flex", quote.FareBrand);
        Assert.False(quote.PriceChanged);
        Assert.Null(quote.PreviousTotal);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("flex", body.RootElement.GetProperty("fare_brand").GetString());
    }

    [Fact]
    public async Task A_supplier_error_envelope_becomes_a_supplier_exception_even_with_status_200()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"success":false,"error":{"code":"RATE_LIMITED","message":"slow down"}}""");
        using var host = new FlightSupplierHost(handler);

        var error = await Assert.ThrowsAsync<SupplierException>(() => host.Backend.SearchAsync(Search, CancellationToken.None));

        Assert.Equal("RATE_LIMITED", error.Code);
        Assert.Equal("slow down", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_non_json_body_is_reported_as_an_invalid_response()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "<html>gateway error</html>");
        using var host = new FlightSupplierHost(handler);

        var error = await Assert.ThrowsAsync<SupplierException>(() => host.Backend.SearchAsync(Search, CancellationToken.None));

        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task Branded_fares_are_mapped_from_the_supplier_payload()
    {
        var handler = new FakeHttpHandler().Respond(
            HttpStatusCode.OK,
            Wrapped("""
                {"fares":[
                  {"brand":"basic","total":"99.90","currency":"EUR","refundable":false,"changeable":false,"cabin_bags":1,"checked_bags":0},
                  {"brand":"flex","total":"1,149.90","currency":"EUR","refundable":true,"changeable":true,"cabin_bags":1,"checked_bags":2}]}
                """));
        using var host = new FlightSupplierHost(handler);

        var fares = await host.Backend.GetBrandedFaresAsync(Offer(), CancellationToken.None);

        Assert.Equal(["basic", "flex"], fares.Select(f => f.Brand));
        Assert.Equal(99.90m, fares[0].Price.Amount);
        Assert.Equal(1149.90m, fares[1].Price.Amount);
        Assert.Equal(new BaggageAllowance(1, 2), fares[1].Baggage);
        Assert.True(fares[1].Refundable);
        Assert.True(fares[1].Changeable);
        Assert.Equal("/v1/flights/S-100/fares", handler.Requests[0].Uri.AbsolutePath);
    }
}
