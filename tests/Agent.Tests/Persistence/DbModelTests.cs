using Agent.Persistence;
using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agent.Tests.PersistenceTests;

public class DbModelTests
{
    private static AgentDbContext Sqlite() => new(
        new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite("Data Source=model-probe.db")
            .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>()
            .Options);

    private static AgentDbContext Npgsql() => new(
        new DbContextOptionsBuilder<AgentDbContext>()
            .UseNpgsql("Host=localhost;Database=model_probe;Username=probe;Password=probe")
            .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>()
            .Options);

    private static IEntityType Entity<T>(AgentDbContext context) =>
        context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(T))!;

    private static bool HasIndex(IEntityType entity, bool unique, params string[] properties) =>
        entity.GetIndexes().Any(i => i.IsUnique == unique && i.Properties.Select(p => p.Name).SequenceEqual(properties));

    [Fact]
    public void The_cache_key_differs_per_provider_and_design_time_flag()
    {
        var factory = new ProviderAwareModelCacheKeyFactory();
        using var sqliteA = Sqlite();
        using var sqliteB = Sqlite();
        using var postgres = Npgsql();

        var keySqlite = factory.Create(sqliteA, designTime: false);

        Assert.Equal(keySqlite, factory.Create(sqliteB, designTime: false));
        Assert.NotEqual(keySqlite, factory.Create(postgres, designTime: false));
        Assert.NotEqual(keySqlite, factory.Create(sqliteA, designTime: true));
        Assert.Equal(factory.Create(postgres, designTime: false), factory.Create(Npgsql(), designTime: false));
    }

    [Fact]
    public void Both_providers_build_a_model_with_the_five_tables_in_one_process()
    {
        using var sqlite = Sqlite();
        using var postgres = Npgsql();

        foreach (var context in new[] { sqlite, postgres })
        {
            var tables = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().Select(e => e.GetTableName()).Order().ToList();
            Assert.Equal(["admin_audit_log", "booking_contexts", "chat_messages", "chat_sessions", "escalation_requests"], tables);
        }
    }

    [Fact]
    public void Only_sqlite_stores_date_times_as_sortable_integers()
    {
        using var sqlite = Sqlite();
        using var postgres = Npgsql();

        var sqliteConverter = Entity<ChatSession>(sqlite).FindProperty(nameof(ChatSession.CreatedAt))!.GetValueConverter();
        var postgresConverter = Entity<ChatSession>(postgres).FindProperty(nameof(ChatSession.CreatedAt))!.GetValueConverter();

        Assert.IsType<DateTimeOffsetToBinaryConverter>(sqliteConverter);
        Assert.Null(postgresConverter);
        Assert.IsType<DateTimeOffsetToBinaryConverter>(Entity<ChatMessage>(sqlite).FindProperty(nameof(ChatMessage.CreatedAt))!.GetValueConverter());
        Assert.IsType<DateTimeOffsetToBinaryConverter>(Entity<AdminAuditLog>(sqlite).FindProperty(nameof(AdminAuditLog.CreatedAt))!.GetValueConverter());
    }

    [Fact]
    public void The_documented_unique_indexes_and_concurrency_token_exist_for_both_providers()
    {
        using var sqlite = Sqlite();
        using var postgres = Npgsql();

        foreach (var context in new[] { sqlite, postgres })
        {
            var session = Entity<ChatSession>(context);
            var message = Entity<ChatMessage>(context);
            var booking = Entity<BookingContext>(context);

            Assert.True(HasIndex(session, true, nameof(ChatSession.SessionToken)));
            Assert.True(HasIndex(message, true, nameof(ChatMessage.MessageId)));
            Assert.True(HasIndex(message, true, nameof(ChatMessage.SessionId), nameof(ChatMessage.Sequence)));
            Assert.True(HasIndex(message, false, nameof(ChatMessage.SessionId), nameof(ChatMessage.IsVisible), nameof(ChatMessage.CreatedAt)));
            Assert.True(HasIndex(booking, true, nameof(BookingContext.SessionId), nameof(BookingContext.ServiceType)));
            Assert.True(booking.FindProperty(nameof(BookingContext.Version))!.IsConcurrencyToken);
        }
    }

    [Fact]
    public void Messages_and_booking_contexts_cascade_with_the_session_while_escalations_and_audit_rows_do_not()
    {
        using var sqlite = Sqlite();

        foreach (var entity in new[] { Entity<ChatMessage>(sqlite), Entity<BookingContext>(sqlite) })
        {
            var key = Assert.Single(entity.GetForeignKeys());
            Assert.Equal(DeleteBehavior.Cascade, key.DeleteBehavior);
            Assert.Equal(typeof(ChatSession), key.PrincipalEntityType.ClrType);
        }

        Assert.Empty(Entity<EscalationRequest>(sqlite).GetForeignKeys());
        Assert.Empty(Entity<AdminAuditLog>(sqlite).GetForeignKeys());
    }

    [Fact]
    public void Column_limits_protect_the_identifier_columns()
    {
        using var postgres = Npgsql();

        Assert.Equal(64, Entity<ChatSession>(postgres).FindProperty(nameof(ChatSession.SessionToken))!.GetMaxLength());
        Assert.Equal(64, Entity<ChatMessage>(postgres).FindProperty(nameof(ChatMessage.MessageId))!.GetMaxLength());
        Assert.Equal(16, Entity<BookingContext>(postgres).FindProperty(nameof(BookingContext.ServiceType))!.GetMaxLength());
        Assert.Null(Entity<ChatMessage>(postgres).FindProperty(nameof(ChatMessage.BlocksJson))!.GetMaxLength());
    }
}
