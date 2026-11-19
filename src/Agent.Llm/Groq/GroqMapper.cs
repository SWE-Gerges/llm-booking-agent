using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;

namespace Agent.Llm.Groq;

/// <summary>
/// Translates between the neutral contracts and the chat-completions wire format (system, user, assistant and tool
/// messages; function tools; assistant tool_calls whose arguments are a JSON string).
/// </summary>
internal static class GroqMapper
{
    public static JsonObject BuildRequest(LlmChatRequest request, string model)
    {
        var messages = new JsonArray();
        var system = SystemPrompts.Concatenate(request);
        if (system.Length > 0)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var message in request.Messages)
        {
            if (message.Role == LlmRole.User)
            {
                AddUserMessages(messages, message);
            }
            else
            {
                messages.Add(MapAssistant(message));
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_completion_tokens"] = request.MaxTokens,
        };

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                    },
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    public static LlmChatResult ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new LlmApiException("The model response had no choices.", null, LlmHttp.Excerpt(json), kind: LlmErrorKind.Permanent);
        }

        var choice = choices[0];
        var finish = choice.TryGetProperty("finish_reason", out var finishElement) && finishElement.ValueKind == JsonValueKind.String
            ? finishElement.GetString()
            : null;
        if (finish == "content_filter")
        {
            throw new LlmApiException("The provider filtered the answer (content_filter).", 200, null, kind: LlmErrorKind.Blocked);
        }

        var message = choice.GetProperty("message");
        var blocks = new List<ContentBlock>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(content.GetString()))
        {
            blocks.Add(new TextBlock(content.GetString()!));
        }

        var calls = 0;
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                blocks.Add(new ToolUseBlock(
                    call.GetProperty("id").GetString() ?? string.Empty,
                    function.GetProperty("name").GetString() ?? string.Empty,
                    ParseArguments(function.TryGetProperty("arguments", out var args) ? args.GetString() : null)));
                calls++;
            }
        }

        var stop = calls > 0 || finish == "tool_calls" ? LlmStopReason.ToolUse : finish == "length" ? LlmStopReason.MaxTokens : LlmStopReason.EndTurn;
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
    /// Tool arguments arrive as a JSON string. A string that is not a JSON object is kept as a JSON string value so the
    /// schema validation rejects it and the model gets an error it can correct, instead of the call being dropped.
    /// </summary>
    private static JsonElement ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Abstractions.AgentJson.EmptyObject;
        }

        try
        {
            using var parsed = JsonDocument.Parse(arguments);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(arguments);
        }
    }

    private static void AddUserMessages(JsonArray messages, LlmMessage message)
    {
        // Tool results become role=tool messages and must directly follow the assistant message that made the calls.
        foreach (var result in message.Content.OfType<ToolResultBlock>())
        {
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolUseId, ["content"] = result.Content });
        }

        var text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        if (!string.IsNullOrWhiteSpace(text))
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        }
        else if (!message.Content.OfType<ToolResultBlock>().Any())
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(no content)" });
        }
    }

    private static JsonObject MapAssistant(LlmMessage message)
    {
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        var node = new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrWhiteSpace(text) ? null : text };

        var calls = new JsonArray();
        foreach (var use in message.Content.OfType<ToolUseBlock>())
        {
            calls.Add(new JsonObject
            {
                ["id"] = use.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = use.Name, ["arguments"] = use.Input.GetRawText() },
            });
        }

        if (calls.Count > 0)
        {
            node["tool_calls"] = calls;
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            node["content"] = "(no content)";
        }

        return node;
    }

    private static LlmUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return LlmUsage.None;
        }

        int Read(JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Read(details, "cached_tokens")
            : 0;
        return new LlmUsage(Math.Max(0, Read(usage, "prompt_tokens") - cached), Read(usage, "completion_tokens"), cached, 0);
    }
}
