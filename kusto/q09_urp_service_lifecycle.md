---
name: urp_service_lifecycle
description: URP/Update Service Lifecycle (HCI) — service start/stop events
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

URP InfraEventSource StartService/StopService — shows update service start/stop lifecycle. Frequent stop/start cycles indicate service instability.

## Query

```kusto
['ETL-Microsoft-URP-InfraEventSource.StartService']
| union ['ETL-Microsoft-URP-InfraEventSource.StopService']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Shows update service start/stop lifecycle. Frequent stop/start cycles indicate service instability. Gap between stop and next start indicates downtime.
