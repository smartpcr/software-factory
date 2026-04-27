---
name: slow_http_requests_fix
description: "HTTP requests taking longer than 5 seconds (corrected: cluster timeout fixed -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/02-slow-http-requests.md
score: 20
---

## Purpose

Identify HTTP requests that exceeded 5 seconds, sorted by slowest first. Useful for detecting performance bottlenecks in the WorkflowUi API.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.RequestStop']
| where OccurrenceTime > ago(1d)
| where elapsedMs > 5000
| project OccurrenceTime, method, path, statusCode, elapsedMs, MachineName
| order by elapsedMs desc
| limit 1000
```

## How to Interpret Results

Focus on the `path` column to identify which endpoints are slow. Cross-reference with `statusCode` — a slow 500 may indicate a timeout or retry loop, while a slow 200 suggests genuinely expensive work.

## Correction Details

**Original query:** hub/02-slow-http-requests.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` (HCI diagnostics, not Hub) + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 20
**Data:** No results returned ([link to result](output/02-slow-http-requests_fix.md))

The query against `ETL-Microsoft-WorkflowUi-Server.RequestStop` failed identically to all other Hub queries in this pass: `Entity ID 'oci-03105076...' of kind 'Database' was not found` on `azshubsupport01.westus`, confirming the ETW provider is not deployed. Were data present, this query would filter HTTP requests exceeding 5 seconds and expose `method`, `path`, `statusCode`, and `elapsedMs` — enabling quantitative identification of slow API endpoints. The observability gap documented in `etw_events.md` (no OpenTelemetry metrics, no distributed tracing) makes this query valuable once deployed, as there is currently no other mechanism for population-level HTTP latency measurement. Scored 20 (Minimal Relevance) reflecting the infrastructure blocker, with meaningful potential uplift to Contextual Signal once the provider is active.
