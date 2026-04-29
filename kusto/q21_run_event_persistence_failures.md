---
name: run_event_persistence_failures
description: RunEvent Persistence Failures — JSONL write errors in TelemetryHubSink
issue: FORGE-GAP-11.7 — SignalR streaming reliability
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects failures in `TelemetryHubSink` (line 50) when persisting run events to JSONL files. A WARNING is emitted when the JSONL write fails, though the SignalR broadcast still proceeds. Persistent write failures mean run history replay (`TelemetryHub.Replay`) will have gaps.

## Forge Gap Reference

**[11.7]** TelemetryHubSink is the fan-out point: it appends to RunHistoryBuffer, persists to JSONL, and streams to SignalR. If persistence fails but SignalR succeeds, clients who miss the live stream (e.g., late-joining UI) will see incomplete history via Replay.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: TelemetryHubSink.cs:50 — LogWarning(ex, "Failed to persist run event {RunId}/{Seq} to JSONL", ...)
traces
| where message has "Failed to persist run event"
| where severityLevel >= 2
| project timestamp, message, customDimensions.RunId, customDimensions.Seq, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **Repeated failures for same RunId** — Run history file may be corrupted or locked. Check disk space and file permissions.
- **ExceptionType = "IOException"** — File system issue (disk full, locked file).
- **ExceptionType = "JsonException"** — Serialization failure in the RunEvent DTO.
- **No rows** — Persistence is working correctly.
