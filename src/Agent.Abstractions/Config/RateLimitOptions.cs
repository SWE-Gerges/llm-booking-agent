using System.ComponentModel.DataAnnotations;

namespace Agent.Abstractions.Config;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Enforcement is on by default; switching it off is meant for load tests and local experiments only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Messages one chat session may send per window.</summary>
    [Range(1, 100000)]
    public int SessionMessagesPerWindow { get; set; } = 30;

    /// <summary>Requests one client IP may send per window across the API (backstop behind the per-session limit).</summary>
    [Range(1, 1000000)]
    public int IpRequestsPerWindow { get; set; } = 120;

    /// <summary>Sessions one integrator key may start per window.</summary>
    [Range(1, 1000000)]
    public int SessionStartsPerWindow { get; set; } = 60;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
