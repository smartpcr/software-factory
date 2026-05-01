---
name: run_event_persistence
description: RunEvent Persistence Failures — JSONL write errors
issue: ISS-2 — SignalR streaming reliability (FORGE-GAP-11.7)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors JSONL persistence failures in `TelemetryHubSink.cs` (line 50).

## Query

```kusto
traces
| where message has "Failed to persist run event"
| where severityLevel >= 2
| project timestamp, message, customDimensions.RunId, customDimensions.Seq, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- Repeated failures for same RunId = corrupted/locked history file
- IOException = disk issue
- JsonException = serialization failure
