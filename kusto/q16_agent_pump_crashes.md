---
name: agent_pump_crashes
description: Agent Invocation Pump Crashes — unhandled exceptions during event streaming
issue: FORGE-GAP-11.6 — ClaudeAgentRunner tool_use not wired
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects crashes in the `PumpEvents` helper in `AgentsEndpoints.cs` (line 463). When the SSE event stream from the Anthropic API encounters an unhandled exception, the agent invocation pump crashes and the `LogError` is emitted. This is the primary signal for tool_use parsing failures — if the runner encounters an unexpected SSE block type (e.g., `tool_use` content block) and fails to parse it, the pump may crash.

## Forge Gap Reference

**[11.6 ↔ 11.5]** Sandbox is built and tested but `ClaudeAgentRunner` never invokes it. `tool_use` protocol parsing is absent. The runner is "chat-only" — ToolCall/ToolResult AgentEventKind values exist but are never emitted.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: AgentsEndpoints.cs:463 — LogError(ex, "Agent invocation {Id} pump crashed", inv.InvocationId)
traces
| where message has "pump crashed"
| where severityLevel >= 3
| project timestamp, message, customDimensions.Id, customDimensions.ExceptionType, customDimensions.ExceptionMessage
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

Each row represents a fatal crash in the agent event pump. The `ExceptionType` and `ExceptionMessage` fields reveal whether the crash was caused by:
- `JsonException` — likely an unexpected SSE content block (e.g., `tool_use`) the parser doesn't handle
- `HttpRequestException` — Anthropic API connectivity failure
- `OperationCanceledException` — client disconnected or timeout
- `AgentBudgetExceededException` — token budget breached mid-stream

If crashes correlate with specific `InvocationId` values, cross-reference with the `agent.event` SignalR messages to see the last event before the crash.
