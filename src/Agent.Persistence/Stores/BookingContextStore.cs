using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Entities = Agent.Persistence.Entities;

namespace Agent.Persistence.Stores;

internal sealed class BookingContextStore(IDbContextFactory<AgentDbContext> factory) : IBookingContextStore
{
    private const int MaxAttempts = 40;

    public async Task<BookingContext?> GetAsync(string sessionId, ServiceType serviceType, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var service = serviceType.ToString();
        var row = await db.BookingContexts.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Session!.SessionToken == sessionId && c.ServiceType == service, cancellationToken);
        return row is null ? null : ToContext(row, sessionId);
    }

    public async Task<IReadOnlyList<BookingContext>> ListAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.BookingContexts.AsNoTracking()
            .Where(c => c.Session!.SessionToken == sessionId)
            .ToListAsync(cancellationToken);
        return rows.Select(r => ToContext(r, sessionId)).ToList();
    }

    public async Task<BookingContext> MutateAsync(
        string sessionId,
        ServiceType serviceType,
        Func<BookingContext, BookingContext> mutation,
        CancellationToken cancellationToken)
    {
        var service = serviceType.ToString();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var internalId = await db.Sessions
                .Where(s => s.SessionToken == sessionId)
                .Select(s => (Guid?)s.Id)
                .SingleOrDefaultAsync(cancellationToken) ?? throw new SessionNotFoundException(sessionId);

            var row = await db.BookingContexts.SingleOrDefaultAsync(c => c.SessionId == internalId && c.ServiceType == service, cancellationToken);
            try
            {
                if (row is null)
                {
                    var created = mutation(BookingContext.Empty(sessionId, serviceType));
                    db.BookingContexts.Add(new Entities.BookingContext
                    {
                        Id = Guid.CreateVersion7(),
                        SessionId = internalId,
                        ServiceType = service,
                        DataJson = Serialize(created),
                        Version = 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    return created with { Version = 1 };
                }

                var updated = mutation(ToContext(row, sessionId));
                row.DataJson = Serialize(updated);
                row.Version++;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return updated with { Version = row.Version };
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another writer changed the row between our read and write: re-read and re-apply the mutation.
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another writer inserted the first row for this (session, service) before us: apply onto the winner.
            }

            await Task.Delay(Random.Shared.Next(1, Math.Min(8 * attempt, 60)), cancellationToken);
        }

        throw new InvalidOperationException($"The booking context for session {sessionId[..Math.Min(8, sessionId.Length)]} stayed contended after {MaxAttempts} attempts.");
    }

    internal static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
        SqliteException sqlite => sqlite.SqliteErrorCode == 19 && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ => false,
    };

    private static string Serialize(BookingContext context) => AgentJson.Serialize(context with { Version = 0 });

    private static BookingContext ToContext(Entities.BookingContext row, string sessionId)
    {
        var context = AgentJson.Deserialize<BookingContext>(row.DataJson)
            ?? BookingContext.Empty(sessionId, Enum.Parse<ServiceType>(row.ServiceType, ignoreCase: true));
        return context with { Version = row.Version };
    }
}

internal sealed class EscalationStore(IDbContextFactory<AgentDbContext> factory) : IEscalationStore, IAuditLog
{
    public async Task CreateAsync(Abstractions.Ports.EscalationRequest request, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Escalations.Add(new Entities.EscalationRequest
        {
            Id = request.Id,
            SessionToken = request.SessionId,
            Reason = request.Reason,
            Summary = request.Summary,
            Status = "open",
            CreatedAt = request.CreatedAt,
        });
        db.AuditLog.Add(new Entities.AdminAuditLog
        {
            Actor = "assistant",
            Action = "escalation.created",
            SessionToken = request.SessionId,
            Details = $"escalation {request.Id}",
            CreatedAt = request.CreatedAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditLog.Add(new Entities.AdminAuditLog
        {
            Actor = entry.Actor,
            Action = entry.Action,
            SessionToken = entry.SessionId,
            Details = entry.Details,
            CreatedAt = entry.At,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
