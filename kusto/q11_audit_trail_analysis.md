---
name: audit_trail_analysis
description: Audit Trail Analysis — Supervising column verification
issue: ISS-4 — Supervising audit column never written (FORGE-GAP-10.8)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Examines audit log entries to confirm the `Supervising` field is never populated (always false).

## Query

```kusto
traces
| where message has "Audit:"
| project timestamp, message, customDimensions.Action, customDimensions.Path, customDimensions.UserId, customDimensions.OutcomeCode
| where customDimensions.Path has "/api/agents/" or customDimensions.Path has "/api/crews/"
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- All rows should exist (audit is working) but Supervising is never "true"
- Filter by agent/crew paths to focus on the endpoints most affected by this gap
