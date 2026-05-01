---
name: signalr_agent_events
description: SignalR Agent Event Fan-Out — agent.event messages to UI groups
issue: ISS-2 — SignalR streaming UI hooks not wired (FORGE-GAP-11.7)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors `agent.event` messages sent to SignalR groups by PumpEvents in `AgentsEndpoints.cs` (lines 406-418). Server fan-out is live but no UI consumer is wired.

## Query

```kusto
traces
| where message has "agent.event" or message has "agent.usage" or message has "agent.budget_exceeded" or message has "agent.reassigned"
| project timestamp, message, customDimensions.agentId, customDimensions.kind, customDimensions.invocationId
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- `agent.event` — Token streaming, tool calls, tool results, errors
- `agent.usage` — Token consumption update
- `agent.budget_exceeded` — Daily crew budget breached
- If rows exist but UI shows no live updates, the gap is confirmed
