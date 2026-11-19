using System.ComponentModel.DataAnnotations;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Options;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared.Trips;

public sealed class TripOptions
{
    public const string SectionName = "Trip";

    /// <summary>
    /// Services the basket may contain: the server-side allow-list, the model cannot add anything else. Unset means
    /// flight, hotel and transfer. (A null default is used because configuration binding appends to a pre-filled list.)
    /// </summary>
    public List<ServiceType>? AllowedServices { get; set; }

    public IReadOnlyList<ServiceType> Allowed => AllowedServices is { Count: > 0 } configured
        ? configured.Where(s => s != ServiceType.Trip).Distinct().ToList()
        : [ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer];

    [Range(1, 3)]
    public int MaxServices { get; set; } = 3;
}

public enum TripLineStatus
{
    Ready,
    NeedsPriceCheck,
}

public sealed record TripLine(
    ServiceType Service,
    string? ItemId,
    string? OptionId,
    string? QuoteId,
    Money? Total,
    int PartySize,
    TripLineStatus Status);

/// <summary>A snapshot of the basket computed from the live per-service booking contexts.</summary>
public sealed record TripState(
    IReadOnlyList<ServiceType> Services,
    IReadOnlyList<TripLine> Lines,
    Money? Total,
    int Travellers,
    bool PassportRequired,
    bool FormSubmitted,
    string? BookingId)
{
    public IReadOnlyList<ServiceType> NeedsPriceCheck => Lines.Where(l => l.Status == TripLineStatus.NeedsPriceCheck).Select(l => l.Service).ToList();

    public bool ReadyToSubmit => BookingId is null && Lines.Count > 0 && Total is not null && NeedsPriceCheck.Count == 0;
}

/// <summary>
/// The trip basket. Which services are in the trip lives in the trip booking context (<c>TripServices</c>) and is
/// written only here, within the server's allow-list and size limit. Prices are never stored in the basket: every
/// view and every submission recomputes the lines from the confirmed selection of each service, so a changed or
/// stale selection can never be checked out at an old price.
/// </summary>
public sealed class TripBasket(IBookingContextStore contexts, TimeProvider clock, IOptions<TripOptions> trip, IOptions<AgentOptions> agent)
{
    public async Task<TripState> ViewAsync(string sessionId, CancellationToken cancellationToken)
    {
        var context = await contexts.GetAsync(sessionId, ServiceType.Trip, cancellationToken) ?? BookingContext.Empty(sessionId, ServiceType.Trip);
        return await ComputeAsync(context, cancellationToken);
    }

    /// <summary>Adds a service whose price is confirmed. Adding clears the traveller form, since the traveller set may change.</summary>
    public async Task<TripState> AddAsync(string sessionId, ServiceType service, CancellationToken cancellationToken)
    {
        var settings = trip.Value;
        if (service == ServiceType.Trip || !settings.Allowed.Contains(service))
        {
            throw new ToolExecutionException($"The service '{service.ToString().ToLowerInvariant()}' cannot be added to a trip.");
        }

        var component = await contexts.GetAsync(sessionId, service, cancellationToken);
        if (!IsConfirmed(component))
        {
            throw new ToolExecutionException(
                $"Choose and confirm the price of the {service.ToString().ToLowerInvariant()} first (re-check or pre-book or select it), then add it to the trip.");
        }

        // Validate the resulting basket (size limit, one currency) before anything is written, so a refused add leaves the trip unchanged.
        var current = await contexts.GetAsync(sessionId, ServiceType.Trip, cancellationToken) ?? BookingContext.Empty(sessionId, ServiceType.Trip);
        await ComputeAsync(Add(current, service, settings), cancellationToken);

        var updated = await contexts.MutateAsync(
            sessionId,
            ServiceType.Trip,
            c => Add(c, service, settings),
            cancellationToken);
        return await ComputeAsync(updated, cancellationToken);
    }

    /// <summary>Removes a service. The traveller form is cleared, so it must be filled in again for the remaining trip.</summary>
    public async Task<TripState> RemoveAsync(string sessionId, ServiceType service, CancellationToken cancellationToken)
    {
        var updated = await contexts.MutateAsync(
            sessionId,
            ServiceType.Trip,
            c => c.BookingId is null && c.TripServices.Contains(service)
                ? c with { TripServices = c.TripServices.Where(s => s != service).ToList(), Form = null }
                : throw new ToolExecutionException($"The {service.ToString().ToLowerInvariant()} is not in the current trip."),
            cancellationToken);
        return await ComputeAsync(updated, cancellationToken);
    }

    internal bool IsConfirmed(BookingContext? component) =>
        component is { SelectedItemId: not null, ConfirmedPrice: { } price }
        && clock.GetUtcNow() - price.ConfirmedAt <= agent.Value.PriceConfirmationMaxAge;

    private static BookingContext Add(BookingContext current, ServiceType service, TripOptions settings)
    {
        // A trip that was already booked is finished; the next add starts a fresh one.
        var fresh = current.BookingId is null
            ? current
            : current with
            {
                PastBookingIds = [.. current.PastBookingIds, current.BookingId],
                BookingId = null,
                IdempotencyKey = null,
                TripServices = [],
                Form = null,
            };

        if (fresh.TripServices.Contains(service))
        {
            return fresh;
        }

        if (fresh.TripServices.Count >= settings.MaxServices)
        {
            throw new ToolExecutionException($"A trip can hold at most {settings.MaxServices} services. Remove one first.");
        }

        return fresh with { TripServices = [.. fresh.TripServices, service], Form = null };
    }

    private async Task<TripState> ComputeAsync(BookingContext tripContext, CancellationToken cancellationToken)
    {
        if (tripContext.BookingId is not null)
        {
            return new TripState(tripContext.TripServices, [], null, 0, false, tripContext.Form is not null, tripContext.BookingId);
        }

        var lines = new List<TripLine>(tripContext.TripServices.Count);
        var passport = false;
        var travellers = 1;
        foreach (var service in tripContext.TripServices)
        {
            var component = await contexts.GetAsync(tripContext.SessionId, service, cancellationToken);
            var partySize = component?.Search?.PartySize ?? 1;
            travellers = Math.Max(travellers, partySize);
            passport |= component?.Requirements.Contains("passport_required") == true;

            lines.Add(IsConfirmed(component)
                ? new TripLine(service, component!.SelectedItemId, component.SelectedOptionId, component.ConfirmedPrice!.QuoteId, component.ConfirmedPrice.Total, partySize, TripLineStatus.Ready)
                : new TripLine(service, component?.SelectedItemId, component?.SelectedOptionId, null, null, partySize, TripLineStatus.NeedsPriceCheck));
        }

        var ready = lines.Count > 0 && lines.All(l => l.Status == TripLineStatus.Ready);
        var currencies = lines.Select(l => l.Total?.Currency).Distinct().ToList();
        Money? total = ready && currencies.Count == 1
            ? new Money(lines.Sum(l => l.Total!.Amount), currencies[0]!)
            : null;

        if (ready && total is null)
        {
            throw new ToolExecutionException("The services in the trip are priced in different currencies and cannot be combined.");
        }

        return new TripState(tripContext.TripServices, lines, total, travellers, passport, tripContext.Form is not null, null);
    }
}
