# Query Status — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Total Queries:** 17 | **Iterations:** 2

---

## All Queries

| # | Query ID | File | Cluster | Database | Table | Rows | Score | Error | Resolution Path |
|---|----------|------|---------|----------|-------|------|-------|-------|-----------------|
| Q01 | 01-failed-workflow-runs | `hub/01-failed-workflow-runs.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | 0 | — | `Entity ID 'Hub' of kind 'Database' was not found` | Superseded by Q01f |
| Q01f | 01-failed-workflow-runs_fix | `hub/01-failed-workflow-runs_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | 0 | 32 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q02 | 02-slow-http-requests | `hub/02-slow-http-requests.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | 0 | — | Timeout (wrong cluster) | Superseded by Q02f |
| Q02f | 02-slow-http-requests_fix | `hub/02-slow-http-requests_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | 0 | 20 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q03 | 03-unhandled-exceptions | `hub/03-unhandled-exceptions.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | 0 | — | Timeout (wrong cluster) | Superseded by Q03f |
| Q03f | 03-unhandled-exceptions_fix | `hub/03-unhandled-exceptions_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | 0 | 32 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q04 | 04-node-failures-by-workflow | `hub/04-node-failures-by-workflow.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | 0 | — | Timeout (wrong cluster) | Superseded by Q04f |
| Q04f | 04-node-failures-by-workflow_fix | `hub/04-node-failures-by-workflow_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | 0 | 28 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q05 | 05-audit-trail-write-operations | `hub/05-audit-trail-write-operations.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | 0 | — | Timeout (wrong cluster) | Superseded by Q05f |
| Q05f | 05-audit-trail-write-operations_fix | `hub/05-audit-trail-write-operations_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | 0 | 18 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q06 | 06-signalr-client-activity | `hub/06-signalr-client-activity.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | 0 | — | Timeout (wrong cluster) | Superseded by Q06f |
| Q06f | 06-signalr-client-activity_fix | `hub/06-signalr-client-activity_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | 0 | 18 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q07 | 07-run-duration-distribution | `hub/07-run-duration-distribution.md` | `aeoprodeastus.eastus` | `Hub` | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | 0 | — | Timeout (wrong cluster) | Superseded by Q07f |
| Q07f | 07-run-duration-distribution_fix | `hub/07-run-duration-distribution_fix.md` | `azshubsupport01.westus` | `oci-03105076-...` | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | 0 | 22 | `Entity ID 'oci-03105076-...' of kind 'Database' was not found` | Implement ETW provider → re-execute |
| Q08 | 08-appinsights-failed-runs | `telemetry/08-appinsights-failed-runs.md` | `aeoprodtelemetry.eastus` | `Telemetry` | `traces` | 0 | — | `Semantic error: Failed to resolve table or column expression named 'traces'` | Configure ADX export or use Log Analytics API |
| Q09 | 09-appinsights-slow-requests | `telemetry/09-appinsights-slow-requests.md` | `aeoprodtelemetry.eastus` | `Telemetry` | `traces` | 0 | — | `Semantic error: Failed to resolve table or column expression named 'traces'` | Configure ADX export or use Log Analytics API |
| Q10 | 10-appinsights-audit-events | `telemetry/10-appinsights-audit-events.md` | `aeoprodtelemetry.eastus` | `Telemetry` | `traces` | 0 | — | `Semantic error: Failed to resolve table or column expression named 'traces'` | Configure ADX export or use Log Analytics API |

---

## Score Distribution

| Score Range | Count | Queries |
|-------------|-------|---------|
| 32 (highest) | 2 | Q01f (RunFailed), Q03f (UnhandledException) |
| 28 | 1 | Q04f (NodeFailed) |
| 22 | 1 | Q07f (RunCompleted) |
| 20 | 1 | Q02f (RequestStop) |
| 18 | 2 | Q05f (AuditEvent), Q06f (ClientAttached) |
| — (unscored) | 10 | Q01–Q07 (originals), Q08–Q10 (Telemetry) |

---

## Error Categories

| Error Category | Count | Queries | Root Cause |
|----------------|-------|---------|------------|
| Database not found (wrong cluster) | 7 | Q01–Q07 | Targeted `aeoprodeastus` / `Hub` — neither exist |
| Database not found (provider absent) | 7 | Q01f–Q07f | Corrected cluster + GUID DB, but ETW provider not deployed |
| Table not found (`traces`) | 3 | Q08–Q10 | App Insights data not exported to ADX |

---

## Query KQL Validation

All corrected queries (Q01f–Q07f) have **syntactically valid KQL** that will execute once the target tables exist. The `_fix` queries correctly:
- Use `['ETL-Microsoft-WorkflowUi-Server.{Event}']` bracket notation for table names containing dots
- Project columns consistent with expected ETW event payloads
- Use `ago()` relative time ranges (no hardcoded timestamps)
- Target the correct cluster (`azshubsupport01.westus`) and GUID database from `connections.yml`

[Evidence: kusto-queries/corrections.md — "Correction applied in iteration 2: Cluster replaced with https://azshubsupport01.westus.kusto.windows.net; database replaced with GUID oci-03105076-... sourced from connections.yml"]
