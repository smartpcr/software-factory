---
name: windows_event_log_errors
description: Windows Event Log Errors — aggregated error counts by provider
issue: N/A — general troubleshooting (consumed EVTX)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Windows Event Log error aggregation — counts Error/Critical events by ProviderName and EventId to identify recurring system issues.

## Query

```kusto
WindowsEvents
| where Level in ("Error", "Critical")
| summarize Count=count() by ProviderName, EventId, Level
| order by Count desc
| limit 1000
```

## How to Interpret Results

Aggregates Windows Event Log errors by provider. High counts for a single provider/EventId indicate a recurring system issue.
