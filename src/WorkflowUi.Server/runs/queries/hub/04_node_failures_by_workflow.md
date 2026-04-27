---
name: node_failures_by_workflow
description: Workflow node failure counts grouped by node and run
database: hub
cluster: https://localhost:8080
schema_validated: false
schema_notes: "HYPOTHETICAL — ETW provider Microsoft-WorkflowUi-Server is proposed but not yet implemented. Table does not exist."
---

## Purpose

Aggregates node-level failures across workflow runs to identify which nodes fail most frequently. Helps pinpoint brittle or unreliable workflow steps.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.NodeFailed']
| where OccurrenceTime > ago(1d)
| summarize FailCount = count(), LastSeen = max(OccurrenceTime) by nodeId, nodeName, runId
| order by FailCount desc
| limit 1000
```

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | ⚠️ HYPOTHETICAL | Proposed ETW provider not yet implemented |
| Column `OccurrenceTime` | ✅ Standard | Standard ETW column |
| Column `nodeId` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `nodeName` | ⚠️ HYPOTHETICAL | Proposed event payload field |
| Column `runId` | ⚠️ HYPOTHETICAL | Proposed event payload field |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Nodes with high `FailCount` values are candidates for investigation. If the same `nodeId` fails across multiple `runId` values, the issue is likely in the node's implementation rather than transient infrastructure problems. Check `LastSeen` to determine if failures are ongoing.
