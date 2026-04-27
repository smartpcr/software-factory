# ETW Events Analysis -- WorkflowUi.Server

> **Generated:** 2026-04-27 | **Iteration:** 0 (initial scan)

## Summary

**No `EventSource` (ETW) providers were found** in the `WorkflowUi.Server` project or its dependency `ExecutionEngine`. The entire application uses:

| Observability Layer | Technology | Location |
|---|---|---|
| Structured logging | `ILogger<T>` (Microsoft.Extensions.Logging) | All classes |
| Workflow telemetry | Custom `RunEvent` DTOs via `EngineEventBridge` | `Telemetry/` |
| Live streaming | SignalR (`TelemetryHub`) | `Hubs/` |
| Persistence | JSONL files via `RunHistoryWriter` | `Persistence/` |
| Audit trail | `IAuditLog` + ILogger | `Auth/AuditMiddleware` |
| ETW reading (tooling) | `Microsoft.Diagnostics.Tracing` | `EtwEventReader/` (reads external ETW, does NOT produce) |

The **EtwEventReader** project reads ETW traces from *external* `.etl` files but does not define any ETW providers itself.

---

## Current Observability Architecture

### ILogger Instrumentation Points

The following table catalogs every `ILogger` call site in WorkflowUi.Server:

| # | Class | Level | Message Template | Key Properties |
|---|---|---|---|---|
| 1 | `Program` | Information | `WorkflowUi.Server started on {MachineName} in {EnvironmentName} with engine v{EngineAssemblyVersion}` | MachineName, EnvironmentName, EngineAssemblyVersion |
| 2 | `AuthServiceCollectionExtensions` | Warning | `DEV BYPASS ACTIVE -- do not use in prod` | (none) |
| 3 | `RequestLoggingEnricher` | Information | `HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms` | Method, Path, StatusCode, ElapsedMs, correlationId (scope) |
| 4 | `AuditMiddleware` | Information | `Audit: {Action} {Path} by {UserId} -> {OutcomeCode}` | Action, Path, UserId, OutcomeCode |
| 5 | `TelemetryHub` | Information | `Client {ConnectionId} attached to run {RunId}` | ConnectionId, RunId |
| 6 | `TelemetryHub` | Information | `Client {ConnectionId} detached from run {RunId}` | ConnectionId, RunId |
| 7 | `TelemetryHubSink` | Warning | `Failed to persist run event {RunId}/{Seq} to JSONL` | RunId, Seq, exception |
| 8 | `RunManager` | Information | `Starting run {RunId} for workflow {WorkflowId}` | RunId, WorkflowId |
| 9 | `RunManager` | Error | `Run {RunId} failed with unhandled exception` | RunId, exception |
| 10 | `RunManager` | Warning | `Failed to publish {Kind} event for run {RunId}` | Kind, RunId, exception |
| 11 | `EngineEventBridge` | Warning | `Unknown engine event type {EventType} ignored` | EventType |
| 12 | `RunCleanupHostedService` | Error | `Run cleanup failed` | exception |
| 13 | `RunCleanupHostedService` | Information | `Cleaned run directory {Dir} (last write {LastWrite})` | Dir, LastWrite |
| 14 | `FileWorkflowStore` | Warning | `Path traversal rejected for id {Id}` | Id |
| 15 | `FileWorkflowStore` | Warning | `Path traversal rejected on save for id {Id}` | Id |
| 16 | `FileWorkflowStore` | Warning | `Path traversal rejected on delete for id {Id}` | Id |
| 17 | `ProblemDetailsWriter` | Error | `Unhandled exception for {CorrelationId}` | CorrelationId, exception |

### Custom Workflow Telemetry Events (RunEvent system)

The `EngineEventBridge` translates CLR events from the `WorkflowEngine` into `RunEvent` DTOs that flow through SignalR and are persisted to JSONL.

#### Native Events (from engine)

| RunEventKind | Source Engine Event | Key Payload Fields |
|---|---|---|
| `WorkflowStarted` | `WorkflowStartedEvent` | workflowId, workflowName, totalNodes |
| `WorkflowCompleted` | `WorkflowCompletedEvent` | workflowId, duration, status |
| `WorkflowFailed` | `WorkflowFailedEvent` | workflowId, duration, errorMessage |
| `WorkflowCancelled` | `WorkflowCancelledEvent` | workflowId, duration, reason |
| `NodeStarted` | `NodeStartedEvent` | nodeInstanceId |
| `NodeCompleted` | `NodeCompletedEvent` | nodeInstanceId, duration |
| `NodeFailed` | `NodeFailedEvent` | nodeInstanceId, errorMessage, duration |
| `NodeCancelled` | `NodeCancelledEvent` | nodeInstanceId, reason |
| `NodeProgress` | `NodeProgressEvent` | nodeInstanceId, progressPercent, status |

#### Synthetic Events (derived by bridge/derivers)

| RunEventKind | Deriver | Key Payload Fields |
|---|---|---|
| `EdgeActivated` | `EdgeActivationDeriver` | sourceNodeId, targetNodeId, triggerMessageType, condition |
| `NodeSkipped` | `SkipDeriver` | reason (BranchNotTaken/SwitchCaseNoMatch), sourceNodeId |

#### Extended Events (from RunManager)

`RunManager` also emits events via `NodeCompletedEx` and `NodeFailedEx` engine handlers:

| Event | Extra Payload Fields | Notes |
|---|---|---|
| `NodeCompleted` (synthetic=true) | inputData, outputData, globalVariables | Rich context for debugging |
| `NodeFailed` (synthetic=true) | inputData, globalVariables | Captures input state at failure |

### Data Flow

```
WorkflowEngine (CLR events)
    |
    v
RunManager.WireEngineEvents()  -->  IRunEventSink.PublishAsync()
    |                                      |
    v                                      v
EngineEventBridge.HandleAsync()     TelemetryHubSink
    |                                   |          |
    v                                   v          v
RunEvent DTO                  RunHistoryBuffer   RunHistoryWriter
                                   |              (JSONL files)
                                   v
                            SignalR TelemetryHub
                                   |
                                   v
                            Browser clients
```

---

## Request Processing Pipeline

```
Incoming HTTP Request
    |
    v
1. VersionHeaderMiddleware          -- Adds X-Workflow-Ui-Version header (no logging)
    |
    v
2. CorrelationIdMiddleware          -- Reads/generates X-Correlation-Id (no logging)
    |
    v
3. RequestLoggingEnricher           -- LOG: "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms"
    |                                  (logs on response completion)
    v
4. ExceptionHandler                 -- LOG: "Unhandled exception for {CorrelationId}" (on error)
    |                                  Returns RFC-7807 ProblemDetails with correlationId
    v
5. Authentication (JWT / DevBypass) -- No direct logging (framework logs)
    |
    v
6. Authorization                    -- No direct logging (framework logs)
    |
    v
7. AuditMiddleware                  -- LOG: "Audit: {Action} {Path} by {UserId} -> {OutcomeCode}"
    |                                  (only for POST/PUT/PATCH/DELETE)
    v
8. Endpoint handler                 -- Varies by endpoint
```

---

## Observability Gap Analysis

### Gaps (no logging or telemetry)

| # | Code Path | Location | Impact |
|---|---|---|---|
| G1 | Authentication success/failure | `DevBypassAuthenticationHandler.HandleAuthenticateAsync()` | No log when auth succeeds or fails (only framework-level) |
| G2 | JWT token extraction for SignalR | `AuthServiceCollectionExtensions` OnMessageReceived | No log when SignalR token is extracted from query string |
| G3 | Workflow save/load/delete outcomes | `FileWorkflowStore` | Only path-traversal rejections are logged; successful CRUD ops rely solely on `IAuditLog` |
| G4 | Run history replay | `TelemetryHub.Replay()` | No log for replay requests |
| G5 | SignalR disconnect cleanup | `TelemetryHub.OnDisconnectedAsync()` | No log for disconnect events |
| G6 | Individual endpoint handler errors | Various endpoint classes | Some endpoints may swallow errors silently |
| G7 | ETag conflict detection | `FileWorkflowStore.SaveAsync/DeleteAsync` | Returns false/PreconditionFailed with no logging |

### Strengths

| # | Area | Coverage |
|---|---|---|
| S1 | Request timing | Every HTTP request is timed via `RequestLoggingEnricher` |
| S2 | Correlation propagation | `CorrelationIdMiddleware` ensures every request has an ID |
| S3 | Write audit trail | `AuditMiddleware` logs all mutating operations |
| S4 | Workflow execution lifecycle | Full event coverage (start, complete, fail, cancel, progress) |
| S5 | Error bubbling | `ProblemDetailsWriter` captures and logs all unhandled exceptions |

---

## Recommended ETW Provider Design (If ETW Were Added)

If this service were to add ETW instrumentation (e.g., for production diagnostics or Kusto ingestion), the following provider structure is recommended:

### Proposed Provider: `Microsoft-WorkflowUi-Server`

```csharp
[EventSource(Name = "Microsoft-WorkflowUi-Server")]
public sealed class WorkflowUiEventSource : EventSource
{
    public static readonly WorkflowUiEventSource Instance = new();

    // --- Request lifecycle ---
    [Event(1, Level = EventLevel.Informational, Message = "Request {0} {1} started")]
    public void RequestStart(string method, string path, string correlationId) { ... }

    [Event(2, Level = EventLevel.Informational, Message = "Request {0} {1} completed in {3}ms with {2}")]
    public void RequestStop(string method, string path, int statusCode, long elapsedMs) { ... }

    [Event(3, Level = EventLevel.Error, Message = "Unhandled exception for correlation {0}: {1}")]
    public void UnhandledException(string correlationId, string exceptionType, string message) { ... }

    // --- Workflow run lifecycle ---
    [Event(10, Level = EventLevel.Informational, Message = "Run {0} started for workflow {1}")]
    public void RunStarted(string runId, string workflowId, string workflowName, int totalNodes) { ... }

    [Event(11, Level = EventLevel.Informational, Message = "Run {0} completed with status {1}")]
    public void RunCompleted(string runId, string status, string duration) { ... }

    [Event(12, Level = EventLevel.Error, Message = "Run {0} failed: {1}")]
    public void RunFailed(string runId, string errorMessage, string duration) { ... }

    [Event(13, Level = EventLevel.Warning, Message = "Run {0} cancelled: {1}")]
    public void RunCancelled(string runId, string reason) { ... }

    // --- Node lifecycle ---
    [Event(20, Level = EventLevel.Verbose, Message = "Node {1} started in run {0}")]
    public void NodeStarted(string runId, string nodeId, string nodeName) { ... }

    [Event(21, Level = EventLevel.Informational, Message = "Node {1} completed in run {0}")]
    public void NodeCompleted(string runId, string nodeId, string nodeName, string duration) { ... }

    [Event(22, Level = EventLevel.Error, Message = "Node {1} failed in run {0}: {3}")]
    public void NodeFailed(string runId, string nodeId, string nodeName, string errorMessage) { ... }

    // --- Auth ---
    [Event(30, Level = EventLevel.Warning, Message = "Auth bypass active for {0}")]
    public void DevBypassActive(string environment) { ... }

    [Event(31, Level = EventLevel.Informational, Message = "Audit: {0} {1} by {2} -> {3}")]
    public void AuditEvent(string action, string path, string userId, int outcomeCode) { ... }

    // --- SignalR ---
    [Event(40, Level = EventLevel.Informational, Message = "Client {0} attached to run {1}")]
    public void ClientAttached(string connectionId, string runId) { ... }

    [Event(41, Level = EventLevel.Informational, Message = "Client {0} detached from run {1}")]
    public void ClientDetached(string connectionId, string runId) { ... }
}
```

### Proposed Kusto Table Mapping (Hub convention)

If deployed as a Hub service, the ETW events would map to these Kusto tables:

| Event Method | Kusto Table |
|---|---|
| `RequestStart` | `['ETL-Microsoft-WorkflowUi-Server.RequestStart']` |
| `RequestStop` | `['ETL-Microsoft-WorkflowUi-Server.RequestStop']` |
| `UnhandledException` | `['ETL-Microsoft-WorkflowUi-Server.UnhandledException']` |
| `RunStarted` | `['ETL-Microsoft-WorkflowUi-Server.RunStarted']` |
| `RunCompleted` | `['ETL-Microsoft-WorkflowUi-Server.RunCompleted']` |
| `RunFailed` | `['ETL-Microsoft-WorkflowUi-Server.RunFailed']` |
| `RunCancelled` | `['ETL-Microsoft-WorkflowUi-Server.RunCancelled']` |
| `NodeStarted` | `['ETL-Microsoft-WorkflowUi-Server.NodeStarted']` |
| `NodeCompleted` | `['ETL-Microsoft-WorkflowUi-Server.NodeCompleted']` |
| `NodeFailed` | `['ETL-Microsoft-WorkflowUi-Server.NodeFailed']` |
| `DevBypassActive` | `['ETL-Microsoft-WorkflowUi-Server.DevBypassActive']` |
| `AuditEvent` | `['ETL-Microsoft-WorkflowUi-Server.AuditEvent']` |
| `ClientAttached` | `['ETL-Microsoft-WorkflowUi-Server.ClientAttached']` |
| `ClientDetached` | `['ETL-Microsoft-WorkflowUi-Server.ClientDetached']` |

---

## Recommended Kusto Queries (Hypothetical -- ETW Not Yet Implemented)

The queries below target the **proposed** ETW provider above. They are templates for when ETW instrumentation is added.

### Query 1: Failed Workflow Runs

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunFailed']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, FormattedMessage, MachineName, runId, errorMessage, duration
| order by OccurrenceTime desc
| limit 1000
```

### Query 2: Slow HTTP Requests (>5s)

```kusto
['ETL-Microsoft-WorkflowUi-Server.RequestStop']
| where OccurrenceTime > ago(1d)
| where elapsedMs > 5000
| project OccurrenceTime, method, path, statusCode, elapsedMs, MachineName
| order by elapsedMs desc
| limit 1000
```

### Query 3: Unhandled Exceptions

```kusto
['ETL-Microsoft-WorkflowUi-Server.UnhandledException']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, correlationId, exceptionType, message, MachineName
| order by OccurrenceTime desc
| limit 1000
```

### Query 4: Node Failures by Workflow

```kusto
['ETL-Microsoft-WorkflowUi-Server.NodeFailed']
| where OccurrenceTime > ago(1d)
| summarize FailCount = count(), LastSeen = max(OccurrenceTime) by nodeId, nodeName, runId
| order by FailCount desc
| limit 1000
```

### Query 5: Audit Trail for Write Operations

```kusto
['ETL-Microsoft-WorkflowUi-Server.AuditEvent']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, action, path, userId, outcomeCode, MachineName
| order by OccurrenceTime desc
| limit 1000
```

### Query 6: SignalR Client Activity

```kusto
['ETL-Microsoft-WorkflowUi-Server.ClientAttached']
| where OccurrenceTime > ago(1d)
| summarize AttachCount = count() by runId, bin(OccurrenceTime, 1h)
| order by OccurrenceTime desc
| limit 1000
```

### Query 7: Run Duration Distribution

```kusto
['ETL-Microsoft-WorkflowUi-Server.RunCompleted']
| where OccurrenceTime > ago(7d)
| project OccurrenceTime, runId, status, duration, MachineName
| order by OccurrenceTime desc
| limit 1000
```

---

## Existing ILogger Queries (Current -- via Application Insights / Console)

Since the app currently uses `ILogger`, these queries apply if logs are collected via Application Insights, ELK, or similar:

### AppInsights: Failed Runs

```kusto
traces
| where timestamp > ago(1d)
| where message has "failed with unhandled exception"
| extend RunId = tostring(customDimensions["RunId"])
| project timestamp, RunId, message, severityLevel
| order by timestamp desc
```

### AppInsights: Slow Requests

```kusto
traces
| where timestamp > ago(1d)
| where message has "HTTP" and message has "responded"
| parse message with "HTTP " Method " " Path " responded " StatusCode " in " ElapsedMs "ms"
| where toint(ElapsedMs) > 5000
| project timestamp, Method, Path, StatusCode, ElapsedMs
| order by toint(ElapsedMs) desc
```

### AppInsights: Audit Events

```kusto
traces
| where timestamp > ago(7d)
| where message startswith "Audit:"
| parse message with "Audit: " Action " " Path " by " UserId " -> " OutcomeCode
| project timestamp, Action, Path, UserId, OutcomeCode
| order by timestamp desc
```

---

## Query Generation Log

| Iteration | Queries Added | Total | Max Score | Notes |
|---|---|---|---|---|
| 0 | 7 hypothetical ETW + 3 AppInsights | 10 | N/A | No ETW providers found; documented current ILogger coverage and proposed ETW design |
