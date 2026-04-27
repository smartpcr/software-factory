---
name: signalr_client_activity
description: SignalR client attachment counts by run and hour
database: Hub
cluster: https://aeoprodeastus.eastus.kusto.windows.net
table: "['ETL-Microsoft-WorkflowUi-Server.ClientAttached']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Track how many SignalR clients attach to workflow runs over time. Useful for understanding live-monitoring usage patterns and detecting connection storms.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.ClientAttached']
| where OccurrenceTime > ago(1d)
| summarize AttachCount = count() by runId, bin(OccurrenceTime, 1h)
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

A spike in `AttachCount` for a single `runId` may indicate a reconnection loop. Broad spikes across many runs suggest a deployment or load event that attracted many observers.
