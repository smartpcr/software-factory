---
name: hostplugin_already_running
description: HostPluginWatchDog Already Running Detection — restart loop detection
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

HostPluginWatchDog FoundProcessAlreadyRunning — detects restart loops by counting already-running detections in 5-minute windows.

## Query

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.FoundProcessAlreadyRunning']
| summarize Count=count() by bin(TimeStamp, 5m), name
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Frequent "already running" events in short intervals may indicate healthy operation. A drop to zero followed by `ProcessStarted` events indicates a process crash/restart.
