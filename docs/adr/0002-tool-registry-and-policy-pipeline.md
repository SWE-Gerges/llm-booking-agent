# ADR 0002: Tool registry and policy pipeline instead of a switch

## Context

The first version of an agent usually dispatches tool calls with a `switch` on the tool name and scatters rules such
as "only one search per turn" or "do not submit before the price is re-checked" through the dispatch code. Adding a
tool then means editing the dispatcher, and rules become hard to test in isolation.

## Decision

Tools implement `IChatTool` (name, description, JSON schema, `ExecuteAsync`) and are registered through DI
(`AddChatTool<T>()`). A `ToolRegistry` exposes definitions in a fixed ordinal order, which keeps the tool block of
the prompt byte-stable and therefore cacheable, and supports lookup by name.

Every call passes the same pipeline: unknown tool, then each registered `IToolCallPolicy` (first denial wins), then
JSON-schema validation of the input, then execution. Policies are small classes that receive a `PolicyContext`
(session, per-turn state, pending call). Two ship with the repository: `OneSearchPerTurnPolicy` and
`FinalizeRequiresConfirmedPricePolicy`. Policies find the tools they care about through marker interfaces
(`ISearchTool`, `IFinalizingTool`) rather than tool names.

Denials, validation failures and tool exceptions become error tool results (`{"error": ...}`) that the model sees and
can recover from; they never abort the turn.

## Consequences

- New tools and new rules are additive and individually unit-testable.
- The model gets actionable feedback (what was wrong, what to do first) instead of silent failures.
- Rule order matters (registration order) and is therefore part of the composition root.
- Policies run for every call, including ones the schema would reject; they must be cheap and must not trust input.
