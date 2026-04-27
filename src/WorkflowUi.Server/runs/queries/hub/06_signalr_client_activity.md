---
name: signalr_client_activity
description: SignalR client attachment counts by run and time bucket
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Counts the number of SignalR clients attaching to workflow runs over time. Helps monitor real-time telemetry hub usage and identify peak periods.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.ClientAttached']
| where OccurrenceTime > ago(1d)
| summarize AttachCount = count() by runId, bin(OccurrenceTime, 1h)
| order by OccurrenceTime desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `runId` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

High `AttachCount` for a single `runId` indicates many users monitoring a workflow execution. Spikes in attachment activity may correlate with important workflow runs. Use in conjunction with detach events to understand session durations.
