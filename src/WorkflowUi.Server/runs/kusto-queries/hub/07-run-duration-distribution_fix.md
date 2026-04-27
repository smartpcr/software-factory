---
name: run_duration_distribution_fix
description: "Completed workflow run durations over the past 7 days (corrected: cluster -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/07-run-duration-distribution.md
score: 22
---

## Purpose

Show all completed workflow runs with their duration and final status. Use this for performance trending and SLA analysis.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunCompleted']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, runId, status, duration, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Look at `duration` to identify outlier runs. Compare runs with `status` = "Succeeded" vs other statuses. Trend this over time to detect gradual performance degradation.

## Correction Details

**Original query:** hub/07-run-duration-distribution.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 22
**Data:** No results returned ([link to result](output/07-run-duration-distribution_fix.md))

This performance query against `ETL-Microsoft-WorkflowUi-Server.RunCompleted` hit the same GUID DB-not-found blocker as all six other Hub queries in this iteration. Were data available, projecting `runId`, `status`, and `duration` over a 7-day window would establish the baseline performance distribution for completed runs and enable detection of gradual degradation — complementing Q01 (RunFailed) by covering successful completions. Per `etw_events.md`, no OpenTelemetry metrics or performance counters are currently instrumented, so this query represents the only planned mechanism for quantitative end-to-end run duration analysis at the population level. Scored 22 for useful performance-trending potential, secondary in priority to failure and exception diagnosis queries.
