---
name: slow_http_requests
description: HTTP requests exceeding 5-second response time
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Finds HTTP requests that took longer than 5 seconds to complete. Useful for identifying performance bottlenecks in the WorkflowUi.Server API endpoints.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RequestStop']
| where OccurrenceTime > ago(1d)
| where elapsedMs > 5000
| project OccurrenceTime, method, path, statusCode, elapsedMs, MachineName
| order by elapsedMs desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.RequestStop` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `MachineName` | ✅ Standard | Standard ETW column |
| Column `method` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `path` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `statusCode` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `elapsedMs` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Sort by `elapsedMs` descending to find the worst offenders. Correlate `path` with specific API endpoints to determine which operations are slow. High `statusCode` values (5xx) combined with long durations may indicate server-side errors causing timeouts.
