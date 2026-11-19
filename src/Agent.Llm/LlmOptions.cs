using System.ComponentModel.DataAnnotations;

namespace Agent.Llm;

public enum LlmProvider
{
    Anthropic,
    Scripted,
    Gemini,
    Groq,
}

/// <summary>
/// Provider selection is a deliberate manual switch read once at startup. There is no automatic failover between
/// providers: a silent switch would change behaviour, cost and data handling without anyone deciding it.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;
}

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Supplied through configuration or the environment (Anthropic__ApiKey). Never logged.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Anthropic:ApiKey is required when Llm:Provider is Anthropic.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    /// <summary>Model identifier. Models differ in which sampling parameters they accept; see Agent:Temperature.</summary>
    [Required]
    public string Model { get; set; } = "claude-sonnet-5-5";

    [Required]
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Per-attempt timeout for one model call.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Base delay before the single retry of a transient failure. Jitter is added.</summary>
    [Range(0, 60000)]
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>Longest wait honoured when the provider answers a transient failure with Retry-After.</summary>
    [Range(0, 120)]
    public int MaxRetryAfterSeconds { get; set; } = 10;
}

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Supplied through configuration or the environment (Gemini__ApiKey). Sent in a header, never in the URL, never logged.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Gemini:ApiKey is required when Llm:Provider is Gemini.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    [Required]
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Per-attempt timeout for one model call.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Base delay before the single retry of a transient failure. Jitter is added.</summary>
    [Range(0, 60000)]
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>Longest wait honoured when the provider answers a transient failure with Retry-After.</summary>
    [Range(0, 120)]
    public int MaxRetryAfterSeconds { get; set; } = 10;
}

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    /// <summary>Supplied through configuration or the environment (Groq__ApiKey). Sent as a bearer token, never in the URL, never logged.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Groq:ApiKey is required when Llm:Provider is Groq.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1/";

    /// <summary>No default: the hosted catalogue changes and is made of third-party models, so the choice is always explicit.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Groq:Model is required when Llm:Provider is Groq.")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Per-attempt timeout for one model call.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Base delay before the single retry of a transient failure. Jitter is added.</summary>
    [Range(0, 60000)]
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>Longest wait honoured when the provider answers a transient failure with Retry-After.</summary>
    [Range(0, 120)]
    public int MaxRetryAfterSeconds { get; set; } = 10;
}

public sealed class ScriptedLlmOptions
{
    public const string SectionName = "Llm:Scripted";

    [Required(AllowEmptyStrings = false, ErrorMessage = "Llm:Scripted:ScriptPath is required when Llm:Provider is Scripted.")]
    public string ScriptPath { get; set; } = string.Empty;
}
