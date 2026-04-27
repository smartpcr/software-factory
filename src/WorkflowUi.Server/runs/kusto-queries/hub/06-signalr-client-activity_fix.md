---
name: signalr_client_activity_fix
description: "SignalR client attachment counts by run and hour (corrected: cluster -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/06-signalr-client-activity.md
score: 18
---

## Purpose

Track how many SignalR clients attach to workflow runs over time. Useful for understanding live-monitoring usage patterns and detecting connection storms.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.ClientAttached']
| where OccurrenceTime > ago(1d)
| summarize AttachCount = count() by runId, bin(OccurrenceTime, 1h)
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

A spike in `AttachCount` for a single `runId` may indicate a reconnection loop. Broad spikes across many runs suggest a deployment or load event that attracted many observers.

## Correction Details

**Original query:** hub/06-signalr-client-activity.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 18
**Data:** No results returned ([link to result](output/06-signalr-client-activity_fix.md))

The `ETL-Microsoft-WorkflowUi-Server.ClientAttached` query failed with the same GUID DB-not-found error confirming the ETW provider has not been deployed on `azshubsupport01`. The query aggregates SignalR client `AttachCount` by `runId` and hourly time bucket — data that would surface reconnection storms or unusual client load on the `TelemetryHub`/`EngineEventBridge` components documented in `etw_events.md`. While not a primary failure-diagnosis signal, spikes in `AttachCount` for a single `runId` could indicate client reconnection loops driven by server-side instability. Scored 18 (Minimal Relevance) as a secondary observability signal; lower priority than exception and failure queries for unblocking.
