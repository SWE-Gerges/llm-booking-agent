# ADR 0004: Guard layer and its ordering

## Context

Before a user message reaches a model, three cheap checks apply: per-session throttling, abuse (the same message
sent repeatedly) and sensitive data (payment numbers). Each can answer without the model, and the order changes both
cost and safety: a check that mutates state or inspects content should not run for traffic that is already being
throttled.

## Decision

`IInboundGuard` implementations run in registration order through `GuardPipeline`, which stops at the first guard
that blocks:

1. `SessionRateLimitGuard`: atomic per-session counter (Redis `INCR` and `EXPIRE` in one transaction on a
   window-bucketed key, in-memory fallback). Enforced by default (30 messages per minute per session).
   A throttled message is not stored and the API answers 429 with `Retry-After`.
2. `RepeatedMessageGuard`: the same trimmed, lower-cased message more than three times in a row gets a canned reply.
   Only a hash is kept in session state. Nothing is stored for blocked repeats.
3. `SensitiveDataGuard`: card numbers (known issuer prefix and Luhn), IBANs (country length and mod-97) and long digit
   runs next to a financial keyword in English or Arabic. Phone and passport numbers are deliberately allowed. The
   exchange is stored with a redacted placeholder, only the kind of data is logged.

A per-IP ASP.NET rate limiter sits in front of all of this as a backstop, and `/chat/start` has its own limit per
integrator key.

## Consequences

- Throttled clients cost one counter increment, not a content scan or a model call.
- Repeated-message detection runs before content inspection, so a flood of identical sensitive messages is cut off
  by the repeat guard rather than re-scanned.
- The order is covered by a test on the DI registration order. Changing it is a deliberate, reviewed act.
- Heuristic detection has false positives and negatives; it reduces exposure, it does not replace not asking for the
  data in the first place (see ADR 0003).
