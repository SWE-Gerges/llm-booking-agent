using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

public static class AuthSchemes
{
    public const string ClientKey = "ClientKey";
    public const string SessionToken = "SessionToken";
    public const string ClientKeyHeader = "X-Client-Key";
    public const string SessionClaim = "sid";
    public const string ClientClaim = "client_name";
}

/// <summary>Matches a presented key against every configured key in constant time, without an early exit.</summary>
public sealed class ClientKeyValidator
{
    private readonly (string Name, byte[] Hash)[] _keys;

    public ClientKeyValidator(IOptions<AuthOptions> options) =>
        _keys = options.Value.ClientKeys.Select(k => (k.Name, SHA256.HashData(Encoding.UTF8.GetBytes(k.Key)))).ToArray();

    /// <summary>Returns the matching key's name, or null. Comparing fixed-size hashes also hides key length.</summary>
    public string? Validate(string? candidate)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty));
        string? match = null;
        foreach (var (name, expected) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                match = name;
            }
        }

        return match;
    }
}

internal sealed class ClientKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ClientKeyValidator validator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AuthSchemes.ClientKeyHeader, out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var name = validator.Validate(header.ToString());
        if (name is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The client key is not valid."));
        }

        var identity = new ClaimsIdentity([new Claim(AuthSchemes.ClientClaim, name)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal sealed class SessionTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!tokens.TryValidate(header[BearerPrefix.Length..].Trim(), out var claims) || claims is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The session token is not valid or has expired."));
        }

        var identity = new ClaimsIdentity([new Claim(AuthSchemes.SessionClaim, claims.SessionId)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
