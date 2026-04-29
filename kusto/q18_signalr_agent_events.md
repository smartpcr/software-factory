---
name: signalr_agent_events
description: SignalR Agent Event Fan-Out — agent.event messages to UI groups
issue: FORGE-GAP-11.7 — SignalR streaming UI hooks not wired
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors `agent.event` messages sent to SignalR groups (`agent.{agentId}`) by the PumpEvents helper in `AgentsEndpoints.cs` (lines 406-418). These events carry `invocationId`, `agentId`, `kind`, `text`, `toolName`, `toolPayload`, and `error` fields. Server fan-out is live but no UI consumer is wired.

## Forge Gap Reference

**[11.7]** Spec lists three UI hooks for streaming events: card live-progress bar (Phase 1.2), `/crews` row updates, PR detail panel "Agent activity" tab. Server fan-out is live; none of the three UI consumers are wired.

## Query

```kusto
// Target: SignalR diagnostics or structured ILogger output
// Source: AgentsEndpoints.cs PumpEvents — sends "agent.event" to group "agent.{agentId}"
// Note: SignalR itself logs connection events at Information level
traces
| where message has "agent.event" or message has "agent.usage" or message has "agent.budget_exceeded" or message has "agent.reassigned"
| project timestamp, message, customDimensions.agentId, customDimensions.kind, customDimensions.invocationId
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **`agent.event`** — Token streaming, tool calls, tool results, errors. `kind` field identifies the event type (Token, ToolCall, ToolResult, Error).
- **`agent.usage`** — Token consumption update (input/output token counts).
- **`agent.budget_exceeded`** — Daily crew budget breached (line 334).
- **`agent.reassigned`** — Agent moved to different crew (line 234).
- If rows exist but the UI shows no live updates, the gap is confirmed: server fan-out works, UI hooks are missing.
