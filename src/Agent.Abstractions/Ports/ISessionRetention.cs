namespace Agent.Abstractions.Ports;

/// <param name="SessionsDeleted">Expired sessions removed in this run (their messages and booking contexts go with them).</param>
/// <param name="Batches">Delete batches executed.</param>
/// <param name="Truncated">True when the run stopped at its batch limit while more expired sessions may remain.</param>
public sealed record RetentionResult(int SessionsDeleted, int Batches, bool Truncated);

/// <summary>Removes data of sessions that stayed idle beyond the retention horizon. Safe to run repeatedly and concurrently.</summary>
public interface ISessionRetention
{
    Task<RetentionResult> PurgeExpiredAsync(CancellationToken cancellationToken);
}
