using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agent.Abstractions.Llm;

namespace Agent.Llm.Scripted;

/// <summary>One scripted model response: either plain text or a single tool call.</summary>
public sealed class ScriptStep
{
    public string? Text { get; set; }

    public ScriptToolUse? ToolUse { get; set; }
}

public sealed class ScriptToolUse
{
    public string Name { get; set; } = string.Empty;

    public JsonElement Input { get; set; }
}

public sealed class ScriptDefinition
{
    public List<ScriptStep> Steps { get; set; } = [];

    public static ScriptDefinition Parse(string json)
    {
        var script = JsonSerializer.Deserialize<ScriptDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The script is empty.");
        script.Validate();
        return script;
    }

    private void Validate()
    {
        if (Steps.Count == 0)
        {
            throw new InvalidOperationException("The script must contain at least one step.");
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if ((step.Text is null) == (step.ToolUse is null))
            {
                throw new InvalidOperationException($"Script step {i} must define exactly one of 'text' or 'toolUse'.");
            }

            if (step.ToolUse is { Name.Length: 0 })
            {
                throw new InvalidOperationException($"Script step {i} has a toolUse without a name.");
            }
        }
    }
}

/// <summary>
/// Deterministic model stand-in driven by a JSON script. It keeps no state: the step to play is derived from the
/// conversation it receives (scripted tool calls carry their step index in the tool-use id), so it is safe for
/// concurrent sessions and survives a cache rebuild. Text steps end a turn; tool steps request a tool.
/// Placeholders: {{today}}, {{today+N}}, {{prior.path}} (latest tool result) and {{toolName.path}}.
/// </summary>
public sealed partial class ScriptedLlmClient(ScriptDefinition script, TimeProvider clock) : ILlmClient
{
    private const string IdPrefix = "scripted-";

    public Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var cursor = CursorFor(request.Messages);
        if (cursor >= script.Steps.Count)
        {
            return Task.FromResult(Reply(new TextBlock("The demo script has no further steps for this conversation.")));
        }

        var step = script.Steps[cursor];
        var results = ToolResults.From(request.Messages);

        if (step.Text is { } text)
        {
            return Task.FromResult(Reply(new TextBlock(RenderString(text, results))));
        }

        var tool = step.ToolUse!;
        var input = RenderNode(JsonNode.Parse(tool.Input.ValueKind == JsonValueKind.Undefined ? "{}" : tool.Input.GetRawText()), results);
        var block = new ToolUseBlock($"{IdPrefix}{cursor}", tool.Name, JsonSerializer.SerializeToElement(input));
        return Task.FromResult(new LlmChatResult([block], LlmStopReason.ToolUse, LlmUsage.None));
    }

    private static int CursorFor(IReadOnlyList<LlmMessage> messages)
    {
        var lastToolIndex = -1;
        var lastStep = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != LlmRole.Assistant)
            {
                continue;
            }

            foreach (var use in messages[i].Content.OfType<ToolUseBlock>())
            {
                if (use.Id.StartsWith(IdPrefix, StringComparison.Ordinal)
                    && int.TryParse(use.Id.AsSpan(IdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var step))
                {
                    lastToolIndex = i;
                    lastStep = step;
                }
            }
        }

        if (lastToolIndex < 0)
        {
            return messages.Count(m => m.Role == LlmRole.Assistant);
        }

        var textOnlyAfter = messages.Skip(lastToolIndex + 1).Count(m => m.Role == LlmRole.Assistant && !m.Content.OfType<ToolUseBlock>().Any());
        return lastStep + 1 + textOnlyAfter;
    }

    private static LlmChatResult Reply(TextBlock text) => new([text], LlmStopReason.EndTurn, LlmUsage.None);

    private JsonNode? RenderNode(JsonNode? node, ToolResults results)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    obj[key] = RenderNode(obj[key], results);
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = RenderNode(array[i], results);
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                var whole = WholePlaceholder().Match(text);
                if (whole.Success)
                {
                    var resolved = Resolve(whole.Groups[1].Value, results);
                    return resolved.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                        ? JsonNode.Parse(resolved.GetRawText())
                        : JsonValue.Create(Stringify(resolved));
                }

                return JsonValue.Create(RenderString(text, results));
            default:
                return node;
        }
    }

    private string RenderString(string text, ToolResults results) =>
        Placeholder().Replace(text, match => Stringify(Resolve(match.Groups[1].Value, results)));

    private JsonElement Resolve(string expression, ToolResults results)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var dateMatch = TodayExpression().Match(expression);
        if (dateMatch.Success)
        {
            var offset = dateMatch.Groups[1].Success ? int.Parse(dateMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return JsonSerializer.SerializeToElement(today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var dot = expression.IndexOf('.', StringComparison.Ordinal);
        var source = dot < 0 ? expression : expression[..dot];
        var path = dot < 0 ? string.Empty : expression[(dot + 1)..];
        var root = results.Find(source)
            ?? throw new InvalidOperationException($"Script placeholder '{{{{{expression}}}}}' has no tool result to read from.");
        return Navigate(root, path, expression);
    }

    private static JsonElement Navigate(JsonElement root, string path, string expression)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    throw new InvalidOperationException($"Script placeholder '{{{{{expression}}}}}' did not resolve at '{name}'.");
                }
            }

            while (bracket >= 0)
            {
                var close = segment.IndexOf(']', bracket);
                var index = int.Parse(segment.AsSpan(bracket + 1, close - bracket - 1), CultureInfo.InvariantCulture);
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
                {
                    throw new InvalidOperationException($"Script placeholder '{{{{{expression}}}}}' has no element [{index}].");
                }

                current = current[index];
                bracket = segment.IndexOf('[', close);
            }
        }

        return current;
    }

    private static string Stringify(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\{\{\s*([^{}]+?)\s*\}\}$")]
    private static partial Regex WholePlaceholder();

    [GeneratedRegex(@"^today(?:([+-]\d+)d?)?$")]
    private static partial Regex TodayExpression();

    /// <summary>Parsed tool results from the conversation, addressable by tool name or as "prior".</summary>
    private sealed class ToolResults
    {
        private readonly Dictionary<string, JsonElement> _byTool = new(StringComparer.Ordinal);
        private JsonElement? _latest;

        public static ToolResults From(IReadOnlyList<LlmMessage> messages)
        {
            var results = new ToolResults();
            var names = messages.SelectMany(m => m.Content.OfType<ToolUseBlock>()).ToDictionary(b => b.Id, b => b.Name, StringComparer.Ordinal);
            foreach (var result in messages.SelectMany(m => m.Content.OfType<ToolResultBlock>()))
            {
                if (result.IsError || ExtractJson(result.Content) is not { } json)
                {
                    continue;
                }

                results._latest = json;
                if (names.TryGetValue(result.ToolUseId, out var name))
                {
                    results._byTool[name] = json;
                }
            }

            return results;
        }

        public JsonElement? Find(string source) =>
            source == "prior" ? _latest : _byTool.TryGetValue(source, out var value) ? value : null;

        private static JsonElement? ExtractJson(string content)
        {
            var start = content.IndexOfAny(['{', '[']);
            var end = content.LastIndexOfAny(['}', ']']);
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(content.AsMemory(start, end - start + 1));
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
