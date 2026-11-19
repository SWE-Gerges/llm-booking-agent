using System.ComponentModel.DataAnnotations;

namespace Agent.Abstractions.Config;

public sealed class TurnOptions
{
    public const string SectionName = "Turn";

    /// <summary>Value of the Retry-After header on the 409 returned when a turn for the same session is already running.</summary>
    [Range(1, 60)]
    public int ContentionRetryAfterSeconds { get; set; } = 2;

    /// <summary>
    /// Lifetime of the distributed lease. A live turn renews it every third of this period; a crashed holder
    /// therefore blocks its session for at most this long. Not used by the in-process gate.
    /// </summary>
    [Range(3, 600)]
    public int LeaseSeconds { get; set; } = 60;

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public TimeSpan RenewInterval => TimeSpan.FromSeconds(LeaseSeconds / 3.0);
}
