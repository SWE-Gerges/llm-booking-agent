using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Core.Options;
using Microsoft.Extensions.Options;

namespace Agent.Core.Tools;

/// <summary>
/// Prepares tool output for the model: removes configured properties, escapes markup so output cannot close the
/// delimiter early, and wraps the result so the prompt can state that everything inside is data, never instructions.
/// </summary>
public sealed class ToolOutputSanitizer
{
    public const string OpenTag = "<tool_output>";
    public const string CloseTag = "</tool_output>";

    private readonly HashSet<string> _strip;

    public ToolOutputSanitizer(IOptions<AgentOptions> options) =>
        _strip = new HashSet<string>(options.Value.SanitizerStripProperties, StringComparer.OrdinalIgnoreCase);

    /// <summary>Strips configured properties from a JSON payload, then wraps it.</summary>
    public string Sanitize(string content)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return Wrap(content);
        }

        if (node is null)
        {
            return Wrap("null");
        }

        Strip(node);
        return Wrap(node.ToJsonString());
    }

    /// <summary>Escapes angle brackets, which keeps forged closing tags inert, and wraps the payload in delimiters.</summary>
    public static string Wrap(string payload)
    {
        var escaped = payload.Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal);
        return $"{OpenTag}\n{escaped}\n{CloseTag}";
    }

    private void Strip(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).Where(_strip.Contains).ToList())
                {
                    obj.Remove(name);
                }

                foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>())
                {
                    Strip(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    Strip(child);
                }

                break;
        }
    }
}
