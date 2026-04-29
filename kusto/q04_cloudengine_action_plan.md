---
name: cloudengine_action_plan
description: CloudEngine Action Plan Execution — ECE action plan starts
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

CloudEngine ActionPlanStart events — shows when ECE action plans were started. Correlate with PowerShellRemoteStepStop events for step completion timing.

## Query

```kusto
['ETL-Microsoft-AzureStack-CloudEngine.ActionPlanStart']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Shows when ECE action plans were started. Correlate with `PowerShellRemoteStepStop` events for step completion timing.
