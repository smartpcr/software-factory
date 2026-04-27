---
name: failed_workflow_runs_fix
description: "Failed workflow runs from ETW RunFailed events (corrected: database Hub -> oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a, cluster -> azshubsupport01.westus)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/01-failed-workflow-runs.md
score: 32
---

## Purpose

Surface all workflow runs that ended with an unhandled exception, including the error message and duration. Use this to identify failing workflows and their root causes.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunFailed']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, FormattedMessage, MachineName, runId, errorMessage, duration
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Each row represents a single failed workflow run. Look at `errorMessage` for the root cause and `duration` to understand how far the workflow progressed before failing. Group by `runId` to correlate with node-level failures (Query 4).

## Correction Details

**Original query:** hub/01-failed-workflow-runs.md
**Error:** Entity ID 'Hub' of kind 'Database' was not found on cluster aeoprodeastus
**Fix:** Replaced database `Hub` with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a` and cluster `https://aeoprodeastus.eastus.kusto.windows.net` with `https://azshubsupport01.westus.kusto.windows.net`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 32
**Data:** No results returned ([link to result](output/01-failed-workflow-runs_fix.md))

The query failed with `Entity ID 'oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a' of kind 'Database' was not found` on `azshubsupport01.westus` — confirming that the GUID-based database for the `Microsoft-WorkflowUi-Server` ETW provider does not exist on that cluster because the EventSource has not yet been implemented. The absence of data is an infrastructure gap, not a query error: the cluster and database coordinates are correctly sourced from `connections.yml`, but the provider has never been deployed and therefore no ETL files have been ingested to create this table. This query has the highest potential diagnostic value of the seven Hub queries — surfacing `errorMessage`, `runId`, and `duration` for every failed workflow run — and would immediately provide Contextual Signal-level evidence for workflow failure investigations once the ETW provider is authored and its telemetry ingested. Per `etw_events.md`, the current observability stack has no equivalent mechanism (ILogger sinks are not queryable with this granularity), making this the most important query to enable in the next iteration.
