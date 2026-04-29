---
name: health_runner_invalid_results
description: Cross-Provider Error Timeline — health runner stale/invalid result detection
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Health RunnerService InvalidOldResult — indicates the health runner detected stale or invalid health check results, pointing to timing issues or health check service failures.

## Query

```kusto
['ETL-Microsoft-AzureStack-Infrastructure-Health-RunnerService.InvalidOldResult']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

InvalidOldResult events indicate the health runner detected stale or invalid health check results — may indicate a timing issue or health check service failure.
