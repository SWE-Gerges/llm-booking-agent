using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Abstractions.Llm;

namespace Agent.Llm.Anthropic;

/// <summary>
/// Translates between the neutral contracts and the Messages API wire format. Three cache breakpoints are used (the
/// provider allows four): the stable system block, the last tool definition and the last message block.
/// </summary>
internal static class AnthropicMapper
{
    public static JsonObject BuildRequest(LlmChatRequest request, string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens,
        };

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var system = new JsonArray { TextBlock(request.SystemStable, cache: true) };
        if (!string.IsNullOrWhiteSpace(request.SystemVolatile))
        {
            system.Add(TextBlock(request.SystemVolatile, cache: false));
        }

        body["system"] = system;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            for (var i = 0; i < request.Tools.Count; i++)
            {
                var tool = request.Tools[i];
                var node = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                };
                if (i == request.Tools.Count - 1)
                {
                    node["cache_control"] = Ephemeral();
                }

                tools.Add(node);
            }

            body["tools"] = tools;
        }

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            messages.Add(MapMessage(message));
        }

        MarkLastBlock(messages);
        body["messages"] = messages;
        return body;
    }

    public static LlmChatResult ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new LlmApiException("The model response had no content array.", null, Excerpt(json), kind: LlmErrorKind.Permanent);
        }

        var blocks = new List<ContentBlock>();
        foreach (var item in content.EnumerateArray())
        {
            switch (item.GetProperty("type").GetString())
            {
                case "text":
                    blocks.Add(new TextBlock(item.GetProperty("text").GetString() ?? string.Empty));
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        item.GetProperty("id").GetString() ?? string.Empty,
                        item.GetProperty("name").GetString() ?? string.Empty,
                        item.TryGetProperty("input", out var input) ? input.Clone() : AgentJsonEmpty()));
                    break;
            }
        }

        var stop = root.TryGetProperty("stop_reason", out var stopElement) ? stopElement.GetString() : null;
        return new LlmChatResult(blocks, MapStopReason(stop), ReadUsage(root));
    }

    public static string Excerpt(string body, int max = 300) =>
        body.Length <= max ? body : string.Concat(body.AsSpan(0, max), "...");

    public static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmStopReason MapStopReason(string? reason) => reason switch
    {
        "tool_use" => LlmStopReason.ToolUse,
        "max_tokens" => LlmStopReason.MaxTokens,
        _ => LlmStopReason.EndTurn,
    };

    private static LlmUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return LlmUsage.None;
        }

        int Read(string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        return new LlmUsage(Read("input_tokens"), Read("output_tokens"), Read("cache_read_input_tokens"), Read("cache_creation_input_tokens"));
    }

    private static JsonObject MapMessage(LlmMessage message)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    content.Add(TextBlock(text.Text, cache: false));
                    break;
                case ToolUseBlock use:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = use.Id,
                        ["name"] = use.Name,
                        ["input"] = JsonNode.Parse(use.Input.GetRawText()),
                    });
                    break;
                case ToolResultBlock result:
                    var node = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.ToolUseId,
                        ["content"] = result.Content,
                    };
                    if (result.IsError)
                    {
                        node["is_error"] = true;
                    }

                    content.Add(node);
                    break;
            }
        }

        if (content.Count == 0)
        {
            content.Add(TextBlock("(no content)", cache: false));
        }

        return new JsonObject
        {
            ["role"] = message.Role == LlmRole.User ? "user" : "assistant",
            ["content"] = content,
        };
    }

    private static void MarkLastBlock(JsonArray messages)
    {
        if (messages.Count == 0 || messages[^1] is not JsonObject last || last["content"] is not JsonArray content || content.Count == 0)
        {
            return;
        }

        if (content[^1] is JsonObject block)
        {
            block["cache_control"] = Ephemeral();
        }
    }

    private static JsonObject TextBlock(string text, bool cache)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        if (cache)
        {
            node["cache_control"] = Ephemeral();
        }

        return node;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static JsonElement AgentJsonEmpty() => Abstractions.AgentJson.EmptyObject;
}
