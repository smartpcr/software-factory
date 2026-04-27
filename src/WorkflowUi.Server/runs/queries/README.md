# Kusto Query Index — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Source:** `etw_events.md` (Iteration 0)
> **Parameter source:** No `metadata.md` found — queries use relative time defaults

---

## Overview

| Metric | Value |
|--------|-------|
| Total queries | 10 |
| Hub (proposed ETW) | 7 |
| Telemetry (AppInsights) | 3 |
| Schema validated | 3 of 10 |

### Schema Validation Summary

- **Hub queries (01–07):** All target **hypothetical** ETW tables from the proposed `Microsoft-WorkflowUi-Server` EventSource provider. The provider is **not yet implemented** — these are templates for when ETW instrumentation is added. All flagged `schema_validated: false`.
- **Telemetry queries (01–03):** Target the standard Application Insights `traces` table. Schema is well-known. Flagged `schema_validated: true`. Note: parsed fields depend on exact ILogger message template formats.

---

## Hub Database (Proposed ETW)

Cluster: `https://localhost:8080` (placeholder — no metadata available)

| # | File | Query Name | Description |
|---|------|------------|-------------|
| 1 | [01_failed_workflow_runs.md](hub/01_failed_workflow_runs.md) | Failed Workflow Runs | Failed workflow runs with error details |
| 2 | [02_slow_http_requests.md](hub/02_slow_http_requests.md) | Slow HTTP Requests (>5s) | HTTP requests exceeding 5-second response time |
| 3 | [03_unhandled_exceptions.md](hub/03_unhandled_exceptions.md) | Unhandled Exceptions | Unhandled exceptions caught by the global error handler |
| 4 | [04_node_failures_by_workflow.md](hub/04_node_failures_by_workflow.md) | Node Failures by Workflow | Workflow node failure counts grouped by node and run |
| 5 | [05_audit_trail_write_operations.md](hub/05_audit_trail_write_operations.md) | Audit Trail for Write Operations | Audit log of all mutating HTTP operations |
| 6 | [06_signalr_client_activity.md](hub/06_signalr_client_activity.md) | SignalR Client Activity | SignalR client attachment counts by run and time bucket |
| 7 | [07_run_duration_distribution.md](hub/07_run_duration_distribution.md) | Run Duration Distribution | Completed workflow run durations and statuses |

## Telemetry Database (Application Insights)

Cluster: `https://api.applicationinsights.io`

| # | File | Query Name | Description |
|---|------|------------|-------------|
| 8 | [01_appinsights_failed_runs.md](telemetry/01_appinsights_failed_runs.md) | AppInsights: Failed Runs | Failed workflow runs detected from ILogger structured log traces |
| 9 | [02_appinsights_slow_requests.md](telemetry/02_appinsights_slow_requests.md) | AppInsights: Slow Requests | Slow HTTP requests (>5s) parsed from ILogger request timing logs |
| 10 | [03_appinsights_audit_events.md](telemetry/03_appinsights_audit_events.md) | AppInsights: Audit Events | Audit trail for mutating operations parsed from ILogger audit logs |

---

## Parameter Substitution Notes

No `metadata.md` was found in `WorkflowUi.Server/` or any parent directory up to the repo root. The following defaults apply:

| Parameter | Default Used | Notes |
|-----------|-------------|-------|
| Time range | `ago(1d)` or `ago(7d)` | Relative — no absolute timestamps needed |
| Cluster (hub) | `https://localhost:8080` | Placeholder — update when real cluster is available |
| Cluster (telemetry) | `https://api.applicationinsights.io` | Standard AppInsights endpoint |
| DeviceUri | N/A | Not referenced by any query |
| CauRunId | N/A | Not referenced by any query |

## Schema Mismatch Flags

| Query | Table | Issue |
|-------|-------|-------|
| Hub 01–07 | `ETL-Microsoft-WorkflowUi-Server.*` | ⚠️ Tables do not exist — ETW provider is proposed but not implemented |
| Telemetry 02 | `traces` (parse fields) | ⚠️ `parse` operator depends on exact ILogger message format; paths with spaces may break parsing |
| Telemetry 03 | `traces` (parse fields) | ⚠️ `parse` operator depends on exact ILogger message format; paths with spaces may break parsing |
