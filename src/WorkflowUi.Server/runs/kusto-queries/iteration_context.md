# Iteration Context — WorkflowUiServer

> **Generated:** 2026-04-27 | **Iteration:** 2 | **Status:** CONTINUE

---

## Previous Query Results (Iteration 2)

| # | Query | Database | Score | Rows | Key Finding |
|---|-------|----------|-------|------|-------------|
| Q01f | 01-failed-workflow-runs_fix | Hub (azshubsupport01) | 32 | 0 | GUID DB not found — ETW provider not deployed |
| Q02f | 02-slow-http-requests_fix | Hub (azshubsupport01) | 20 | 0 | GUID DB not found — ETW provider not deployed |
| Q03f | 03-unhandled-exceptions_fix | Hub (azshubsupport01) | 32 | 0 | GUID DB not found — ETW provider not deployed |
| Q04f | 04-node-failures-by-workflow_fix | Hub (azshubsupport01) | 28 | 0 | GUID DB not found — ETW provider not deployed |
| Q05f | 05-audit-trail-write-operations_fix | Hub (azshubsupport01) | 18 | 0 | GUID DB not found — ETW provider not deployed |
| Q06f | 06-signalr-client-activity_fix | Hub (azshubsupport01) | 18 | 0 | GUID DB not found — ETW provider not deployed |
| Q07f | 07-run-duration-distribution_fix | Hub (azshubsupport01) | 22 | 0 | GUID DB not found — ETW provider not deployed |
| Q08 | 08-appinsights-failed-runs | Telemetry | — | 0 | `traces` table not in ADX — no schema equivalent |
| Q09 | 09-appinsights-slow-requests | Telemetry | — | 0 | `traces` table not in ADX — no schema equivalent |
| Q10 | 10-appinsights-audit-events | Telemetry | — | 0 | `traces` table not in ADX — no schema equivalent |

---

## Unresolved Blockers

### Blocker 1 (Critical): ETW Provider Not Deployed

**Affected queries:** Q01–Q07 (all Hub queries, both original and _fix variants)
**Error:** `Entity ID 'oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a' of kind 'Database' was not found`
**Cluster confirmed:** `https://azshubsupport01.westus.kusto.windows.net`
**Root cause:** The ETW EventSource `Microsoft-WorkflowUi-Server` does not exist in production code. No ETL files have ever been collected from this provider. The GUID database coordinates are correct per `connections.yml`, but the database itself was never populated.
**Resolution path:**
1. Implement `Microsoft-WorkflowUi-Server` EventSource in `src/WorkflowUi.Server/` (add class inheriting `System.Diagnostics.Tracing.EventSource` with `[EventSource(Name = "Microsoft-WorkflowUi-Server")]`)
2. Add event methods: `RunFailed`, `RunCompleted`, `NodeFailed`, `UnhandledException`, `RequestStop`, `AuditEvent`, `ClientAttached`
3. Deploy the service to an environment where ETL collection is active
4. Collect ETL files and ingest via the `EtlIterator` pipeline into the Hub Kusto database
5. Retry Hub queries Q01f–Q07f — they are correctly formed and will execute once data exists

### Blocker 2 (Infrastructure): `traces` Table Not in ADX Telemetry

**Affected queries:** Q08–Q10 (all Telemetry queries)
**Error:** `Semantic error: 'where' operator: Failed to resolve table or column expression named 'traces'`
**Cluster:** `aeoprodtelemetry`
**Root cause:** The Telemetry ADX database does not contain an App Insights `traces` table. The `WorkflowUi.Server` uses `ILogger` which by default emits to Application Insights via `Microsoft.ApplicationInsights.AspNetCore`, but that data lives in Application Insights / Azure Monitor, not in ADX.
**Resolution path (two options):**
- **Option A (preferred for Kusto querying):** Configure Diagnostics Settings on the Application Insights resource to export logs to an Event Hub, then connect that Event Hub to the ADX Telemetry database via a Data Connection. Once flowing, the `traces` table (or equivalent) will appear in ADX and Q08–Q10 can execute.
- **Option B (immediate workaround):** Query Application Insights directly via the Azure Monitor Logs API (`https://api.loganalytics.io/v1/workspaces/{workspaceId}/query`) instead of Kusto CLI. The queries are valid KQL and will work against the Log Analytics workspace backing App Insights.

---

## Gaps to Address

| Gap | Type | Priority | Notes |
|-----|------|----------|-------|
| ETW provider not implemented | Code gap | **HIGH** | Blocks all 7 Hub queries across both iterations |
| No ETL ingestion pipeline for WorkflowUi.Server | Infrastructure gap | HIGH | Dependent on ETW provider being implemented first |
| App Insights not exported to ADX | Infrastructure gap | MEDIUM | Blocks Q08–Q10; workaround available via Log Analytics API |
| No `code_issues.md` | Analysis gap | LOW | No ISS-N issues defined; scoring is observability-gap based, not incident-based |

---

## Next Query Number: 11

---

## Existing Query Files

- `kusto/hub/01-failed-workflow-runs.md`
- `kusto/hub/01-failed-workflow-runs_fix.md`
- `kusto/hub/02-slow-http-requests.md`
- `kusto/hub/02-slow-http-requests_fix.md`
- `kusto/hub/03-unhandled-exceptions.md`
- `kusto/hub/03-unhandled-exceptions_fix.md`
- `kusto/hub/04-node-failures-by-workflow.md`
- `kusto/hub/04-node-failures-by-workflow_fix.md`
- `kusto/hub/05-audit-trail-write-operations.md`
- `kusto/hub/05-audit-trail-write-operations_fix.md`
- `kusto/hub/06-signalr-client-activity.md`
- `kusto/hub/06-signalr-client-activity_fix.md`
- `kusto/hub/07-run-duration-distribution.md`
- `kusto/hub/07-run-duration-distribution_fix.md`
- `kusto/telemetry/08-appinsights-failed-runs.md`
- `kusto/telemetry/09-appinsights-slow-requests.md`
- `kusto/telemetry/10-appinsights-audit-events.md`

---

## Already-Recommended Library Queries (do NOT re-recommend)

_(No library queries were recommended in `etw_events.md` for this repository — all etw_events.md queries reference "Issue: N/A -- general troubleshooting" against the consumed provider catalog, not the WorkflowUi.Server proposed provider.)_

---

## Recommended Next Actions

1. **Implement ETW EventSource** — highest leverage action; unblocks all 7 Hub queries at once. See `Blocker 1` above for the required event method names derived from the query table names.
2. **Configure App Insights → ADX export** or switch Q08–Q10 to query Log Analytics directly — unblocks Telemetry queries with minimal code change.
3. **Define `code_issues.md`** — create incident-specific issue definitions (ISS-1, ISS-2, ...) so that future query scoring can reference root-cause hypotheses rather than observability gaps.
4. **Retry Q01f–Q07f** once ETL data is ingested — no query KQL changes needed; the corrections are valid.
