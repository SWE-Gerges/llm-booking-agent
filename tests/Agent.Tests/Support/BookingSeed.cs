using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;
using Agent.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Entities = Agent.Persistence.Entities;

namespace Agent.Tests.Support;

internal static class BookingSeed
{
    public static Task<BookingContext> SeedAsync(
        DomainHarness harness,
        SessionContext session,
        ServiceType service,
        decimal total = 123.45m,
        bool selected = true,
        bool priced = true,
        bool form = true)
    {
        var now = harness.Clock.GetUtcNow();
        var raw = new Dictionary<string, string>
        {
            ["passengers[0].firstName"] = "Jane",
            ["passengers[0].lastName"] = "Doe",
            ["contact.email"] = "user@example.com",
        };
        var masked = new Dictionary<string, string>
        {
            ["passengers[0].firstName"] = "J***",
            ["passengers[0].lastName"] = "D***",
            ["contact.email"] = "u***@example.com",
        };

        return harness.Contexts.MutateAsync(
            session.SessionId,
            service,
            c => c with
            {
                SelectedItemId = selected ? "item-1" : null,
                SelectedOptionId = selected ? "option-1" : null,
                ConfirmedPrice = priced ? new PriceConfirmation("qt-seed", new Money(total, "EUR"), now) : null,
                Form = form ? new FormRecord(raw, masked, now) : null,
            },
            CancellationToken.None);
    }

    public static async Task<List<Entities.AdminAuditLog>> AuditRowsAsync(DomainHarness harness, string? action = null)
    {
        await using var db = await harness.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        var rows = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        return action is null ? rows : rows.Where(r => r.Action == action).ToList();
    }
}
