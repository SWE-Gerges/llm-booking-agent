using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Llm;

namespace Agent.Core.Telemetry;

/// <summary>How a tool call ended. Closed set so metric tags stay bounded.</summary>
public enum ToolOutcome
{
    Ok,
    Error,
    Denied,
    Invalid,
    Unknown,
}

/// <summary>
/// The only place that creates instruments and spans. One <see cref="Meter"/> and one <see cref="ActivitySource"/>
/// share the name <see cref="Name"/>; there is no exporter dependency, any OpenTelemetry-compatible listener can
/// subscribe by that name. Tags are drawn from closed sets or from registered names; message text, tool input and
/// output, tokens and personal data are never used as tag values.
/// </summary>
public sealed class AgentTelemetry : IDisposable
{
    public const string Name = "LlmBookingAgent";

    public const string TurnsInstrument = "agent.turns";
    public const string ToolCallsInstrument = "agent.tool_calls";
    public const string GuardHitsInstrument = "agent.guard_hits";
    public const string LlmLatencyInstrument = "agent.llm.latency";
    public const string LlmInputTokensInstrument = "agent.llm.input_tokens";
    public const string LlmOutputTokensInstrument = "agent.llm.output_tokens";
    public const string LlmCacheReadTokensInstrument = "agent.llm.cache_read_tokens";
    public const string LlmCacheWriteTokensInstrument = "agent.llm.cache_write_tokens";
    public const string RateLimitRejectionsInstrument = "agent.rate_limit_rejections";

    public const string TurnSpan = "chat.turn";
    public const string ToolCallSpan = "chat.tool_call";

    private readonly Counter<long> _turns;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _guardHits;
    private readonly Histogram<double> _llmLatency;
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Counter<long> _cacheReadTokens;
    private readonly Counter<long> _cacheWriteTokens;
    private readonly Counter<long> _rateLimitRejections;

    public AgentTelemetry()
    {
        Meter = new Meter(Name);
        ActivitySource = new ActivitySource(Name);

        _turns = Meter.CreateCounter<long>(TurnsInstrument, "{turn}", "Chat turns by outcome.");
        _toolCalls = Meter.CreateCounter<long>(ToolCallsInstrument, "{call}", "Tool calls by tool and outcome.");
        _guardHits = Meter.CreateCounter<long>(GuardHitsInstrument, "{hit}", "Messages answered by an inbound guard, by guard.");
        _llmLatency = Meter.CreateHistogram<double>(LlmLatencyInstrument, "ms", "Latency of one model call.");
        _inputTokens = Meter.CreateCounter<long>(LlmInputTokensInstrument, "{token}", "Input tokens reported by the model provider.");
        _outputTokens = Meter.CreateCounter<long>(LlmOutputTokensInstrument, "{token}", "Output tokens reported by the model provider.");
        _cacheReadTokens = Meter.CreateCounter<long>(LlmCacheReadTokensInstrument, "{token}", "Prompt-cache read tokens reported by the model provider.");
        _cacheWriteTokens = Meter.CreateCounter<long>(LlmCacheWriteTokensInstrument, "{token}", "Prompt-cache write tokens reported by the model provider.");
        _rateLimitRejections = Meter.CreateCounter<long>(RateLimitRejectionsInstrument, "{rejection}", "Requests rejected by a rate limiter, by source.");
    }

    public Meter Meter { get; }

    public ActivitySource ActivitySource { get; }

    public Activity? StartTurn() => ActivitySource.StartActivity(TurnSpan, ActivityKind.Internal);

    public void CompleteTurn(Activity? span, ChatOutcome outcome) => CompleteTurn(span, OutcomeTag(outcome));

    /// <summary>The caller went away before the turn finished.</summary>
    public void CompleteAbortedTurn(Activity? span) => CompleteTurn(span, "aborted");

    public Activity? StartToolCall(string tool, int iteration) =>
        ActivitySource.StartActivity(ToolCallSpan, ActivityKind.Internal)?.SetTag("tool", tool).SetTag("iteration", iteration);

    public void CompleteToolCall(Activity? span, string tool, ToolOutcome outcome)
    {
        var tag = outcome.ToString().ToLowerInvariant();
        span?.SetTag("outcome", tag);
        _toolCalls.Add(1, new KeyValuePair<string, object?>("tool", tool), new KeyValuePair<string, object?>("outcome", tag));
    }

    public void RecordGuardHit(string guard) =>
        _guardHits.Add(1, new KeyValuePair<string, object?>("guard", guard));

    public void RecordLlmCall(TimeSpan elapsed, bool succeeded, LlmUsage? usage)
    {
        _llmLatency.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error"));
        if (usage is null)
        {
            return;
        }

        _inputTokens.Add(usage.InputTokens);
        _outputTokens.Add(usage.OutputTokens);
        _cacheReadTokens.Add(usage.CacheReadTokens);
        _cacheWriteTokens.Add(usage.CacheWriteTokens);
    }

    /// <param name="source">"session", "ip" or "start".</param>
    public void RecordRateLimitRejection(string source) =>
        _rateLimitRejections.Add(1, new KeyValuePair<string, object?>("source", source));

    /// <summary>Derives a bounded, stable guard name from a registered guard type, for example SensitiveDataGuard to sensitive_data.</summary>
    public static string GuardName(Type guardType)
    {
        var name = guardType.Name;
        if (name.EndsWith("Guard", StringComparison.Ordinal) && name.Length > "Guard".Length)
        {
            name = name[..^"Guard".Length];
        }

        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        Meter.Dispose();
        ActivitySource.Dispose();
    }

    private void CompleteTurn(Activity? span, string outcome)
    {
        span?.SetTag("outcome", outcome);
        _turns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    private static string OutcomeTag(ChatOutcome outcome) => outcome switch
    {
        ChatOutcome.Completed => "completed",
        ChatOutcome.Guarded => "guarded",
        ChatOutcome.RateLimited => "rate_limited",
        ChatOutcome.Escalated => "escalated",
        ChatOutcome.Failed => "failed",
        _ => "failed",
    };
}
