using System.Net.Http.Headers;
using System.Text;
using Agent.Abstractions.Llm;
using Polly.Timeout;

namespace Agent.Llm.Shared;

/// <summary>
/// Transport shell shared by the HTTP model clients: one POST, one body, provider-neutral error classification and
/// secret redaction. The retry itself lives in the HTTP pipeline (see <see cref="LlmResilience"/>), not here.
/// </summary>
internal static class LlmHttp
{
    private const int ExcerptLength = 300;
    private const string Redacted = "[redacted]";

    /// <summary>Posts a JSON payload and returns the body of a successful response, or throws a classified <see cref="LlmApiException"/>.</summary>
    /// <param name="secret">The API key in use. It is removed from anything copied out of a provider response.</param>
    /// <param name="errorMessage">Extracts the provider's own error message from an error body, or returns null.</param>
    public static async Task<string> PostJsonAsync(
        HttpClient http,
        string relativeUrl,
        string payload,
        string? secret,
        Func<string, string?> errorMessage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmApiException("The model provider could not be reached.", null, null, ex, LlmErrorKind.Transient);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new LlmApiException("The model provider did not answer in time.", null, null, ex, LlmErrorKind.Transient);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            var status = (int)response.StatusCode;
            var detail = Redact(errorMessage(body) ?? Excerpt(body), secret);
            throw new LlmApiException(
                $"The model provider returned {status}.",
                status,
                detail,
                kind: LlmApiException.Classify(status),
                retryAfter: ReadRetryAfter(response, clock));
        }
    }

    public static string Excerpt(string body, int max = ExcerptLength) =>
        body.Length <= max ? body : string.Concat(body.AsSpan(0, max), "...");

    /// <summary>Removes the API key from text that came from the provider, in case an error body echoes the credential.</summary>
    public static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, Redacted, StringComparison.Ordinal);

    /// <summary>Reads Retry-After as delta-seconds or an HTTP date. Negative and past values become zero.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response, TimeProvider clock) =>
        ReadRetryAfter(response.Headers.RetryAfter, clock);

    public static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header, TimeProvider clock)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - clock.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
