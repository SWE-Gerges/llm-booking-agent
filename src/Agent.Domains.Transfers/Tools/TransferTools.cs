using System.Globalization;
using System.Text.Json;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Tools;
using Agent.Domains.Shared;

namespace Agent.Domains.Transfers.Tools;

internal static class TransferView
{
    public static object Summary(TransferOffer o) => new
    {
        id = o.Id,
        vehicle = o.VehicleType,
        maxPassengers = o.MaxPassengers,
        durationMinutes = o.DurationMinutes,
        price = o.Price,
        freeWaitingMinutes = o.FreeWaitingMinutes,
        freeCancellation = o.FreeCancellation,
    };

    public static async Task<(TransferOffer Offer, TransferSearchParams Params)> RequireOfferAsync(
        SearchSnapshots snapshots,
        string sessionId,
        string offerId,
        CancellationToken cancellationToken)
    {
        var (_, snapshot) = await snapshots.RequireFreshAsync(sessionId, ServiceType.Transfer, handle: null, cancellationToken);
        var offer = SearchSnapshots.ReadResults<TransferOffer>(snapshot).FirstOrDefault(o => o.Id == offerId)
            ?? throw new ToolExecutionException($"Transfer '{offerId}' is not part of the latest search results.");
        return (offer, SearchSnapshots.ReadParams<TransferSearchParams>(snapshot));
    }
}

public sealed class SearchTransfersTool(ITransferBackend backend, SearchSnapshots snapshots, TimeProvider clock) : ChatToolBase(
    "search_transfers",
    "Search ground transfers (for example airport to hotel). Date is yyyy-MM-dd, time is HH:mm. If the user did not say how many passengers, omit passengers and say what was assumed.",
    """
    {"type":"object","properties":{
      "pickup":{"type":"string","minLength":2,"maxLength":80},
      "dropoff":{"type":"string","minLength":2,"maxLength":80},
      "date":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},
      "time":{"type":"string","pattern":"^([01]\\d|2[0-3]):[0-5]\\d$"},
      "passengers":{"type":"integer","minimum":1,"maximum":12}
    },"required":["pickup","dropoff","date","time"],"additionalProperties":false}
    """), ISearchTool
{
    public ServiceType Service => ServiceType.Transfer;

    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var date = input.RequireDate("date");
        if (date < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
        {
            throw new ToolExecutionException("The transfer date is in the past.");
        }

        var time = TimeOnly.ParseExact(input.GetString("time")!, "HH:mm", CultureInfo.InvariantCulture);
        var requested = input.GetInt("passengers");
        var search = new TransferSearchParams(input.GetString("pickup")!.Trim(), input.GetString("dropoff")!.Trim(), date, time, requested ?? 1);

        var offers = (await backend.SearchAsync(search, cancellationToken)).OrderBy(o => o.Price.Amount).ToList();
        if (offers.Count == 0)
        {
            throw new ToolExecutionException($"No vehicle can carry {search.Passengers} passengers. Try a smaller group or split the booking.");
        }

        var snapshot = await snapshots.SaveAsync(context.SessionId, ServiceType.Transfer, search, offers, search.Passengers, cancellationToken);
        context.Session.AddRequestedService(ServiceType.Transfer);

        var note = requested is null
            ? "The user did not say how many passengers, so 1 passenger was assumed. Tell the user and offer to search again with the right number."
            : null;
        return ResultWithView(
            new { handle = snapshot.Handle, search, offers },
            "transfer_results",
            new { handle = snapshot.Handle, currency = offers[0].Price.Currency, count = offers.Count, offers = offers.Select(TransferView.Summary) },
            note);
    }
}

public sealed class GetTransferDetailsTool(SearchSnapshots snapshots) : ChatToolBase(
    "get_transfer_details",
    "Show the full details (vehicle, capacity, waiting time, cancellation) of one transfer from the latest search.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, search) = await TransferView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        return ResultWithView(new { offer, search }, "transfer_details", new { offer = TransferView.Summary(offer), search });
    }
}

public sealed class GetTransferPriceTool(ITransferBackend backend, SearchSnapshots snapshots) : ChatToolBase(
    "get_transfer_price",
    "Get the live price of one transfer from the latest search. This only informs; use select_transfer to choose it.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, search) = await TransferView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        var quote = await backend.GetQuoteAsync(offer, search, cancellationToken);
        return Result(
            new { offerId = offer.Id, total = quote.Total, previousTotal = quote.PreviousTotal, priceChanged = quote.PriceChanged, expiresAt = quote.ExpiresAt },
            "transfer_price");
    }
}

public sealed class SelectTransferTool(ITransferBackend backend, SearchSnapshots snapshots, IBookingContextStore contexts, TimeProvider clock) : ChatToolBase(
    "select_transfer",
    "Choose one transfer from the latest search. The server re-quotes the live price and records it; it can then be added to the trip.",
    """{"type":"object","properties":{"offerId":{"type":"string","maxLength":64}},"required":["offerId"],"additionalProperties":false}""")
{
    public override async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var (offer, search) = await TransferView.RequireOfferAsync(snapshots, context.SessionId, input.GetString("offerId")!, cancellationToken);
        var quote = await backend.GetQuoteAsync(offer, search, cancellationToken);

        var confirmation = new PriceConfirmation(quote.QuoteId, quote.Total, clock.GetUtcNow());
        await contexts.MutateAsync(
            context.SessionId,
            ServiceType.Transfer,
            c => c with { SelectedItemId = offer.Id, SelectedOptionId = offer.VehicleType, ConfirmedPrice = confirmation },
            cancellationToken);

        var note = quote.PriceChanged
            ? $"The price changed from {quote.PreviousTotal!.Amount} to {quote.Total.Amount} {quote.Total.Currency}. Tell the user before going on."
            : null;
        return Result(
            new { offerId = offer.Id, vehicle = offer.VehicleType, total = quote.Total, previousTotal = quote.PreviousTotal, priceChanged = quote.PriceChanged, expiresAt = quote.ExpiresAt },
            "transfer_selected",
            modelNote: note);
    }
}
