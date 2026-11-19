using System.Collections.Concurrent;
using Agent.Abstractions.Ports;

namespace Agent.Core.Conversation;

/// <summary>
/// Serialises turns per session inside one process. A second request that arrives while a turn is running is refused
/// rather than queued, which keeps a runaway client from stacking model calls. With several API instances, register
/// the Redis gate instead (Agent.Persistence) so the rule holds across processes.
/// </summary>
public sealed class InProcessTurnGate : ITurnGate
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public ValueTask<ITurnLease?> TryEnterAsync(string sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ITurnLease?>(_active.TryAdd(sessionId, 0) ? new Lease(this, sessionId) : null);

    private sealed class Lease(InProcessTurnGate gate, string sessionId) : ITurnLease
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate._active.TryRemove(sessionId, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
