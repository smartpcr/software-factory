# Observability Gaps — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Source:** kusto-queries/iteration_context.md, kusto-queries/corrections.md, query output files

---

## Gap 1 (Critical): ETW Provider Not Implemented

| Field | Value |
|-------|-------|
| **Provider Name** | `Microsoft-WorkflowUi-Server` |
| **Gap Type** | Code — missing EventSource class |
| **Priority** | **HIGH** — blocks all 14 Hub queries |
| **Affected Queries** | Q01–Q07 (original), Q01f–Q07f (corrected) |
| **Target Tables** | `ETL-Microsoft-WorkflowUi-Server.RunFailed`, `.RunCompleted`, `.NodeFailed`, `.UnhandledException`, `.RequestStop`, `.AuditEvent`, `.ClientAttached` |
| **Cluster** | `https://azshubsupport01.westus.kusto.windows.net` |
| **Database** | `oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a` |

**Evidence:**
- [Evidence: kusto-queries/hub/output/01-failed-workflow-runs_fix.md — `Entity ID 'oci-03105076-5f1e-49ce-83fd-376252c253a3-031ba884-be50-4cd2-8573-62421b14a32a' of kind 'Database' was not found`]
- [Code: crp/workflow/src/WorkflowUi.Server/Telemetry/ — directory contains EngineEventBridge.cs, RunEvent.cs, RunEventKind.cs, IRunEventSink.cs, InMemoryRunEventSink.cs, SequenceAllocator.cs, EdgeActivationDeriver.cs, SkipDeriver.cs — no EventSource subclass]

**Impact:** Without this provider, the following diagnostic capabilities are completely unavailable:
- Workflow failure diagnosis (Q01f: `RunFailed` — score 32)
- Exception root-cause analysis (Q03f: `UnhandledException` — score 32)
- DAG node failure aggregation (Q04f: `NodeFailed` — score 28)
- Run duration trending (Q07f: `RunCompleted` — score 22)
- HTTP latency analysis (Q02f: `RequestStop` — score 20)
- Operational audit (Q05f: `AuditEvent` — score 18)
- SignalR client monitoring (Q06f: `ClientAttached` — score 18)

**Current observability state:** The service uses `ILogger<T>` in `RunManager`, `EngineEventBridge`, `AuditMiddleware`, `RequestLoggingEnricher`, and `TelemetryHub`. These logs flow to Application Insights via the ASP.NET Core provider but are **not queryable in Kusto**. There is no ETW instrumentation, no OpenTelemetry metrics, and no distributed tracing.
[Code: crp/workflow/src/WorkflowUi.Server/Hosting/RunManager.cs:L32 — `ILogger<RunManager> logger` field, no EventSource usage]
[Code: crp/workflow/src/WorkflowUi.Server/Hosting/RequestLoggingEnricher.cs:L53–L58 — `LogInformation("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms")` — ILogger only]

---

## Gap 2 (Infrastructure): `traces` Table Absent from ADX Telemetry

| Field | Value |
|-------|-------|
| **Table Name** | `traces` |
| **Gap Type** | Infrastructure — missing ADX data connection |
| **Priority** | **MEDIUM** — blocks 3 Telemetry queries; workaround available |
| **Affected Queries** | Q08, Q09, Q10 |
| **Cluster** | `https://aeoprodtelemetry.eastus.kusto.windows.net` |
| **Database** | `Telemetry` |

**Evidence:**
- [Evidence: kusto-queries/telemetry/output/08-appinsights-failed-runs.md — `Semantic error: 'where' operator: Failed to resolve table or column expression named 'traces'`]
- [Evidence: kusto-queries/corrections.md, Failure Group B — "Correction status: UNFIXABLE — no schema equivalent"]

**Impact:** The 3 Telemetry queries (Q08–Q10) are valid KQL that would work against Application Insights / Log Analytics, but the `traces` table does not exist in the ADX Telemetry database. The data is flowing to Application Insights but is not exported to ADX.

**Workaround:** Query Application Insights directly via the Azure Monitor Logs API (`https://api.loganalytics.io/v1/workspaces/{workspaceId}/query`). The KQL syntax is identical.

**Permanent fix:** Configure Diagnostics Settings on the Application Insights resource to export to Event Hub → ADX Telemetry database Data Connection.

---

## Gap 3 (Analysis): No `code_issues.md`

| Field | Value |
|-------|-------|
| **Gap Type** | Analysis — no incident-specific issue definitions |
| **Priority** | **LOW** |
| **Impact** | Query scoring is based on observability-gap potential, not confirmed root-cause hypotheses |

**Evidence:**
- [Evidence: kusto-queries/iteration_context.md, Gaps table — "No `code_issues.md` | Analysis gap | LOW | No ISS-N issues defined; scoring is observability-gap based, not incident-based"]

**Remediation:** Create `code_issues.md` with ISS-1, ISS-2, etc. issue definitions to enable structured scoring against specific root-cause hypotheses in future investigation iterations.

---

## Gap Dependency Chain

```
Gap 1 (ETW Provider) ──blocks──> ETL ingestion ──blocks──> Hub DB creation ──blocks──> Q01f–Q07f execution
Gap 2 (ADX Export)   ──blocks──> traces table in ADX ──blocks──> Q08–Q10 execution
Gap 3 (code_issues)  ──blocks──> structured incident scoring (non-critical)
```

Gap 1 must be resolved first — it has the highest diagnostic impact and no workaround.
Gap 2 has an immediate workaround (Log Analytics API) while the permanent fix is configured.
Gap 3 is informational and does not block query execution.
