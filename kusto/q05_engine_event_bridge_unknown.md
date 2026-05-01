---
name: engine_event_bridge_unknown
description: EngineEventBridge Unknown Event Types — unmapped CLR events
issue: ISS-1 — ToolCall/ToolResult events not bridged (FORGE-GAP-11.6)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects unknown engine event types that `EngineEventBridge` (line 45) cannot map to `RunEventKind`.

## Query

```kusto
traces
| where message has "Unknown engine event type"
| project timestamp, message, customDimensions.EventType
| summarize Count=count() by tostring(customDimensions.EventType)
| order by Count desc
| limit 1000
```

## How to Interpret Results

- EventType = "ToolCallEvent" or "ToolResultEvent" — tool events produced but not bridged
- No rows — either tool_use not wired yet, or all event types properly mapped
