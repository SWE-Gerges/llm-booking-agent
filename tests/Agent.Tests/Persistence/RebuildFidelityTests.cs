using Agent.Abstractions;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Conversation;
using Agent.Llm.Scripted;
using Agent.Tests.Support;

namespace Agent.Tests.PersistenceTests;

/// <summary>
/// Runs the real orchestrator and tools over the real EF stores, then proves that a session rebuilt after a cache miss is
/// the same session: not just the visible text, but tool blocks, requested services, guard counters, state and window.
/// </summary>
public class RebuildFidelityTests
{
    private const string Script = """
        { "steps": [
          { "toolUse": { "name": "search_flights", "input": { "origin": "AMS", "destination": "LIS", "departureDate": "{{today+30}}" } } },
          { "text": "Found {{search_flights.count}} flights." },
          { "toolUse": { "name": "recheck_flight_price", "input": { "offerId": "{{search_flights.offers[0].id}}" } } },
          { "text": "Price {{recheck_flight_price.total.amount}}." },
          { "toolUse": { "name": "get_branded_fares", "input": { "offerId": "{{search_flights.offers[0].id}}" } } },
          { "text": "Fares listed." },
          { "toolUse": { "name": "search_hotels", "input": { "destination": "Lisbon", "checkIn": "{{today+30}}", "checkOut": "{{today+33}}" } } },
          { "text": "Found {{search_hotels.count}} hotels." },
          { "toolUse": { "name": "get_payment_methods", "input": {} } },
          { "text": "Methods listed." },
          { "toolUse": { "name": "escalate_to_human", "input": { "reason": "The user asked for a person." } } },
          { "text": "I passed this to a human agent." }
        ] }
        """;

    private static readonly string[] Messages =
    [
        "find flights", "check the price", "show fares", "find hotels", "payment options",
    ];

    private static Task<DomainHarness> CreateAsync() => DomainHarness.CreateAsync(new Dictionary<string, string?>
    {
        ["Agent:HistoryWindow"] = "12",
        ["Agent:TrimChunk"] = "4",
    });

    private static async Task RunTurnsAsync(DomainHarness h, string sessionId, IEnumerable<string> messages)
    {
        h.Llm.Default = (request, ct) => new ScriptedLlmClient(ScriptDefinition.Parse(Script), h.Clock).SendAsync(request, ct);
        foreach (var message in messages)
        {
            var reply = await h.Get<IChatOrchestrator>().HandleMessageAsync(sessionId, message, CancellationToken.None);
            Assert.NotEqual(ChatOutcome.Failed, reply.Outcome);
        }
    }

    private static string Json(SessionContext session) => AgentJson.Serialize(session);

    [Fact]
    public async Task A_rebuilt_session_equals_the_cached_one_after_tool_calls_trimming_escalation_and_repeats()
    {
        using var h = await CreateAsync();
        var id = (await h.Get<ConversationService>().CreateAsync("en", CancellationToken.None)).SessionId;

        await RunTurnsAsync(h, id, [.. Messages, "human please", "go on", "go on"]);
        var cached = await h.Get<IChatCache>().GetAsync(id, CancellationToken.None);
        await h.Get<IChatCache>().RemoveAsync(id, CancellationToken.None);
        var rebuilt = await h.Get<ConversationService>().LoadAsync(id, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Equal(Json(cached), Json(rebuilt));

        // The scenario really exercised what the rebuild has to restore.
        Assert.True(rebuilt.WindowStartSequence > 1, "history should have been trimmed");
        var stored = await h.Get<IMessageStore>().LoadFromAsync(id, 0, CancellationToken.None);
        Assert.True(stored.Count > rebuilt.History.Count, "the database keeps messages the prompt window dropped");
        Assert.Equal(rebuilt.WindowStartSequence, rebuilt.History[0].Sequence);
        Assert.Equal(StoredMessageRole.User, rebuilt.History[0].Role);
        Assert.Contains(rebuilt.History, m => m.Blocks.OfType<ToolUseBlock>().Any());
        Assert.Contains(rebuilt.History, m => m.Blocks.OfType<ToolResultBlock>().Any());
        Assert.Equal([ServiceType.Flight, ServiceType.Hotel], rebuilt.RequestedServices);
        Assert.Equal(SessionState.Escalated, rebuilt.State);
        Assert.Equal(2, rebuilt.RepeatCount);
        Assert.False(string.IsNullOrEmpty(rebuilt.RepeatHash));
        Assert.Equal(stored[^1].Sequence, rebuilt.LastSequence);
    }

    [Fact]
    public async Task A_rebuild_without_trimming_restores_the_full_history_with_tool_blocks()
    {
        using var h = await DomainHarness.CreateAsync();
        var id = (await h.Get<ConversationService>().CreateAsync("en", CancellationToken.None)).SessionId;

        await RunTurnsAsync(h, id, Messages.Take(3));
        var cached = await h.Get<IChatCache>().GetAsync(id, CancellationToken.None);
        await h.Get<IChatCache>().RemoveAsync(id, CancellationToken.None);
        var rebuilt = await h.Get<ConversationService>().LoadAsync(id, CancellationToken.None);

        Assert.Equal(Json(cached!), Json(rebuilt));
        Assert.Equal(12, rebuilt.History.Count);
        Assert.Equal(1, rebuilt.WindowStartSequence);
        Assert.Equal(6, rebuilt.History.Count(m => m.Blocks.Any(b => b is not TextBlock)));
        Assert.Equal(6, rebuilt.History.Count(m => !m.IsVisible));
    }

    [Fact]
    public async Task The_prompt_after_a_cache_miss_starts_with_exactly_the_history_that_was_cached()
    {
        using var h = await CreateAsync();
        var id = (await h.Get<ConversationService>().CreateAsync("en", CancellationToken.None)).SessionId;
        await RunTurnsAsync(h, id, Messages);
        var cachedBefore = await h.Get<IChatCache>().GetAsync(id, CancellationToken.None);
        await h.Get<IChatCache>().RemoveAsync(id, CancellationToken.None);
        var calls = h.Llm.Requests.Count;

        await h.Get<IChatOrchestrator>().HandleMessageAsync(id, "human please", CancellationToken.None);

        var firstRequestOfTheTurn = h.Llm.Requests[calls];
        var expectedPrefix = cachedBefore!.History.Where(m => m.Role != StoredMessageRole.Notice).Select(m => m.ToLlmMessage()).ToList();
        Assert.Equal(expectedPrefix.Count + 1, firstRequestOfTheTurn.Messages.Count);
        Assert.Equal(
            AgentJson.Serialize<IReadOnlyList<LlmMessage>>(expectedPrefix),
            AgentJson.Serialize<IReadOnlyList<LlmMessage>>(firstRequestOfTheTurn.Messages.Take(expectedPrefix.Count).ToList()));
        Assert.Equal("human please", ((TextBlock)firstRequestOfTheTurn.Messages[^1].Content[0]).Text);
        Assert.Contains("flight, hotel", firstRequestOfTheTurn.SystemVolatile, StringComparison.Ordinal);
        Assert.True(cachedBefore.WindowStartSequence > 1);
    }

    [Fact]
    public async Task A_rebuilt_session_keeps_working_and_stays_consistent_with_the_database()
    {
        using var h = await CreateAsync();
        var id = (await h.Get<ConversationService>().CreateAsync("en", CancellationToken.None)).SessionId;
        await RunTurnsAsync(h, id, Messages.Take(4));
        await h.Get<IChatCache>().RemoveAsync(id, CancellationToken.None);

        await RunTurnsAsync(h, id, Messages.Skip(4));
        var cached = await h.Get<IChatCache>().GetAsync(id, CancellationToken.None);
        await h.Get<IChatCache>().RemoveAsync(id, CancellationToken.None);
        var rebuilt = await h.Get<ConversationService>().LoadAsync(id, CancellationToken.None);

        Assert.Equal(Json(cached!), Json(rebuilt));
        var sequences = (await h.Get<IMessageStore>().LoadFromAsync(id, 0, CancellationToken.None)).Select(m => m.Sequence).ToList();
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
    }
}
