---
name: engine_event_bridge_unknown
description: EngineEventBridge Unknown Event Types — unmapped CLR events
issue: FORGE-GAP-11.6 — ToolCall/ToolResult events not bridged
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects unknown engine event types that `EngineEventBridge` (line 45) cannot map to `RunEventKind`. When a new event type is added (e.g., `ToolCallEvent`, `ToolResultEvent`) but the bridge's switch statement isn't updated, a WARNING is emitted. This is the primary signal for detecting missing event-type mappings.

## Forge Gap Reference

**[11.6 ↔ 11.5]** The `ToolCall` and `ToolResult` AgentEventKind values exist but are never emitted. If tool execution is wired in the future, the EngineEventBridge must be updated to map `ToolCallEvent` → `RunEventKind.ToolCall` and `ToolResultEvent` → `RunEventKind.ToolResult`. Until then, any tool-related engine event hitting the bridge will produce this warning.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: EngineEventBridge.cs:45 — LogWarning("Unknown engine event type {EventType} ignored", engineEvent.GetType().Name)
traces
| where message has "Unknown engine event type"
| project timestamp, message, customDimensions.EventType
| summarize Count=count() by customDimensions.EventType
| order by Count desc
| limit 1000
```

## How to Interpret Results

- **EventType = "ToolCallEvent" or "ToolResultEvent"** — Confirms tool execution events are being produced but not mapped through the bridge to SignalR. This is the expected state while forge gap 11.6 is open.
- **Any other EventType** — New engine event types added without bridge support. Each needs a corresponding `RunEventKind` mapping.
- **No rows** — Either no unknown events are being produced (tool_use not wired yet) or all event types are properly mapped.
