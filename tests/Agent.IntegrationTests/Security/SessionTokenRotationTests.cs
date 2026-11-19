using System.Globalization;
using System.Net;
using Agent.Api.Security;
using Agent.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agent.IntegrationTests.Security;

public sealed class SessionTokenRotationTests
{
    private const string KeyOne = "first-signing-key-0123456789-abcdefghij";
    private const string KeyTwo = "second-signing-key-0123456789-abcdefghi";
    private const string KeyThree = "third-signing-key-0123456789-abcdefghij";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-06-01T12:00:00Z", CultureInfo.InvariantCulture));

    [Fact]
    public void New_tokens_carry_the_current_key_id_and_validate()
    {
        var service = Service(Current("k1", KeyOne));

        var (token, expires) = service.Issue("session-1");

        Assert.StartsWith("v2.k1.", token, StringComparison.Ordinal);
        Assert.True(service.TryValidate(token, out var claims));
        Assert.Equal("session-1", claims!.SessionId);
        Assert.Equal(expires, claims.ExpiresAt);
    }

    [Fact]
    public void A_token_issued_before_rotation_still_verifies_through_the_previous_key_and_new_tokens_use_the_new_kid()
    {
        var before = Service(Current("k1", KeyOne));
        var (oldToken, _) = before.Issue("session-1");

        var after = Service(Current("k2", KeyTwo, Previous("k1", KeyOne, _clock.GetUtcNow().AddDays(1))));
        var (newToken, _) = after.Issue("session-2");

        Assert.True(after.TryValidate(oldToken, out var oldClaims));
        Assert.Equal("session-1", oldClaims!.SessionId);
        Assert.StartsWith("v2.k2.", newToken, StringComparison.Ordinal);
        Assert.True(after.TryValidate(newToken, out _));
        Assert.Equal("k2", after.CurrentKeyId);

        // The pre-rotation deployment does not know the new key id.
        Assert.False(before.TryValidate(newToken, out _));
    }

    [Fact]
    public void A_previous_key_stops_verifying_at_its_retirement_but_current_tokens_keep_working()
    {
        var before = Service(Current("k1", KeyOne));
        var (oldToken, _) = before.Issue("session-1");
        var after = Service(Current("k2", KeyTwo, Previous("k1", KeyOne, _clock.GetUtcNow().AddMinutes(10))));
        var (newToken, _) = after.Issue("session-2");

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(after.TryValidate(oldToken, out _));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(after.TryValidate(oldToken, out _));
        Assert.True(after.TryValidate(newToken, out _));
    }

    [Fact]
    public void A_token_with_an_unknown_key_id_is_rejected()
    {
        var service = Service(Current("k2", KeyTwo, Previous("k1", KeyOne, _clock.GetUtcNow().AddDays(1))));
        var other = Service(Current("zz", KeyThree));
        var (foreign, _) = other.Issue("session-1");

        Assert.False(service.TryValidate(foreign, out _));
    }

    [Fact]
    public void Changing_the_key_id_of_a_valid_token_breaks_the_signature()
    {
        var service = Service(Current("k2", KeyTwo, Previous("k1", KeyOne, _clock.GetUtcNow().AddDays(1))));
        var (token, _) = service.Issue("session-1");

        var retargeted = token.Replace("v2.k2.", "v2.k1.", StringComparison.Ordinal);

        Assert.NotEqual(token, retargeted);
        Assert.False(service.TryValidate(retargeted, out _));
    }

    [Fact]
    public void Tampered_payloads_and_signatures_are_rejected()
    {
        var service = Service(Current("k1", KeyOne));
        var (token, _) = service.Issue("session-1");
        var parts = token.Split('.');

        var otherPayload = Service(Current("k1", KeyOne)).Issue("session-2").Token.Split('.')[2];
        var swappedPayload = $"{parts[0]}.{parts[1]}.{otherPayload}.{parts[3]}";
        var flippedSignature = $"{parts[0]}.{parts[1]}.{parts[2]}.{FlipLastChar(parts[3])}";
        var truncatedSignature = $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3][..^4]}";

        Assert.False(service.TryValidate(swappedPayload, out _));
        Assert.False(service.TryValidate(flippedSignature, out _));
        Assert.False(service.TryValidate(truncatedSignature, out _));
    }

    [Fact]
    public void Tokens_in_the_earlier_format_without_a_key_id_are_rejected_without_throwing()
    {
        var service = Service(Current("k1", KeyOne));
        var (token, _) = service.Issue("session-1");
        var parts = token.Split('.');
        var legacy = $"v1.{parts[2]}.{parts[3]}";

        Assert.False(service.TryValidate(legacy, out var claims));
        Assert.Null(claims);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v2")]
    [InlineData("v2.k1")]
    [InlineData("v2.k1.payload")]
    [InlineData("v2.k1.a.b.c")]
    [InlineData("v3.k1.a.b")]
    [InlineData("v2.k 1.a.b")]
    [InlineData("v2..a.b")]
    [InlineData("v2.k1.%%%.%%%")]
    [InlineData("not a token at all")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        var service = Service(Current("k1", KeyOne));

        Assert.False(service.TryValidate(token, out var claims));
        Assert.Null(claims);
    }

    [Fact]
    public void Overlong_tokens_are_rejected_before_any_parsing()
    {
        var service = Service(Current("k1", KeyOne));

        Assert.False(service.TryValidate("v2.k1." + new string('a', 600) + ".sig", out _));
    }

    [Fact]
    public void Tokens_still_expire_after_their_lifetime_whatever_key_signed_them()
    {
        var service = Service(Current("k1", KeyOne));
        var (token, expires) = service.Issue("session-1");

        _clock.SetUtcNow(expires);

        Assert.False(service.TryValidate(token, out _));
    }

    [Fact]
    public void Key_validation_rejects_duplicate_ids_short_keys_reused_keys_and_missing_retirement()
    {
        var validator = new AuthOptionsValidator();
        var valid = Current("k2", KeyTwo, Previous("k1", KeyOne, _clock.GetUtcNow().AddDays(1)));
        Assert.True(validator.Validate(null, valid).Succeeded);

        var duplicateId = Current("k2", KeyTwo, Previous("k2", KeyOne, _clock.GetUtcNow().AddDays(1)));
        var shortKey = Current("k2", KeyTwo, Previous("k1", "too-short", _clock.GetUtcNow().AddDays(1)));
        var sameKeyAsCurrent = Current("k2", KeyTwo, Previous("k1", KeyTwo, _clock.GetUtcNow().AddDays(1)));
        var sameKeyTwice = Current("k3", KeyThree, Previous("k1", KeyOne, _clock.GetUtcNow().AddDays(1)), Previous("k2", KeyOne, _clock.GetUtcNow().AddDays(1)));
        var noRetirement = Current("k2", KeyTwo, Previous("k1", KeyOne, null));
        var badId = Current("k2", KeyTwo, Previous("bad id!", KeyOne, _clock.GetUtcNow().AddDays(1)));

        Assert.Contains("duplicates", Failure(validator, duplicateId), StringComparison.Ordinal);
        Assert.Contains("at least 32", Failure(validator, shortKey), StringComparison.Ordinal);
        Assert.Contains("must differ", Failure(validator, sameKeyAsCurrent), StringComparison.Ordinal);
        Assert.Contains("must differ", Failure(validator, sameKeyTwice), StringComparison.Ordinal);
        Assert.Contains("RetiresAt is required", Failure(validator, noRetirement), StringComparison.Ordinal);
        Assert.Contains("KeyId must be", Failure(validator, badId), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_refuses_to_start_with_an_invalid_previous_key()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Auth:PreviousSessionTokenKeys:0:KeyId"] = "k0",
            ["Auth:PreviousSessionTokenKeys:0:Key"] = KeyOne,
        });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("RetiresAt", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Through_the_real_host_rotation_keeps_old_sessions_alive_until_the_previous_key_retires()
    {
        using var first = new ApiFactory();
        var configuration = first.Services.GetRequiredService<IConfiguration>();
        var firstApi = new TestApi(first.CreateClient());
        var oldToken = await firstApi.StartSessionAsync();
        Assert.Equal(HttpStatusCode.OK, (await firstApi.MessageAsync(oldToken, "hello")).StatusCode);

        // A second deployment after rotation shares the database but signs with k2 and trusts k1 for ten more minutes.
        using var second = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = configuration["ConnectionStrings:Sqlite"],
            ["Llm:Scripted:ScriptPath"] = configuration["Llm:Scripted:ScriptPath"],
            ["DataProtection:KeyRingPath"] = configuration["DataProtection:KeyRingPath"],
            ["Auth:SessionTokenSigningKey"] = KeyTwo,
            ["Auth:SessionTokenKeyId"] = "k2",
            ["Auth:PreviousSessionTokenKeys:0:KeyId"] = "k1",
            ["Auth:PreviousSessionTokenKeys:0:Key"] = ApiFactory.SigningKey,
            ["Auth:PreviousSessionTokenKeys:0:RetiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture),
        });
        var secondApi = new TestApi(second.CreateClient());

        Assert.Equal(HttpStatusCode.OK, (await secondApi.MessageAsync(oldToken, "still me")).StatusCode);
        var newToken = await secondApi.StartSessionAsync();
        Assert.StartsWith("v2.k2.", newToken, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await secondApi.MessageAsync(newToken, "hi")).StatusCode);

        // The old deployment cannot verify tokens signed with the new key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await firstApi.MessageAsync(newToken, "hi")).StatusCode);

        second.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondApi.MessageAsync(oldToken, "too late")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondApi.MessageAsync(newToken, "still fine")).StatusCode);
    }

    private SessionTokenService Service(AuthOptions options) => new(Options.Create(options), _clock);

    private static AuthOptions Current(string keyId, string key, params PreviousSigningKeyOptions[] previous) => new()
    {
        SessionTokenKeyId = keyId,
        SessionTokenSigningKey = key,
        PreviousSessionTokenKeys = [.. previous],
        ClientKeys = [new ClientKeyOptions { Name = "test", Key = "test-client-key-0123456789" }],
    };

    private static PreviousSigningKeyOptions Previous(string keyId, string key, DateTimeOffset? retiresAt) =>
        new() { KeyId = keyId, Key = key, RetiresAt = retiresAt };

    private static string Failure(AuthOptionsValidator validator, AuthOptions options)
    {
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        return string.Join(" | ", result.Failures!);
    }

    private static string FlipLastChar(string value) => value[..^1] + (value[^1] == 'A' ? 'B' : 'A');
}
