---
name: run_duration_distribution
description: Completed workflow run durations over the past 7 days
database: hub
cluster: (not configured — no metadata.md found)
table: "['ETL-Microsoft-WorkflowUi-Server.RunCompleted']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Show all completed workflow runs with their duration and final status. Use this for performance trending and SLA analysis.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunCompleted']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, runId, status, duration, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Look at `duration` to identify outlier runs. Compare runs with `status` = "Succeeded" vs other statuses. Trend this over time to detect gradual performance degradation.
