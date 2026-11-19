using System.Text.Json;
using Agent.Abstractions.Llm;
using Agent.Llm.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Llm.Anthropic;

/// <summary>
/// Messages API client over a typed <see cref="HttpClient"/>. Transient failures (connection errors, 408/429/5xx) are
/// retried once by the HTTP pipeline; that is safe because a model call has no side effects. Retries never span a
/// tool execution because tools run between model calls, outside this class.
/// </summary>
public sealed class AnthropicChatClient(
    HttpClient http,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicChatClient> logger,
    TimeProvider clock) : ILlmClient
{
    public async Task<LlmChatResult> SendAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var payload = AnthropicMapper.BuildRequest(request, settings.Model).ToJsonString();

        var body = await LlmHttp.PostJsonAsync(http, "v1/messages", payload, settings.ApiKey, AnthropicMapper.ErrorMessage, clock, cancellationToken);

        LlmChatResult result;
        try
        {
            result = AnthropicMapper.ParseResponse(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LlmApiException(
                "The model response could not be parsed.",
                200,
                LlmHttp.Redact(AnthropicMapper.Excerpt(body), settings.ApiKey),
                ex,
                LlmErrorKind.Permanent);
        }

        logger.LogInformation(
            "LLM call: model={Model} stop={Stop} input={Input} output={Output} cache_read={CacheRead} cache_write={CacheWrite}",
            settings.Model,
            result.StopReason,
            result.Usage.InputTokens,
            result.Usage.OutputTokens,
            result.Usage.CacheReadTokens,
            result.Usage.CacheWriteTokens);
        return result;
    }
}
