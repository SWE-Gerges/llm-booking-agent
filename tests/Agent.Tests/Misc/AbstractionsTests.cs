using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;

namespace Agent.Tests.Misc;

public sealed class ContentBlockSerializationTests
{
    [Fact]
    public void Blocks_round_trip_through_the_polymorphic_json_format()
    {
        IReadOnlyList<ContentBlock> blocks =
        [
            new TextBlock("hello"),
            new ToolUseBlock("t1", "search", AgentJson.Parse("""{"origin":"AMS","adults":2}""")),
            new ToolResultBlock("t1", "{\"ok\":true}", IsError: true),
        ];

        var json = AgentJson.Serialize(blocks);
        var back = AgentJson.Deserialize<List<ContentBlock>>(json)!;

        Assert.Contains("\"type\":\"text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"tool_use\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"tool_result\"", json, StringComparison.Ordinal);
        Assert.Equal("hello", Assert.IsType<TextBlock>(back[0]).Text);
        var use = Assert.IsType<ToolUseBlock>(back[1]);
        Assert.Equal("t1", use.Id);
        Assert.Equal("search", use.Name);
        Assert.Equal(2, use.Input.GetProperty("adults").GetInt32());
        var result = Assert.IsType<ToolResultBlock>(back[2]);
        Assert.True(result.IsError);
        Assert.Equal("{\"ok\":true}", result.Content);
        Assert.Equal(json, AgentJson.Serialize(back));
    }

    [Fact]
    public void Conversation_messages_keep_their_sequence_and_map_to_provider_roles()
    {
        var now = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        var message = new ConversationMessage("m1", StoredMessageRole.ToolResult, [new ToolResultBlock("t1", "{}")], false, now) { Sequence = 7 };

        var back = AgentJson.Deserialize<ConversationMessage>(AgentJson.Serialize(message))!;

        Assert.Equal(7, back.Sequence);
        Assert.Equal(StoredMessageRole.ToolResult, back.Role);
        Assert.False(back.IsVisible);
        Assert.Equal(now, back.CreatedAt);
        Assert.Equal(LlmRole.User, back.LlmRole);
        Assert.Equal(LlmRole.User, back.ToLlmMessage().Role);
    }

    [Theory]
    [InlineData(StoredMessageRole.User, LlmRole.User)]
    [InlineData(StoredMessageRole.ToolResult, LlmRole.User)]
    [InlineData(StoredMessageRole.Assistant, LlmRole.Assistant)]
    [InlineData(StoredMessageRole.ToolCall, LlmRole.Assistant)]
    public void Stored_roles_map_to_provider_roles(StoredMessageRole stored, LlmRole expected)
    {
        var message = new ConversationMessage("m", stored, [new TextBlock("x")], true, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, message.LlmRole);
    }

    [Fact]
    public void Visible_text_joins_only_the_text_blocks()
    {
        var message = new ConversationMessage(
            "m",
            StoredMessageRole.Assistant,
            [new TextBlock("a"), new ToolUseBlock("t", "n", AgentJson.EmptyObject), new TextBlock("b")],
            true,
            DateTimeOffset.UnixEpoch);

        Assert.Equal("ab", message.VisibleText);
    }

    [Fact]
    public void Session_context_round_trips_with_all_turn_relevant_flags()
    {
        var session = new SessionContext
        {
            SessionId = "abc",
            State = SessionState.Escalated,
            Locale = "ar",
            RequestedServices = [ServiceType.Flight, ServiceType.Hotel],
            CreatedAt = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
            LastActivityAt = new DateTimeOffset(2026, 3, 10, 9, 5, 0, TimeSpan.Zero),
            LastSequence = 42,
            WindowStartSequence = 35,
            RepeatHash = "HASH",
            RepeatCount = 2,
            History = [new ConversationMessage("m", StoredMessageRole.User, [new TextBlock("hi")], true, DateTimeOffset.UnixEpoch) { Sequence = 35 }],
        };

        var json = AgentJson.Serialize(session);
        var back = AgentJson.Deserialize<SessionContext>(json)!;

        Assert.Equal(json, AgentJson.Serialize(back));
        Assert.Equal(SessionState.Escalated, back.State);
        Assert.Equal([ServiceType.Flight, ServiceType.Hotel], back.RequestedServices);
        Assert.Equal(35, back.WindowStartSequence);
        Assert.Equal(42, back.LastSequence);
        Assert.Equal("HASH", back.RepeatHash);
        Assert.Equal(2, back.RepeatCount);
        Assert.Contains("\"state\":\"escalated\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_services_are_not_duplicated()
    {
        var session = new SessionContext { SessionId = "s" };

        session.AddRequestedService(ServiceType.Hotel);
        session.AddRequestedService(ServiceType.Hotel);
        session.AddRequestedService(ServiceType.Flight);

        Assert.Equal([ServiceType.Hotel, ServiceType.Flight], session.RequestedServices);
    }

    [Fact]
    public void Parsed_elements_stay_valid_after_the_source_document_is_gone()
    {
        var element = AgentJson.Parse("""{"a":{"b":[1,2,3]}}""");

        Assert.Equal(3, element.GetProperty("a").GetProperty("b").GetArrayLength());
        Assert.Equal("{}", AgentJson.EmptyObject.GetRawText());
    }

    [Fact]
    public void Null_properties_are_omitted_and_names_are_camel_case()
    {
        var json = AgentJson.Serialize(new ToolResult { Content = "{}", State = null, ModelNote = null });

        Assert.DoesNotContain("state", json, StringComparison.Ordinal);
        Assert.Contains("\"content\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isError\":false", json, StringComparison.Ordinal);
    }
}

public sealed class ToolContractTests
{
    [Fact]
    public void Error_results_carry_a_json_error_object_and_the_error_flag()
    {
        var result = ToolResult.Error("Something \"quoted\" failed.");

        Assert.True(result.IsError);
        Assert.Null(result.State);
        using var document = JsonDocument.Parse(result.Content);
        Assert.Equal("Something \"quoted\" failed.", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Ok_results_carry_state_and_model_fields()
    {
        var result = ToolResult.Ok("""{"a":1}""", state: "s", modelContent: "short", modelNote: "note");

        Assert.False(result.IsError);
        Assert.Equal("s", result.State);
        Assert.Equal("short", result.ModelContent);
        Assert.Equal("note", result.ModelNote);
        Assert.Equal("""{"a":1}""", result.Content);
    }

    [Fact]
    public void Policy_decisions()
    {
        Assert.True(PolicyDecision.Allow.IsAllowed);
        var denied = PolicyDecision.Deny("because");
        Assert.False(denied.IsAllowed);
        Assert.Equal("because", denied.MessageForModel);
    }

    [Fact]
    public void Turn_state_counts_searches_and_remembers_whether_the_first_carried_an_assumption()
    {
        var turn = new TurnState();

        turn.RecordCall("lookup", isSearch: false, carriedAssumption: true);
        Assert.Equal(0, turn.SearchCount);
        Assert.False(turn.FirstSearchCarriedAssumption);

        turn.RecordCall("search", isSearch: true, carriedAssumption: true);
        Assert.Equal(1, turn.SearchCount);
        Assert.True(turn.FirstSearchCarriedAssumption);
        Assert.False(turn.CorrectedSearchUsed);

        turn.RecordCall("search", isSearch: true, carriedAssumption: false);
        Assert.Equal(2, turn.SearchCount);
        Assert.True(turn.FirstSearchCarriedAssumption);
        Assert.True(turn.CorrectedSearchUsed);
        Assert.Equal(["lookup", "search", "search"], turn.ToolsCalled);
    }

    [Fact]
    public void Tool_context_exposes_the_session_id()
    {
        var session = new SessionContext { SessionId = "sid" };

        var context = new ToolContext(session, new TurnState());

        Assert.Equal("sid", context.SessionId);
        Assert.Same(session, context.Session);
    }

    [Fact]
    public void An_empty_booking_context_is_bound_to_a_session_and_service()
    {
        var context = BookingContext.Empty("sid", ServiceType.Hotel);

        Assert.Equal("sid", context.SessionId);
        Assert.Equal(ServiceType.Hotel, context.ServiceType);
        Assert.Null(context.ConfirmedPrice);
        Assert.Null(context.IdempotencyKey);
        Assert.Equal(0, context.Version);
    }

    [Fact]
    public void Llm_exception_exposes_status_and_body_excerpt()
    {
        var error = new LlmApiException("boom", 429, "slow down");

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("slow down", error.BodyExcerpt);
        Assert.Equal("boom", error.Message);
    }
}
