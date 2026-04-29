---
name: cloudengine_remote_step
description: CloudEngine Remote Step Completion — failed action plan steps
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

CloudEngine PowerShellRemoteStepStop events at Warning/Error level — indicates failed action plan steps.

## Query

```kusto
['ETL-Microsoft-AzureStack-CloudEngine.PowerShellRemoteStepStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Warning/Error level stop events indicate failed action plan steps. Cross-reference with ActionPlanStart timestamps to identify failing phases.
