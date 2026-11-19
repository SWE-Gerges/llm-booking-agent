# ADR 0008: Distributed turn lock

## Context

A turn (a chat message or a form submission) reads the session, may call the model several times, runs tools with side
effects and writes the history back. Two turns for the same session running at once would interleave history writes
and could act twice on the same intent. The first version serialised turns with an in-process dictionary, which only
holds while there is a single API instance.

## Decision

Turn admission is a port, `ITurnGate.TryEnterAsync(sessionId)`, returning a lease or null. It never waits: contention
is answered, not queued.

- **In-process gate** (`InProcessTurnGate`, Agent.Core) is the default and what tests use.
- **Redis gate** (Agent.Persistence) is selected when `ConnectionStrings:Redis` is set. A lease is the key
  `turn:session:{id}` created with `SET key token NX PX lease`, where the token is random per attempt. A running turn
  renews the lease every third of its lifetime with a compare-and-pexpire Lua script; release is a compare-and-delete
  Lua script. Both scripts check the token, so a holder whose lease already expired can neither extend nor delete the
  next holder's lease.
- **Bound.** The lease lasts `Turn:LeaseSeconds` (default 60). A live turn keeps renewing for as long as it runs, even
  past the 3-minute turn deadline; a crashed holder blocks its session for at most one lease.
- **Redis outage.** If Redis cannot be reached the call logs a warning and uses a process-local lock for that request,
  so chat stays available. Exclusivity then holds only within one process until Redis returns. If renewals fail for
  longer than a lease, the lease can expire under a live turn; the same degradation applies and a second request may
  be admitted. This is accepted: a short window of weaker exclusivity is preferred to chat going down with Redis.
- **Contention policy.** The second request for the same session gets `409 Conflict` as problem+json with a
  `Retry-After` header (`Turn:ContentionRetryAfterSeconds`, default 2). Chat messages and form submissions share the
  same gate, so a form cannot slip in beside a running turn.

## Consequences

- Several API instances can share one Redis and keep the one-turn-per-session rule without sticky routing.
- A client that retries on 409 after the advertised delay converges without queues building up in the server.
- This is a lease, not a consensus lock: it assumes one Redis primary and bounded clock drift of the Redis server's
  key expiry, and it does not fence writes. Stores still protect their own invariants (unique indexes, optimistic
  versions), so a rare double admission cannot corrupt booking data, though a tool call could run twice; submit tools
  stay idempotent for that reason.
- Without Redis the gate is per process, which is the documented single-instance setup.
