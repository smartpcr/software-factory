---
name: nc_gateway_plugin_errors
description: Network Controller Gateway Plugin Errors — gateway tunnel health
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Network Controller HostAgent GatewayPlugin Error events — direct error events from the gateway plugin indicating tunnel health degradation.

## Query

```kusto
['ETL-Microsoft.Windows.NetworkController.HostAgent.GatewayPlugin.Error']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Direct error events from the gateway plugin. Check `UpdatedHealthState` events for gateway tunnel health degradation.
