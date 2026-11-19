using System.Text.Json;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Domains.Hotels.Forms;
using Agent.Domains.Shared;

namespace Agent.Domains.Hotels.Tools;

internal static class HotelView
{
    public static object Summary(Hotel h) => new
    {
        id = h.Id,
        name = h.Name,
        stars = h.Stars,
        area = h.Area,
        fromPricePerNight = h.FromPricePerNight,
        amenities = h.Amenities,
        guestRating = h.GuestRating,
    };

    public static async Task<(Hotel Hotel, HotelSearchParams Params, BookingContext Context)> RequireHotelAsync(
        SearchSnapshots snapshots,
        string sessionId,
        string hotelId,
        CancellationToken cancellationToken)
    {
        var (context, snapshot) = await snapshots.RequireFreshAsync(sessionId, ServiceType.Hotel, handle: null, cancellationToken);
        var hotel = SearchSnapshots.ReadResults<Hotel>(snapshot).FirstOrDefault(h => h.Id == hotelId)
            ?? throw new ToolExecutionException($"Hotel '{hotelId}' is not part of the latest search results.");
        return (hotel, SearchSnapshots.ReadParams<HotelSearchParams>(snapshot), context);
    }
}

public sealed class SearchHotelsTool(IHotelBackend backend, SearchSnapshots snapshots, TimeProvider clock) : ChatToolBase(
    "search_hotels",
    "Search hotels for a destination and stay dates (yyyy-MM-dd). If the user did not give the number of guests or rooms, omit them and say what was assumed.",
    """
    {"type":"object","properties":{
      "destination":{"type":"string","minLength":2,"maxLength":60},
      "checkIn":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},
      "checkOut":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},
      "adults":{"type":"integer","minimum":1,"maximum":9},
      "rooms":{"type":"integer","minimum":1,"maximum":5}
    },"required":["destination","checkIn","checkOut"],"additionalProperties":false}
    """), ISearchTool
{
    private const int MaxResults = 10;

    public ServiceType Service => ServiceType.Hotel;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var checkIn = input.RequireDate("checkIn");
        var checkOut = input.RequireDate("checkOut");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (checkIn < today)
        {
            throw new ToolExecutionException("The check-in date is in the past.");
        }

        if (checkOut <= checkIn)
        {
            throw new ToolExecutionException("The check-out date must be after the check-in date.");
        }

        var requestedAdults = input.GetInt("adults");
        var requestedRooms = input.GetInt("rooms");
        var search = new HotelSearchParams(input.GetString("destination")!.Trim(), checkIn, checkOut, requestedAdults ?? 2, requestedRooms ?? 1);

        var hotels = (await backend.SearchAsync(search, cancellationToken)).OrderBy(h => h.FromPricePerNight.Amount).Take(MaxResults).ToList();
        var snapshot = await snapshots.SaveAsync(context.SessionId, ServiceType.Hotel, search, hotels, search.Adults, cancellationToken);
        context.Session.AddRequestedService(ServiceType.Hotel);

        var note = requestedAdults is null || requestedRooms is null
            ? $"The user did not give the full guest details, so {search.Adults} adult(s) in {search.Rooms} room(s) were assumed. Tell the user and offer to search again with the right numbers."
            : null;
        return ResultWithView(
            new { handle = snapshot.Handle, search, nights = search.Nights, hotels },
            "hotel_results",
            new { handle = snapshot.Handle, nights = search.Nights, currency = hotels.FirstOrDefault()?.FromPricePerNight.Currency, count = hotels.Count, hotels = hotels.Select(HotelView.Summary) },
            note);
    }
}

public sealed class FilterHotelsTool(SearchSnapshots snapshots) : ChatToolBase(
    "filter_hotels",
    "Filter and sort the hotels of the latest search without searching again.",
    """
    {"type":"object","properties":{
      "handle":{"type":"string"},
      "maxPricePerNight":{"type":"number","exclusiveMinimum":0},
      "minStars":{"type":"integer","minimum":1,"maximum":5},
      "minRating":{"type":"number","minimum":0,"maximum":10},
      "amenities":{"type":"array","items":{"type":"string"},"maxItems":7},
      "sortBy":{"type":"string","enum":["price","rating","stars"]}
    },"required":["handle"],"additionalProperties":false}
    """)
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (_, snapshot) = await snapshots.RequireFreshAsync(context.SessionId, ServiceType.Hotel, input.GetString("handle"), cancellationToken);
        IEnumerable<Hotel> hotels = SearchSnapshots.ReadResults<Hotel>(snapshot);

        if (input.GetDecimal("maxPricePerNight") is { } maxPrice)
        {
            hotels = hotels.Where(h => h.FromPricePerNight.Amount <= maxPrice);
        }

        if (input.GetInt("minStars") is { } minStars)
        {
            hotels = hotels.Where(h => h.Stars >= minStars);
        }

        if (input.GetDecimal("minRating") is { } minRating)
        {
            hotels = hotels.Where(h => (decimal)h.GuestRating >= minRating);
        }

        var amenities = input.GetStrings("amenities");
        if (amenities.Count > 0)
        {
            hotels = hotels.Where(h => amenities.All(a => h.Amenities.Contains(a, StringComparer.OrdinalIgnoreCase)));
        }

        hotels = input.GetString("sortBy") switch
        {
            "rating" => hotels.OrderByDescending(h => h.GuestRating),
            "stars" => hotels.OrderByDescending(h => h.Stars).ThenBy(h => h.FromPricePerNight.Amount),
            _ => hotels.OrderBy(h => h.FromPricePerNight.Amount),
        };

        var result = hotels.ToList();
        return ResultWithView(
            new { handle = snapshot.Handle, hotels = result },
            "hotel_results",
            new { handle = snapshot.Handle, count = result.Count, hotels = result.Select(HotelView.Summary) });
    }
}

public sealed class GetHotelRoomsTool(IHotelBackend backend, SearchSnapshots snapshots) : ChatToolBase(
    "get_hotel_rooms",
    "List room types and rates (board, price, cancellation policy) of one hotel from the latest search.",
    """{"type":"object","properties":{"hotelId":{"type":"string","maxLength":64}},"required":["hotelId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (hotel, search, _) = await HotelView.RequireHotelAsync(snapshots, context.SessionId, input.GetString("hotelId")!, cancellationToken);
        var rooms = await backend.GetRoomsAsync(hotel, search, cancellationToken);
        return Result(new { hotel = HotelView.Summary(hotel), nights = search.Nights, rooms }, "hotel_rooms");
    }
}

public sealed class PrebookRoomTool(IHotelBackend backend, SearchSnapshots snapshots, IBookingContextStore contexts, TimeProvider clock) : ChatToolBase(
    "prebook_room",
    "Pre-book a room rate with the supplier to lock the live price before booking. This selects the room for booking.",
    """{"type":"object","properties":{"hotelId":{"type":"string","maxLength":64},"roomId":{"type":"string","maxLength":32},"rateId":{"type":"string","maxLength":32}},"required":["hotelId","roomId","rateId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (hotel, search, _) = await HotelView.RequireHotelAsync(snapshots, context.SessionId, input.GetString("hotelId")!, cancellationToken);
        var rooms = await backend.GetRoomsAsync(hotel, search, cancellationToken);
        var room = rooms.FirstOrDefault(r => r.RoomId == input.GetString("roomId"))
            ?? throw new ToolExecutionException("That room is not offered by this hotel. Use get_hotel_rooms to see valid room ids.");
        var rate = room.Rates.FirstOrDefault(r => r.RateId == input.GetString("rateId"))
            ?? throw new ToolExecutionException("That rate is not offered for this room. Use get_hotel_rooms to see valid rate ids.");

        var prebook = await backend.PrebookAsync(hotel, room, rate, search, cancellationToken);
        var confirmation = new PriceConfirmation(prebook.QuoteId, prebook.Total, clock.GetUtcNow());
        await contexts.MutateAsync(
            context.SessionId,
            ServiceType.Hotel,
            c => c with { SelectedItemId = hotel.Id, SelectedOptionId = $"{room.RoomId}:{rate.RateId}", ConfirmedPrice = confirmation },
            cancellationToken);

        var payload = new
        {
            hotelId = hotel.Id,
            roomId = room.RoomId,
            rateId = rate.RateId,
            total = prebook.Total,
            previousTotal = prebook.PreviousTotal,
            priceChanged = prebook.PriceChanged,
            expiresAt = prebook.ExpiresAt,
            cancellation = rate.Cancellation,
        };
        var note = prebook.PriceChanged
            ? $"The price changed from {prebook.PreviousTotal!.Amount} to {prebook.Total.Amount} {prebook.Total.Currency}. Tell the user before going on."
            : null;
        return Result(payload, "hotel_price", modelNote: note);
    }
}

public sealed class CollectGuestDetailsTool(SearchSnapshots snapshots) : ChatToolBase(
    "collect_guest_details",
    "Show the secure guest details form. Never ask for guest details in chat; call this instead.",
    """{"type":"object","properties":{},"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (booking, _) = await snapshots.RequireFreshAsync(context.SessionId, ServiceType.Hotel, handle: null, cancellationToken);
        if (booking.SelectedItemId is null || booking.ConfirmedPrice is null)
        {
            throw new ToolExecutionException("Pre-book a room first, then show the form.");
        }

        return Result(
            GuestFormSchema.Build(),
            "guest_form",
            modelContent: """{"status":"form_shown","instruction":"The secure guest form is now displayed to the user. Do not ask for these details in chat. Wait for the user to submit it."}""");
    }
}

public sealed class SubmitHotelBookingTool(BookingSubmitter submitter, IBookingContextStore contexts) : ChatToolBase(
    "submit_hotel_booking",
    "Create the hotel booking and move it to payment. Takes no arguments: price, room and guest come from the server.",
    """{"type":"object","properties":{},"additionalProperties":false}"""), IFinalizingTool
{
    public ServiceType Service => ServiceType.Hotel;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var submitted = await submitter.SubmitAsync(context.SessionId, ServiceType.Hotel, cancellationToken);
        var masked = (await contexts.GetAsync(context.SessionId, ServiceType.Hotel, cancellationToken))?.Form?.Masked;
        var lead = masked is null
            ? null
            : $"{masked.GetValueOrDefault("leadGuest.firstName")} {masked.GetValueOrDefault("leadGuest.lastName")}".Trim();
        var confirmation = submitted.Confirmation;

        var payload = new
        {
            bookingId = confirmation.BookingId,
            status = confirmation.Status.ToString(),
            total = confirmation.Total,
            paymentUrl = confirmation.PaymentUrl,
            replayed = submitted.Replayed,
            leadGuest = lead,
        };
        return ResultWithView(
            payload,
            "booking_created",
            new { bookingId = confirmation.BookingId, status = confirmation.Status.ToString(), total = confirmation.Total, replayed = submitted.Replayed, nextStep = "The user completes payment on the secure payment page." });
    }
}
