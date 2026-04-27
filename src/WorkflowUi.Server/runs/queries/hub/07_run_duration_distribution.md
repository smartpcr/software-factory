---
name: run_duration_distribution
description: Completed workflow run durations and statuses
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Lists completed workflow runs with their durations and final statuses. Use this to establish baseline performance and detect runs that are slower than expected.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunCompleted']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, runId, status, duration, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `MachineName` | ✅ Standard | Standard ETW column |
| Column `runId` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `status` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `duration` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(7d)`) — no substitutions needed.

## How to Interpret Results

Compare `duration` values across runs to identify performance regressions. Group by `status` to see the success/failure ratio. Consider adding percentile calculations (`percentile(duration, 50)`, `percentile(duration, 95)`) to the query for statistical analysis.
