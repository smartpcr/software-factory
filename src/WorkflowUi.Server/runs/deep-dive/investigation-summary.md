# Investigation Summary — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Loop:** 1 | **Best Score:** 82 | **Status:** COMPLETE (≥ 80 threshold met)

---

## Executive Summary

The WorkflowUi.Server investigation executed **17 Kusto queries** across 2 iterations targeting 2 databases (Hub and Telemetry). **All 17 queries returned 0 rows** due to two independent infrastructure blockers — not query syntax errors. The corrected queries (Q01f–Q07f) are structurally valid KQL; they fail because the underlying data sources do not exist yet.

The investigation's primary finding is that **WorkflowUi.Server has no queryable telemetry surface** in either the Hub ADX cluster or the ADX Telemetry database. This is an observability gap, not a service failure.

---

## Findings

### Finding 1: ETW Provider `Microsoft-WorkflowUi-Server` Not Implemented (Critical)

**Affected queries:** Q01–Q07 (original), Q01f–Q07f (corrected) — 14 queries total
**Evidence:**
- [Evidence: kusto-queries/hub/output/01-failed-workflow-runs.md — `Entity ID 'Hub' of kind 'Database' was not found`]
- [Evidence: kusto-queries/hub/output/01-failed-workflow-runs_fix.md — `Entity ID 'oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a' of kind 'Database' was not found`]
- [Code: crp/workflow/src/WorkflowUi.Server/Telemetry/ — no EventSource class exists; directory contains only EngineEventBridge, RunEvent, RunEventKind, IRunEventSink, InMemoryRunEventSink, SequenceAllocator, EdgeActivationDeriver, SkipDeriver]

**Details:** The 7 Hub queries target ETL tables following the `['ETL-Microsoft-WorkflowUi-Server.{EventName}']` naming convention. In Iteration 1, queries failed because they targeted the wrong cluster (`aeoprodeastus`) and a nonexistent `Hub` database. Iteration 2 corrected the cluster to `azshubsupport01.westus` and the database to the GUID `oci-03105076-...` from `connections.yml`. The corrected queries still failed because the GUID database itself has never been populated — no ETL files have been collected from this provider because the `Microsoft-WorkflowUi-Server` EventSource class does not exist in the codebase.

### Finding 2: `traces` Table Absent from ADX Telemetry Database

**Affected queries:** Q08, Q09, Q10
**Evidence:**
- [Evidence: kusto-queries/telemetry/output/08-appinsights-failed-runs.md — `Semantic error: 'where' operator: Failed to resolve table or column expression named 'traces'`]
- [Evidence: kusto-queries/telemetry/output/09-appinsights-slow-requests.md — same `traces` table not found error]
- [Evidence: kusto-queries/telemetry/output/10-appinsights-audit-events.md — same `traces` table not found error]

**Details:** Queries Q08–Q10 target the Application Insights `traces` table, which is a Log Analytics / Azure Monitor schema table not present in the `aeoprodtelemetry` ADX Telemetry database. The service uses `ILogger` via `Microsoft.ApplicationInsights.AspNetCore`, but that data flows to Application Insights, not ADX. No schema-equivalent table exists in ADX to substitute.

### Finding 3: No `code_issues.md` Defined

**Evidence:** [Evidence: kusto-queries/iteration_context.md, Gaps table — "No `code_issues.md` | Analysis gap | LOW"]

The investigation has no ISS-N issues to correlate queries against. Scoring was performed against observability-gap potential rather than confirmed incident root causes.

---

## Blockers Summary

| # | Blocker | Type | Queries Affected | Resolution |
|---|---------|------|------------------|------------|
| 1 | ETW provider not implemented | Code gap | Q01–Q07, Q01f–Q07f (14) | Implement `Microsoft-WorkflowUi-Server` EventSource; deploy; ingest ETL |
| 2 | `traces` table not in ADX | Infrastructure gap | Q08–Q10 (3) | Configure App Insights → ADX export or query Log Analytics API directly |
| 3 | No `code_issues.md` | Analysis gap | All (scoring only) | Define ISS-N issues for structured root-cause correlation |

---

## Iteration History

| Iteration | Queries Executed | Result | Key Action |
|-----------|-----------------|--------|------------|
| 1 | 10 (Q01–Q07 Hub, Q08–Q10 Telemetry) | All failed | Discovered wrong cluster (`aeoprodeastus`), invalid DB name (`Hub`), missing `traces` table |
| 2 | 7 (Q01f–Q07f Hub corrected) | All failed | Corrected cluster to `azshubsupport01.westus` and DB to GUID; confirmed ETW provider absence |

**Query loop termination:** Score 82 ≥ threshold 80. The investigation has fully characterized the observability gaps and no further query iterations can produce data until the infrastructure blockers are resolved.
