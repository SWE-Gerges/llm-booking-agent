using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Agent.Abstractions.Config;
using Agent.Api.Security;
using Agent.Core.Telemetry;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Agent.Api.Infrastructure;

public static class RateLimitingSetup
{
    public const string StartPolicy = "start";

    // The global limiter runs first and the endpoint policy only after it admitted the request, so the limiter
    // that was consulted last is the one that rejected. Each partition factory leaves its name here.
    private const string SourceItem = "rate-limit-source";

    /// <summary>
    /// Per-IP backstop for every endpoint plus a per-integrator-key limit on session starts. The per-session message
    /// limit lives in the orchestrator's guard pipeline; these limits protect the API itself. Enforced by default.
    /// </summary>
    public static IServiceCollection AddAgentRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, configured) =>
        {
            var settings = configured.Value;
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!settings.Enabled || context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                context.Items[SourceItem] = "ip";

                var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(address, _ => Window(settings.IpRequestsPerWindow, settings));
            });

            limiter.AddPolicy(StartPolicy, context =>
            {
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                context.Items[SourceItem] = "start";

                // Partition on a digest so the key itself never becomes a dictionary key or shows up in diagnostics.
                var key = context.Request.Headers[AuthSchemes.ClientKeyHeader].ToString();
                var partition = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => Window(settings.SessionStartsPerWindow, settings));
            });

            limiter.OnRejected = async (rejection, cancellationToken) =>
            {
                var http = rejection.HttpContext;
                var retryAfter = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : settings.Window;
                var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);

                var source = http.Items.TryGetValue(SourceItem, out var recorded) ? recorded as string : null;
                http.RequestServices.GetRequiredService<AgentTelemetry>().RecordRateLimitRejection(source ?? "ip");

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = $"Rate limit exceeded. Retry after {seconds} seconds.",
                    },
                });
            };
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits, RateLimitOptions settings) => new()
    {
        PermitLimit = permits,
        Window = settings.Window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
