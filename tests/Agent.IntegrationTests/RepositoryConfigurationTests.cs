using System.Text.Json;
using Agent.IntegrationTests.Support;

namespace Agent.IntegrationTests;

/// <summary>Checks configuration files, so a drift is caught by the test run.</summary>
public sealed class RepositoryConfigurationTests
{
    [Fact]
    public void No_configuration_suppresses_logging_above_warning()
    {
        var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "src", "Agent.Api", "appsettings.json")));
        var levels = appsettings.RootElement.GetProperty("Logging").GetProperty("LogLevel").EnumerateObject().ToList();

        Assert.NotEmpty(levels);
        Assert.All(levels, level => Assert.Contains(level.Value.GetString(), new[] { "Trace", "Debug", "Information", "Warning" }));
    }
}
