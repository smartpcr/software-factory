---
name: appinsights_failed_runs
description: Failed workflow runs detected via ILogger traces in Application Insights
database: Telemetry
cluster: https://aeoprodtelemetry.eastus.kusto.windows.net
table: traces
schema_validated: false
---

## Purpose

Find workflow runs that failed with an unhandled exception, using the ILogger message emitted by `RunManager`. This query works with the current observability stack (no ETW required).

## Query

```kusto
traces
| where timestamp > ago(1d)
| where message has "failed with unhandled exception"
| extend RunId = tostring(customDimensions["RunId"])
| project timestamp, RunId, message, severityLevel
| order by timestamp desc
```

## How to Interpret Results

Each row is a failed run. The `RunId` is extracted from structured log properties. Join with other `traces` rows using `RunId` to reconstruct the full execution timeline. `severityLevel` 3 = Error.
