using Agent.Api.Endpoints;
using Agent.Api.Infrastructure;
using Agent.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agent.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>Registers authentication, error handling, rate limiting and OpenAPI for the HTTP surface.</summary>
    public static IServiceCollection AddAgentApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<ClientKeyValidator>();
        services.AddSingleton<SessionTokenService>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ClientKeyAuthenticationHandler>(AuthSchemes.ClientKey, _ => { })
            .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(AuthSchemes.SessionToken, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.ClientKey, policy => policy.AddAuthenticationSchemes(AuthSchemes.ClientKey).RequireAuthenticatedUser())
            .AddPolicy(AuthPolicies.Session, policy => policy.AddAuthenticationSchemes(AuthSchemes.SessionToken).RequireAuthenticatedUser());

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                if (CorrelationIdMiddleware.Get(context.HttpContext) is { } correlationId)
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId;
                }
            });
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.AddAgentRateLimiting();
        services.AddAgentOpenApi();
        return services;
    }

    public static WebApplication UseAgentApi(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next();
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOpenApi();
        app.MapChatEndpoints();
        app.MapReferenceEndpoints();
        return app;
    }
}
