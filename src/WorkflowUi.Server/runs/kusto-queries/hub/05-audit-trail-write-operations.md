---
name: audit_trail_write_operations
description: Audit trail for all mutating HTTP operations (POST/PUT/PATCH/DELETE)
database: hub
cluster: (not configured — no metadata.md found)
table: "['ETL-Microsoft-WorkflowUi-Server.AuditEvent']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Review all write operations (workflow saves, deletes, run starts) over the past 7 days, including who performed them and the outcome code.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.AuditEvent']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, action, path, userId, outcomeCode, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Look at `outcomeCode` for non-2xx values to find rejected or failed operations. The `userId` column identifies who performed the action. Filter by `path` to focus on specific API endpoints.
