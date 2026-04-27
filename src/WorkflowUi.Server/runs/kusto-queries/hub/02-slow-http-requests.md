---
name: slow_http_requests
description: HTTP requests taking longer than 5 seconds
database: hub
cluster: (not configured — no metadata.md found)
table: "['ETL-Microsoft-WorkflowUi-Server.RequestStop']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Identify HTTP requests that exceeded 5 seconds, sorted by slowest first. Useful for detecting performance bottlenecks in the WorkflowUi API.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RequestStop']
| where OccurrenceTime > ago(1d)
| where elapsedMs > 5000
| project OccurrenceTime, method, path, statusCode, elapsedMs, MachineName
| order by elapsedMs desc
| limit 1000
```

## How to Interpret Results

Focus on the `path` column to identify which endpoints are slow. Cross-reference with `statusCode` — a slow 500 may indicate a timeout or retry loop, while a slow 200 suggests genuinely expensive work.
