---
name: appinsights_audit_events
description: Audit trail for write operations via ILogger traces
database: Telemetry
cluster: https://aeoprodtelemetry.eastus.kusto.windows.net
table: traces
schema_validated: false
---

## Purpose

Review all mutating HTTP operations logged by `AuditMiddleware` using the ILogger message. This is the current-stack equivalent of Query 5 (ETW-based audit trail).

## Query

```kusto
traces
| where timestamp > ago(7d)
| where message startswith "Audit:"
| parse message with "Audit: " Action " " Path " by " UserId " -> " OutcomeCode
| project timestamp, Action, Path, UserId, OutcomeCode
| order by timestamp desc
```

## How to Interpret Results

Each row is a mutating operation (POST/PUT/PATCH/DELETE) that passed through `AuditMiddleware`. The `OutcomeCode` is the HTTP status code. Filter by `UserId` to audit a specific user's actions.
