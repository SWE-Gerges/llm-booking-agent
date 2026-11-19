using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using IFormDataProtector = Agent.Abstractions.Ports.IFormDataProtector;

namespace Agent.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options, IFormDataProtector? formDataProtector = null) : DbContext(options)
{
    /// <summary>Encrypts the booking-context column. Null only at design time, where the column type is all that matters.</summary>
    internal IFormDataProtector? FormDataProtector { get; } = formDataProtector;

    public DbSet<ChatSession> Sessions => Set<ChatSession>();

    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    public DbSet<BookingContext> BookingContexts => Set<BookingContext>();

    public DbSet<EscalationRequest> Escalations => Set<EscalationRequest>();

    public DbSet<AdminAuditLog> AuditLog => Set<AdminAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatSession>(e =>
        {
            e.ToTable("chat_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SessionToken).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.SessionToken).IsUnique();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.Locale).HasMaxLength(8).IsRequired();
            e.Property(x => x.RequestedServices).HasMaxLength(128).IsRequired();
            e.Property(x => x.RepeatHash).HasMaxLength(64);
            e.HasIndex(x => x.LastActivityAt);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MessageId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.MessageId).IsUnique();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.BlocksJson).IsRequired();
            e.HasIndex(x => new { x.SessionId, x.IsVisible, x.CreatedAt });
            e.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
            e.HasOne(x => x.Session).WithMany(s => s.Messages).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BookingContext>(e =>
        {
            e.ToTable("booking_contexts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ServiceType).HasMaxLength(16).IsRequired();
            var data = e.Property(x => x.DataJson).IsRequired();
            if (FormDataProtector is { } protector)
            {
                // Raw passenger and contact details live in this JSON, so the whole column is stored encrypted.
                data.HasConversion(plain => protector.Protect(plain), stored => protector.Unprotect(stored));
            }

            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.SessionId, x.ServiceType }).IsUnique();
            e.HasOne(x => x.Session).WithMany(s => s.BookingContexts).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EscalationRequest>(e =>
        {
            e.ToTable("escalation_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.SessionToken).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.SessionToken);
        });

        modelBuilder.Entity<AdminAuditLog>(e =>
        {
            e.ToTable("admin_audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(64).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.SessionToken).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => x.CreatedAt);
        });

        if (Database.IsSqlite())
        {
            // SQLite cannot order or compare DateTimeOffset natively; store it as a sortable integer.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.ClrType.GetProperties().Where(p => p.PropertyType == typeof(DateTimeOffset)))
                {
                    modelBuilder.Entity(entityType.ClrType).Property(property.Name).HasConversion(new DateTimeOffsetToBinaryConverter());
                }
            }
        }
    }
}

/// <summary>
/// The model differs per provider (SQLite value converters) and per form-data protector (the encryption converter
/// captures it), and EF caches models per context type by default. Including both in the key lets one process use
/// several providers or key rings side by side, as the test suites do, without a converter leaking across containers.
/// </summary>
internal sealed class ProviderAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime, (context as AgentDbContext)?.FormDataProtector);
}
