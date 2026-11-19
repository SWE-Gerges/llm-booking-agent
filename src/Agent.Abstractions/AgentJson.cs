using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Abstractions;

/// <summary>Shared serializer settings for persisted history, cache entries and tool payloads.</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement EmptyObject { get; } = Parse("{}");
}
