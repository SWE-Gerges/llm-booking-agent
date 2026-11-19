using System.Globalization;
using Agent.Abstractions.Config;
using Agent.Abstractions.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent.Api.Infrastructure;

/// <summary>Maps domain exceptions to RFC 7807 responses. Unknown failures return a generic 500 and are logged, never echoed.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, IOptions<TurnOptions> turn, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var (status, title, detail) = exception switch
        {
            SessionNotFoundException => (StatusCodes.Status404NotFound, "Session not found", "The session does not exist. Start a new session."),
            SessionExpiredException => (StatusCodes.Status410Gone, "Session expired", "The session expired after a period of inactivity. Start a new session."),
            TurnInProgressException => (StatusCodes.Status409Conflict, "Request already in progress", "Another request for this session is still being processed. Wait for it to finish."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "The request could not be completed."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        if (exception is TurnInProgressException)
        {
            httpContext.Response.Headers.RetryAfter = turn.Value.ContentionRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = null,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}
