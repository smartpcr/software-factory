---
name: audit_trail_write_operations_fix
description: "Audit trail for all mutating HTTP operations (corrected: cluster -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/05-audit-trail-write-operations.md
score: 18
---

## Purpose

Review all write operations (workflow saves, deletes, run starts) over the past 7 days, including who performed them and the outcome code.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.AuditEvent']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, action, path, userId, outcomeCode, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Look at `outcomeCode` for non-2xx values to find rejected or failed operations. The `userId` column identifies who performed the action. Filter by `path` to focus on specific API endpoints.

## Correction Details

**Original query:** hub/05-audit-trail-write-operations.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 18
**Data:** No results returned ([link to result](output/05-audit-trail-write-operations_fix.md))

This audit trail query against `ETL-Microsoft-WorkflowUi-Server.AuditEvent` returned the same GUID DB-not-found error, blocked by the absent ETW provider on `azshubsupport01`. Were data present, the query would surface all POST/PUT/PATCH/DELETE operations over the past 7 days with `userId`, `action`, `path`, and `outcomeCode` — primarily useful for operational audit and security investigations rather than failure diagnosis. Scored 18 (Minimal Relevance) because its primary value is access-pattern analysis and change-tracking, not root-cause diagnosis of workflow failures; it is the lowest-priority of the Hub queries to unblock.
