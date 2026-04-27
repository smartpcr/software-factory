---
name: unhandled_exceptions
description: Unhandled exceptions caught by the global error handler
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Lists all unhandled exceptions caught by the `ProblemDetailsWriter` global error handler. Each row includes the correlation ID for tracing the full request lifecycle.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.UnhandledException']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, correlationId, exceptionType, message, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `MachineName` | ✅ Standard | Standard ETW column |
| Column `correlationId` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `exceptionType` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `message` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Group by `exceptionType` to identify the most common exception classes. Use the `correlationId` to join with request logs and trace the full request that triggered the exception. Clusters of exceptions at similar times may indicate cascading failures.
