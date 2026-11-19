using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Llm.Scripted;
using Agent.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.LlmClients;

public sealed class ScriptedLlmClientTests
{
    private const string Script = """
        {"steps":[
          {"toolUse":{"name":"search","input":{"date":"{{today+30}}","back":"{{today}}","label":"trip {{today+1}}","adults":2}}},
          {"text":"Found {{search.items[0].id}} of {{prior.total}}"},
          {"toolUse":{"name":"book","input":{"id":"{{search.items[1].id}}","count":"{{search.total}}","ok":"{{search.flag}}"}}},
          {"text":"done {{book.ref}}"}
        ]}
        """;

    private static readonly DateTimeOffset Start = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private static ScriptedLlmClient Create(string script = Script) =>
        new(ScriptDefinition.Parse(script), new FakeTimeProvider(Start));

    private static LlmChatRequest Req(IReadOnlyList<LlmMessage> messages) =>
        new() { SystemStable = "s", SystemVolatile = "v", Messages = messages };

    private static LlmMessage ToolCallMessage(int step, string name) =>
        new(LlmRole.Assistant, [new ToolUseBlock($"scripted-{step}", name, AgentJson.EmptyObject)]);

    private static LlmMessage ToolResultMessage(int step, string json) =>
        new(LlmRole.User, [new ToolResultBlock($"scripted-{step}", $"<tool_output>\n{json}\n</tool_output>")]);

    private const string SearchResult = """{"items":[{"id":"F1"},{"id":"F2"}],"total":7,"flag":true}""";

    private static List<LlmMessage> ConversationAfterSearch() =>
        [LlmMessage.User("hi"), ToolCallMessage(0, "search"), ToolResultMessage(0, SearchResult)];

    [Fact]
    public async Task First_step_is_a_tool_call_with_a_scripted_id_and_rendered_dates()
    {
        var result = await Create().SendAsync(Req([LlmMessage.User("hi")]), default);

        Assert.Equal(LlmStopReason.ToolUse, result.StopReason);
        var use = Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal("scripted-0", use.Id);
        Assert.Equal("search", use.Name);
        Assert.Equal("2026-04-09", use.Input.GetProperty("date").GetString());
        Assert.Equal("2026-03-10", use.Input.GetProperty("back").GetString());
        Assert.Equal("trip 2026-03-11", use.Input.GetProperty("label").GetString());
        Assert.Equal(2, use.Input.GetProperty("adults").GetInt32());
    }

    [Fact]
    public async Task A_text_step_ends_the_turn_and_reads_values_from_earlier_tool_results()
    {
        var result = await Create().SendAsync(Req(ConversationAfterSearch()), default);

        Assert.Equal(LlmStopReason.EndTurn, result.StopReason);
        Assert.Equal("Found F1 of 7", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
    }

    [Fact]
    public async Task Whole_placeholders_that_resolve_to_numbers_or_booleans_keep_their_json_type()
    {
        List<LlmMessage> messages = [.. ConversationAfterSearch(), LlmMessage.Assistant("Found F1 of 7"), LlmMessage.User("book it")];

        var result = await Create().SendAsync(Req(messages), default);

        var use = Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal("scripted-2", use.Id);
        Assert.Equal("F2", use.Input.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Number, use.Input.GetProperty("count").ValueKind);
        Assert.Equal(7, use.Input.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.True, use.Input.GetProperty("ok").ValueKind);
    }

    [Fact]
    public async Task Tool_name_placeholders_find_the_latest_result_of_that_tool()
    {
        List<LlmMessage> messages =
        [
            .. ConversationAfterSearch(),
            LlmMessage.Assistant("Found F1 of 7"),
            LlmMessage.User("book it"),
            ToolCallMessage(2, "book"),
            ToolResultMessage(2, """{"ref":"BK-1"}"""),
        ];

        var result = await Create().SendAsync(Req(messages), default);

        Assert.Equal("done BK-1", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text);
    }

    [Fact]
    public async Task Once_the_script_is_exhausted_a_clear_message_is_returned()
    {
        List<LlmMessage> messages =
        [
            .. ConversationAfterSearch(),
            LlmMessage.Assistant("Found F1 of 7"),
            LlmMessage.User("book it"),
            ToolCallMessage(2, "book"),
            ToolResultMessage(2, """{"ref":"BK-1"}"""),
            LlmMessage.Assistant("done BK-1"),
            LlmMessage.User("anything else?"),
        ];

        var result = await Create().SendAsync(Req(messages), default);

        Assert.Contains("no further steps", Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_client_is_stateless_so_the_same_request_gives_the_same_answer_every_time()
    {
        var client = Create();
        var request = Req(ConversationAfterSearch());

        var first = await client.SendAsync(request, default);
        var second = await client.SendAsync(request, default);

        Assert.Equal(AgentJson.Serialize(first), AgentJson.Serialize(second));
    }

    [Fact]
    public async Task Interleaved_conversations_on_one_instance_do_not_interfere()
    {
        var client = Create();
        var conversationA = Req([LlmMessage.User("hi from a")]);
        var conversationB = Req(ConversationAfterSearch());

        var a1 = await client.SendAsync(conversationA, default);
        var b1 = await client.SendAsync(conversationB, default);
        var a2 = await client.SendAsync(conversationA, default);
        var b2 = await client.SendAsync(conversationB, default);

        Assert.Equal("scripted-0", Assert.IsType<ToolUseBlock>(Assert.Single(a1.Blocks)).Id);
        Assert.Equal("scripted-0", Assert.IsType<ToolUseBlock>(Assert.Single(a2.Blocks)).Id);
        Assert.Equal("Found F1 of 7", Assert.IsType<TextBlock>(Assert.Single(b1.Blocks)).Text);
        Assert.Equal("Found F1 of 7", Assert.IsType<TextBlock>(Assert.Single(b2.Blocks)).Text);
    }

    [Fact]
    public async Task Text_only_scripts_advance_by_the_number_of_assistant_replies()
    {
        var client = Create("""{"steps":[{"text":"one"},{"text":"two"},{"text":"three"}]}""");

        var first = await client.SendAsync(Req([LlmMessage.User("a")]), default);
        var second = await client.SendAsync(Req([LlmMessage.User("a"), LlmMessage.Assistant("one"), LlmMessage.User("b")]), default);
        var third = await client.SendAsync(
            Req([LlmMessage.User("a"), LlmMessage.Assistant("one"), LlmMessage.User("b"), LlmMessage.Assistant("two"), LlmMessage.User("c")]),
            default);

        Assert.Equal(["one", "two", "three"], new[] { first, second, third }.Select(r => ((TextBlock)r.Blocks[0]).Text));
    }

    [Fact]
    public async Task A_placeholder_without_a_matching_tool_result_is_an_error()
    {
        var client = Create("""{"steps":[{"text":"{{nosuchtool.x}}"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.SendAsync(Req([LlmMessage.User("hi")]), default));
    }

    [Theory]
    [InlineData("{{prior.missing}}")]
    [InlineData("{{prior.items[9].id}}")]
    [InlineData("{{prior.total.deeper}}")]
    public async Task A_placeholder_that_does_not_resolve_in_the_tool_result_is_an_error(string placeholder)
    {
        var client = Create($$$"""{"steps":[{"toolUse":{"name":"search"}},{"text":"{{{placeholder}}}"}]}""");
        var request = Req(ConversationAfterSearch());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.SendAsync(request, default));

        Assert.Contains("placeholder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tool_step_without_input_sends_an_empty_object()
    {
        var client = Create("""{"steps":[{"toolUse":{"name":"ping"}}]}""");

        var result = await client.SendAsync(Req([LlmMessage.User("hi")]), default);

        var use = Assert.IsType<ToolUseBlock>(Assert.Single(result.Blocks));
        Assert.Equal("{}", use.Input.GetRawText());
    }

    [Theory]
    [InlineData("""{"steps":[]}""")]
    [InlineData("""{"steps":[{}]}""")]
    [InlineData("""{"steps":[{"text":"a","toolUse":{"name":"x"}}]}""")]
    [InlineData("""{"steps":[{"toolUse":{"name":""}}]}""")]
    [InlineData("""{"steps":[{"toolUse":{}}]}""")]
    public void Invalid_scripts_are_rejected_at_load_time(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ScriptDefinition.Parse(json));
    }

    [Fact]
    public void Scripts_that_are_not_json_are_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => ScriptDefinition.Parse("not json"));
    }

    [Fact]
    public void A_valid_script_parses_into_steps()
    {
        var script = ScriptDefinition.Parse(Script);

        Assert.Equal(4, script.Steps.Count);
        Assert.NotNull(script.Steps[0].ToolUse);
        Assert.NotNull(script.Steps[1].Text);
    }
}
