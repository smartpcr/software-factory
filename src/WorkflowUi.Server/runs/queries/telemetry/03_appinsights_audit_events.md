---
name: appinsights_audit_events
description: Audit trail for mutating operations parsed from ILogger audit logs
database: telemetry
cluster: https://api.applicationinsights.io
schema_validated: true
schema_notes: "Uses standard Application Insights 'traces' table with parse operator on audit message."
---

## Purpose

Extracts the audit trail for all write operations (POST, PUT, PATCH, DELETE) from the `AuditMiddleware` ILogger output. Parses action, path, user ID, and outcome code from the structured message.

Source log site: `AuditMiddleware` — `"Audit: {Action} {Path} by {UserId} -> {OutcomeCode}"` (Information level)

## Query

```kusto
traces
| where timestamp > ago(7d)
| where message startswith "Audit:"
| parse message with "Audit: " Action " " Path " by " UserId " -> " OutcomeCode
| project timestamp, Action, Path, UserId, OutcomeCode
| order by timestamp desc
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `traces` | ✅ Valid | Standard Application Insights table |
| Column `timestamp` | ✅ Valid | Standard column |
| Column `message` | ✅ Valid | Standard column |
| Parsed field `Action` | ⚠️ Parse-dependent | Relies on exact message template format |
| Parsed field `Path` | ⚠️ Parse-dependent | Paths with spaces may break the parse pattern |
| Parsed field `UserId` | ⚠️ Parse-dependent | Relies on exact message template format |
| Parsed field `OutcomeCode` | ⚠️ Parse-dependent | Relies on exact message template format |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(7d)`) — no substitutions needed.

## How to Interpret Results

Each row represents a mutating HTTP operation. Filter by `OutcomeCode` to find failures. Group by `UserId` to build per-user activity reports. The `Action` field corresponds to the HTTP method (POST, PUT, PATCH, DELETE). Consider using `customDimensions` properties instead of `parse` if structured log properties are available in your Application Insights workspace.
