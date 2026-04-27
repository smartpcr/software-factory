# Query Corrections

> **Generated:** 2026-04-27 | **Iteration:** 2 | **Incident:** WorkflowUiServer

This document records all queries that failed in iterations 1–2 and the disposition of each correction attempt.

---

## Iteration 1 Failures (Original 10 Queries)

### Failure Group A: Hub ETW Queries (Q01–Q07) — Wrong Cluster / Invalid Database `Hub`

| Query | Original Cluster | Error |
|-------|-----------------|-------|
| `01-failed-workflow-runs` | `aeoprodeastus.eastus` | Database 'Hub' not found |
| `02-slow-http-requests` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |
| `03-unhandled-exceptions` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |
| `04-node-failures-by-workflow` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |
| `05-audit-trail-write-operations` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |
| `06-signalr-client-activity` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |
| `07-run-duration-distribution` | `aeoprodeastus.eastus` | Timeout (wrong cluster) |

**Correction applied in iteration 2:** Cluster replaced with `https://azshubsupport01.westus.kusto.windows.net`; database replaced with GUID `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a` sourced from `connections.yml`.

**Correction outcome:** **FAILED — infrastructure absence.** The GUID database does not exist on `azshubsupport01` either. Error: `Entity ID 'oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a' of kind 'Database' was not found`.

**Root cause of continued failure:** The `Microsoft-WorkflowUi-Server` ETW EventSource has never been implemented in production code. No ETL files have been ingested from this provider, so no database or tables exist on any cluster. This is not a query syntax error or a wrong-cluster error — it is an observability infrastructure gap.

**Corrective action required:** Implement the `Microsoft-WorkflowUi-Server` EventSource class in `WorkflowUi.Server`, deploy it, collect ETL files, and ingest them into the Hub Kusto database before these queries can execute. See `iteration_context.md` for recommended next steps.

---

### Failure Group B: Telemetry App Insights Queries (Q08–Q10) — `traces` Table Not Found

| Query | Cluster | Database | Error |
|-------|---------|----------|-------|
| `08-appinsights-failed-runs` | `aeoprodtelemetry` | Telemetry | Semantic error: table 'traces' not found |
| `09-appinsights-slow-requests` | `aeoprodtelemetry` | Telemetry | Semantic error: table 'traces' not found |
| `10-appinsights-audit-events` | `aeoprodtelemetry` | Telemetry | Semantic error: table 'traces' not found |

**Correction status: UNFIXABLE — no schema equivalent.**

The `traces` table is an Application Insights / Azure Monitor schema table. It is not present in the ADX Telemetry database (`aeoprodtelemetry`). The Telemetry database schema (`telemetry.schema.json`) contains no table with an equivalent structure (structured log messages with `severityLevel`, `message`, `customDimensions` fields).

**Why corrections cannot be generated:**
- No equivalent table exists in the schema to substitute for `traces`
- App Insights data would need to be explicitly exported/piped into ADX via a Diagnostics Settings → Event Hub → ADX Data Connection pipeline
- Until that pipeline is set up, no Kusto query can access this data

**Corrective action required:** Configure App Insights → ADX continuous export for the `WorkflowUi.Server` Application Insights resource, or use the Application Insights REST API / Azure Monitor query API directly (not Kusto CLI). These queries cannot be retried against ADX until the export pipeline is operational.

---

## Summary

| Group | Queries | Correction Attempted | Correction Successful | Blocker |
|-------|---------|----------------------|-----------------------|---------|
| Hub ETW (Q01–Q07) | 7 | ✅ Yes — cluster + DB GUID corrected | ❌ No | ETW provider `Microsoft-WorkflowUi-Server` not deployed; GUID DB absent on all clusters |
| Telemetry App Insights (Q08–Q10) | 3 | ❌ No — no schema equivalent | ❌ No | `traces` table not in ADX; no exportable schema substitute |
| **Total** | **10** | 7 | **0** | Two independent infrastructure gaps |
