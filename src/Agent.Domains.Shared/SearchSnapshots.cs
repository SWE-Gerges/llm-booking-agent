using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Options;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared;

/// <summary>
/// Stores search results per session and service behind a handle. Follow-up tools resolve items from the stored
/// snapshot, so a model can refer to an offer by id but can never supply its own price or details.
/// </summary>
public sealed class SearchSnapshots(IBookingContextStore contexts, TimeProvider clock, IOptions<AgentOptions> options)
{
    /// <summary>Saves a new snapshot. A new search invalidates any earlier selection, confirmed price and (after a booking) form.</summary>
    public Task<SearchSnapshot> SaveAsync(string sessionId, ServiceType service, object searchParams, object results, CancellationToken cancellationToken) =>
        SaveAsync(sessionId, service, searchParams, results, partySize: 1, cancellationToken);

    /// <inheritdoc cref="SaveAsync(string, ServiceType, object, object, CancellationToken)"/>
    public async Task<SearchSnapshot> SaveAsync(string sessionId, ServiceType service, object searchParams, object results, int partySize, CancellationToken cancellationToken)
    {
        var snapshot = new SearchSnapshot(
            Guid.NewGuid().ToString("N")[..12],
            clock.GetUtcNow(),
            AgentJson.Serialize(searchParams),
            AgentJson.Serialize(results),
            partySize);

        await contexts.MutateAsync(
            sessionId,
            service,
            c => c with
            {
                Search = snapshot,
                SelectedItemId = null,
                SelectedOptionId = null,
                ConfirmedPrice = null,
                Form = c.BookingId is null ? c.Form : null,
                IdempotencyKey = null,
                BookingId = null,
                PastBookingIds = c.BookingId is null ? c.PastBookingIds : [.. c.PastBookingIds, c.BookingId],
            },
            cancellationToken);
        return snapshot;
    }

    /// <summary>Returns the stored context and its snapshot, or throws a model-readable error if none exists, it is stale, or the handle is outdated.</summary>
    public async Task<(BookingContext Context, SearchSnapshot Snapshot)> RequireFreshAsync(
        string sessionId,
        ServiceType service,
        string? handle,
        CancellationToken cancellationToken)
    {
        var context = await contexts.GetAsync(sessionId, service, cancellationToken);
        if (context?.Search is not { } snapshot)
        {
            throw new ToolExecutionException("There are no search results yet. Run a search first.");
        }

        if (clock.GetUtcNow() - snapshot.CreatedAt > options.Value.SearchMaxAge)
        {
            throw new ToolExecutionException("The search results are out of date. Run a new search.");
        }

        if (handle is not null && !string.Equals(handle, snapshot.Handle, StringComparison.Ordinal))
        {
            throw new ToolExecutionException("That search handle is not current. Use the handle from the latest search.");
        }

        return (context, snapshot);
    }

    public static T ReadParams<T>(SearchSnapshot snapshot) =>
        AgentJson.Deserialize<T>(snapshot.ParamsJson) ?? throw new JsonException("The stored search parameters are unreadable.");

    public static IReadOnlyList<T> ReadResults<T>(SearchSnapshot snapshot) =>
        AgentJson.Deserialize<List<T>>(snapshot.ResultsJson) ?? [];
}

/// <summary>Contact fields collected by both the passenger and the guest form.</summary>
public sealed class ContactDetails
{
    public string? Email { get; set; }

    public string? Phone { get; set; }
}
