using System.Text.Json;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Llm.Groq;

/// <summary>
/// Chat-completions client over a typed <see cref="HttpClient"/>. The API key travels as a bearer token only.
/// Transient failures are retried once by the HTTP pipeline (honouring Retry-After up to a cap); prompt-cache
/// breakpoints do not exist for this provider, so the stable and volatile system text are concatenated.
/// </summary>
public sealed class GroqChatClient(
    HttpClient http,
    IOptions<GroqOptions> options,
    ILogger<GroqChatClient> logger,
    TimeProvider clock) : ILlmClient
{
    public async Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var payload = GroqMapper.BuildRequest(request, settings.Model).ToJsonString();

        var body = await LlmHttp.PostJsonAsync(http, "chat/completions", payload, settings.ApiKey, GroqMapper.ErrorMessage, clock, cancellationToken);

        LlmChatResult result;
        try
        {
            result = GroqMapper.ParseResponse(body);
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
            "LLM call: provider=groq model={Model} stop={Stop} input={Input} output={Output} cache_read={CacheRead}",
            settings.Model,
            result.StopReason,
            result.Usage.InputTokens,
            result.Usage.OutputTokens,
            result.Usage.CacheReadTokens);
        return result;
    }
}
