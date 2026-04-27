---
name: failed_workflow_runs
description: Failed workflow runs with error details
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Retrieves all failed workflow runs from the proposed `RunFailed` ETW event table, showing the run ID, error message, duration, and machine name. Use this to identify workflows that terminated abnormally.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunFailed']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, FormattedMessage, MachineName, runId, errorMessage, duration
| order by OccurrenceTime desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.RunFailed` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `FormattedMessage` | ✅ Standard | Standard ETW column |
| Column `MachineName` | ✅ Standard | Standard ETW column |
| Column `runId` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `errorMessage` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `duration` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Each row represents a workflow run that failed. Look for patterns in `errorMessage` to identify systemic issues. Check `MachineName` to determine if failures are localized to specific nodes.
