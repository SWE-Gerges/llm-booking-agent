using System.Text.Json;
using Agent.Abstractions;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;

namespace Agent.Tests.Support;

internal class FakeTool : IChatTool
{
    public const string DefaultSchema = """{"type":"object","properties":{"q":{"type":"string"}},"additionalProperties":false}""";

    private readonly Func<ToolContext, int, ToolResult> _handler;
    private readonly JsonElement _schema;

    public FakeTool(string name, Func<ToolContext, int, ToolResult>? handler = null, string? schemaJson = null)
    {
        Name = name;
        _handler = handler ?? ((_, _) => ToolResult.Ok("""{"ok":true}"""));
        _schema = AgentJson.Parse(schemaJson ?? DefaultSchema);
    }

    public string Name { get; }

    public string Description => $"Fake tool {Name}";

    public virtual JsonElement InputSchema => _schema;

    public int Calls { get; private set; }

    public List<string> Inputs { get; } = [];

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement input, CancellationToken cancellationToken)
    {
        Calls++;
        Inputs.Add(input.GetRawText());
        return Task.FromResult(_handler(context, Calls));
    }
}

internal sealed class FakeSearchTool(string name, Func<ToolContext, int, ToolResult>? handler = null)
    : FakeTool(name, handler), ISearchTool
{
    public ServiceType Service => ServiceType.Flight;
}

internal sealed class FakeFinalizingTool(string name, Func<ToolContext, int, ToolResult>? handler = null)
    : FakeTool(name, handler, """{"type":"object","properties":{},"additionalProperties":false}"""), IFinalizingTool
{
    public ServiceType Service => ServiceType.Flight;
}

/// <summary>Counts how often the schema is read, which shows whether the validator caches the compiled schema.</summary>
internal sealed class CountingSchemaTool(string name, string schemaJson) : FakeTool(name, null, schemaJson)
{
    private readonly JsonElement _schemaElement = AgentJson.Parse(schemaJson);

    public int SchemaReads { get; private set; }

    public override JsonElement InputSchema
    {
        get
        {
            SchemaReads++;
            return _schemaElement;
        }
    }
}

internal sealed class DenyPolicy(string message) : IToolCallPolicy
{
    public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PolicyDecision.Deny(message));
}
