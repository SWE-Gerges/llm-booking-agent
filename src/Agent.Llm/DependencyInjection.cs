using Agent.Abstractions.Llm;
using Agent.Llm.Anthropic;
using Agent.Llm.Gemini;
using Agent.Llm.Groq;
using Agent.Llm.Scripted;
using Agent.Llm.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Llm;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the single <see cref="ILlmClient"/> selected by <c>Llm:Provider</c> (Scripted, Anthropic, Gemini or Groq).
    /// Only the chosen provider's options are bound and validated; there is no failover between providers.
    /// </summary>
    public static IServiceCollection AddAgentLlm(this IServiceCollection services, IConfiguration configuration)
    {
        var llm = new LlmOptions();
        configuration.GetSection(LlmOptions.SectionName).Bind(llm);
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        switch (llm.Provider)
        {
            case LlmProvider.Anthropic:
                AddAnthropic(services, configuration);
                break;
            case LlmProvider.Gemini:
                AddGemini(services, configuration);
                break;
            case LlmProvider.Groq:
                AddGroq(services, configuration);
                break;
            case LlmProvider.Scripted:
                AddScripted(services, configuration);
                break;
            default:
                throw new InvalidOperationException($"Unsupported Llm:Provider '{llm.Provider}'.");
        }

        return services;
    }

    private static void AddAnthropic(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddLlmHttpClient<AnthropicChatClient, AnthropicOptions>(
            "anthropic-messages",
            o => Settings(o.BaseUrl, o.TimeoutSeconds, o.RetryDelayMilliseconds, o.MaxRetryAfterSeconds),
            (client, o) =>
            {
                client.DefaultRequestHeaders.Add("x-api-key", o.ApiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", o.ApiVersion);
            });
    }

    private static void AddGemini(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GeminiOptions>()
            .Bind(configuration.GetSection(GeminiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddLlmHttpClient<GeminiChatClient, GeminiOptions>(
            "gemini-generate-content",
            o => Settings(o.BaseUrl, o.TimeoutSeconds, o.RetryDelayMilliseconds, o.MaxRetryAfterSeconds),
            (client, o) => client.DefaultRequestHeaders.Add("x-goog-api-key", o.ApiKey));
    }

    private static void AddGroq(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GroqOptions>()
            .Bind(configuration.GetSection(GroqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddLlmHttpClient<GroqChatClient, GroqOptions>(
            "groq-chat-completions",
            o => Settings(o.BaseUrl, o.TimeoutSeconds, o.RetryDelayMilliseconds, o.MaxRetryAfterSeconds),
            (client, o) => client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.ApiKey));
    }

    private static void AddScripted(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScriptedLlmOptions>()
            .Bind(configuration.GetSection(ScriptedLlmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider =>
        {
            var path = provider.GetRequiredService<IOptions<ScriptedLlmOptions>>().Value.ScriptPath;
            var root = provider.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
            return ScriptDefinition.Parse(File.ReadAllText(Path.GetFullPath(path, root)));
        });
        services.AddSingleton<ILlmClient, ScriptedLlmClient>();
    }

    private static LlmHttpSettings Settings(string baseUrl, int timeoutSeconds, int retryDelayMilliseconds, int maxRetryAfterSeconds) => new(
        new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
        TimeSpan.FromSeconds(timeoutSeconds),
        TimeSpan.FromMilliseconds(retryDelayMilliseconds),
        TimeSpan.FromSeconds(maxRetryAfterSeconds));
}
