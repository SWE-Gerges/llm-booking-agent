namespace Agent.IntegrationTests.Support;

public static class RepoPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string RepoRoot => Root.Value;

    public static string OpenApiSnapshot => Path.Combine(RepoRoot, "docs", "openapi.json");

    public static string DemoScript => Path.Combine(RepoRoot, "demo", "demo-script.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LlmBookingAgent.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate LlmBookingAgent.slnx above the test output folder.");
    }
}
