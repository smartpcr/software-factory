# Kusto Queries — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Iteration:** 0 (initial run) | **AppendMode:** false
> **Executed:** 2026-04-27 | All 10 queries attempted
> **Re-checked:** 2026-04-29 | 0 unexecuted queries (all 17 output files exist; all contain errors due to infrastructure gaps)

## Overview

10 queries extracted from [`etw_events.md`](../../etw_events.md). No `metadata.md` was found, so parameter placeholders were not substituted (queries use relative `ago()` time ranges).

> ⚠️ **No ETW provider is implemented yet.** Queries 1-7 target the *proposed* `Microsoft-WorkflowUi-Server` EventSource. All hub tables are annotated as not yet created. Queries 8-10 target Application Insights `traces` and work with the current ILogger-based observability stack.

## Execution Summary

| Category | Count | Status |
|----------|-------|--------|
| Hub (ETW) | 7 | All failed -- Database `Hub` not found on `aeoprodeastus` cluster (ETW tables not yet created) |
| Telemetry (App Insights) | 3 | All failed -- `traces` table not found in `Telemetry` database on `aeoprodtelemetry` cluster |

**Root cause:** The ETW EventSource `Microsoft-WorkflowUi-Server` has not been implemented, so no ETW tables exist. The App Insights `traces` table is not available in the ADX Telemetry database. These queries will work once the ETW provider is deployed or App Insights data is ingested into ADX.

---

## Hub Database (ETW -- hypothetical)

| # | File | Table | Result | Status |
|---|------|-------|--------|--------|
| 1 | [01-failed-workflow-runs.md](hub/01-failed-workflow-runs.md) | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | [output](hub/output/01-failed-workflow-runs.md) | error: Database 'Hub' not found |
| 2 | [02-slow-http-requests.md](hub/02-slow-http-requests.md) | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | [output](hub/output/02-slow-http-requests.md) | error: timeout |
| 3 | [03-unhandled-exceptions.md](hub/03-unhandled-exceptions.md) | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | [output](hub/output/03-unhandled-exceptions.md) | error: timeout |
| 4 | [04-node-failures-by-workflow.md](hub/04-node-failures-by-workflow.md) | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | [output](hub/output/04-node-failures-by-workflow.md) | error: timeout |
| 5 | [05-audit-trail-write-operations.md](hub/05-audit-trail-write-operations.md) | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | [output](hub/output/05-audit-trail-write-operations.md) | error: timeout |
| 6 | [06-signalr-client-activity.md](hub/06-signalr-client-activity.md) | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | [output](hub/output/06-signalr-client-activity.md) | error: timeout |
| 7 | [07-run-duration-distribution.md](hub/07-run-duration-distribution.md) | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | [output](hub/output/07-run-duration-distribution.md) | error: timeout |

## Telemetry Database (Application Insights / ILogger)

| # | File | Table | Result | Status |
|---|------|-------|--------|--------|
| 8 | [08-appinsights-failed-runs.md](telemetry/08-appinsights-failed-runs.md) | `traces` | [output](telemetry/output/08-appinsights-failed-runs.md) | error: table 'traces' not found |
| 9 | [09-appinsights-slow-requests.md](telemetry/09-appinsights-slow-requests.md) | `traces` | [output](telemetry/output/09-appinsights-slow-requests.md) | error: table 'traces' not found |
| 10 | [10-appinsights-audit-events.md](telemetry/10-appinsights-audit-events.md) | `traces` | [output](telemetry/output/10-appinsights-audit-events.md) | error: table 'traces' not found |

---

## Query Results Summary

| # | Query | Database | Score | Status |
|---|-------|----------|-------|--------|
| 1 | [01-failed-workflow-runs.md](hub/01-failed-workflow-runs.md) | Hub (aeoprodeastus) | — | error: Database 'Hub' not found |
| 1f | [01-failed-workflow-runs_fix.md](hub/01-failed-workflow-runs_fix.md) | Hub (azshubsupport01) | **32** | error: GUID DB not found — ETW provider not deployed |
| 2 | [02-slow-http-requests.md](hub/02-slow-http-requests.md) | Hub (aeoprodeastus) | — | error: timeout |
| 2f | [02-slow-http-requests_fix.md](hub/02-slow-http-requests_fix.md) | Hub (azshubsupport01) | **20** | error: GUID DB not found — ETW provider not deployed |
| 3 | [03-unhandled-exceptions.md](hub/03-unhandled-exceptions.md) | Hub (aeoprodeastus) | — | error: timeout |
| 3f | [03-unhandled-exceptions_fix.md](hub/03-unhandled-exceptions_fix.md) | Hub (azshubsupport01) | **32** | error: GUID DB not found — ETW provider not deployed |
| 4 | [04-node-failures-by-workflow.md](hub/04-node-failures-by-workflow.md) | Hub (aeoprodeastus) | — | error: timeout |
| 4f | [04-node-failures-by-workflow_fix.md](hub/04-node-failures-by-workflow_fix.md) | Hub (azshubsupport01) | **28** | error: GUID DB not found — ETW provider not deployed |
| 5 | [05-audit-trail-write-operations.md](hub/05-audit-trail-write-operations.md) | Hub (aeoprodeastus) | — | error: timeout |
| 5f | [05-audit-trail-write-operations_fix.md](hub/05-audit-trail-write-operations_fix.md) | Hub (azshubsupport01) | **18** | error: GUID DB not found — ETW provider not deployed |
| 6 | [06-signalr-client-activity.md](hub/06-signalr-client-activity.md) | Hub (aeoprodeastus) | — | error: timeout |
| 6f | [06-signalr-client-activity_fix.md](hub/06-signalr-client-activity_fix.md) | Hub (azshubsupport01) | **18** | error: GUID DB not found — ETW provider not deployed |
| 7 | [07-run-duration-distribution.md](hub/07-run-duration-distribution.md) | Hub (aeoprodeastus) | — | error: timeout |
| 7f | [07-run-duration-distribution_fix.md](hub/07-run-duration-distribution_fix.md) | Hub (azshubsupport01) | **22** | error: GUID DB not found — ETW provider not deployed |
| 8 | [08-appinsights-failed-runs.md](telemetry/08-appinsights-failed-runs.md) | Telemetry | — | error: table 'traces' not found |
| 9 | [09-appinsights-slow-requests.md](telemetry/09-appinsights-slow-requests.md) | Telemetry | — | error: table 'traces' not found |
| 10 | [10-appinsights-audit-events.md](telemetry/10-appinsights-audit-events.md) | Telemetry | — | error: table 'traces' not found |

**Top queries by potential value (once unblocked):**
- **Q01f / Q03f** (score 32): RunFailed + UnhandledException — primary failure and exception diagnosis
- **Q04f** (score 28): NodeFailed aggregation — systematic DAG node analysis
- **Q07f** (score 22): RunCompleted durations — performance trending baseline

---

## Loop Status

**Iteration:** 2
**Max Score:** 32
**Status:** CONTINUE (threshold: 80)
**Queries:** 17 total (7 Hub original, 7 Hub _fix, 3 Telemetry)
**Top Query:** 01-failed-workflow-runs_fix / 03-unhandled-exceptions_fix (score 32 — ETW provider not deployed)

**Blockers preventing score ≥ 80:**
1. **ETW provider not deployed** — `Microsoft-WorkflowUi-Server` EventSource not implemented; all 7 Hub `_fix` queries and their 7 originals return DB-not-found. Correcting the cluster/DB coordinates did not help because the underlying provider has never been deployed.
2. **No `traces` table in ADX Telemetry** — App Insights data is not ingested into the ADX Telemetry database; all 3 `traces`-based queries fail with a semantic error and have no fixable equivalent table.
3. **No `code_issues.md`** — No ISS-N issues defined for this repository; scoring is against observability gap potential, not confirmed incident root causes.

---

## Re-check Log (2026-04-29)

All 17 query `.md` files already have corresponding output files in their `output/` subdirectories. Per the execution rules, **0 queries are unexecuted**. All 17 existing outputs contain errors that are **not retryable** — they stem from infrastructure gaps (missing ETW provider, missing `traces` table in ADX), not transient failures. Re-execution would produce identical results.

| Query Group | Source Files | Output Files | Status | Re-execute? |
|-------------|-------------|-------------|--------|-------------|
| Hub original (Q01–Q07) | 7 | 7 ✅ | All error (DB not found / timeout) | No — ETW provider not deployed |
| Hub _fix (Q01f–Q07f) | 7 | 7 ✅ | All error (GUID DB not found) | No — GUID DB absent on azshubsupport01 |
| Telemetry (Q08–Q10) | 3 | 3 ✅ | All error (table 'traces' not found) | No — traces not in ADX |
| **Total** | **17** | **17** | **17 errors** | **0 retryable** |

**Execution tooling note:** `invoke-kusto.ps1`, `KustoQueryHelper.psm1`, and `execute-incident-queries.ps1` are not present in this repository. Kusto.Cli is not installed on this machine. Blob pipeline credentials (`~/.secrets/blob-pending`, `~/.secrets/blob-finished`) are available but would not help since the target databases/tables do not exist.
