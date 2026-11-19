# ADR 0001: Provider-agnostic LLM port

## Context

The assistant must run against different model providers (the first client is Anthropic, others are
planned) and in tests without any network. Provider SDK types leaking into the orchestrator would tie conversation
logic, persisted history and tests to one vendor's wire format.

## Decision

`Agent.Abstractions` defines a small neutral contract: `LlmMessage` with `TextBlock`, `ToolUseBlock` and
`ToolResultBlock` content, `LlmToolDefinition`, `LlmChatRequest` (stable and volatile system text kept apart),
`LlmChatResult` (blocks, stop reason, usage) and `ILlmClient.SendAsync`. The orchestrator and the stores only know
these types. Each provider lives behind the port in `Agent.Llm` and owns its own wire mapping, retries and error
mapping. The stable/volatile split in the request is explicit so that a provider with prompt caching can place its
cache breakpoints without the orchestrator knowing about them.

History is stored as neutral content-block JSON, not as provider payloads, so a conversation can in principle be
continued by another provider.

## Consequences

- The orchestrator is tested with a deterministic `ScriptedLlmClient` and fake clients; no provider is needed.
- Adding a provider is one class plus a registration; nothing in `Agent.Core` changes.
- The neutral model is the lowest common denominator: provider features that do not map (extended thinking blocks,
  server tools) are not represented and would need a deliberate extension of the contract.
- Every provider needs its own fixture tests for the mapping, which is extra work per provider.
