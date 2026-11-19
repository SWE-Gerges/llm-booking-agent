using Agent.Abstractions.Chat;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Guards;
using Agent.Core.Language;
using Agent.Core.Options;
using Agent.Tests.Support;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Agent.Tests.GuardTests;

public sealed class RepeatedMessageGuardTests
{
    private static SessionContext NewSession() => new() { SessionId = "s1" };

    private static RepeatedMessageGuard NewGuard(int limit = 3) =>
        new(TestOptions.Of(new AgentOptions { RepeatedMessageLimit = limit }));

    [Fact]
    public async Task Three_identical_messages_pass_and_the_fourth_is_blocked()
    {
        var guard = NewGuard();
        var session = NewSession();

        for (var i = 0; i < 3; i++)
        {
            var result = await guard.EvaluateAsync(new GuardContext(session, "hello"), default);
            Assert.False(result.IsBlocked);
        }

        var blocked = await guard.EvaluateAsync(new GuardContext(session, "hello"), default);

        Assert.True(blocked.IsBlocked);
        Assert.Equal(MessageKey.RepeatedMessage, blocked.Reply);
        Assert.Equal(ChatOutcome.Guarded, blocked.Outcome);
        Assert.False(blocked.PersistExchange);
    }

    [Fact]
    public async Task Comparison_ignores_case_and_surrounding_whitespace()
    {
        var guard = NewGuard(limit: 1);
        var session = NewSession();

        await guard.EvaluateAsync(new GuardContext(session, "Hello"), default);
        var result = await guard.EvaluateAsync(new GuardContext(session, "  hELLO  "), default);

        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task A_different_message_resets_the_streak()
    {
        var guard = NewGuard();
        var session = NewSession();
        for (var i = 0; i < 3; i++)
        {
            await guard.EvaluateAsync(new GuardContext(session, "hello"), default);
        }

        await guard.EvaluateAsync(new GuardContext(session, "something else"), default);
        var again = await guard.EvaluateAsync(new GuardContext(session, "hello"), default);

        Assert.False(again.IsBlocked);
        Assert.Equal(1, session.RepeatCount);
    }

    [Fact]
    public async Task The_counter_stops_growing_once_the_limit_is_exceeded()
    {
        var guard = NewGuard();
        var session = NewSession();

        for (var i = 0; i < 50; i++)
        {
            await guard.EvaluateAsync(new GuardContext(session, "spam"), default);
        }

        Assert.Equal(4, session.RepeatCount);
    }

    [Fact]
    public async Task The_message_text_is_stored_only_as_a_hash()
    {
        var guard = NewGuard();
        var session = NewSession();

        await guard.EvaluateAsync(new GuardContext(session, "super private words"), default);

        Assert.DoesNotContain("private", session.RepeatHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, session.RepeatHash!.Length);
    }
}

public sealed class SessionRateLimitGuardTests
{
    [Fact]
    public async Task Blocks_with_rate_limited_outcome_and_retry_after()
    {
        var limiter = Substitute.For<ISessionRateLimiter>();
        limiter.TryAcquireAsync("s1", Arg.Any<CancellationToken>()).Returns(new RateLimitDecision(false, TimeSpan.FromSeconds(12)));
        var guard = new SessionRateLimitGuard(limiter);

        var result = await guard.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "s1" }, "hi"), default);

        Assert.True(result.IsBlocked);
        Assert.Equal(ChatOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);
        Assert.Equal(MessageKey.RateLimited, result.Reply);
    }

    [Fact]
    public async Task Passes_when_the_limiter_allows()
    {
        var limiter = Substitute.For<ISessionRateLimiter>();
        limiter.TryAcquireAsync("s1", Arg.Any<CancellationToken>()).Returns(RateLimitDecision.Allowed);
        var guard = new SessionRateLimitGuard(limiter);

        var result = await guard.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "s1" }, "hi"), default);

        Assert.False(result.IsBlocked);
    }
}

public sealed class GuardPipelineTests
{
    private sealed class RecordingGuard(string name, List<string> calls, bool block) : IInboundGuard
    {
        public ValueTask<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
        {
            calls.Add(name);
            return ValueTask.FromResult(block ? new GuardResult { IsBlocked = true, Reply = MessageKey.RateLimited } : GuardResult.Pass);
        }
    }

    [Fact]
    public async Task Guards_run_in_order_and_the_first_block_short_circuits_the_rest()
    {
        var calls = new List<string>();
        var pipeline = new GuardPipeline([new RecordingGuard("a", calls, false), new RecordingGuard("b", calls, true), new RecordingGuard("c", calls, false)]);

        var result = await pipeline.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "s" }, "x"), default);

        Assert.True(result.IsBlocked);
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public async Task All_guards_run_when_none_blocks()
    {
        var calls = new List<string>();
        var pipeline = new GuardPipeline([new RecordingGuard("a", calls, false), new RecordingGuard("b", calls, false)]);

        var result = await pipeline.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "s" }, "x"), default);

        Assert.False(result.IsBlocked);
        Assert.Equal(["a", "b"], calls);
    }
}

public sealed class SensitiveDataGuardTests
{
    [Fact]
    public async Task Blocked_message_is_replaced_by_a_placeholder_and_the_log_names_only_the_kind()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var guard = new SensitiveDataGuard(factory.CreateLogger<SensitiveDataGuard>());

        var result = await guard.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "abcdef0123456789" }, "pay with 4111 1111 1111 1111"), default);

        Assert.True(result.IsBlocked);
        Assert.True(result.PersistExchange);
        Assert.Equal("[redacted: card number]", result.PersistedUserText);
        var entry = Assert.Single(logs.Entries);
        Assert.Contains("CardNumber", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("4111", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef0123456789", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_message_passes_without_logging()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var guard = new SensitiveDataGuard(factory.CreateLogger<SensitiveDataGuard>());

        var result = await guard.EvaluateAsync(new GuardContext(new SessionContext { SessionId = "s" }, "flights to Lisbon"), default);

        Assert.False(result.IsBlocked);
        Assert.Empty(logs.Entries);
    }
}

public sealed class PiiMaskerTests
{
    [Fact]
    public void Names_keep_only_the_first_letter_of_each_part()
    {
        Assert.Equal("J*** S***", PiiMasker.MaskName("Jane Smith"));
        Assert.Equal("J***", PiiMasker.MaskName("  Jane "));
    }

    [Fact]
    public void Email_keeps_first_letter_and_domain()
    {
        Assert.Equal("j***@example.com", PiiMasker.MaskEmail("jane.smith@example.com"));
        Assert.Equal("***", PiiMasker.MaskEmail("not-an-email"));
    }

    [Fact]
    public void Phone_keeps_the_first_two_and_last_two_digits()
    {
        Assert.Equal("+15*******00", PiiMasker.MaskPhone("+15555550100"));
        Assert.Equal("***", PiiMasker.MaskPhone("1234"));
    }

    [Theory]
    [InlineData("contact.email", "jane@example.com", "j***@example.com")]
    [InlineData("contact.phone", "+15555550100", "+15*******00")]
    [InlineData("passengers[0].firstName", "Jane", "J***")]
    [InlineData("passengers[0].lastName", "Smith", "S***")]
    [InlineData("passengers[0].passportNumber", "X1234567", "***")]
    [InlineData("passengers[0].dateOfBirth", "1990-01-31", "***")]
    [InlineData("passengers[0].nationality", "NL", "NL")]
    [InlineData("specialRequests", "quiet room", "quiet room")]
    public void Fields_are_masked_by_what_their_name_says_they_hold(string field, string value, string expected)
    {
        Assert.Equal(expected, PiiMasker.MaskField(field, value));
    }

    [Fact]
    public void Empty_values_stay_empty_and_the_masked_copy_has_the_same_keys()
    {
        var raw = new Dictionary<string, string> { ["email"] = "jane@example.com", ["note"] = string.Empty };

        var masked = PiiMasker.MaskAll(raw);

        Assert.Equal(raw.Keys.Order(), masked.Keys.Order());
        Assert.Equal(string.Empty, masked["note"]);
        Assert.DoesNotContain("jane@", masked["email"], StringComparison.Ordinal);
    }
}
