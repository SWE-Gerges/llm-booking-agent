using Agent.Abstractions.Llm;

namespace Agent.Llm.Shared;

internal static class SystemPrompts
{
    /// <summary>
    /// Providers without prompt-cache breakpoints get one system text: the stable block first, then the volatile one.
    /// The order is fixed so the stable prefix stays identical between calls and any implicit prefix caching can apply.
    /// </summary>
    public static string Concatenate(LlmChatRequest request) =>
        string.Join("\n\n", new[] { request.SystemStable, request.SystemVolatile }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
