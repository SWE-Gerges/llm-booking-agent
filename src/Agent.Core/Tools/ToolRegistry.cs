using Agent.Abstractions.Llm;
using Agent.Abstractions.Tools;

namespace Agent.Core.Tools;

/// <summary>
/// Holds every registered tool. Definitions are exposed in a fixed (ordinal by name) order so the tool block of the
/// prompt is byte-stable between requests and stays cacheable.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IChatTool> _byName;

    public ToolRegistry(IEnumerable<IChatTool> tools)
    {
        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var duplicate = ordered.GroupBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Tool '{duplicate.Key}' is registered more than once.");
        }

        Tools = ordered;
        _byName = ordered.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Definitions = ordered.Select(t => new LlmToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();
    }

    public IReadOnlyList<IChatTool> Tools { get; }

    public IReadOnlyList<LlmToolDefinition> Definitions { get; }

    public IChatTool? Find(string name) => _byName.GetValueOrDefault(name);
}
