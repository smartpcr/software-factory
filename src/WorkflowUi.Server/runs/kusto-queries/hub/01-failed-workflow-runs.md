---
name: failed_workflow_runs
description: Failed workflow runs from ETW RunFailed events
database: hub
cluster: (not configured — no metadata.md found)
table: "['ETL-Microsoft-WorkflowUi-Server.RunFailed']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Surface all workflow runs that ended with an unhandled exception, including the error message and duration. Use this to identify failing workflows and their root causes.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunFailed']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, FormattedMessage, MachineName, runId, errorMessage, duration
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Each row represents a single failed workflow run. Look at `errorMessage` for the root cause and `duration` to understand how far the workflow progressed before failing. Group by `runId` to correlate with node-level failures (Query 4).
