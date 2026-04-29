---
name: urp_health_check_failures
description: URP Health Check Failures (HCI) — missing or empty content directories
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

URP InfraEventSource HealthCheckResultDirectoryIsEmptyOrDoesNotExist — indicates update content directories are missing or empty, a common root cause for update discovery failures.

## Query

```kusto
['ETL-Microsoft-URP-InfraEventSource.HealthCheckResultDirectoryIsEmptyOrDoesNotExist']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Indicates update content directories are missing or empty — a common root cause for update discovery failures.
