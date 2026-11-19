# ADR 0010: Metrics and tracing through System.Diagnostics

## Context

Operating the assistant needs answers to questions logs answer badly: how many turns end in an apology, which tools
are denied or invalid, how often a guard fires, how long model calls take, how many tokens (and cached tokens) a day
costs, and who is being rate limited. The measurements must not become a new leak of message text or personal data,
and the code should not depend on any one monitoring vendor.

## Decision

`AgentTelemetry` (Agent.Core) is the single owner of one `Meter` and one `ActivitySource`, both named
`LlmBookingAgent`. The code depends only on `System.Diagnostics`; no exporter package is referenced.

| Instrument | Type | Tags |
| --- | --- | --- |
| `agent.turns` | counter | `outcome`: completed, guarded, rate_limited, escalated, failed, aborted |
| `agent.tool_calls` | counter | `tool` (registered name or `unknown`), `outcome`: ok, error, denied, invalid, unknown |
| `agent.guard_hits` | counter | `guard`: session_rate_limit, repeated_message, sensitive_data |
| `agent.llm.latency` | histogram, ms | `outcome`: ok, error |
| `agent.llm.input_tokens`, `agent.llm.output_tokens`, `agent.llm.cache_read_tokens`, `agent.llm.cache_write_tokens` | counters | none |
| `agent.rate_limit_rejections` | counter | `source`: session, ip, start |

Spans: `chat.turn` for every orchestrated turn and `chat.tool_call` for every tool call, nested under the turn span
(tags `tool`, `outcome`, `iteration`; the turn span carries `outcome`).

Tag values come from closed sets or from registered names only. A tool name the model invents is reported as `unknown`,
never as supplied. Message text, tool input and output, tokens and personal data are never used as tags or span
attributes; tests assert the full tag key set and that sample personal data never appears.

**Attach an exporter.** In the host, subscribe to the meter and the source by name, for example with the OpenTelemetry
SDK: `AddMeter("LlmBookingAgent")` and `AddSource("LlmBookingAgent")` on the metrics and tracing builders, then add the
exporter of your choice. Without a subscriber the instruments cost almost nothing. Any `MeterListener` or
`ActivityListener` works the same way, which is how the unit tests observe them.

## Consequences

- Dashboards and alerts can be built on stable names and bounded tag sets; cardinality is limited by the tool list and
  the three guards.
- Vendor choice stays a deployment decision; adding an exporter is a host change, not a code change in the domain.
- Token counters use what the provider reports, so they are only as accurate as the provider's usage block; providers
  without prompt caching report zero for the cache counters.
- Latency covers the model call only. Tool time is visible as span duration, not as a histogram.
