---
name: nc_firewall_plugin_errors
description: Network Controller Firewall Plugin Errors — OVSDB debug errors
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Network Controller HostAgent FirewallPlugin OvsdbDbg at Warning/Error level — OVSDB debug events indicating firewall rule application failures.

## Query

```kusto
['ETL-Microsoft.Windows.NetworkController.HostAgent.FirewallPlugin.OvsdbDbg']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

OVSDB debug events at Warning/Error level indicate firewall rule application failures. High event counts suggest persistent connectivity issues.
