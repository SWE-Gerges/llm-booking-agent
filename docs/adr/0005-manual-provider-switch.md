# ADR 0005: Manual provider switch, no automatic failover

## Context

Running several model providers invites automatic failover: if one errors, try the next. For a booking assistant that
is risky. Providers differ in tool-call behaviour, prompt-cache semantics, data-handling terms and cost. A silent
switch mid-conversation changes all of these without anyone deciding it, and a conversation continued by a different
model may behave differently with the same tool results.

## Decision

The provider is chosen by configuration (`Llm:Provider` = `Scripted`, `Anthropic`, `Gemini` or `Groq`) and read once at
startup. Only the selected provider's client is registered and only its options are bound and validated (API key
required, `Groq:Model` required because that catalogue is third-party models and has no sensible default). There is no
failover. When the model fails, the turn ends with a localized apology, the history (including any tool side effects
that already happened) is persisted, and the client can retry.

Resilience stays inside one provider, through one shared HTTP pipeline: a per-attempt timeout (per-client setting) and
one retry for transient failures (connection errors, timeouts, 408, 429, 5xx) with jitter. When a transient response
carries `Retry-After`, that delay replaces the base delay, capped by `MaxRetryAfterSeconds` (default 10) so a hostile
or broken header cannot park a turn. Permanent 4xx responses are never retried. A model call has no side effects, so
the retry is safe; retries never span a tool execution, and the flight supplier adapter only retries GET requests.

Every client classifies its failures the same way on `LlmApiException`: `Transient`, `Permanent` or `Blocked` (a
safety or content-policy refusal), plus the requested `RetryAfter`. The orchestrator still treats any of them as a
failed turn; the classification is for operators and logs, not for switching provider. API keys travel in headers only
and are redacted from anything copied out of a provider response.

Provider differences stay inside the clients. Prompt-cache breakpoints exist only for Anthropic; the other clients
send one system text (stable block first, then the volatile block) and ignore the layout. Gemini has no tool-call ids,
so its calls get stable synthesised ids and results are matched back to function names through the history.

The `Scripted` provider is a first-class choice for demos and end-to-end tests, not a fallback.

## Consequences

- Behaviour, cost and data flow are predictable and auditable per deployment.
- A provider outage is visible to users as apologies instead of being hidden; operators switch deliberately.
- Switching provider needs a restart. Per-tenant or per-request routing is out of scope.
- The Gemini and Groq clients are verified against recorded-style fixtures only; they have not been exercised against
  the live services in this repository's tests.
- Each provider needs its own mapper and fixture tests, which is the price of keeping the neutral contract small.
