using System.Text.RegularExpressions;

namespace Agent.Api.Infrastructure;

/// <summary>Accepts a well-formed <c>X-Correlation-Id</c> or generates one, echoes it, and adds it to the log scope.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var id = Valid().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) => context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Valid();
}
