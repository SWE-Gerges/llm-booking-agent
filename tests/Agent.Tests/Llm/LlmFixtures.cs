using System.Text.Json.Nodes;
using Agent.Abstractions;
using Agent.Abstractions.Llm;

namespace Agent.Tests.LlmClients;

/// <summary>Recorded-style provider bodies and the neutral requests that the golden tests translate.</summary>
internal static class LlmFixtures
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Llm", "Fixtures", relativePath));

    public static JsonNode Node(string relativePath) => JsonNode.Parse(Read(relativePath))!;

    public const string SearchFlightsSchema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{
          "origin":{"type":"string","pattern":"^[A-Za-z]{3}$","description":"Departure airport code"},
          "adults":{"type":"integer","minimum":1,"maximum":9},
          "cabin":{"type":"string","enum":["economy","business"]},
          "fares":{"type":"array","items":{"type":"string"},"maxItems":3},
          "maxPrice":{"type":"number","exclusiveMinimum":0,"default":100},
          "note":{"type":["string","null"]}
        },"required":["origin"],"additionalProperties":false}
        """;

    public const string EmptySchema = """{"type":"object","properties":{},"additionalProperties":false}""";

    public static IReadOnlyList<LlmToolDefinition> Tools() =>
    [
        new LlmToolDefinition("search_flights", "Search flight offers.", AgentJson.Parse(SearchFlightsSchema)),
        new LlmToolDefinition("get_booking_status", "Report booking status.", AgentJson.Parse(EmptySchema)),
    ];

    /// <summary>One user message, two tools, system text in two blocks.</summary>
    public static LlmChatRequest BasicRequest(double? temperature = null) => new()
    {
        SystemStable = "You are a booking assistant.",
        SystemVolatile = "Today is 2030-01-02.",
        Messages = [LlmMessage.User("Find flights from AMS to LIS")],
        Tools = Tools(),
        MaxTokens = 512,
        Temperature = temperature,
    };

    /// <summary>Parallel tool calls, an error result, two consecutive user messages and a closing question.</summary>
    public static LlmChatRequest HistoryRequest() => new()
    {
        SystemStable = "You are a booking assistant.",
        SystemVolatile = "Today is 2030-01-02.",
        Messages =
        [
            LlmMessage.User("Find flights"),
            new LlmMessage(
                LlmRole.Assistant,
                [
                    new TextBlock("Searching."),
                    new ToolUseBlock("call-1", "search_flights", AgentJson.Parse("""{"origin":"AMS"}""")),
                    new ToolUseBlock("call-2", "get_booking_status", AgentJson.Parse("{}")),
                ]),
            new LlmMessage(
                LlmRole.User,
                [
                    new ToolResultBlock("call-1", "{\"count\":2}"),
                    new ToolResultBlock("call-2", "{\"error\":\"none\"}", IsError: true),
                ]),
            LlmMessage.User("Thanks"),
            LlmMessage.Assistant("You are welcome"),
            LlmMessage.User("Cheapest please"),
        ],
        Tools = Tools(),
        MaxTokens = 512,
    };
}
