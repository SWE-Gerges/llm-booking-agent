using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Language;
using Agent.Core.Options;
using Agent.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Guards;

public sealed record GuardContext(SessionContext Session, string Message);

/// <summary>Outcome of one guard. A blocked message is answered with a canned reply and never reaches the model.</summary>
public sealed record GuardResult
{
    public static readonly GuardResult Pass = new();

    public bool IsBlocked { get; init; }

    public MessageKey Reply { get; init; }

    public ChatOutcome Outcome { get; init; }

    /// <summary>When true the user message (or its placeholder) and the canned reply are stored in history.</summary>
    public bool PersistExchange { get; init; }

    /// <summary>Replaces the user text in history, so the sensitive original is never stored.</summary>
    public string? PersistedUserText { get; init; }

    public TimeSpan? RetryAfter { get; init; }
}

public interface IInboundGuard
{
    ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken);
}

/// <summary>Runs guards in registration order and stops at the first one that blocks.</summary>
public sealed class GuardPipeline(IEnumerable<IInboundGuard> guards, AgentTelemetry? telemetry = null)
{
    private readonly IInboundGuard[] _guards = guards.ToArray();

    public async ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        foreach (var guard in _guards)
        {
            var result = await guard.EvaluateAsync(context, cancellationToken);
            if (result.IsBlocked)
            {
                telemetry?.RecordGuardHit(AgentTelemetry.GuardName(guard.GetType()));
                return result;
            }
        }

        return GuardResult.Pass;
    }
}

public sealed class SessionRateLimitGuard(ISessionRateLimiter limiter, AgentTelemetry? telemetry = null) : IInboundGuard
{
    public async ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        var decision = await limiter.TryAcquireAsync(context.Session.SessionId, cancellationToken);
        if (!decision.IsAllowed)
        {
            telemetry?.RecordRateLimitRejection("session");
        }

        return decision.IsAllowed
            ? GuardResult.Pass
            : new GuardResult
            {
                IsBlocked = true,
                Reply = MessageKey.RateLimited,
                Outcome = ChatOutcome.RateLimited,
                RetryAfter = decision.RetryAfter,
            };
    }
}

public sealed class RepeatedMessageGuard(IOptions<AgentOptions> options) : IInboundGuard
{
    public ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        var limit = options.Value.RepeatedMessageLimit;
        var normalized = context.Message.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var session = context.Session;

        if (string.Equals(session.RepeatHash, hash, StringComparison.Ordinal))
        {
            session.RepeatCount = Math.Min(session.RepeatCount + 1, limit + 1);
        }
        else
        {
            session.RepeatHash = hash;
            session.RepeatCount = 1;
        }

        return ValueTask.FromResult(
            session.RepeatCount > limit
                ? new GuardResult { IsBlocked = true, Reply = MessageKey.RepeatedMessage, Outcome = ChatOutcome.Guarded }
                : GuardResult.Pass);
    }
}

public sealed class SensitiveDataGuard(ILogger<SensitiveDataGuard> logger) : IInboundGuard
{
    public ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        var kind = SensitiveDataDetector.Detect(context.Message);
        if (kind is null)
        {
            return ValueTask.FromResult(GuardResult.Pass);
        }

        logger.LogWarning("Sensitive data blocked: kind={Kind} session={Session}", kind, LogSafe.Short(context.Session.SessionId));
        return ValueTask.FromResult(new GuardResult
        {
            IsBlocked = true,
            Reply = MessageKey.SensitiveData,
            Outcome = ChatOutcome.Guarded,
            PersistExchange = true,
            PersistedUserText = $"[redacted: {Describe(kind.Value)}]",
        });
    }

    private static string Describe(SensitiveDataKind kind) => kind switch
    {
        SensitiveDataKind.CardNumber => "card number",
        SensitiveDataKind.Iban => "bank account number",
        _ => "account number",
    };
}

public static class LogSafe
{
    /// <summary>Shortens identifiers and tokens so logs can correlate a session without exposing a usable credential.</summary>
    public static string Short(string value) => value.Length <= 8 ? value : string.Concat(value.AsSpan(0, 8), "...");
}
