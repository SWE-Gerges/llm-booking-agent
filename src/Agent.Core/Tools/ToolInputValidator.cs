using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Agent.Abstractions.Tools;
using Json.Schema;

namespace Agent.Core.Tools;

/// <summary>Validates model-supplied tool input against the tool's JSON schema. Compiled schemas are cached per tool.</summary>
public sealed class ToolInputValidator
{
    private const int MaxMessageLength = 600;

    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    /// <summary>Returns null when the input is valid, otherwise a short description of what is wrong.</summary>
    public string? Validate(IChatTool tool, JsonElement input)
    {
        var schema = _schemas.GetOrAdd(tool.Name, _ => JsonSchema.FromText(tool.InputSchema.GetRawText()));
        var results = schema.Evaluate(input, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return null;
        }

        var builder = new StringBuilder($"Invalid input for tool '{tool.Name}'.");
        foreach (var detail in (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }))
        {
            foreach (var error in detail.Errors!)
            {
                builder.Append(' ').Append(detail.InstanceLocation).Append(": ").Append(error.Value).Append('.');
                if (builder.Length >= MaxMessageLength)
                {
                    return builder.ToString(0, MaxMessageLength);
                }
            }
        }

        return builder.ToString();
    }
}
