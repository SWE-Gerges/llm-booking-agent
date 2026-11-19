namespace Agent.Abstractions.Ports;

/// <summary>Proof that the caller owns the right to run a turn for one session. Disposing it releases the right.</summary>
public interface ITurnLease : IAsyncDisposable
{
}

/// <summary>
/// Admits at most one turn (a chat message or a form submission) per session at a time. Implementations are
/// non-blocking: contention returns null instead of waiting, so a runaway client cannot stack work behind a slow turn.
/// </summary>
public interface ITurnGate
{
    /// <returns>A lease, or null when another turn for the same session is already running.</returns>
    ValueTask<ITurnLease?> TryEnterAsync(string sessionId, CancellationToken cancellationToken);
}
