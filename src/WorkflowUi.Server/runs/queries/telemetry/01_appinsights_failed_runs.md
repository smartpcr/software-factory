---
name: appinsights_failed_runs
description: Failed workflow runs detected from ILogger structured log traces
database: telemetry
cluster: https://api.applicationinsights.io
schema_validated: true
schema_notes: "Uses standard Application Insights 'traces' table — schema is well-known."
---

## Purpose

Finds workflow runs that failed with unhandled exceptions by searching ILogger-produced structured logs in Application Insights. This query targets the *current* observability layer (ILogger) — not the proposed ETW provider.

Source log site: `RunManager` — `"Run {RunId} failed with unhandled exception"` (Error level)

## Query

```kusto
traces
| where timestamp > ago(1d)
| where message has "failed with unhandled exception"
| extend RunId = tostring(customDimensions["RunId"])
| project timestamp, RunId, message, severityLevel
| order by timestamp desc
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `traces` | ✅ Valid | Standard Application Insights table |
| Column `timestamp` | ✅ Valid | Standard column |
| Column `message` | ✅ Valid | Standard column |
| Column `severityLevel` | ✅ Valid | Standard column |
| Column `customDimensions` | ✅ Valid | Standard dynamic column for structured log properties |
| Property `customDimensions["RunId"]` | ✅ Expected | Emitted by ILogger structured logging from `RunManager` |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Each row represents a workflow run that threw an unhandled exception. The `RunId` (extracted from structured log properties) can be used to correlate with JSONL persistence files in the `runs/` directory. `severityLevel` should always be Error (3) for these entries.
