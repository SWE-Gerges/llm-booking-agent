using System.Text.Json;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Llm.Gemini;

/// <summary>
/// generateContent client over a typed <see cref="HttpClient"/>. The API key travels in the <c>x-goog-api-key</c>
/// header only. Transient failures are retried once by the HTTP pipeline; prompt-cache breakpoints do not exist for
/// this provider, so the stable and volatile system text are simply concatenated.
/// </summary>
public sealed class GeminiChatClient(
    HttpClient http,
    IOptions<GeminiOptions> options,
    ILogger<GeminiChatClient> logger,
    TimeProvider clock) : ILlmClient
{
    public async Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var payload = GeminiMapper.BuildRequest(request).ToJsonString();
        var url = $"v1beta/models/{Uri.EscapeDataString(settings.Model)}:generateContent";

        var body = await LlmHttp.PostJsonAsync(http, url, payload, settings.ApiKey, GeminiMapper.ErrorMessage, clock, cancellationToken);

        LlmChatResult result;
        try
        {
            result = GeminiMapper.ParseResponse(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new LlmApiException(
                "The model response could not be parsed.",
                200,
                LlmHttp.Redact(LlmHttp.Excerpt(body), settings.ApiKey),
                ex,
                LlmErrorKind.Permanent);
        }

        logger.LogInformation(
            "LLM call: provider=gemini model={Model} stop={Stop} input={Input} output={Output} cache_read={CacheRead}",
            settings.Model,
            result.StopReason,
            result.Usage.InputTokens,
            result.Usage.OutputTokens,
            result.Usage.CacheReadTokens);
        return result;
    }
}
