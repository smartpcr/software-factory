---
name: telemetry_hub_connections
description: TelemetryHub Connection Lifecycle — client attach/detach tracking
issue: ISS-2 — SignalR streaming verification (FORGE-GAP-11.7)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Tracks SignalR client connections to run event streams via `TelemetryHub.cs` (lines 48, 69).

## Query

```kusto
traces
| where message has "attached to run" or message has "detached from run"
| project timestamp, message, customDimensions.ConnectionId, customDimensions.RunId
| summarize AttachCount=countif(message has "attached"), DetachCount=countif(message has "detached") by bin(timestamp, 1h)
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- AttachCount = 0 means no UI clients subscribing
- Both > 0 = healthy; if UI still dark, issue is client-side
