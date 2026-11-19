using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Abstractions;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;

namespace Agent.Llm.Gemini;

/// <summary>
/// Translates between the neutral contracts and the generateContent wire format. The API has no tool-call ids, so
/// calls get stable synthesised ids and results are matched back to function names through the history.
/// </summary>
internal static class GeminiMapper
{
    private static readonly HashSet<string> BlockedFinishReasons = new(StringComparer.Ordinal)
    {
        "SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII",
    };

    public static JsonObject BuildRequest(LlmChatRequest request)
    {
        var body = new JsonObject();

        var system = SystemPrompts.Concatenate(request);
        if (system.Length > 0)
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } };
        }

        var names = ToolNames(request.Messages);
        var contents = new JsonArray();
        string? currentRole = null;
        List<JsonObject>? currentParts = null;
        var groups = new List<(string Role, List<JsonObject> Parts)>();

        // The API wants strictly alternating turns, so consecutive messages of one role are merged into one content.
        foreach (var message in request.Messages)
        {
            var role = message.Role == LlmRole.User ? "user" : "model";
            var parts = MapParts(message, names);
            if (role == currentRole && currentParts is not null)
            {
                currentParts.AddRange(parts);
            }
            else
            {
                currentParts = parts;
                currentRole = role;
                groups.Add((role, parts));
            }
        }

        foreach (var (role, parts) in groups)
        {
            var array = new JsonArray();
            foreach (var part in parts)
            {
                array.Add(part);
            }

            contents.Add(new JsonObject { ["role"] = role, ["parts"] = array });
        }

        body["contents"] = contents;

        var declarations = new JsonArray();
        foreach (var tool in request.Tools)
        {
            var declaration = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
            var parameters = ConvertSchema(tool.InputSchema);
            if (parameters["properties"] is JsonObject { Count: > 0 })
            {
                declaration["parameters"] = parameters;
            }

            declarations.Add(declaration);
        }

        if (declarations.Count > 0)
        {
            body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } };
        }

        var generation = new JsonObject { ["maxOutputTokens"] = request.MaxTokens };
        if (request.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        body["generationConfig"] = generation;
        return body;
    }

    public static LlmChatResult ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LlmApiException("The model response was not a JSON object.", null, LlmHttp.Excerpt(json), kind: LlmErrorKind.Permanent);
        }

        var hasCandidate = root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0;

        if (!hasCandidate)
        {
            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var reason))
            {
                throw new LlmApiException($"The provider blocked the prompt ({reason.GetString()}).", 200, null, kind: LlmErrorKind.Blocked);
            }

            throw new LlmApiException("The model response had no candidates.", null, LlmHttp.Excerpt(json), kind: LlmErrorKind.Permanent);
        }

        var candidate = candidates[0];
        var finish = candidate.TryGetProperty("finishReason", out var finishElement) ? finishElement.GetString() : null;
        if (finish is not null && BlockedFinishReasons.Contains(finish))
        {
            throw new LlmApiException($"The provider blocked the answer ({finish}).", 200, null, kind: LlmErrorKind.Blocked);
        }

        var responseId = root.TryGetProperty("responseId", out var idElement) ? idElement.GetString() : null;
        var blocks = new List<ContentBlock>();
        var text = new StringBuilder();
        var calls = 0;

        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var call))
                {
                    FlushText(blocks, text);
                    var name = call.GetProperty("name").GetString() ?? string.Empty;
                    var args = call.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
                        ? argsElement.Clone()
                        : AgentJson.EmptyObject;
                    var id = call.TryGetProperty("id", out var callId) && callId.GetString() is { Length: > 0 } provided
                        ? provided
                        : SynthesiseId(responseId, calls, name, args);
                    blocks.Add(new ToolUseBlock(id, name, args));
                    calls++;
                }
                else if (part.TryGetProperty("text", out var textElement)
                    && !(part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True))
                {
                    text.Append(textElement.GetString());
                }
            }
        }

        FlushText(blocks, text);

        var stop = calls > 0 ? LlmStopReason.ToolUse : finish == "MAX_TOKENS" ? LlmStopReason.MaxTokens : LlmStopReason.EndTurn;
        return new LlmChatResult(blocks, stop, ReadUsage(root));
    }

    public static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reduces a JSON Schema to the OpenAPI subset the API accepts: types are upper-cased, nullable unions become
    /// <c>nullable</c>, and keywords it rejects (additionalProperties, exclusive bounds, defaults, combinators, $-keywords)
    /// are dropped. Server-side validation still uses the full schema, so nothing is weakened.
    /// </summary>
    internal static JsonObject ConvertSchema(JsonElement schema)
    {
        var result = new JsonObject();
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var nullable = false;
        foreach (var property in schema.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    var type = ReadType(property.Value, ref nullable);
                    if (type is not null)
                    {
                        result["type"] = type.ToUpperInvariant();
                    }

                    break;
                case "description" when property.Value.ValueKind == JsonValueKind.String:
                    result["description"] = property.Value.GetString();
                    break;
                case "format" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "enum" or "date-time" or "int32" or "int64" or "float" or "double":
                    result["format"] = property.Value.GetString();
                    break;
                case "enum" or "required":
                    result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    break;
                case "const":
                    result["enum"] = new JsonArray { JsonNode.Parse(property.Value.GetRawText()) };
                    break;
                case "minimum" or "maximum" or "minItems" or "maxItems" or "minLength" or "maxLength" or "pattern":
                    result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    break;
                case "properties" when property.Value.ValueKind == JsonValueKind.Object:
                    var properties = new JsonObject();
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        properties[child.Name] = ConvertSchema(child.Value);
                    }

                    result["properties"] = properties;
                    break;
                case "items":
                    result["items"] = ConvertSchema(property.Value);
                    break;
            }
        }

        if (nullable)
        {
            result["nullable"] = true;
        }

        return result;
    }

    private static string? ReadType(JsonElement element, ref bool nullable)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        string? type = null;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.GetString() == "null")
                {
                    nullable = true;
                }
                else
                {
                    type ??= item.GetString();
                }
            }
        }

        return type;
    }

    private static Dictionary<string, string> ToolNames(IReadOnlyList<LlmMessage> messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var use in messages.SelectMany(m => m.Content).OfType<ToolUseBlock>())
        {
            names[use.Id] = use.Name;
        }

        return names;
    }

    private static List<JsonObject> MapParts(LlmMessage message, Dictionary<string, string> names)
    {
        var parts = new List<JsonObject>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(new JsonObject { ["text"] = text.Text });
                    break;
                case ToolUseBlock use:
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = use.Name,
                            ["args"] = use.Input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(use.Input.GetRawText()) : new JsonObject(),
                        },
                    });
                    break;
                case ToolResultBlock result:
                    parts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = names.GetValueOrDefault(result.ToolUseId, "unknown_tool"),
                            ["response"] = new JsonObject { [result.IsError ? "error" : "output"] = result.Content },
                        },
                    });
                    break;
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new JsonObject { ["text"] = "(no content)" });
        }

        return parts;
    }

    private static void FlushText(List<ContentBlock> blocks, StringBuilder text)
    {
        if (text.Length > 0)
        {
            blocks.Add(new TextBlock(text.ToString()));
            text.Clear();
        }
    }

    private static string SynthesiseId(string? responseId, int index, string name, JsonElement args)
    {
        var seed = $"{responseId}|{index}|{name}|{args.GetRawText()}";
        return "gemini-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16].ToLowerInvariant();
    }

    private static LlmUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return LlmUsage.None;
        }

        int Read(string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        var cached = Read("cachedContentTokenCount");
        var prompt = Read("promptTokenCount");
        return new LlmUsage(Math.Max(0, prompt - cached), Read("candidatesTokenCount") + Read("thoughtsTokenCount"), cached, 0);
    }
}
