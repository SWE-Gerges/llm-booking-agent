using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Agent.Core.Options;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    [Required]
    public string AssistantName { get; set; } = "Aria";

    [Required]
    [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Currency must be an upper-case ISO 4217 code.")]
    public string Currency { get; set; } = "EUR";

    [Required]
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Session default locale used when the first message carries no script signal. "en" or "ar".</summary>
    [Required]
    public string DefaultLocale { get; set; } = "en";

    [Range(4, 400)]
    public int HistoryWindow { get; set; } = 20;

    /// <summary>History is trimmed in blocks of this size so the cached prompt prefix changes rarely.</summary>
    [Range(1, 100)]
    public int TrimChunk { get; set; } = 8;

    [Range(1, 32)]
    public int MaxToolIterations { get; set; } = 8;

    [Range(5, 900)]
    public int TurnTimeoutSeconds { get; set; } = 180;

    [Range(1, 20000)]
    public int MaxMessageLength { get; set; } = 2000;

    /// <summary>The same message may be sent this many times in a row; the next repeat is answered without the model.</summary>
    [Range(1, 20)]
    public int RepeatedMessageLimit { get; set; } = 3;

    [Range(1, 1440)]
    public int PriceConfirmationMaxAgeMinutes { get; set; } = 15;

    [Range(1, 1440)]
    public int SearchMaxAgeMinutes { get; set; } = 30;

    [Range(64, 8192)]
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Sampling temperature sent to the model. Unset by default because current Claude models reject non-default values; set it for models that accept it.</summary>
    [Range(0.0, 1.0)]
    public double? Temperature { get; set; }

    /// <summary>Property names removed from tool output before the model sees it (case-insensitive, any depth).</summary>
    public List<string> SanitizerStripProperties { get; set; } =
    [
        "supplierRef", "supplierId", "internalId", "rawPayload", "cost", "margin", "commission", "netPrice",
    ];

    public TimeSpan TurnTimeout => TimeSpan.FromSeconds(TurnTimeoutSeconds);

    public TimeSpan PriceConfirmationMaxAge => TimeSpan.FromMinutes(PriceConfirmationMaxAgeMinutes);

    public TimeSpan SearchMaxAge => TimeSpan.FromMinutes(SearchMaxAgeMinutes);
}

internal sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var failures = new List<string>();
        if (options.DefaultLocale is not ("en" or "ar"))
        {
            failures.Add("Agent:DefaultLocale must be 'en' or 'ar'.");
        }

        if (options.HistoryWindow <= options.TrimChunk)
        {
            failures.Add("Agent:HistoryWindow must be larger than Agent:TrimChunk.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            failures.Add($"Agent:TimeZone '{options.TimeZone}' is not a known time zone.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
