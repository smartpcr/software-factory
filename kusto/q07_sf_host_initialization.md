---
name: sf_host_initialization
description: Service Fabric Host Initialization — host process startup events
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Service Fabric Host InitializeStateSerializersStart — shows host process startup. Absence during expected startup windows indicates the host process failed to start.

## Query

```kusto
['ETL-Microsoft-AzureStack-Common-Infrastructure-HostModel-ServiceFabricHost.InitializeStateSerializersStart']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Shows Service Fabric host process startup. Absence of these events during expected startup windows indicates the host process failed to start.
