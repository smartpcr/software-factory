---
name: hostplugin_watchdog_errors
description: HostPluginWatchDog Error Events — process start failures and slow launches
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

HostPluginWatchDog EnsureProcessStarted/Stop errors — detects process start failures and slow process launches from the Azure Stack Compute HostPluginWatchDog provider.

## Query

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.EnsureProcessStartedStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName, correlationVector, name, executablePath, duration, exception
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Level <= 3 captures Critical (1), Error (2), and Warning (3) events. Non-null `exception` field indicates process start failures. Long `duration` values indicate slow process launches.
