using Agent.Abstractions.Llm;
using Agent.Llm;
using Agent.Llm.Anthropic;
using Agent.Llm.Gemini;
using Agent.Llm.Groq;
using Agent.Llm.Scripted;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Tests.LlmClients;

/// <summary>Provider selection is configuration only: exactly one client is registered and bad settings fail at startup.</summary>
public sealed class LlmProviderSwitchTests : IDisposable
{
    private readonly string _script = Path.Combine(Path.GetTempPath(), "llm-switch-" + Guid.NewGuid().ToString("N") + ".json");

    public LlmProviderSwitchTests() => File.WriteAllText(_script, """{ "steps": [ { "text": "ok" } ] }""");

    public void Dispose() => File.Delete(_script);

    private IHostApplicationBuilder Builder(IDictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAgentLlm(builder.Configuration);
        return builder;
    }

    [Theory]
    [InlineData("Scripted", typeof(ScriptedLlmClient))]
    [InlineData("Anthropic", typeof(AnthropicChatClient))]
    [InlineData("Gemini", typeof(GeminiChatClient))]
    [InlineData("Groq", typeof(GroqChatClient))]
    public async Task The_configured_provider_is_the_one_registered(string provider, Type expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = provider,
            ["Llm:Scripted:ScriptPath"] = _script,
            ["Anthropic:ApiKey"] = "k",
            ["Gemini:ApiKey"] = "k",
            ["Groq:ApiKey"] = "k",
            ["Groq:Model"] = "test-model",
        };
        var builder = Builder(settings);

        using var host = ((HostApplicationBuilder)builder).Build();
        await host.StartAsync();

        Assert.IsType(expected, host.Services.GetRequiredService<ILlmClient>());
        Assert.Equal(1, builder.Services.Count(d => d.ServiceType == typeof(ILlmClient)));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Anthropic", "Anthropic:ApiKey")]
    [InlineData("Gemini", "Gemini:ApiKey")]
    [InlineData("Groq", "Groq:ApiKey")]
    public async Task A_missing_api_key_fails_at_startup_and_names_the_setting(string provider, string setting)
    {
        var builder = Builder(new Dictionary<string, string?> { ["Llm:Provider"] = provider, ["Groq:Model"] = "test-model" });
        using var host = ((HostApplicationBuilder)builder).Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groq_has_no_default_model_and_fails_at_startup_without_one()
    {
        Assert.Equal(string.Empty, new GroqOptions().Model);
        var builder = Builder(new Dictionary<string, string?> { ["Llm:Provider"] = "Groq", ["Groq:ApiKey"] = "k" });
        using var host = ((HostApplicationBuilder)builder).Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Groq:Model", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scripted_provider_needs_a_script_path()
    {
        var builder = Builder(new Dictionary<string, string?> { ["Llm:Provider"] = "Scripted" });
        using var host = ((HostApplicationBuilder)builder).Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("ScriptPath", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_name_fails_when_the_services_are_registered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Provider"] = "Mystery" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentLlm(configuration));
    }

    [Fact]
    public async Task Options_of_unselected_providers_are_not_validated()
    {
        // Gemini is selected and valid; the Anthropic section is empty and must not matter.
        var builder = Builder(new Dictionary<string, string?> { ["Llm:Provider"] = "Gemini", ["Gemini:ApiKey"] = "k" });
        using var host = ((HostApplicationBuilder)builder).Build();

        await host.StartAsync();

        Assert.IsType<GeminiChatClient>(host.Services.GetRequiredService<ILlmClient>());
        await host.StopAsync();
    }

    [Fact]
    public void Defaults_are_documented_and_per_client()
    {
        Assert.Equal("claude-sonnet-5-5", new AnthropicOptions().Model);
        Assert.Equal("gemini-2.5-flash", new GeminiOptions().Model);
        Assert.Equal(60, new AnthropicOptions().TimeoutSeconds);
        Assert.Equal(60, new GeminiOptions().TimeoutSeconds);
        Assert.Equal(30, new GroqOptions().TimeoutSeconds);
        Assert.Equal(10, new GroqOptions().MaxRetryAfterSeconds);
        Assert.Equal("https://api.groq.com/openai/v1/", new GroqOptions().BaseUrl);
        Assert.Equal("https://generativelanguage.googleapis.com/", new GeminiOptions().BaseUrl);
    }

    [Theory]
    [InlineData("Gemini:TimeoutSeconds", "0")]
    [InlineData("Gemini:MaxRetryAfterSeconds", "500")]
    [InlineData("Gemini:BaseUrl", "not a url")]
    public async Task Out_of_range_or_malformed_numbers_and_urls_fail_at_startup(string key, string value)
    {
        var builder = Builder(new Dictionary<string, string?> { ["Llm:Provider"] = "Gemini", ["Gemini:ApiKey"] = "k", [key] = value });
        using var host = ((HostApplicationBuilder)builder).Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
