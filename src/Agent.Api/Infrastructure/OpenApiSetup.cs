using Agent.Api.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace Agent.Api.Infrastructure;

public static class OpenApiSetup
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddAgentOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "LLM Booking Agent API",
                    Version = "1.0.0",
                    Description = "REST backend for an LLM-orchestrated flight and hotel booking assistant. "
                        + "Start a session with an integrator key, then chat with the returned session token. "
                        + "Money and identity never come from the model; forms bypass it.",
                };

                // The host is whatever served the request; leaving it out keeps the committed document stable.
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["ClientKey"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Header,
                        Name = "X-Client-Key",
                        Description = "Integrator key, used only to start sessions and list tools.",
                    },
                    ["SessionToken"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "Signed session token",
                        Description = "Token returned by POST /api/v1/chat/start.",
                    },
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var policy = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAuthorizeData>()
                    .Select(a => a.Policy)
                    .FirstOrDefault(p => p is not null);

                var scheme = policy switch
                {
                    AuthPolicies.ClientKey => "ClientKey",
                    AuthPolicies.Session => "SessionToken",
                    _ => null,
                };

                if (scheme is not null && context.Document is { } document)
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] },
                    ];
                }

                return Task.CompletedTask;
            });
        });
        return services;
    }
}
