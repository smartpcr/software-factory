# ETW Events Discovery Report

**Repository:** `E:\work\github\crp\workflow` (Workflow.sln)
**Service Type:** Standalone tool (ETW consumer + Forge API + Execution Engine)
**Generated:** 2026-04-29
**Iteration:** 0 (initial discovery)

---

## Summary

This repository is primarily an ETW **consumer and analysis tool** — it parses `.etl`/`.evtx` files from Azure Stack services and ingests them into Kusto. It does not emit ETW events itself in production.

However, it uses three active observability patterns for its own code:
1. **ILogger** (Microsoft.Extensions.Logging) — 100+ structured log calls across all projects
2. **System.Diagnostics.Metrics** — `Forge.Api.Phase16` meter with 11 counters + 5 histograms
3. **SignalR hub events** — 9 distinct event types across `ForgeHub` and `TelemetryHub`

An **EventSource skeleton** (`Microsoft-WorkflowUi-Server`) exists at `src/WorkflowUi.Server/runs/deep-dive/etw-eventsource-skeleton.cs` with 7 planned events, but it is NOT compiled into any production project and has zero call sites.

---

## ETW Events by Code Issue

### ISS-1: ClaudeAgentRunner tool_use Not Wired (FORGE-GAP-11.6 + 11.5)

**Code Path:** `AgentsEndpoints.cs:360` → `AgentRunnerDispatcher.RunClaudeSdk()` → SSE stream → `PumpEvents:406-463`

| Logger Method | Provider / Sink | Event | Level | Key Data | Code Location |
|---|---|---|---|---|---|
| `_logger.LogError(ex, "Agent invocation {Id} pump crashed")` | ILogger | PumpCrashed | Error | InvocationId, ExceptionType | `AgentsEndpoints.cs:463` |
| `_logger.LogWarning("Claude HTTP failed (attempt {Attempt}); retrying")` | ILogger | ClaudeHttpRetry | Warning | Attempt number | `AgentRunnerDispatcher.cs:250` |
| `_logger.LogWarning("Claude returned {Status} (attempt {Attempt}); retrying")` | ILogger | ClaudeStatusRetry | Warning | HTTP status, attempt | `AgentRunnerDispatcher.cs:259` |
| `_logger.LogWarning("Unknown engine event type {EventType} ignored")` | ILogger | UnknownEventType | Warning | EventType name | `EngineEventBridge.cs:45` |

**Logging gaps (CRITICAL):**
- `AgentToolHost.cs` — **ZERO ILogger calls**. Sandbox enforcement (tool allowlist, path traversal prevention, symlink detection, budget checks) is completely silent.
- `AgentRunnerDispatcher.cs` — Process.Start() failures (lines 83-97), CLI timeouts (line 464-468), disposal failures (lines 492-493) are all caught without logging.
- No `tool_use` SSE content block parsing exists — the runner only handles `text` type blocks. Any `tool_use` block from the Anthropic API will cause a pump crash (observable via the LogError above).

**Troubleshooting value:** The `pump crashed` error is the PRIMARY signal for this gap. `JsonException` in the exception type indicates the runner encountered an unexpected SSE block type (e.g., `tool_use`). The `Unknown engine event type` warning confirms ToolCall/ToolResult events are produced but not bridged to SignalR.

---

### ISS-2: SignalR Streaming UI Hooks Not Wired (FORGE-GAP-11.7)

**Code Path:** `AgentsEndpoints.PumpEvents:406` → `ForgeHub` group `agent.{agentId}` → (no UI consumer)

| Logger Method | Provider / Sink | Event | Level | Key Data | Code Location |
|---|---|---|---|---|---|
| `hub.Clients.Group().SendAsync("agent.event", ...)` | SignalR | agent.event | — | invocationId, agentId, kind, text, toolName | `AgentsEndpoints.cs:406-418` |
| `hub.Clients.Group().SendAsync("agent.usage", ...)` | SignalR | agent.usage | — | agentId, inputTokens, outputTokens, costUsd | `AgentsEndpoints.cs:487-499` |
| `hub.Clients.All.SendAsync("agent.reassigned", ...)` | SignalR | agent.reassigned | — | agentId, fromCrewId, toCrewId | `AgentsEndpoints.cs:233-236` |
| `_logger.LogInformation("Client {ConnectionId} attached to run {RunId}")` | ILogger | ClientAttached | Info | ConnectionId, RunId | `TelemetryHub.cs:48` |
| `_logger.LogInformation("Client {ConnectionId} detached from run {RunId}")` | ILogger | ClientDetached | Info | ConnectionId, RunId | `TelemetryHub.cs:69` |
| `_logger.LogWarning("Failed to persist run event {RunId}/{Seq}")` | ILogger | PersistFailed | Warning | RunId, Seq, ExceptionType | `TelemetryHubSink.cs:50` |

**Logging gaps:**
- `ForgeHub.cs` — ZERO ILogger calls in `Subscribe()`/`Unsubscribe()` methods. Cannot audit group membership changes.
- No client-side subscription verification — server fan-out is confirmed working, but there's no way to verify UI clients are actually receiving events.

**Troubleshooting value:** If `agent.event` messages are being sent (visible in SignalR diagnostics) but the UI shows no live updates, the gap is confirmed: server fan-out works but UI consumer hooks are missing. The `ClientAttached` / `ClientDetached` logs in TelemetryHub show whether any UI clients are subscribing to run streams.

---

### ISS-3: Budget Enforcement Observability (FORGE-GAP-11.10)

**Code Path:** `AgentToolHost.Session.RecordTokens():108` → throws `AgentBudgetExceededException` → `PumpEvents:463` catches

| Logger Method | Provider / Sink | Event | Level | Key Data | Code Location |
|---|---|---|---|---|---|
| `hub.Clients.All.SendAsync("agent.budget_exceeded", ...)` | SignalR | agent.budget_exceeded | — | crewId, dailyBudgetUsd, spentTodayUsd, agentId | `AgentsEndpoints.cs:333-336` |
| `_logger.LogError(ex, "Agent invocation {Id} pump crashed")` | ILogger | PumpCrashed | Error | AgentBudgetExceededException | `AgentsEndpoints.cs:463` |

**Logging gaps:**
- `RecordTokens()` throws without logging — budget breaches are only observable through exception propagation or SignalR broadcast.
- No dedicated `LogWarning("Budget exceeded for agent {AgentId}")` call before throwing.

**Troubleshooting value:** Budget breaches appear as `pump crashed` errors with `ExceptionType=AgentBudgetExceededException`. The `agent.budget_exceeded` SignalR event confirms the broadcast was sent. No rows = either no breaches or silent enforcement.

---

### ISS-4: Supervising Audit Column Never Written (FORGE-GAP-10.8)

**Code Path:** `AuditMiddleware.cs:60` → `AuditRow.Supervising` → (always false)

| Logger Method | Provider / Sink | Event | Level | Key Data | Code Location |
|---|---|---|---|---|---|
| `_logger.LogInformation("Audit: {Action} {Path} by {UserId} → {OutcomeCode}")` | ILogger | AuditEvent | Info | action, path, userId, outcomeCode | `AuditMiddleware.cs:60` |

**Logging gaps:**
- `StepBudgetChecker.cs:81` hardcodes `Supervising = false` in every AuditRow.
- No code path sets `Supervising = true` anywhere in the codebase.
- The audit log itself works, but the `Supervising` field is never populated.

**Troubleshooting value:** The audit trail captures all mutating operations, but the `Supervising` column is always `false` — filtering by `Supervising = true` will always return zero rows.

---

### ISS-5: MeSettings Validation Gap (FORGE-GAP-10.2)

**Code Path:** `MeSettingsEndpoints.cs` → accepts arbitrary strings for DefaultProductId/DefaultRepoId

**No ILogger or telemetry calls** guard the validation path. The endpoint accepts invalid product/repo IDs without emitting any warning or metric.

**Logging gaps:**
- No `LogWarning("Invalid DefaultProductId {Id} — product not found")` call.
- No `forge.settings.invalid_default` counter.

**Troubleshooting value:** There are NO observable signals for invalid settings. A dedicated validation log or metric would detect misconfigured user defaults.

---

### ISS-6: PR RerunBuild/CancelBuild Are Fake (FORGE-GAP-4.9)

**Code Path:** PR endpoints → flip cached state → synthesize log tails → return 202

**No ILogger calls** distinguish fake CI operations from real ones. The endpoints return 202 with synthesized data, giving no indication that no actual CI provider was invoked.

**Troubleshooting value:** There are NO observable signals to detect that CI operations are fake. The 202 response looks identical to a real CI call.

---

## Discovered Provider Catalog

### 1. EventSource (Skeleton — NOT in production)

| Field | Value |
|---|---|
| **Provider Name** | `Microsoft-WorkflowUi-Server` |
| **File** | `src/WorkflowUi.Server/runs/deep-dive/etw-eventsource-skeleton.cs` |
| **Status** | ⚠️ SKELETON — not compiled, zero call sites |
| **Singleton** | `WorkflowUiServerEventSource.Log` |

| Event ID | Method | Level | Message | Planned Table |
|---|---|---|---|---|
| 1 | RunFailed | Error | Run {0} failed after {2}ms: {1} | `['ETL-Microsoft-WorkflowUi-Server.RunFailed']` |
| 2 | RunCompleted | Informational | Run {0} completed with status {1} in {2}ms | `['ETL-Microsoft-WorkflowUi-Server.RunCompleted']` |
| 3 | NodeFailed | Error | Node {1} ({0}) failed in run {2}: {3} | `['ETL-Microsoft-WorkflowUi-Server.NodeFailed']` |
| 4 | UnhandledException | Critical | Unhandled exception [{1}] for {0}: {2} | `['ETL-Microsoft-WorkflowUi-Server.UnhandledException']` |
| 5 | RequestStop | Informational | HTTP {0} {1} -> {2} in {3}ms | `['ETL-Microsoft-WorkflowUi-Server.RequestStop']` |
| 6 | AuditEvent | Informational | Audit: {0} {1} by {2} -> {3} | `['ETL-Microsoft-WorkflowUi-Server.AuditEvent']` |
| 7 | ClientAttached | Informational | Client {1} attached to run {0} | `['ETL-Microsoft-WorkflowUi-Server.ClientAttached']` |

### 2. System.Diagnostics.Metrics (Production — Active)

**Meter:** `Forge.Api.Phase16` v1.0 (defined in `LeadAutoDispatcher.cs:51`)

#### Counters

| Instrument Name | Source File | Tags |
|---|---|---|
| `forge.dispatch.lead_no_capacity` | LeadAutoDispatcher.cs | crewId |
| `forge.coding.over_budget` | StepBudgetChecker.cs | stepId |
| `forge.queue.enqueued` | CrewQueueWorker.cs | targetKind |
| `forge.queue.ack_timeout` | CrewQueueWorker.cs | targetKind |
| `forge.queue.strain.fired` | CrewQueueWorker.cs | crewId |
| `forge.sync.workitem.items` | WorkItemSyncMetrics.cs | provider, kind |
| `forge.sync.workitem.pagination.batches` | WorkItemSyncMetrics.cs | provider |
| `forge.sync.workitem.errors` | WorkItemSyncMetrics.cs | provider, errorClass |
| `forge.sync.workitem.agent_ready_assignments` | WorkItemSyncMetrics.cs | team |
| `forge.sync.pr.state_transitions` | ForgeMetrics.cs | from, to |
| `forge.sync.pr.comments` | ForgeMetrics.cs | kind |
| `forge.sync.pr.attention_flips` | ForgeMetrics.cs | direction |
| `forge.pr.comment.authority_refused` | ForgeMetrics.cs | actorKind |
| `forge.pr.comment.linker_matched` | ForgeMetrics.cs | heuristic |
| `forge.worktree.cleanup` | ForgeMetrics.cs | outcome, prState |

#### Histograms

| Instrument Name | Unit | Source File | Tags |
|---|---|---|---|
| `forge.dispatch.lead_to_crew.latency_ms` | ms | LeadAutoDispatcher.cs | crewId, pair, outcome |
| `forge.queue.ack_latency_ms` | ms | CrewQueueWorker.cs | crewId |
| `forge.queue.dwell_ms` | ms | CrewQueueWorker.cs | crewId |
| `forge.sync.workitem.duration_ms` | ms | WorkItemSyncMetrics.cs | provider, kind, outcome |
| `forge.sync.pr.duration_ms` | ms | ForgeMetrics.cs | provider, outcome |

### 3. SignalR Hub Events (Production — Active)

| Hub | Method Name | Group Pattern | Payload Fields | Source |
|---|---|---|---|---|
| ForgeHub | `agent.event` | `agent.{agentId}` | invocationId, agentId, kind, at, text, toolName, toolPayload, error | AgentsEndpoints.cs:406 |
| ForgeHub | `agent.usage` | `agent.{agentId}` | invocationId, agentId, ledgerId, inputTokens, outputTokens, costUsd, status, error | AgentsEndpoints.cs:487 |
| ForgeHub | `agent.reassigned` | All | agentId, fromCrewId, toCrewId, fromModel, toModel | AgentsEndpoints.cs:233 |
| ForgeHub | `agent.budget_exceeded` | All | crewId, dailyBudgetUsd, spentTodayUsd, agentId | AgentsEndpoints.cs:333 |
| ForgeHub | `task.event` | `task.{workItemId}` | workItemId, kind, agentId, crewId, payload, at | TaskChannel.cs:102 |
| ForgeHub | `task.budget-warning` | `task.{workItemId}` | kind, stepId, expected, actual, breach, softFail | StepBudgetChecker.cs:84 |
| ForgeHub | `task.delegated` | `task.{workItemId}` | assignee, crewId, workItemId | LeadAutoDispatcher.cs:230 |
| ForgeHub | `queue.changed` | All | kind, item | CrewsEndpoints.cs |
| TelemetryHub | `ReceiveRunEvent` | `{runId}` | RunEvent DTO (full) | TelemetryHubSink.cs:54 |

### 4. Consumed ETW Providers (ETW Consumer Tool)

The repo's ETL processing pipeline dynamically ingests ETW events from other Azure Stack services into Kusto tables named `['ETL-{ProviderName}.{EventName}']`. Known providers from feature files:

| Category | Provider | Sample Events |
|---|---|---|
| Compute (Hub) | `Microsoft-AzureStack-Compute-HostPluginWatchDog` | EnsureProcessStarted, ProcessStarted, FoundProcessAlreadyRunning, ConfigFilesFound |
| CloudEngine (Hub) | `Microsoft-AzureStack-CloudEngine` | ActionPlanStart, PowerShellRemoteStepStop |
| Infrastructure (Hub) | `Microsoft-AzureStack-Common-Infrastructure-Http` | ClientRequestStop |
| Infrastructure (Hub) | `Microsoft-AzureStack-Common-Infrastructure-HostModel-ServiceFabricHost` | InitializeStateSerializersStart |
| Health (Hub) | `Microsoft-AzureStack-Infrastructure-Health-HealthStorePlugin` | FaultSubStateMachine |
| Health (Hub) | `Microsoft-AzureStack-Infrastructure-Health-RunnerService` | InvalidOldResult |
| HCI Update (Non-Hub) | `Microsoft-URP-InfraEventSource` | HealthCheckResultDirectoryIsEmptyOrDoesNotExist, StartService, StopService |
| NC Host Agent (Hub) | `Microsoft.Windows.NetworkController.HostAgent.*` | FirewallPlugin, VNetPlugin, Service, GatewayPlugin events |

---

## Summary: Key Observability Signals for Troubleshooting

### ILogger Events (by priority for forge_gaps.md issues)

| Priority | Event | Level | Source | Mapped Issue |
|---|---|---|---|---|
| **CRITICAL** | `pump crashed` | Error | AgentsEndpoints.cs:463 | ISS-1 (tool_use) |
| **CRITICAL** | `Unknown engine event type {EventType}` | Warning | EngineEventBridge.cs:45 | ISS-1 (tool_use) |
| HIGH | `Claude HTTP failed (attempt {Attempt})` | Warning | AgentRunnerDispatcher.cs:250 | ISS-1 (SSE reliability) |
| HIGH | `Claude returned {Status} (attempt {Attempt})` | Warning | AgentRunnerDispatcher.cs:259 | ISS-1 (SSE reliability) |
| HIGH | `Failed to persist run event {RunId}/{Seq}` | Warning | TelemetryHubSink.cs:50 | ISS-2 (SignalR) |
| MEDIUM | `Client {ConnectionId} attached to run {RunId}` | Info | TelemetryHub.cs:48 | ISS-2 (SignalR) |
| MEDIUM | `Client {ConnectionId} detached from run {RunId}` | Info | TelemetryHub.cs:69 | ISS-2 (SignalR) |
| MEDIUM | `Audit: {Action} {Path} by {UserId}` | Info | AuditMiddleware.cs:60 | ISS-4 (Supervising) |
| LOW | `Failed to start shared Copilot server` | Error | CopilotServerManager.cs:146 | ISS-1 (runtime) |
| LOW | `HTTP {Method} {Path} responded {StatusCode}` | Info | RequestLoggingEnricher.cs:53 | General |

### OTel Metrics (by priority)

| Priority | Metric | Type | Mapped Issue |
|---|---|---|---|
| HIGH | `forge.coding.over_budget` | Counter | ISS-3 (budget) |
| HIGH | `forge.queue.ack_timeout` | Counter | General (queue health) |
| HIGH | `forge.sync.workitem.errors` | Counter | General (sync reliability) |
| MEDIUM | `forge.dispatch.lead_to_crew.latency_ms` | Histogram | General (dispatch performance) |
| MEDIUM | `forge.queue.dwell_ms` | Histogram | General (queue latency) |

---

## Logging Gaps

1. **`AgentToolHost.cs` (entire file)** — ZERO ILogger calls. Sandbox enforcement decisions (tool allowlist checks, path traversal prevention, symlink detection, budget validation) are completely silent. This is the MOST CRITICAL logging gap — when an agent tries to use a blocked tool or access a forbidden path, there is no record of the attempt.

2. **`AgentRunnerDispatcher.cs:83-97`** — Process.Start() failures are caught without logging. If the Claude CLI binary fails to launch, the error is swallowed silently.

3. **`AgentRunnerDispatcher.cs:464-468`** — OperationCanceledException (CLI timeout) caught without logging. Timeouts during agent execution produce no warning.

4. **`ForgeHub.cs:24,27`** — Subscribe/Unsubscribe methods have no ILogger calls. Cannot audit SignalR group membership changes.

5. **`AgentsEndpoints.cs:456-460`** — OperationCanceledException in PumpEvents caught without logging. Client disconnections during streaming produce no trace.

6. **`CopilotAIExecutor.cs:177,335,341`** — Three silent catch blocks: serialization errors, cleanup errors, and generic ignore blocks.

7. **`FileAIPromptResolver.cs:328,363,367`** — Silent catch blocks for JSON parse failures and cancellation during metadata extraction.

8. **`RecordTokens()` (AgentToolHost)** — Throws AgentBudgetExceededException without prior logging. Budget breaches are only observable through exception propagation.

9. **`MeSettingsEndpoints.cs`** — No validation logging for invalid DefaultProductId/DefaultRepoId values.

10. **`LeadAutoDispatcher.cs:305-308,323-326`** — TryGetAssigneeId/TryGetCrewId JSON parse failures return null silently.

---

## Recommended Kusto Queries

Since this repository uses ILogger (not ETW) for its own telemetry, queries target structured log sinks (Application Insights `traces` table, Seq, or console JSON output).

### Query 1: Agent Invocation Pump Crashes

**Issue:** ISS-1 — ClaudeAgentRunner tool_use not wired
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "pump crashed"
| where severityLevel >= 3
| project timestamp, message, customDimensions.Id, customDimensions.ExceptionType, customDimensions.ExceptionMessage
| order by timestamp desc
| limit 1000
```

**Expected result:** Each row is a fatal crash in the agent event pump. `ExceptionType=JsonException` indicates the runner hit an unexpected SSE block type (e.g., `tool_use`). `ExceptionType=HttpRequestException` indicates API failure. `ExceptionType=AgentBudgetExceededException` indicates budget breach.

---

### Query 2: Claude API HTTP Retries

**Issue:** ISS-1 — ClaudeAgentRunner SSE reliability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Claude HTTP failed" or message has "Claude returned"
| where severityLevel >= 2
| project timestamp, message, customDimensions.Attempt, customDimensions.Status, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

**Expected result:** "Claude HTTP failed" = network exception on attempt N. "Claude returned {Status}" = 429 (rate limited), 503 (unavailable), 529 (overloaded). Attempt > 3 = approaching max retries; exhaustion leads to pump crash.

---

### Query 3: SignalR Agent Event Fan-Out

**Issue:** ISS-2 — SignalR streaming UI hooks not wired
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "agent.event" or message has "agent.usage" or message has "agent.budget_exceeded" or message has "agent.reassigned"
| project timestamp, message, customDimensions.agentId, customDimensions.kind, customDimensions.invocationId
| order by timestamp desc
| limit 1000
```

**Expected result:** If rows exist but UI shows no live updates, server fan-out works but UI consumer hooks are missing (confirmed gap). `kind` field identifies event type: Token, ToolCall, ToolResult, Error.

---

### Query 4: TelemetryHub Connection Lifecycle

**Issue:** ISS-2 — SignalR streaming verification
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "attached to run" or message has "detached from run"
| project timestamp, message, customDimensions.ConnectionId, customDimensions.RunId
| summarize AttachCount=countif(message has "attached"), DetachCount=countif(message has "detached") by bin(timestamp, 1h)
| order by timestamp desc
| limit 1000
```

**Expected result:** AttachCount = 0 means no UI clients subscribing to run streams. Both counts > 0 = healthy server-client lifecycle. If positive but UI still dark, issue is client-side.

---

### Query 5: EngineEventBridge Unknown Event Types

**Issue:** ISS-1 — ToolCall/ToolResult events not bridged
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Unknown engine event type"
| project timestamp, message, customDimensions.EventType
| summarize Count=count() by tostring(customDimensions.EventType)
| order by Count desc
| limit 1000
```

**Expected result:** EventType = "ToolCallEvent" or "ToolResultEvent" confirms tool execution events are produced but not mapped through the bridge to SignalR. Expected while gap 11.6 is open.

---

### Query 6: RunEvent Persistence Failures

**Issue:** ISS-2 — SignalR streaming reliability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Failed to persist run event"
| where severityLevel >= 2
| project timestamp, message, customDimensions.RunId, customDimensions.Seq, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

**Expected result:** Repeated failures for same RunId = corrupted/locked JSONL history file. IOException = disk issue. JsonException = serialization bug.

---

### Query 7: Agent Budget Exceeded Events

**Issue:** ISS-3 — Budget enforcement observability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "budget_exceeded" or message has "AgentBudgetExceededException" or message has "pump crashed"
| where customDimensions.ExceptionType == "AgentBudgetExceededException" or message has "budget"
| project timestamp, message, customDimensions.agentId, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

**Expected result:** "pump crashed" + AgentBudgetExceededException = budget exceeded mid-stream. "budget_exceeded" = SignalR broadcast was sent. No rows = no breaches or silent enforcement gap.

---

### Query 8: Copilot Server Lifecycle

**Issue:** ISS-1 — Agent runtime infrastructure
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Copilot server" or message has "Shared Copilot"
| project timestamp, message, severityLevel, customDimensions.Port
| order by timestamp desc
| limit 1000
```

**Expected result:** Shows Copilot SDK server start/stop/restart lifecycle. "ping failed, restarting" = server died and was auto-recovered. "Failed to start" at Error level = infrastructure problem.

---

### Query 9: Workflow Engine Failures

**Issue:** General — execution engine reliability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Node execution failed" or message has "Fatal error creating node" or message has "Unhandled node failure"
| where severityLevel >= 3
| project timestamp, message, customDimensions.NodeId, customDimensions.ErrorMessage
| order by timestamp desc
| limit 1000
```

**Expected result:** Shows workflow DAG node failures. "Fatal error creating node" = node definition/type resolution failure. "Unhandled node failure" = no error handler configured, workflow will fail.

---

### Query 10: Dead Letter Queue Activity

**Issue:** General — message routing reliability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "dead letter queue" or message has "Dead letter"
| project timestamp, message, severityLevel, customDimensions.Reason, customDimensions.MessageType
| order by timestamp desc
| limit 1000
```

**Expected result:** Messages landing in DLQ indicate routing failures or node crashes. High DLQ volume = systemic issue. `Reason` field explains why the message was dead-lettered.

---

### Query 11: Audit Trail Analysis (Supervising Gap)

**Issue:** ISS-4 — Supervising column never written
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "Audit:"
| project timestamp, message, customDimensions.Action, customDimensions.Path, customDimensions.UserId, customDimensions.OutcomeCode
| where customDimensions.Path has "/api/agents/" or customDimensions.Path has "/api/crews/"
| order by timestamp desc
| limit 1000
```

**Expected result:** Shows all mutating operations on agent/crew endpoints. The `Supervising` field is never true in any row — confirming the audit gap.

---

### Query 12: TaskChannel Broadcast Failures

**Issue:** ISS-2 — real-time event delivery reliability
**Database:** Application Logs (ILogger sink)

```kusto
traces
| where message has "TaskTranscript persist failed" or message has "TaskChannel broadcast failed"
| project timestamp, message, customDimensions.WorkItemId, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

**Expected result:** Persistence failures indicate SQLite lock contention under load. Broadcast failures indicate SignalR connection issues. Both are best-effort — the event continues even on failure.

---

### Query 13: Dispatch Latency and Capacity

**Issue:** General — agent queue health
**Database:** OTel Metrics (Prometheus / OTLP sink)

```promql
# PromQL equivalent for OTel metrics sink
histogram_quantile(0.95, rate(forge_dispatch_lead_to_crew_latency_ms_bucket[5m]))
# or
sum(rate(forge_queue_ack_timeout_total[5m])) by (targetKind)
```

**Expected result:** p95 dispatch latency > 30s indicates capacity issues. Rising ack_timeout rate indicates queue strain. `forge.queue.strain.fired` counter confirms the system detected overload.

---

## Recommended Pre-Built Queries from Query Library

No pre-built query library exists for this repository. Queries are generated directly from ILogger and OTel metric call site analysis.

---

## Query Generation Log

| Iteration | Queries Added | Total Queries | Max Score | Reason |
|-----------|---------------|---------------|-----------|--------|
| 0 | 13 (Q1-Q13) | 13 | N/A | Initial discovery: scanned all src/ projects, built provider catalog (1 skeleton EventSource, 1 OTel Meter with 20 instruments, 9 SignalR event types, 100+ ILogger calls). Mapped 6 correctness issues from forge_gaps.md to observable signals. Identified 10 critical logging gaps. |
