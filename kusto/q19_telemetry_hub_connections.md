---
name: telemetry_hub_connections
description: TelemetryHub Connection Lifecycle — client attach/detach to run groups
issue: FORGE-GAP-11.7 — SignalR streaming verification
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors TelemetryHub SignalR connection lifecycle. `TelemetryHub.cs` logs Information-level messages when clients attach (line 48) and detach (line 69) from run groups. This verifies whether UI clients are actually subscribing to real-time workflow event streams.

## Forge Gap Reference

**[11.7]** The TelemetryHub streams `RunEvent` DTOs to per-run groups via `ITelemetryClient.ReceiveRunEvent()`. If no clients attach, the UI live-progress features are completely dark.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: TelemetryHub.cs:48 — LogInformation("Client {ConnectionId} attached to run {RunId}", ...)
// Source: TelemetryHub.cs:69 — LogInformation("Client {ConnectionId} detached from run {RunId}", ...)
traces
| where message has "attached to run" or message has "detached from run"
| project timestamp, message, customDimensions.ConnectionId, customDimensions.RunId
| summarize AttachCount=countif(message has "attached"), DetachCount=countif(message has "detached") by bin(timestamp, 1h)
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **AttachCount = 0** — No UI clients are subscribing to run events. The live-progress bar, node status updates, and workflow timeline are all disconnected.
- **AttachCount >> DetachCount** — Clients are connecting but not cleanly disconnecting (potential connection leak).
- **Both > 0** — Healthy SignalR lifecycle; if UI still shows no updates, the issue is in the client-side event handler.
