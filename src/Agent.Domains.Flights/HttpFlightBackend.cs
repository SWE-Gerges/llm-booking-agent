using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Abstractions.Booking;

namespace Agent.Domains.Flights;

public enum FlightBackendProvider
{
    InMemory,
    Http,
}

public sealed class FlightBackendOptions
{
    public const string SectionName = "Backends:Flights";

    public FlightBackendProvider Provider { get; set; } = FlightBackendProvider.InMemory;

    /// <summary>Base address of the supplier API. Required when Provider is Http.</summary>
    [Url]
    public string BaseUrl { get; set; } = "https://flights.example.com/";

    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Sample anti-corruption layer for a REST flight supplier. The supplier's wrapper envelope, snake_case DTOs and
/// string prices stay inside this class; the rest of the system only sees the domain types. Reads (GET) are retried by
/// the HTTP pipeline; the price re-check is a POST and is never retried.
/// </summary>
public sealed class HttpFlightBackend(HttpClient http, TimeProvider clock) : IFlightBackend
{
    public async Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchParams search, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            $"from={Uri.EscapeDataString(search.Origin)}",
            $"to={Uri.EscapeDataString(search.Destination)}",
            $"depart={search.DepartureDate:yyyy-MM-dd}",
            $"adults={search.Adults}",
            $"cabin={Uri.EscapeDataString(search.Cabin)}",
        };
        if (search.ReturnDate is { } returnDate)
        {
            query.Add($"return={returnDate:yyyy-MM-dd}");
        }

        using var response = await http.GetAsync($"v1/flights/search?{string.Join('&', query)}", cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);

        // Suppliers commonly return a single hit as an object instead of a one-element array.
        var itineraries = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("itineraries", out var list) ? list : payload;
        var items = itineraries.ValueKind == JsonValueKind.Array ? itineraries.EnumerateArray().ToList() : [itineraries];
        return items.Select(item => Map(item.Deserialize<SupplierItinerary>(SupplierJson)!, search)).ToList();
    }

    public async Task<IReadOnlyList<BrandedFare>> GetBrandedFaresAsync(FlightOffer offer, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"v1/flights/{Uri.EscapeDataString(offer.Id)}/fares", cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);
        var faresElement = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("fares", out var list) ? list : payload;
        var fares = faresElement.ValueKind == JsonValueKind.Array ? faresElement.Deserialize<List<SupplierFare>>(SupplierJson)! : [];
        return fares.Select(f => new BrandedFare(
            f.Brand,
            new Money(ParseAmount(f.Total), f.Currency),
            new BaggageAllowance(f.CabinBags, f.CheckedBags),
            f.Refundable,
            f.Changeable)).ToList();
    }

    public async Task<FlightQuote> RecheckPriceAsync(FlightOffer offer, string? fareBrand, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "v1/flights/price-check",
            new { offer_ref = offer.Id, fare_brand = fareBrand ?? offer.FareBrand },
            SupplierJson,
            cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);
        var quote = payload.Deserialize<SupplierQuote>(SupplierJson)!;

        var total = new Money(ParseAmount(quote.Total), quote.Currency);
        var changed = total.Amount != offer.Price.Amount && quote.FareBrand == offer.FareBrand;
        return new FlightQuote(
            quote.QuoteRef,
            offer.Id,
            quote.FareBrand,
            total,
            changed ? offer.Price : null,
            changed,
            quote.ExpiresAt ?? clock.GetUtcNow().AddMinutes(10));
    }

    private static async Task<JsonElement> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new SupplierException("invalid_response", $"The supplier returned a non-JSON body with status {(int)response.StatusCode}: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var isObject = root.ValueKind == JsonValueKind.Object;
            var success = response.IsSuccessStatusCode && (!isObject || !root.TryGetProperty("success", out var flag) || flag.ValueKind != JsonValueKind.False);
            if (!success)
            {
                var error = isObject && root.TryGetProperty("error", out var e) ? e : default;
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new SupplierException(code ?? $"http_{(int)response.StatusCode}", message ?? "The supplier reported a failure.");
            }

            return isObject && root.TryGetProperty("payload", out var payload) ? payload.Clone() : root.Clone();
        }
    }

    private static FlightOffer Map(SupplierItinerary item, FlightSearchParams search)
    {
        FlightSegment ToSegment(SupplierLeg leg) =>
            new(leg.From, leg.To, leg.Dep, leg.Arr, item.Carrier, leg.Flight, leg.DurationMin);

        return new FlightOffer(
            item.Ref,
            item.SupplierCode,
            item.Legs.Where(l => l.Direction != "inbound").Select(ToSegment).ToList(),
            item.Legs.Where(l => l.Direction == "inbound").Select(ToSegment).ToList(),
            new Money(ParseAmount(item.Fare.Total), item.Fare.Currency),
            item.Fare.Brand,
            new BaggageAllowance(item.Baggage.Cabin, item.Baggage.Checked),
            item.Fare.Refundable,
            item.NeedsPassport);
    }

    private static decimal ParseAmount(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions SupplierJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class SupplierItinerary
    {
        public string Ref { get; set; } = string.Empty;

        public string Carrier { get; set; } = string.Empty;

        public string SupplierCode { get; set; } = "supplier-a";

        public bool NeedsPassport { get; set; }

        public List<SupplierLeg> Legs { get; set; } = [];

        public SupplierFare Fare { get; set; } = new();

        public SupplierBaggage Baggage { get; set; } = new();
    }

    private sealed class SupplierLeg
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;

        public DateTimeOffset Dep { get; set; }

        public DateTimeOffset Arr { get; set; }

        public string Flight { get; set; } = string.Empty;

        public int DurationMin { get; set; }

        public string Direction { get; set; } = "outbound";
    }

    private sealed class SupplierFare
    {
        public string Brand { get; set; } = "basic";

        public string Total { get; set; } = "0";

        public string Currency { get; set; } = "EUR";

        public bool Refundable { get; set; }

        public bool Changeable { get; set; }

        public int CabinBags { get; set; }

        public int CheckedBags { get; set; }
    }

    private sealed class SupplierBaggage
    {
        public int Cabin { get; set; }

        public int Checked { get; set; }
    }

    private sealed class SupplierQuote
    {
        public string QuoteRef { get; set; } = string.Empty;

        public string FareBrand { get; set; } = string.Empty;

        public string Total { get; set; } = "0";

        public string Currency { get; set; } = "EUR";

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
