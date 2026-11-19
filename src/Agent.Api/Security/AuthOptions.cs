using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

public sealed class ClientKeyOptions
{
    [Required]
    [RegularExpression("^[A-Za-z0-9._-]{1,64}$", ErrorMessage = "Client key names may contain letters, digits, '.', '_' and '-'.")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MinLength(16, ErrorMessage = "Client keys must be at least 16 characters.")]
    public string Key { get; set; } = string.Empty;
}

public sealed class PreviousSigningKeyOptions
{
    [Required]
    [RegularExpression("^[A-Za-z0-9_-]{1,32}$", ErrorMessage = "Key ids may contain letters, digits, '_' and '-' (at most 32 characters).")]
    public string KeyId { get; set; } = string.Empty;

    [Required]
    [MinLength(32, ErrorMessage = "Previous signing keys must be at least 32 characters.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The key stops verifying tokens at this instant. Required: a previous key must never live forever.</summary>
    public DateTimeOffset? RetiresAt { get; set; }
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Integrator keys accepted by POST /chat/start. Each key is rate limited on its own.</summary>
    public List<ClientKeyOptions> ClientKeys { get; set; } = [];

    /// <summary>HMAC key for session tokens. Supply it through configuration or the environment; at least 32 characters.</summary>
    [Required]
    [MinLength(32, ErrorMessage = "Auth:SessionTokenSigningKey must be at least 32 characters.")]
    public string SessionTokenSigningKey { get; set; } = string.Empty;

    /// <summary>Identifier of the current signing key, written into every new token as its <c>kid</c>.</summary>
    [Required]
    [RegularExpression("^[A-Za-z0-9_-]{1,32}$", ErrorMessage = "Auth:SessionTokenKeyId may contain letters, digits, '_' and '-' (at most 32 characters).")]
    public string SessionTokenKeyId { get; set; } = "k1";

    /// <summary>Earlier signing keys that still verify tokens until their <see cref="PreviousSigningKeyOptions.RetiresAt"/>. New tokens never use them.</summary>
    public List<PreviousSigningKeyOptions> PreviousSessionTokenKeys { get; set; } = [];

    /// <summary>Session tokens expire this long after they are issued, whatever the activity.</summary>
    [Range(1, 1440)]
    public int SessionTokenLifetimeMinutes { get; set; } = 120;

    public TimeSpan SessionTokenLifetime => TimeSpan.FromMinutes(SessionTokenLifetimeMinutes);
}

internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();
        if (options.ClientKeys.Count == 0)
        {
            failures.Add("Auth:ClientKeys must contain at least one integrator key.");
        }

        if (options.ClientKeys.GroupBy(k => k.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            failures.Add("Auth:ClientKeys names must be unique.");
        }

        for (var i = 0; i < options.ClientKeys.Count; i++)
        {
            var key = options.ClientKeys[i];
            if (string.IsNullOrWhiteSpace(key.Name) || key.Key is not { Length: >= 16 })
            {
                failures.Add($"Auth:ClientKeys:{i} needs a Name and a Key of at least 16 characters.");
            }
        }

        ValidateSigningKeys(options, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSigningKeys(AuthOptions options, List<string> failures)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { options.SessionTokenKeyId };
        var keys = new HashSet<string>(StringComparer.Ordinal) { options.SessionTokenSigningKey };

        for (var i = 0; i < options.PreviousSessionTokenKeys.Count; i++)
        {
            var previous = options.PreviousSessionTokenKeys[i];
            if (string.IsNullOrWhiteSpace(previous.KeyId) || !KeyIdShape.IsMatch(previous.KeyId))
            {
                failures.Add($"Auth:PreviousSessionTokenKeys:{i}:KeyId must be 1 to 32 letters, digits, '_' or '-'.");
            }
            else if (!ids.Add(previous.KeyId))
            {
                failures.Add($"Auth:PreviousSessionTokenKeys:{i}:KeyId duplicates another signing key id.");
            }

            if (previous.Key is not { Length: >= 32 })
            {
                failures.Add($"Auth:PreviousSessionTokenKeys:{i}:Key must be at least 32 characters.");
            }
            else if (!keys.Add(previous.Key))
            {
                failures.Add($"Auth:PreviousSessionTokenKeys:{i}:Key must differ from the current key and from every other previous key.");
            }

            if (previous.RetiresAt is null)
            {
                failures.Add($"Auth:PreviousSessionTokenKeys:{i}:RetiresAt is required.");
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex KeyIdShape = new("^[A-Za-z0-9_-]{1,32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
