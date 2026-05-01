---
name: taskchannel_broadcast_failures
description: TaskChannel Broadcast Failures — SignalR/persistence errors
issue: ISS-2 — real-time event delivery reliability (FORGE-GAP-11.7)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors TaskChannel errors in `TaskChannel.cs` (lines 96, 117).

## Query

```kusto
traces
| where message has "TaskTranscript persist failed" or message has "TaskChannel broadcast failed"
| project timestamp, message, customDimensions.WorkItemId, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- Persistence failures = SQLite lock contention under load
- Broadcast failures = SignalR connection issues
- Both are best-effort — event processing continues on failure
