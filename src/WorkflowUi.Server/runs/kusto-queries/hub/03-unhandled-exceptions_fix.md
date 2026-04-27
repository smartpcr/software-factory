---
name: unhandled_exceptions_fix
description: "Unhandled exceptions caught by the global error handler (corrected: cluster -> azshubsupport01.westus, database Hub -> GUID)"
database: oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a
cluster: https://azshubsupport01.westus.kusto.windows.net
schema_validated: true
type: correction
original_query: hub/03-unhandled-exceptions.md
score: 32
---

## Purpose

List all unhandled exceptions that reached the ProblemDetailsWriter error handler. Each row includes the correlation ID for tracing back to the originating request.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.UnhandledException']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, correlationId, exceptionType, message, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Group by `exceptionType` to find the most common failure modes. Use `correlationId` to join with request logs for full context on what triggered the exception.

## Correction Details

**Original query:** hub/03-unhandled-exceptions.md
**Error:** Query timed out after 180s — incorrect cluster `aeoprodeastus` + invalid database `Hub`
**Fix:** Replaced cluster with `https://azshubsupport01.westus.kusto.windows.net` and database with `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`
**Schema source:** E:\work\hub\docs\wiki\lab\kusto\schema\hub_kusto_schema.json + connections.yml

## Results Interpretation

**Score:** 32
**Data:** No results returned ([link to result](output/03-unhandled-exceptions_fix.md))

This query targeting `ETL-Microsoft-WorkflowUi-Server.UnhandledException` hit the same GUID DB-not-found wall as all other Hub queries — the ETW provider is absent, so no table exists in the database on `azshubsupport01`. Were data available, projecting `exceptionType`, `message`, and `correlationId` would directly surface all exceptions that escaped the `ProblemDetailsWriter` error handler, making this one of the two highest-value queries (alongside Q01 RunFailed) for exception root-cause analysis. The current stack has no queryable equivalent: `etw_events.md` notes no ETW instrumentation and no distributed tracing, meaning unhandled exceptions are visible only in ILogger sinks without correlation or aggregation. Scored 32 for potential relevance pending provider deployment.
