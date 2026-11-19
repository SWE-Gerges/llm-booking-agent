using Agent.Abstractions;
using Agent.Abstractions.Chat;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Guards;
using Agent.Core.Language;
using Agent.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Agent.Tests.Orchestration;

public sealed class OrchestratorGuardTests
{
    private static readonly Localizer Text = new();

    [Fact]
    public void Guards_are_registered_in_the_order_rate_limit_then_repeated_then_sensitive()
    {
        using var harness = OrchestratorHarness.Create();

        var order = harness.Get<IEnumerable<IInboundGuard>>().Select(g => g.GetType()).ToList();

        Assert.Equal([typeof(SessionRateLimitGuard), typeof(RepeatedMessageGuard), typeof(SensitiveDataGuard)], order);
    }

    [Fact]
    public async Task Rate_limited_message_gets_retry_after_and_nothing_is_persisted_or_sent_to_the_model()
    {
        using var harness = OrchestratorHarness.Create();
        harness.RateLimiter.Decision = new RateLimitDecision(false, TimeSpan.FromSeconds(30));
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "hello");

        Assert.Equal(ChatOutcome.RateLimited, reply.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), reply.RetryAfter);
        Assert.Equal(Text.Get(MessageKey.RateLimited, "en"), reply.Reply);
        Assert.Equal(0, harness.Llm.CallCount);
        Assert.Empty(await harness.StoredAsync(sessionId));
        var session = await harness.Conversation.LoadAsync(sessionId, CancellationToken.None);
        Assert.Null(session.RepeatHash);
    }

    [Fact]
    public async Task Fourth_identical_message_in_a_row_gets_the_canned_reply_without_a_model_call()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.Default = (_, _) => Task.FromResult(LlmResults.Text("ok"));
        var sessionId = await harness.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            var passed = await harness.SendAsync(sessionId, "Same thing");
            Assert.Equal(ChatOutcome.Completed, passed.Outcome);
        }

        var blocked = await harness.SendAsync(sessionId, "  same THING ");

        Assert.Equal(ChatOutcome.Guarded, blocked.Outcome);
        Assert.Equal(Text.Get(MessageKey.RepeatedMessage, "en"), blocked.Reply);
        Assert.Equal(3, harness.Llm.CallCount);
        Assert.Equal(6, (await harness.StoredAsync(sessionId)).Count);
    }

    [Fact]
    public async Task A_different_message_after_repeats_is_answered_normally_again()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.Default = (_, _) => Task.FromResult(LlmResults.Text("ok"));
        var sessionId = await harness.StartAsync();
        for (var i = 0; i < 4; i++)
        {
            await harness.SendAsync(sessionId, "same");
        }

        var reply = await harness.SendAsync(sessionId, "something new");

        Assert.Equal(ChatOutcome.Completed, reply.Outcome);
    }

    [Fact]
    public async Task Card_number_is_never_stored_logged_or_sent_to_the_model()
    {
        using var harness = OrchestratorHarness.Create();
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "my card is 4111 1111 1111 1111 exp 12/29");

        Assert.Equal(ChatOutcome.Guarded, reply.Outcome);
        Assert.Equal(Text.Get(MessageKey.SensitiveData, "en"), reply.Reply);
        Assert.Equal(0, harness.Llm.CallCount);

        var stored = await harness.StoredAsync(sessionId);
        Assert.Equal([StoredMessageRole.User, StoredMessageRole.Assistant], stored.Select(m => m.Role));
        Assert.Equal("[redacted: card number]", stored[0].VisibleText);
        Assert.DoesNotContain("4111", AgentJson.Serialize(stored), StringComparison.Ordinal);

        var cached = await harness.Cache.GetAsync(sessionId, CancellationToken.None);
        Assert.DoesNotContain("4111", AgentJson.Serialize(cached), StringComparison.Ordinal);

        var warnings = harness.Logs.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Contains(warnings, e => e.Message.Contains("CardNumber", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logs.Entries, e => e.Message.Contains("4111", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("send it to DE89 3704 0044 0532 0130 00 please", "[redacted: bank account number]")]
    [InlineData("my account number is 12345678", "[redacted: account number]")]
    public async Task Other_financial_identifiers_are_replaced_by_a_redacted_placeholder(string message, string placeholder)
    {
        using var harness = OrchestratorHarness.Create();
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, message);

        Assert.Equal(ChatOutcome.Guarded, reply.Outcome);
        Assert.Equal(placeholder, (await harness.StoredAsync(sessionId))[0].VisibleText);
        Assert.Equal(0, harness.Llm.CallCount);
    }

    [Fact]
    public async Task Sensitive_data_reply_is_localized_to_the_users_script()
    {
        using var harness = OrchestratorHarness.Create();
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "رقم بطاقتي ٤١١١ ١١١١ ١١١١ ١١١١");

        Assert.Equal("ar", reply.Locale);
        Assert.Equal(Text.Get(MessageKey.SensitiveData, "ar"), reply.Reply);
        Assert.Equal(0, harness.Llm.CallCount);
    }

    [Fact]
    public async Task Phone_and_passport_numbers_are_allowed_through_to_the_model()
    {
        using var harness = OrchestratorHarness.Create();
        harness.Llm.EnqueueText("Noted.");
        var sessionId = await harness.StartAsync();

        var reply = await harness.SendAsync(sessionId, "call me on +15555550100, passport A12345678");

        Assert.Equal(ChatOutcome.Completed, reply.Outcome);
        Assert.Equal(1, harness.Llm.CallCount);
    }

    [Fact]
    public async Task Sensitive_guard_runs_after_the_repeated_guard_so_repeats_are_counted_first()
    {
        using var harness = OrchestratorHarness.Create();
        var sessionId = await harness.StartAsync();

        await harness.SendAsync(sessionId, "my card is 4111 1111 1111 1111");
        var session = await harness.Conversation.LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal(1, session.RepeatCount);
        Assert.NotNull(session.RepeatHash);
    }
}
