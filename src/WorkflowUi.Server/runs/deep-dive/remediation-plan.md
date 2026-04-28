# Remediation Plan — WorkflowUi.Server Observability

> **Generated:** 2026-04-27 | **Source:** kusto-queries/iteration_context.md, kusto-queries/corrections.md

---

## Priority 1: Implement `Microsoft-WorkflowUi-Server` EventSource

**Justification:** All 14 Hub queries (Q01–Q07, Q01f–Q07f) fail because the ETW provider `Microsoft-WorkflowUi-Server` has never been implemented. No ETL files have been collected, so no database or tables exist on the Hub cluster.
[Evidence: kusto-queries/hub/output/01-failed-workflow-runs_fix.md — `Entity ID 'oci-03105076-...' of kind 'Database' was not found`]
[Evidence: kusto-queries/corrections.md — "Root cause of continued failure: The Microsoft-WorkflowUi-Server ETW EventSource has never been implemented in production code"]

### Step 1a: Create EventSource Class

Add `WorkflowUiServerEventSource.cs` to `src/WorkflowUi.Server/Telemetry/`.

**Required event methods** (derived from the ETL table names targeted by queries Q01f–Q07f):

| Event Method | ETL Table Target | Query | Score | Mapped Codebase Source |
|-------------|------------------|-------|-------|----------------------|
| `RunFailed` | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | Q01f | 32 | `RunManager` — workflow failure path |
| `RunCompleted` | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | Q07f | 22 | `RunManager` — workflow completion path |
| `NodeFailed` | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | Q04f | 28 | `EngineEventBridge.HandleAsync` → `RunEventKind.NodeFailed` |
| `UnhandledException` | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | Q03f | 32 | `ProblemDetailsWriter.UseProblemDetailsExceptionHandler` (line 100–105) |
| `RequestStop` | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | Q02f | 20 | `RequestLoggingEnricher.InvokeAsync` (line 53–58) |
| `AuditEvent` | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | Q05f | 18 | `AuditMiddleware.InvokeAsync` (line 58–65) |
| `ClientAttached` | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | Q06f | 18 | `TelemetryHub.AttachRun` (line 33–52) |

A compilable skeleton is provided in `deep-dive/etw-eventsource-skeleton.cs`.

### Step 1b: Wire EventSource Calls into Existing Code

Call the EventSource singleton at the corresponding code locations listed above. For example:
- In `RunManager`, after logging a failed run, call `WorkflowUiServerEventSource.Log.RunFailed(runId, errorMessage, duration)`
- In `ProblemDetailsWriter.UseProblemDetailsExceptionHandler`, after `logger.LogError(...)`, call `WorkflowUiServerEventSource.Log.UnhandledException(correlationId, exceptionType, message)`
- In `RequestLoggingEnricher.InvokeAsync`, after `logger.LogInformation(...)`, call `WorkflowUiServerEventSource.Log.RequestStop(method, path, statusCode, elapsedMs)`

### Step 1c: Deploy and Verify ETL Collection

1. Build and deploy `WorkflowUi.Server` to an environment with active ETL collection
2. Verify ETL files are being produced by the `EtlIterator` pipeline
3. Confirm the GUID database `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a` is created on `azshubsupport01.westus` with the expected 7 tables
4. Re-execute Q01f–Q07f — **no KQL changes needed**; the corrected queries are valid and will return data once tables exist

---

## Priority 2: Configure App Insights → ADX Telemetry Export

**Justification:** Queries Q08–Q10 target the `traces` table which exists in Application Insights but is not present in the ADX Telemetry database.
[Evidence: kusto-queries/telemetry/output/08-appinsights-failed-runs.md — `Semantic error: 'where' operator: Failed to resolve table or column expression named 'traces'`]
[Evidence: kusto-queries/corrections.md — "Correction status: UNFIXABLE — no schema equivalent"]

### Step 2a: Immediate Workaround — Query Log Analytics API

The 3 Telemetry queries (Q08–Q10) use valid KQL and can execute against Application Insights directly:
```
POST https://api.loganalytics.io/v1/workspaces/{workspaceId}/query
Authorization: Bearer {token}
Content-Type: application/json

{ "query": "<KQL from Q08/Q09/Q10>" }
```

### Step 2b: Permanent Fix — ADX Data Connection

1. Navigate to the Application Insights resource for `WorkflowUi.Server` in Azure Portal
2. Under **Diagnostic Settings**, create a new setting to export `traces` to an Event Hub
3. On the ADX Telemetry cluster (`aeoprodtelemetry`), create a Data Connection from that Event Hub to the `Telemetry` database
4. Verify the `traces` table (or its ADX equivalent) appears in the database schema
5. Re-execute Q08–Q10 against `aeoprodtelemetry` / `Telemetry`

---

## Priority 3: Re-Execute Hub Queries Post-Ingestion

**Justification:** After ETL ingestion is operational (Priority 1 complete), the corrected queries Q01f–Q07f should execute successfully.
[Evidence: kusto-queries/corrections.md — "The GUID database coordinates are correct per connections.yml"]

### Execution Plan

| Query | File | Expected Table | Key Columns |
|-------|------|---------------|-------------|
| Q01f | `hub/01-failed-workflow-runs_fix.md` | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | `runId`, `errorMessage`, `duration` |
| Q02f | `hub/02-slow-http-requests_fix.md` | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | `method`, `path`, `statusCode`, `elapsedMs` |
| Q03f | `hub/03-unhandled-exceptions_fix.md` | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | `correlationId`, `exceptionType`, `message` |
| Q04f | `hub/04-node-failures-by-workflow_fix.md` | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | `nodeId`, `nodeName`, `runId`, `FailCount` |
| Q05f | `hub/05-audit-trail-write-operations_fix.md` | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | `action`, `path`, `userId`, `outcomeCode` |
| Q06f | `hub/06-signalr-client-activity_fix.md` | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | `runId`, `AttachCount` |
| Q07f | `hub/07-run-duration-distribution_fix.md` | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | `runId`, `status`, `duration` |

**Cluster:** `https://azshubsupport01.westus.kusto.windows.net`
**Database:** `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a`

---

## Priority Summary

| # | Action | Unblocks | Effort | Dependencies |
|---|--------|----------|--------|-------------|
| 1 | Implement EventSource + deploy | Q01f–Q07f (14 queries) | Medium | None |
| 2 | Configure ADX export | Q08–Q10 (3 queries) | Low | Azure Portal access |
| 3 | Re-execute corrected queries | Data validation | Low | Priority 1 complete |
