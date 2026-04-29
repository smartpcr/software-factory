---
name: health_fault_state_machine
description: Health Fault State Machine Transitions — fault creation, resolution, state transitions
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Health HealthStorePlugin FaultSubStateMachine — tracks health fault creation, resolution, and state transitions.

## Query

```kusto
['ETL-Microsoft-AzureStack-Infrastructure-Health-HealthStorePlugin.FaultSubStateMachine']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

Tracks health fault creation, resolution, and state transitions. Level 2 (Error) events indicate new faults being raised.
