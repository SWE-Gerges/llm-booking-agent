using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Persistence;
using Agent.Persistence.Stores;
using Agent.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Entities = Agent.Persistence.Entities;

namespace Agent.Tests.PersistenceTests;

public class BookingContextStoreTests
{
    [Fact]
    public async Task A_missing_context_is_null_and_the_list_is_empty()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        Assert.Null(await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None));
        Assert.Empty(await h.Contexts.ListAsync(s.SessionId, CancellationToken.None));
        Assert.Null(await h.Contexts.GetAsync("unknown", ServiceType.Hotel, CancellationToken.None));
        Assert.Empty(await h.Contexts.ListAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task The_first_mutation_starts_from_an_empty_context_and_creates_version_one()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        BookingContext? seen = null;

        var created = await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Flight,
            c =>
            {
                seen = c;
                return c with { SelectedItemId = "offer-1" };
            },
            CancellationToken.None);

        Assert.Equal(BookingContext.Empty(s.SessionId, ServiceType.Flight), seen);
        Assert.Equal(1, created.Version);
        var stored = await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);
        Assert.Equal("offer-1", stored!.SelectedItemId);
        Assert.Equal(1, stored.Version);
        Assert.Equal(ServiceType.Flight, stored.ServiceType);
        Assert.Equal(s.SessionId, stored.SessionId);
    }

    [Fact]
    public async Task Every_write_increments_the_version_and_sees_the_previous_state()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.Contexts.MutateAsync(s.SessionId, ServiceType.Hotel, c => c with { SelectedItemId = "a" }, CancellationToken.None);
        string? previous = null;

        var second = await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Hotel,
            c =>
            {
                previous = c.SelectedItemId;
                return c with { SelectedOptionId = "b" };
            },
            CancellationToken.None);
        var third = await h.Contexts.MutateAsync(s.SessionId, ServiceType.Hotel, c => c, CancellationToken.None);

        Assert.Equal("a", previous);
        Assert.Equal(2, second.Version);
        Assert.Equal(3, third.Version);
        var stored = await h.Contexts.GetAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None);
        Assert.Equal(("a", "b", 3L), (stored!.SelectedItemId, stored.SelectedOptionId, stored.Version));
    }

    [Fact]
    public async Task A_mutation_cannot_overwrite_the_store_managed_version()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var created = await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { Version = 999 }, CancellationToken.None);
        var updated = await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { Version = 5 }, CancellationToken.None);

        Assert.Equal(1, created.Version);
        Assert.Equal(2, updated.Version);
        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        var row = await db.BookingContexts.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.Version);
        Assert.Contains("\"version\":0", row.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rich_context_round_trips_through_the_database()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var now = h.Clock.GetUtcNow();
        var rich = new BookingContext
        {
            SessionId = s.SessionId,
            ServiceType = ServiceType.Flight,
            Search = new SearchSnapshot("handle-1", now, """{"origin":"AMS"}""", """[{"id":"fl-1","note":"déjà"}]"""),
            SelectedItemId = "fl-1",
            SelectedOptionId = "flex",
            ConfirmedPrice = new PriceConfirmation("qt-1", new Money(1234.56m, "EUR"), now),
            Form = new FormRecord(
                new Dictionary<string, string> { ["passengers[0].firstName"] = "Jane" },
                new Dictionary<string, string> { ["passengers[0].firstName"] = "J***" },
                now),
            IdempotencyKey = "key-1",
            BookingId = "BK-1",
            PastBookingIds = ["BK-0"],
        };

        await h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Flight,
            _ => rich,
            CancellationToken.None);
        var loaded = await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(rich with { Version = 1 }), AgentJson.Serialize(loaded));
        Assert.Equal(1234.56m, loaded!.ConfirmedPrice!.Total.Amount);
        Assert.Equal("J***", loaded.Form!.Masked["passengers[0].firstName"]);
        Assert.Equal(["BK-0"], loaded.PastBookingIds);
    }

    [Fact]
    public async Task Services_and_sessions_are_isolated_and_the_list_returns_every_service()
    {
        using var h = await DomainHarness.CreateAsync();
        var a = await h.NewSessionAsync();
        var b = await h.NewSessionAsync();
        await h.Contexts.MutateAsync(a.SessionId, ServiceType.Flight, c => c with { SelectedItemId = "a-flight" }, CancellationToken.None);
        await h.Contexts.MutateAsync(a.SessionId, ServiceType.Hotel, c => c with { SelectedItemId = "a-hotel" }, CancellationToken.None);
        await h.Contexts.MutateAsync(b.SessionId, ServiceType.Flight, c => c with { SelectedItemId = "b-flight" }, CancellationToken.None);

        var listed = await h.Contexts.ListAsync(a.SessionId, CancellationToken.None);

        Assert.Equal(["a-flight", "a-hotel"], listed.Select(c => c.SelectedItemId).Order(StringComparer.Ordinal));
        Assert.Equal("b-flight", (await h.Contexts.GetAsync(b.SessionId, ServiceType.Flight, CancellationToken.None))!.SelectedItemId);
        Assert.Null(await h.Contexts.GetAsync(b.SessionId, ServiceType.Hotel, CancellationToken.None));
    }

    [Fact]
    public async Task Mutating_an_unknown_session_is_an_error_and_a_failing_mutation_changes_nothing()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { SelectedItemId = "keep" }, CancellationToken.None);

        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            h.Contexts.MutateAsync("unknown", ServiceType.Flight, c => c, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, _ => throw new InvalidOperationException("bad mutation"), CancellationToken.None));

        var stored = await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);
        Assert.Equal(("keep", 1L), (stored!.SelectedItemId, stored.Version));
    }

    [Fact]
    public async Task Concurrent_mutations_on_one_row_are_all_applied()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c, CancellationToken.None);
        var applications = 0;
        var ids = Enumerable.Range(0, 8).Select(i => $"BK-{i}").ToList();

        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Flight,
            c =>
            {
                Interlocked.Increment(ref applications);
                return c with { PastBookingIds = [.. c.PastBookingIds, id] };
            },
            CancellationToken.None))));

        var stored = await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);
        Assert.Equal(ids.Order(StringComparer.Ordinal), stored!.PastBookingIds.Order(StringComparer.Ordinal));
        Assert.Equal(9, stored.Version);
        Assert.True(applications >= 8);
        Assert.Equal(Enumerable.Range(2, 8).Select(v => (long)v), results.Select(r => r.Version).Order());
    }

    [Fact]
    public async Task Concurrent_first_writes_all_apply_and_no_unique_violation_leaks()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var ids = Enumerable.Range(0, 8).Select(i => $"BK-{i}").ToList();

        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => h.Contexts.MutateAsync(
            s.SessionId,
            ServiceType.Hotel,
            c => c with { PastBookingIds = [.. c.PastBookingIds, id] },
            CancellationToken.None))));

        var stored = await h.Contexts.GetAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None);
        Assert.Equal(ids.Order(StringComparer.Ordinal), stored!.PastBookingIds.Order(StringComparer.Ordinal));
        Assert.Equal(8, stored.Version);
        Assert.Equal(Enumerable.Range(1, 8).Select(v => (long)v), results.Select(r => r.Version).Order());
        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.BookingContexts.CountAsync());
    }

    [Fact]
    public async Task Concurrent_writes_to_different_services_do_not_interfere()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => Task.Run(() => h.Contexts.MutateAsync(s.SessionId, ServiceType.Flight, c => c with { PastBookingIds = [.. c.PastBookingIds, $"f{i}"] }, CancellationToken.None)))
                .Concat(Enumerable.Range(0, 4).Select(i => Task.Run(() => h.Contexts.MutateAsync(s.SessionId, ServiceType.Hotel, c => c with { PastBookingIds = [.. c.PastBookingIds, $"h{i}"] }, CancellationToken.None)))));

        Assert.Equal(4, (await h.Contexts.GetAsync(s.SessionId, ServiceType.Flight, CancellationToken.None))!.PastBookingIds.Count);
        Assert.Equal(4, (await h.Contexts.GetAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None))!.PastBookingIds.Count);
    }

    [Fact]
    public async Task A_real_unique_violation_is_recognised_and_other_failures_are_not()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var factory = h.Get<IDbContextFactory<AgentDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Sessions.Add(new Entities.ChatSession { Id = Guid.NewGuid(), SessionToken = s.SessionId, State = "Active", Locale = "en", CreatedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch });
            var unique = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<SqliteException>(unique.InnerException);
            Assert.True(BookingContextStore.IsUniqueViolation(unique));
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.BookingContexts.Add(new Entities.BookingContext { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), ServiceType = "Flight", DataJson = "{}", Version = 1, UpdatedAt = DateTimeOffset.UnixEpoch });
            var foreignKey = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<SqliteException>(foreignKey.InnerException);
            Assert.False(BookingContextStore.IsUniqueViolation(foreignKey));
        }

        Assert.False(BookingContextStore.IsUniqueViolation(new DbUpdateException("no inner exception")));
        Assert.False(BookingContextStore.IsUniqueViolation(new DbUpdateException("other", new InvalidOperationException("not a database error"))));
    }
}

public class EscalationStoreTests
{
    private static EscalationRequest Request(string id, string sessionId, DateTimeOffset at) =>
        new(id, sessionId, "Needs a person", "Summary text", at);

    [Fact]
    public async Task An_escalation_writes_the_request_and_its_audit_row_together()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var at = h.Clock.GetUtcNow();

        await h.Get<IEscalationStore>().CreateAsync(Request("esc-1", s.SessionId, at), CancellationToken.None);

        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        var request = await db.Escalations.AsNoTracking().SingleAsync();
        Assert.Equal(("esc-1", s.SessionId, "Needs a person", "Summary text", "open", at), (request.Id, request.SessionToken, request.Reason, request.Summary, request.Status, request.CreatedAt));
        var audit = await db.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal(("assistant", "escalation.created", s.SessionId, "escalation esc-1", at), (audit.Actor, audit.Action, audit.SessionToken, audit.Details, audit.CreatedAt));
    }

    [Fact]
    public async Task A_failed_escalation_leaves_neither_a_second_request_nor_a_second_audit_row()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        var store = h.Get<IEscalationStore>();
        await store.CreateAsync(Request("esc-1", s.SessionId, h.Clock.GetUtcNow()), CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(Request("esc-1", s.SessionId, h.Clock.GetUtcNow()), CancellationToken.None));

        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.Escalations.CountAsync());
        Assert.Equal(1, await db.AuditLog.CountAsync());
    }

    [Fact]
    public async Task An_escalation_outlives_its_deleted_session()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.Get<IEscalationStore>().CreateAsync(Request("esc-1", s.SessionId, h.Clock.GetUtcNow()), CancellationToken.None);

        await h.Get<ISessionStore>().DeleteAsync(s.SessionId, CancellationToken.None);

        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.Equal(s.SessionId, (await db.Escalations.AsNoTracking().SingleAsync()).SessionToken);
        Assert.Single(await BookingSeed.AuditRowsAsync(h));
    }

    [Fact]
    public async Task The_audit_log_stores_entries_with_and_without_a_session()
    {
        using var h = await DomainHarness.CreateAsync();
        var audit = h.Get<IAuditLog>();
        var at = h.Clock.GetUtcNow();

        await audit.WriteAsync(new AuditEntry("api", "session.deleted", "sess-1", "details", at), CancellationToken.None);
        await audit.WriteAsync(new AuditEntry("system", "startup", null, null, at.AddSeconds(1)), CancellationToken.None);

        var rows = await BookingSeed.AuditRowsAsync(h);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("api", "session.deleted", "sess-1", "details"), (rows[0].Actor, rows[0].Action, rows[0].SessionToken, rows[0].Details));
        Assert.Null(rows[1].SessionToken);
        Assert.Null(rows[1].Details);
        Assert.True(rows[1].Id > rows[0].Id);
    }
}
