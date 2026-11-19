using Agent.Core.Options;
using Agent.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agent.IntegrationTests;

public sealed class OptionsBindingTests
{
    [Fact]
    public void An_empty_temperature_binds_to_null_so_the_parameter_is_omitted()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?> { ["Agent:Temperature"] = string.Empty });

        var options = factory.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

        Assert.Null(options.Temperature);
    }

    [Fact]
    public void Temperature_is_unset_by_default_and_can_be_configured()
    {
        using var unset = new ApiFactory();
        using var configured = new ApiFactory(overrides: new Dictionary<string, string?> { ["Agent:Temperature"] = "0.2" });

        Assert.Null(unset.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Temperature);
        Assert.Equal(0.2, configured.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Temperature);
    }

    [Fact]
    public void The_default_anthropic_model_is_sonnet_5_5_from_code_and_from_appsettings()
    {
        Assert.Equal("claude-sonnet-5-5", new Agent.Llm.AnthropicOptions().Model);

        using var factory = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "Anthropic",
            ["Anthropic:ApiKey"] = "test-key-not-used",
        });
        Assert.Equal("claude-sonnet-5-5", factory.Services.GetRequiredService<IOptions<Agent.Llm.AnthropicOptions>>().Value.Model);
    }

    [Fact]
    public void Invalid_options_fail_the_host_at_startup()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?> { ["Auth:SessionTokenSigningKey"] = "too-short" });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("SessionTokenSigningKey", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_client_keys_fail_the_host_at_startup()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?> { ["Auth:ClientKeys:0:Key"] = "short" });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ClientKeys", failure.ToString(), StringComparison.Ordinal);
    }
}
