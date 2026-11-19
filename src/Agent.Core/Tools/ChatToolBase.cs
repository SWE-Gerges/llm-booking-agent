using System.Globalization;
using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Tools;

namespace Agent.Core.Tools;

/// <summary>Removes the boilerplate shared by every tool: name, description, a schema given as JSON text, and result helpers.</summary>
public abstract class ChatToolBase : IChatTool
{
    protected ChatToolBase(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        InputSchema = AgentJson.Parse(inputSchemaJson);
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }

    public abstract Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken);

    /// <summary>Result whose raw payload goes to the API consumer; the model sees the sanitised payload (or <paramref name="modelContent"/>).</summary>
    protected static ToolResult Result(object payload, string? state = null, string? modelContent = null, string? modelNote = null) =>
        ToolResult.Ok(AgentJson.Serialize(payload), state, modelContent, modelNote);

    /// <summary>Like <see cref="Result"/> but with a compact object as the model's view of the payload.</summary>
    protected static ToolResult ResultWithView(object payload, string? state, object modelView, string? modelNote = null) =>
        ToolResult.Ok(AgentJson.Serialize(payload), state, AgentJson.Serialize(modelView), modelNote);
}

/// <summary>Typed readers for model-supplied input. Input has already passed schema validation, so these only extract.</summary>
public static class ToolInput
{
    public static string? GetString(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetInt(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    public static decimal? GetDecimal(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;

    public static bool? GetBool(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static IReadOnlyList<string> GetStrings(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    /// <summary>Parses a required yyyy-MM-dd date, failing with a message the model can act on.</summary>
    public static DateOnly RequireDate(this JsonElement input, string name)
    {
        var text = input.GetString(name);
        return text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ToolExecutionException($"'{name}' must be a date in the format yyyy-MM-dd.");
    }

    public static DateOnly? OptionalDate(this JsonElement input, string name) =>
        input.GetString(name) is null ? null : input.RequireDate(name);
}
