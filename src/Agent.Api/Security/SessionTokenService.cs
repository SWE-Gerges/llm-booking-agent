using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

public sealed record SessionTokenClaims(string SessionId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and validates short-lived session tokens: <c>v2.&lt;kid&gt;.&lt;payload&gt;.&lt;signature&gt;</c>, where the
/// signature is HMAC-SHA256 over <c>v2.&lt;kid&gt;.&lt;payload&gt;</c>. A token is bound to one session id and carries
/// its own expiry, so it can be checked without a lookup and cannot be moved to another session. The key id selects
/// the verification key: new tokens always use the current key, and a previous key keeps verifying older tokens only
/// until its configured retirement. Tokens without a key id (the earlier <c>v1</c> format) are rejected.
/// </summary>
public sealed partial class SessionTokenService
{
    private const string Version = "v2";
    private const int MaxTokenLength = 512;

    private readonly SigningKey _current;
    private readonly Dictionary<string, SigningKey> _byId;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;

    public SessionTokenService(IOptions<AuthOptions> options, TimeProvider clock)
    {
        var settings = options.Value;
        _current = new SigningKey(settings.SessionTokenKeyId, Encoding.UTF8.GetBytes(settings.SessionTokenSigningKey), null);
        _byId = new Dictionary<string, SigningKey>(StringComparer.Ordinal) { [_current.Id] = _current };
        foreach (var previous in settings.PreviousSessionTokenKeys)
        {
            _byId[previous.KeyId] = new SigningKey(previous.KeyId, Encoding.UTF8.GetBytes(previous.Key), previous.RetiresAt);
        }

        _lifetime = settings.SessionTokenLifetime;
        _clock = clock;
    }

    /// <summary>Id of the key that signs new tokens.</summary>
    public string CurrentKeyId => _current.Id;

    public (string Token, DateTimeOffset ExpiresAt) Issue(string sessionId)
    {
        var now = _clock.GetUtcNow();
        var expires = now.Add(_lifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(sessionId, now.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds()));
        var body = $"{Version}.{_current.Id}.{WebEncoders.Base64UrlEncode(payload)}";
        return ($"{body}.{WebEncoders.Base64UrlEncode(Sign(_current, body))}", expires);
    }

    public bool TryValidate(string? token, out SessionTokenClaims? claims)
    {
        claims = null;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Version || !KeyIdShape().IsMatch(parts[1]))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        if (!_byId.TryGetValue(parts[1], out var key) || (key.RetiresAt is { } retiresAt && now >= retiresAt))
        {
            return false;
        }

        byte[] signature;
        byte[] payloadBytes;
        try
        {
            signature = WebEncoders.Base64UrlDecode(parts[3]);
            payloadBytes = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(key, $"{parts[0]}.{parts[1]}.{parts[2]}")))
        {
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(payloadBytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || string.IsNullOrEmpty(payload.Sid))
        {
            return false;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (now >= expires)
        {
            return false;
        }

        claims = new SessionTokenClaims(payload.Sid, DateTimeOffset.FromUnixTimeSeconds(payload.Iat), expires);
        return true;
    }

    private static byte[] Sign(SigningKey key, string text) => HMACSHA256.HashData(key.Material, Encoding.UTF8.GetBytes(text));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex KeyIdShape();

    private sealed record SigningKey(string Id, byte[] Material, DateTimeOffset? RetiresAt);

    private sealed record Payload(
        [property: JsonPropertyName("sid")] string Sid,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}
