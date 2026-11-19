using System.Text.Json;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Domains.Flights.Forms;
using Agent.Domains.Shared;

namespace Agent.Domains.Flights.Tools;

internal static class FlightView
{
    /// <summary>Compact model-facing view: no supplier identity, no internals.</summary>
    public static object Summary(FlightOffer o) => new
    {
        id = o.Id,
        carrier = o.Outbound[0].Carrier,
        route = $"{o.Outbound[0].Origin}-{o.Outbound[^1].Destination}",
        departAt = o.Outbound[0].DepartAt,
        arriveAt = o.Outbound[^1].ArriveAt,
        stops = o.Stops,
        durationMinutes = o.DurationMinutes,
        returnDepartAt = o.Inbound.Count > 0 ? o.Inbound[0].DepartAt : (DateTimeOffset?)null,
        price = o.Price,
        fareBrand = o.FareBrand,
        refundable = o.Refundable,
        checkedBags = o.Baggage.CheckedPieces,
    };

    public static object Detail(FlightOffer o) => new
    {
        id = o.Id,
        price = o.Price,
        fareBrand = o.FareBrand,
        refundable = o.Refundable,
        baggage = o.Baggage,
        outbound = o.Outbound.Select(Segment),
        inbound = o.Inbound.Select(Segment),
    };

    private static object Segment(FlightSegment s) => new
    {
        from = s.Origin,
        to = s.Destination,
        departAt = s.DepartAt,
        arriveAt = s.ArriveAt,
        flight = s.FlightNumber,
        durationMinutes = s.DurationMinutes,
    };

    public static async Task<(FlightOffer Offer, BookingContext Context, FlightSearchParams Params)> RequireOfferAsync(
        SearchSnapshots snapshots,
        string sessionId,
        string offerId,
        CancellationToken cancellationToken)
    {
        var (context, snapshot) = await snapshots.RequireFreshAsync(sessionId, ServiceType.Flight, handle: null, cancellationToken);
        var offer = SearchSnapshots.ReadResults<FlightOffer>(snapshot).FirstOrDefault(o => o.Id == offerId)
            ?? throw new ToolExecutionException($"Offer '{offerId}' is not part of the latest search results.");
        return (offer, context, SearchSnapshots.ReadParams<FlightSearchParams>(snapshot));
    }
}

public sealed class SearchFlightsTool(IFlightBackend backend, SearchSnapshots snapshots, TimeProvider clock) : ChatToolBase(
    "search_flights",
    "Search flight offers. Dates are yyyy-MM-dd and airports are three-letter codes. If the user did not give the number of travellers, omit adults and say what was assumed.",
    """
    {"type":"object","properties":{
      "origin":{"type":"string","pattern":"^[A-Za-z]{3}$","description":"Departure airport code"},
      "destination":{"type":"string","pattern":"^[A-Za-z]{3}$","description":"Arrival airport code"},
      "departureDate":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},
      "returnDate":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},
      "adults":{"type":"integer","minimum":1,"maximum":9},
      "cabin":{"type":"string","enum":["economy","premium_economy","business","first"]}
    },"required":["origin","destination","departureDate"],"additionalProperties":false}
    """), ISearchTool
{
    private const int MaxResults = 10;

    public ServiceType Service => ServiceType.Flight;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var origin = input.GetString("origin")!.ToUpperInvariant();
        var destination = input.GetString("destination")!.ToUpperInvariant();
        if (origin == destination)
        {
            throw new ToolExecutionException("Origin and destination must be different airports.");
        }

        var departure = input.RequireDate("departureDate");
        var returnDate = input.OptionalDate("returnDate");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (departure < today)
        {
            throw new ToolExecutionException("The departure date is in the past.");
        }

        if (returnDate is { } back && back < departure)
        {
            throw new ToolExecutionException("The return date is before the departure date.");
        }

        var requestedAdults = input.GetInt("adults");
        var search = new FlightSearchParams(origin, destination, departure, returnDate, requestedAdults ?? 1, input.GetString("cabin") ?? "economy");
        var offers = (await backend.SearchAsync(search, cancellationToken)).OrderBy(o => o.Price.Amount).Take(MaxResults).ToList();
        var snapshot = await snapshots.SaveAsync(context.SessionId, ServiceType.Flight, search, offers, search.Adults, cancellationToken);
        context.Session.AddRequestedService(ServiceType.Flight);

        var note = requestedAdults is null
            ? "The user did not say how many travellers, so 1 adult was assumed. Tell the user this and offer to search again with the right number."
            : null;
        return ResultWithView(
            new { handle = snapshot.Handle, search, offers },
            "flight_results",
            new { handle = snapshot.Handle, currency = offers.FirstOrDefault()?.Price.Currency, count = offers.Count, offers = offers.Select(FlightView.Summary) },
            note);
    }
}

public sealed class FilterFlightOffersTool(SearchSnapshots snapshots) : ChatToolBase(
    "filter_flight_offers",
    "Filter and sort the offers of the latest flight search without searching again.",
    """
    {"type":"object","properties":{
      "handle":{"type":"string","description":"Handle returned by the latest search"},
      "maxPrice":{"type":"number","exclusiveMinimum":0},
      "maxStops":{"type":"integer","minimum":0,"maximum":3},
      "carriers":{"type":"array","items":{"type":"string"},"maxItems":10},
      "departAfter":{"type":"string","pattern":"^([01]\\d|2[0-3]):[0-5]\\d$","description":"Earliest outbound departure time, HH:mm UTC"},
      "refundableOnly":{"type":"boolean"},
      "sortBy":{"type":"string","enum":["price","duration","departure"]}
    },"required":["handle"],"additionalProperties":false}
    """)
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (_, snapshot) = await snapshots.RequireFreshAsync(context.SessionId, ServiceType.Flight, input.GetString("handle"), cancellationToken);
        IEnumerable<FlightOffer> offers = SearchSnapshots.ReadResults<FlightOffer>(snapshot);

        if (input.GetDecimal("maxPrice") is { } maxPrice)
        {
            offers = offers.Where(o => o.Price.Amount <= maxPrice);
        }

        if (input.GetInt("maxStops") is { } maxStops)
        {
            offers = offers.Where(o => o.Stops <= maxStops);
        }

        var carriers = input.GetStrings("carriers");
        if (carriers.Count > 0)
        {
            offers = offers.Where(o => carriers.Contains(o.Outbound[0].Carrier, StringComparer.OrdinalIgnoreCase));
        }

        if (input.GetString("departAfter") is { } after && TimeOnly.TryParse(after, out var earliest))
        {
            offers = offers.Where(o => TimeOnly.FromDateTime(o.Outbound[0].DepartAt.UtcDateTime) >= earliest);
        }

        if (input.GetBool("refundableOnly") == true)
        {
            offers = offers.Where(o => o.Refundable);
        }

        offers = input.GetString("sortBy") switch
        {
            "duration" => offers.OrderBy(o => o.DurationMinutes),
            "departure" => offers.OrderBy(o => o.Outbound[0].DepartAt),
            _ => offers.OrderBy(o => o.Price.Amount),
        };

        var result = offers.ToList();
        return ResultWithView(
            new { handle = snapshot.Handle, offers = result },
            "flight_results",
            new { handle = snapshot.Handle, count = result.Count, offers = result.Select(FlightView.Summary) });
    }
}

public sealed class GetFlightDetailsTool(SearchSnapshots snapshots) : ChatToolBase(
    "get_flight_details",
    "Show the full itinerary (segments, times, baggage) of one offer from the latest search.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, _, _) = await FlightView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        return ResultWithView(offer, "flight_details", FlightView.Detail(offer));
    }
}

public sealed class GetBrandedFaresTool(IFlightBackend backend, SearchSnapshots snapshots) : ChatToolBase(
    "get_branded_fares",
    "List the fare brands (basic, standard, flex) with prices and baggage for one offer.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, _, _) = await FlightView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        var fares = await backend.GetBrandedFaresAsync(offer, cancellationToken);
        return Result(new { offerId = offer.Id, fares }, "flight_fares");
    }
}

public sealed class RecheckFlightPriceTool(IFlightBackend backend, SearchSnapshots snapshots, IBookingContextStore contexts, TimeProvider clock) : ChatToolBase(
    "recheck_flight_price",
    "Re-check the live price of the chosen offer (and fare brand) before booking. This selects the offer for booking.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64},"fareBrand":{"type":"string","enum":["basic","standard","flex"]}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, _, _) = await FlightView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        var quote = await backend.RecheckPriceAsync(offer, input.GetString("fareBrand"), cancellationToken);

        var confirmation = new PriceConfirmation(quote.QuoteId, quote.Total, clock.GetUtcNow());
        await contexts.MutateAsync(
            context.SessionId,
            ServiceType.Flight,
            c => c with
            {
                SelectedItemId = offer.Id,
                SelectedOptionId = quote.FareBrand,
                ConfirmedPrice = confirmation,
                Requirements = offer.RequiresPassport ? ["passport_required"] : [],
            },
            cancellationToken);

        var payload = new
        {
            offerId = offer.Id,
            fareBrand = quote.FareBrand,
            total = quote.Total,
            previousTotal = quote.PreviousTotal,
            priceChanged = quote.PriceChanged,
            expiresAt = quote.ExpiresAt,
        };
        var note = quote.PriceChanged
            ? $"The price changed from {quote.PreviousTotal!.Amount} to {quote.Total.Amount} {quote.Total.Currency}. Tell the user before going on."
            : null;
        return Result(payload, "flight_price", modelNote: note);
    }
}

public sealed class CollectPassengerDetailsTool(SearchSnapshots snapshots) : ChatToolBase(
    "collect_passenger_details",
    "Show the secure passenger details form. Never ask for passenger details in chat; call this instead.",
    """{"type":"object","properties":{},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (booking, snapshot) = await snapshots.RequireFreshAsync(context.SessionId, ServiceType.Flight, handle: null, cancellationToken);
        if (booking.SelectedItemId is null || booking.ConfirmedPrice is null)
        {
            throw new ToolExecutionException("Re-check the price of the chosen flight first, then show the form.");
        }

        var offer = SearchSnapshots.ReadResults<FlightOffer>(snapshot).First(o => o.Id == booking.SelectedItemId);
        var search = SearchSnapshots.ReadParams<FlightSearchParams>(snapshot);
        var schema = PassengerFormSchema.Build(search.Adults, offer.RequiresPassport, offer.Supplier);
        return Result(
            schema,
            "passenger_form",
            modelContent: """{"status":"form_shown","instruction":"The secure passenger form is now displayed to the user. Do not ask for these details in chat. Wait for the user to submit it."}""");
    }
}

public sealed class SubmitFlightBookingTool(BookingSubmitter submitter, IBookingContextStore contexts) : ChatToolBase(
    "submit_flight_booking",
    "Create the flight booking and move it to payment. Takes no arguments: price, flight and passengers come from the server.",
    """{"type":"object","properties":{},"additionalProperties":false}"""), IFinalizingTool
{
    public ServiceType Service => ServiceType.Flight;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var submitted = await submitter.SubmitAsync(context.SessionId, ServiceType.Flight, cancellationToken);
        var booking = await contexts.GetAsync(context.SessionId, ServiceType.Flight, cancellationToken);
        var masked = booking?.Form?.Masked;
        var lead = masked is null ? null : $"{masked.GetValueOrDefault("passengers[0].firstName")} {masked.GetValueOrDefault("passengers[0].lastName")}".Trim();
        var confirmation = submitted.Confirmation;

        var payload = new
        {
            bookingId = confirmation.BookingId,
            status = confirmation.Status.ToString(),
            total = confirmation.Total,
            paymentUrl = confirmation.PaymentUrl,
            replayed = submitted.Replayed,
            leadPassenger = lead,
        };
        return ResultWithView(
            payload,
            "booking_created",
            new { bookingId = confirmation.BookingId, status = confirmation.Status.ToString(), total = confirmation.Total, replayed = submitted.Replayed, nextStep = "The user completes payment on the secure payment page." });
    }
}
