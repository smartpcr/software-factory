---
name: node_failures_by_workflow_fix
description: "Aggregate node failure counts per workflow run (corrected: cluster -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/04-node-failures-by-workflow.md
score: 28
---

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

## Correction Details

**Original query:** hub/04-node-failures-by-workflow.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 28
**Data:** No results returned ([link to result](output/04-node-failures-by-workflow_fix.md))

The query summarizing `ETL-Microsoft-WorkflowUi-Server.NodeFailed` events by `nodeId`/`nodeName`/`runId` failed with the same GUID DB-not-found error as all Hub queries in this iteration. This aggregation query would identify which specific nodes in a workflow DAG fail most frequently across runs — a class of structural insight not available from any current observability mechanism (per `etw_events.md`, there is no ETW, no metrics, and no distributed tracing). High `FailCount` for a repeated `nodeName` would indicate a brittle node requiring code-level investigation, while per-run correlation with Q01 (RunFailed) would establish whether node failures are causal to full-run failures. Scored 28 for its targeted potential diagnostic value once the provider is deployed.
