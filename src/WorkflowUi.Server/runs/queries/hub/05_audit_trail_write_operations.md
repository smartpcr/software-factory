---
name: audit_trail_write_operations
description: Audit log of all mutating HTTP operations (POST/PUT/PATCH/DELETE)
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Retrieves the audit trail for all write operations (POST, PUT, PATCH, DELETE) processed by the `AuditMiddleware`. Shows who performed what action and the outcome.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.AuditEvent']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, action, path, userId, outcomeCode, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `MachineName` | ✅ Standard | Standard ETW column |
| Column `action` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `path` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `userId` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `outcomeCode` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(7d)`) — no substitutions needed.

## How to Interpret Results

Filter by `outcomeCode` to find failed operations (non-2xx status codes). Group by `userId` to see per-user activity. Unexpected `action`+`path` combinations may indicate unauthorized access attempts.
