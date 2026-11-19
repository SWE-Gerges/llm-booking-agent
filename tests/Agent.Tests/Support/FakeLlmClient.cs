using Agent.Abstractions;
using Agent.Abstractions.Llm;

namespace Agent.Tests.Support;

/// <summary>Queue-driven model stand-in that records every request it receives.</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<Func<LlmChatRequest, CancellationToken, Task<LlmChatResult>>> _queue = new();

    public List<LlmChatRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    /// <summary>Used when the queue is empty.</summary>
    public Func<LlmChatRequest, CancellationToken, Task<LlmChatResult>>? Default { get; set; }

    public FakeLlmClient Enqueue(LlmChatResult result) => Enqueue((_, _) => Task.FromResult(result));

    public FakeLlmClient Enqueue(Func<LlmChatRequest, CancellationToken, Task<LlmChatResult>> handler)
    {
        _queue.Enqueue(handler);
        return this;
    }

    public FakeLlmClient EnqueueText(string text) => Enqueue(LlmResults.Text(text));

    public FakeLlmClient EnqueueTools(params ToolUseBlock[] uses) => Enqueue(LlmResults.Tools(uses));

    public FakeLlmClient EnqueueThrow(Exception exception) => Enqueue((_, _) => throw exception);

    public Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_queue.Count > 0)
        {
            return _queue.Dequeue()(request, cancellationToken);
        }

        return Default is { } fallback
            ? fallback(request, cancellationToken)
            : throw new InvalidOperationException("The fake LLM has no response queued.");
    }
}

internal static class LlmResults
{
    public static LlmChatResult Text(string text) => new([new TextBlock(text)], LlmStopReason.EndTurn, new LlmUsage(10, 5));

    public static LlmChatResult Tools(params ToolUseBlock[] uses) => new(uses, LlmStopReason.ToolUse, new LlmUsage(10, 5));

    public static LlmChatResult Mixed(IReadOnlyList<ContentBlock> blocks, LlmStopReason stop) => new(blocks, stop, new LlmUsage(10, 5));

    public static ToolUseBlock Use(string id, string name, string inputJson = "{}") => new(id, name, AgentJson.Parse(inputJson));
}
