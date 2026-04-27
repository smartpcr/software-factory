---
name: node_failures_by_workflow
description: Aggregate node failure counts per workflow run
database: hub
cluster: (not configured — no metadata.md found)
table: "['ETL-Microsoft-WorkflowUi-Server.NodeFailed']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

Summarize node-level failures by node name and run, showing which nodes fail most frequently. Useful for pinpointing brittle nodes in a workflow DAG.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.NodeFailed']
| where OccurrenceTime > ago(1d)
| summarize FailCount = count(), LastSeen = max(OccurrenceTime) by nodeId, nodeName, runId
| order by FailCount desc
| limit 1000
```

## How to Interpret Results

High `FailCount` for a specific `nodeName` across multiple runs indicates a systematic issue with that node's implementation or its dependencies. Cross-reference `runId` with Query 1 to check if the run itself also failed.
