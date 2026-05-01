---
name: agent_pump_crashes
description: Agent Invocation Pump Crashes — unhandled exceptions during SSE event streaming
issue: ISS-1 — ClaudeAgentRunner tool_use not wired (FORGE-GAP-11.6)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects crashes in the `PumpEvents` helper in `AgentsEndpoints.cs` (line 463). When the SSE event stream from the Anthropic API encounters an unhandled exception, the pump crashes and `LogError` is emitted. Primary signal for tool_use parsing failures.

## Query

```kusto
traces
| where message has "pump crashed"
| where severityLevel >= 3
| project timestamp, message, customDimensions.Id, customDimensions.ExceptionType, customDimensions.ExceptionMessage
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- `JsonException` — unexpected SSE content block (e.g., `tool_use`) the parser doesn't handle
- `HttpRequestException` — Anthropic API connectivity failure
- `OperationCanceledException` — client disconnected or timeout
- `AgentBudgetExceededException` — token budget breached mid-stream
