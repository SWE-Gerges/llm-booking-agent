using Agent.Abstractions;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Persistence;
using Agent.Persistence.Stores;
using Agent.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.PersistenceTests;

public class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private static SessionContext Sample(string id = "sess-abc123") => new()
    {
        SessionId = id,
        State = SessionState.Escalated,
        Locale = "ar",
        RequestedServices = [ServiceType.Hotel, ServiceType.Flight],
        CreatedAt = T0,
        LastActivityAt = T0.AddMinutes(5),
        LastSequence = 42,
        WindowStartSequence = 17,
        RepeatHash = "ABCDEF0123",
        RepeatCount = 2,
    };

    [Fact]
    public async Task A_session_round_trips_every_field()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();
        var session = Sample();

        await store.CreateAsync(session, CancellationToken.None);
        var loaded = await store.FindAsync(session.SessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(AgentJson.Serialize(session), AgentJson.Serialize(loaded));
        Assert.Equal([ServiceType.Hotel, ServiceType.Flight], loaded.RequestedServices);
        Assert.Equal(SessionState.Escalated, loaded.State);
        Assert.Equal((42L, 17L, 2, "ABCDEF0123"), (loaded.LastSequence, loaded.WindowStartSequence, loaded.RepeatCount, loaded.RepeatHash));
        Assert.Empty(loaded.History);
    }

    [Fact]
    public async Task Saving_updates_the_mutable_state_and_ignores_the_history()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();
        var session = Sample();
        await store.CreateAsync(session, CancellationToken.None);

        session.State = SessionState.Active;
        session.RequestedServices = [ServiceType.Flight];
        session.LastActivityAt = T0.AddMinutes(20);
        session.LastSequence = 50;
        session.WindowStartSequence = 30;
        session.RepeatHash = null;
        session.RepeatCount = 0;
        session.History = [new ConversationMessage("m1", StoredMessageRole.User, [new TextBlock("kept in the message store only")], true, T0) { Sequence = 50 }];
        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.FindAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(SessionState.Active, loaded!.State);
        Assert.Equal([ServiceType.Flight], loaded.RequestedServices);
        Assert.Equal(T0.AddMinutes(20), loaded.LastActivityAt);
        Assert.Equal((50L, 30L, 0), (loaded.LastSequence, loaded.WindowStartSequence, loaded.RepeatCount));
        Assert.Null(loaded.RepeatHash);
        Assert.Empty(loaded.History);
    }

    [Fact]
    public async Task An_empty_service_list_round_trips()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();
        var session = Sample();
        session.RequestedServices = [];
        await store.CreateAsync(session, CancellationToken.None);

        Assert.Empty((await store.FindAsync(session.SessionId, CancellationToken.None))!.RequestedServices);
    }

    [Fact]
    public async Task An_unknown_session_is_null_on_find_and_an_error_on_save()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();

        Assert.Null(await store.FindAsync("missing", CancellationToken.None));
        await Assert.ThrowsAsync<SessionNotFoundException>(() => store.SaveAsync(Sample("missing"), CancellationToken.None));
        Assert.False(await store.DeleteAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Session_tokens_are_unique()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();
        await store.CreateAsync(Sample(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(Sample(), CancellationToken.None));

        Assert.True(BookingContextStore.IsUniqueViolation(error));
    }

    [Fact]
    public async Task Deleting_removes_the_session()
    {
        using var h = await DomainHarness.CreateAsync();
        var store = h.Get<ISessionStore>();
        await store.CreateAsync(Sample(), CancellationToken.None);

        Assert.True(await store.DeleteAsync("sess-abc123", CancellationToken.None));

        Assert.Null(await store.FindAsync("sess-abc123", CancellationToken.None));
        Assert.False(await store.DeleteAsync("sess-abc123", CancellationToken.None));
    }
}

public class MessageStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private static async Task<string> NewSessionAsync(DomainHarness h) => (await h.NewSessionAsync()).SessionId;

    private static ConversationMessage Message(long sequence, StoredMessageRole role = StoredMessageRole.User, bool visible = true, string? text = null) =>
        new($"msg-{Guid.NewGuid():N}", role, [new TextBlock(text ?? $"message {sequence}")], visible, T0.AddSeconds(sequence)) { Sequence = sequence };

    [Fact]
    public async Task Full_block_json_round_trips_including_tool_blocks_and_json_input()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();
        var input = AgentJson.Parse("""{"origin":"AMS","nested":{"list":[1,2,{"b":"c"}],"flag":true},"text":"déjà مرحبا"}""");
        var call = new ConversationMessage("m-call", StoredMessageRole.ToolCall, [new TextBlock("let me look"), new ToolUseBlock("toolu_1", "search_flights", input)], false, T0) { Sequence = 1 };
        var result = new ConversationMessage("m-result", StoredMessageRole.ToolResult, [new ToolResultBlock("toolu_1", "<tool_output>{\"ok\":true}</tool_output>", true)], false, T0.AddSeconds(1)) { Sequence = 2 };
        var reply = new ConversationMessage("m-reply", StoredMessageRole.Assistant, [new TextBlock("Found it")], true, T0.AddSeconds(2)) { Sequence = 3 };

        await store.AppendAsync(id, [call, result, reply], CancellationToken.None);
        var loaded = await store.LoadFromAsync(id, 0, CancellationToken.None);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(
            AgentJson.Serialize<IReadOnlyList<ConversationMessage>>([call, result, reply]),
            AgentJson.Serialize<IReadOnlyList<ConversationMessage>>(loaded));
        var use = Assert.IsType<ToolUseBlock>(loaded[0].Blocks[1]);
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("c", use.Input.GetProperty("nested").GetProperty("list")[2].GetProperty("b").GetString());
        Assert.Equal("déjà مرحبا", use.Input.GetProperty("text").GetString());
        var toolResult = Assert.IsType<ToolResultBlock>(Assert.Single(loaded[1].Blocks));
        Assert.True(toolResult.IsError);
        Assert.Equal((StoredMessageRole.ToolCall, StoredMessageRole.ToolResult, StoredMessageRole.Assistant), (loaded[0].Role, loaded[1].Role, loaded[2].Role));
        Assert.Equal((false, false, true), (loaded[0].IsVisible, loaded[1].IsVisible, loaded[2].IsVisible));
        Assert.Equal(("m-call", 1L, T0), (loaded[0].MessageId, loaded[0].Sequence, loaded[0].CreatedAt));
    }

    [Fact]
    public async Task Messages_load_in_sequence_order_regardless_of_append_order()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();

        await store.AppendAsync(id, [Message(3)], CancellationToken.None);
        await store.AppendAsync(id, [Message(1), Message(2)], CancellationToken.None);

        Assert.Equal([1L, 2L, 3L], (await store.LoadFromAsync(id, 0, CancellationToken.None)).Select(m => m.Sequence));
    }

    [Fact]
    public async Task Loading_from_a_sequence_returns_only_the_window()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();
        await store.AppendAsync(id, Enumerable.Range(1, 6).Select(i => Message(i)).ToList(), CancellationToken.None);

        var window = await store.LoadFromAsync(id, 4, CancellationToken.None);

        Assert.Equal([4L, 5L, 6L], window.Select(m => m.Sequence));
        Assert.Empty(await store.LoadFromAsync(id, 7, CancellationToken.None));
    }

    [Fact]
    public async Task Visible_history_returns_the_latest_visible_messages_oldest_first()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();
        await store.AppendAsync(
            id,
            [
                Message(1, visible: true, text: "u1"),
                Message(2, StoredMessageRole.ToolCall, visible: false),
                Message(3, StoredMessageRole.ToolResult, visible: false),
                Message(4, StoredMessageRole.Assistant, visible: true, text: "a1"),
                Message(5, visible: true, text: "u2"),
                Message(6, StoredMessageRole.Assistant, visible: true, text: "a2"),
            ],
            CancellationToken.None);

        var lastTwo = await store.GetVisibleAsync(id, 2, CancellationToken.None);
        var all = await store.GetVisibleAsync(id, 50, CancellationToken.None);

        Assert.Equal(["u2", "a2"], lastTwo.Select(m => m.VisibleText));
        Assert.Equal(["u1", "a1", "u2", "a2"], all.Select(m => m.VisibleText));
        Assert.All(all, m => Assert.True(m.IsVisible));
    }

    [Fact]
    public async Task Sessions_do_not_see_each_others_messages()
    {
        using var h = await DomainHarness.CreateAsync();
        var first = await NewSessionAsync(h);
        var second = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();

        await store.AppendAsync(first, [Message(1, text: "mine")], CancellationToken.None);
        await store.AppendAsync(second, [Message(1, text: "theirs")], CancellationToken.None);

        Assert.Equal("mine", Assert.Single(await store.LoadFromAsync(first, 0, CancellationToken.None)).VisibleText);
        Assert.Equal("theirs", Assert.Single(await store.GetVisibleAsync(second, 10, CancellationToken.None)).VisibleText);
    }

    [Fact]
    public async Task A_duplicate_sequence_in_one_session_is_a_unique_violation()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();
        await store.AppendAsync(id, [Message(1)], CancellationToken.None);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.AppendAsync(id, [Message(1)], CancellationToken.None));

        Assert.True(BookingContextStore.IsUniqueViolation(error));
        Assert.Single(await store.LoadFromAsync(id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_duplicate_message_id_is_rejected_and_the_batch_is_not_partially_stored()
    {
        using var h = await DomainHarness.CreateAsync();
        var first = await NewSessionAsync(h);
        var second = await NewSessionAsync(h);
        var store = h.Get<IMessageStore>();
        var shared = Message(1) with { MessageId = "same-id" };
        await store.AppendAsync(first, [shared], CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.AppendAsync(second, [Message(1), Message(2) with { MessageId = "same-id" }], CancellationToken.None));

        Assert.Empty(await store.LoadFromAsync(second, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Appending_to_an_unknown_session_is_an_error()
    {
        using var h = await DomainHarness.CreateAsync();

        await Assert.ThrowsAsync<SessionNotFoundException>(() => h.Get<IMessageStore>().AppendAsync("missing", [Message(1)], CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_session_cascades_to_its_messages_and_booking_contexts_only()
    {
        using var h = await DomainHarness.CreateAsync();
        var doomed = await h.NewSessionAsync();
        var survivor = await h.NewSessionAsync();
        var messages = h.Get<IMessageStore>();
        await messages.AppendAsync(doomed.SessionId, [Message(1), Message(2)], CancellationToken.None);
        await messages.AppendAsync(survivor.SessionId, [Message(1)], CancellationToken.None);
        await BookingSeed.SeedAsync(h, doomed, ServiceType.Flight);
        await BookingSeed.SeedAsync(h, doomed, ServiceType.Hotel);
        await BookingSeed.SeedAsync(h, survivor, ServiceType.Flight);

        Assert.True(await h.Get<ISessionStore>().DeleteAsync(doomed.SessionId, CancellationToken.None));

        await using var db = await h.Get<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.Messages.CountAsync());
        Assert.Equal(1, await db.BookingContexts.CountAsync());
        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.Empty(await messages.LoadFromAsync(doomed.SessionId, 0, CancellationToken.None));
        Assert.Empty(await h.Contexts.ListAsync(doomed.SessionId, CancellationToken.None));
        Assert.Single(await messages.LoadFromAsync(survivor.SessionId, 0, CancellationToken.None));
    }
}
