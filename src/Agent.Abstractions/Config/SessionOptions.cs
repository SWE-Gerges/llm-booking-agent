using System.ComponentModel.DataAnnotations;

namespace Agent.Abstractions.Config;

public sealed class SessionOptions
{
    public const string SectionName = "Session";

    /// <summary>Single idle timeout that governs both the cache TTL and when a session counts as expired.</summary>
    [Range(1, 1440)]
    public int IdleTtlMinutes { get; set; } = 30;

    public TimeSpan IdleTtl => TimeSpan.FromMinutes(IdleTtlMinutes);
}
