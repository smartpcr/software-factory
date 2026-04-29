---
name: hostplugin_watchdog_lifecycle
description: HostPluginWatchDog Process Lifecycle — process start events and arguments
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

HostPluginWatchDog ProcessStarted events — shows which processes were started by the watchdog and when. Compare with FoundProcessAlreadyRunning to detect restart loops.

## Query

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.ProcessStarted']
| project TimeStamp, ProcessID, ProcessName, Level, name, executablePath, arguments, workingDirectory
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Shows which processes were started by the watchdog and when. Compare with `FoundProcessAlreadyRunning` to detect restart loops.
