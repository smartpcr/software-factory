# ETW Events Discovery Report

**Repository:** `E:\work\github\crp\workflow` (Workflow.sln)
**Generated:** 2026-04-26
**Iteration:** 1

---

## Summary

**No production EventSource classes were found in this repository.**

This repository is an ETW **consumer and analysis tool** -- it reads, parses, and ingests ETW events from `.etl`/`.evtx` files produced by other Azure Stack services into Kusto. It does not emit ETW events itself.

### Repository Purpose

| Component | Description |
|-----------|-------------|
| **EtwIngest** | BDD test project for ETW parsing and Kusto ingestion |
| **EtlIterator** | Batch ETL file processor (parallel processing, schema discovery, CSV export) |
| **EtwEventReader** | Event reader with formatters and models for ETW events |
| **Common.Diagnostics.EtwParser** | Core ETL/EVTX parsing library using `Microsoft.Diagnostics.Tracing.TraceEvent` |
| **Common.Diagnostics.EtwProcessor** | ETW processing and Kusto ingestion pipeline |
| **ExecutionEngine** | Workflow execution engine with node-based execution model |
| **ProgressTree** | Progress tracking library for workflow execution |
| **WorkflowUi.Server** | Web UI with SignalR-based real-time telemetry streaming |
| **Forge.Api** | Git/ADO integration API with auth, webhooks, and agent orchestration |
| **CodeIntelligence** | AST analyzer including `ObservabilityAnalyzer` that detects ETW patterns in other repos |

---

## Discovered EventSource Classes

### Production EventSource Classes: **NONE**

Scanned all `.cs` files under `src/` (excluding test directories). No classes inheriting from `System.Diagnostics.Tracing.EventSource` were found in production code.

Search patterns used:
- `: EventSource` (direct inheritance)
- `[EventSource(` attribute
- `[Event(` attribute on methods

### Test-Only EventSource Classes (not production)

The following EventSource references exist **only in unit test code** as string-literal fixtures for the `ObservabilityAnalyzer`:

| Class | File | Purpose |
|-------|------|---------|
| `OrderEventSource` | `src/ast/CodeIntelligence.UnitTests/Analyzers/ObservabilityAnalyzerTests.cs` | Test fixture -- string literal for ETW provider name extraction |
| `MyEventSource` | `src/ast/CodeIntelligence.UnitTests/Analyzers/ObservabilityAnalyzerTests.cs` | Test fixture -- string literal for ETW event detection |

These are inline test code snippets (not compiled classes) used to verify the `ObservabilityAnalyzer` can detect EventSource patterns in source code it analyzes.

---

## Observability Patterns Actually Used

This repository uses standard .NET observability patterns (not ETW):

### 1. ILogger (Microsoft.Extensions.Logging)

Used across 90+ files in all production services:

| Component | Logger Usage |
|-----------|-------------|
| `WorkflowUi.Server` | `ILogger<T>` injected via DI in endpoints, hubs, middleware, and hosted services |
| `ExecutionEngine` | `ILogger<T>` in `WorkflowEngine`, `NodeFactory`, `AINode`, `MessageRouter`, `DeadLetterQueue` |
| `EngineEventBridge` | `ILogger<EngineEventBridge>` for CLR-to-SignalR event mapping diagnostics |
| `TelemetryHub` | `ILogger<TelemetryHub>` for SignalR connection lifecycle |
| `Forge.Api` | `ILogger<T>` in auth services, Git operations, provider sync, webhooks, and agents |
| `CodeIntelligence (AST)` | `ILogger<T>` in LSP client, embedding services, graph DB, and query services |

### 2. SignalR Telemetry (Real-Time UI Events)

| Class | Namespace | Description |
|-------|-----------|-------------|
| `TelemetryHub` | `WorkflowUi.Server.Hubs` | SignalR hub streaming per-run workflow events to UI clients |
| `TelemetryHubSink` | `WorkflowUi.Server.Hubs` | Publishes `RunEvent` DTOs to SignalR groups |
| `EngineEventBridge` | `WorkflowUi.Server.Telemetry` | Adapts engine `WorkflowEvent` CLR events into `RunEvent` transport DTOs |

### 3. Custom WorkflowEvent System

The `ExecutionEngine` uses a custom CLR event system (`WorkflowEvent` base class) for engine lifecycle events -- these are in-process .NET events consumed by `EngineEventBridge`, not ETW events.

---

## Recommended Kusto Queries

Since this repository does not emit ETW events, there are no ETW-based Kusto queries to recommend.

The repository **processes** ETW events from other Azure Stack services. Tables it creates in Kusto are dynamically named based on source ETL file contents:

```
Table naming convention (Hub services):
  ['ETL-{ProviderName}.{EventMethodName}']

Example:
  ['ETL-Microsoft-AzureStack-Compute-Pipeline.VmPipelineCriticalError']
```

To query data ingested by this tool, use the table names from the source ETL files.

---

## Logging Gaps

| Area | Gap | Impact |
|------|-----|--------|
| No ETW instrumentation | No `EventSource` subclass in any production project | Cannot trace execution via ETW sessions; must rely on ILogger sinks |
| No OpenTelemetry metrics | No `Meter` or `Counter<T>` instrumentation | No quantitative performance metrics (latency histograms, throughput counters) |
| No distributed tracing | No `ActivitySource` usage | Cannot correlate operations across services via W3C trace context |

---

## Query Generation Log

| Iteration | New Queries | Total Queries | Max Score | Reason |
|-----------|-------------|---------------|-----------|--------|
| 0 | 0 | 0 | N/A | Scanned all src/ projects. No production EventSource classes found. Repository is an ETW consumer/analyzer tool, not an ETW producer. |
| 1 | 15 (Q1-Q15) | 15 | N/A | Shifted focus to consumed ETW providers. Scanned feature files, parsers, and ingest pipeline code for hardcoded provider names and event names. Generated queries targeting the `['ETL-{Provider}.{Event}']` table convention used by this tool. |

---

## Iteration 1: Consumed ETW Providers

### Approach

Since this repository produces no ETW events of its own, iteration 1 focuses on the ETW providers **consumed** by this tool. The codebase dynamically parses `.etl`/`.evtx` files and creates Kusto tables named `['ETL-{ProviderName}.{EventName}']`. Provider and event names are discovered at runtime from the trace files themselves (via `DynamicTraceEventParser`), not hardcoded.

However, the BDD feature files (`src/EtwIngest/Features/*.feature`) and the `EtlIterator/Program.cs` contain **concrete examples** of providers and events processed from real Azure Stack ETL files. These represent the known provider catalog.

### Table Naming Convention

The code in `EventIdentifier.ToSafeIdentifier()` and `EtlIterator/Program.cs` establishes:

```
Table name = ETL-{ProviderName}.{EventName}
  where EventName has "/" characters stripped
```

Examples:
- Provider `Microsoft-AzureStack-Compute-HostPluginWatchDog`, Event `EnsureProcessStarted/Stop`
  -> Table: `['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.EnsureProcessStartedStop']`

### Standard Columns (all ETL-ingested tables)

Every table created by this tool includes these standard columns from `EtlFileParser.CreateSchemaFromEvent()`:

| Column | Type | Source |
|--------|------|--------|
| `TimeStamp` | datetime | `traceEvent.TimeStamp` |
| `ProcessID` | int | `traceEvent.ProcessID` |
| `ProcessName` | string | `traceEvent.ProcessName` |
| `Level` | int | `traceEvent.Level` (0=Always, 1=Critical, 2=Error, 3=Warning, 4=Info, 5=Verbose) |
| `Opcode` | int | `traceEvent.Opcode` |
| `OpcodeName` | string | `traceEvent.OpcodeName` |

Plus event-specific payload columns extracted from `traceEvent.PayloadNames`.

### Discovered ETW Providers

#### Category 1: Azure Stack Compute (Hub)

| Provider | Events | Source |
|----------|--------|--------|
| `Microsoft-AzureStack-Compute-HostPluginWatchDog` | ManifestData, StartWatchDog/Start, StartWatchDog/Stop, ConfigFilesFound, ReadConfigFromStore, EnsureProcessStarted/Start, EnsureProcessStarted/Stop, ProcessStarted, FoundProcessAlreadyRunning | EtwIngest.feature |

#### Category 2: Azure Stack CloudEngine / ECE (Hub)

| Provider | Events | Source |
|----------|--------|--------|
| `Microsoft-AzureStack-CloudEngine` | ActionPlanStart, PowerShellRemoteStepStop | EtwIngest.feature |

#### Category 3: Azure Stack Infrastructure (Hub)

| Provider | Events | Source |
|----------|--------|--------|
| `Microsoft-AzureStack-Common-Infrastructure-Http` | ClientRequestStop | EtwIngest.feature |
| `Microsoft-AzureStack-Common-Infrastructure-HostModel-ServiceFabricHost` | InitializeStateSerializersStart | EtwIngest.feature |
| `Microsoft-AzureStack-Infrastructure-ServiceSettings-Sdk` | OperationStart | EtwIngest.feature |
| `Microsoft-AzureStack-Infrastructure-Health-Refresher` | RefreshCycleStop | EtwIngest.feature |
| `Microsoft-AzureStack-Infrastructure-Health-RunnerService` | InvalidOldResult | EtwIngest.feature |
| `Microsoft-AzureStack-Infrastructure-Health-WindowsServiceHost` | ServiceAssemblyFindResult | EtwIngest.feature |
| `Microsoft-AzureStack-Infrastructure-Health-HealthStorePlugin` | FaultSubStateMachine | EtwIngest.feature |

#### Category 4: Azure Stack Usage (Hub)

| Provider | Events | Source |
|----------|--------|--------|
| `Microsoft-AzureStack-Usage` | UsageQueryRequest | EtwIngest.feature |
| `Microsoft-AzureStack-UsageAdminHealthProbe` | EndHealthProbe | EtwIngest.feature |
| `Microsoft-AzureStack-Roles-KB-EventSource` | LiveUpdateKbStart | EtwIngest.feature |

#### Category 5: Azure Stack HCI Update (Non-Hub)

| Provider | Events | Source |
|----------|--------|--------|
| `Microsoft-URP-InfraEventSource` | HealthCheckResultDirectoryIsEmptyOrDoesNotExist, ResolverGetAll, StartService, StopService | IngestHciLog.feature |

#### Category 6: Network Controller Host Agent (Hub)

| Provider | Sample Events | Source |
|----------|---------------|--------|
| `Microsoft.Windows.NetworkController.HostAgent.FirewallPlugin` | AddNicRedirectionRules, ApplyingState, CertValidated, OvsdbDbg, PluginEvent, ProcessingUpdate, Update, UpdateCompleted | EtwIngest.feature |
| `Microsoft.Windows.NetworkController.HostAgent.VNetPlugin` | ApplyingState, BuildingState, BuiltState, CertValidated, DnsProxyOnUpdate, DnsSocketPool, OvsdbDbg, PortState, VfpPort, VfpSwitch | EtwIngest.feature |
| `Microsoft.Windows.NetworkController.HostAgent.Service` | ChangingPortState, OvsdbDbg, PluginNotification, PolicyApplied, PortStateCheck, ReconcilePorts | EtwIngest.feature |
| `Microsoft.Windows.NetworkController.HostAgent.GatewayPlugin` | AddGateway, AddGatewayCommon, Error, HealthMonitorTimerCallbackEnd, StartGatewayMonitoring, UpdatedHealthState, VmmsPid | EtwIngest.feature |
| `Microsoft.Windows.NetworkController.HostAgent.ServiceInsertionPlugin` | ApplyingState, NoActiveUpdates, NoInfrastructureRules, OvsdbDbg, PluginEvent, PortState, SchedulingUpdate, Update, UpdateCompleted | EtwIngest.feature |
| `Microsoft.Windows.NetworkController.HostAgent.VSwitchPlugin` | CertValidated, ChannelClosedCallback | EtwIngest.feature |

#### Category 7: Windows / System

| Provider | Events | Source |
|----------|--------|--------|
| `MSNT_SystemTrace` | EventTrace/PartitionInfoExtensionV2 | EtwIngest.feature |
| `Microsoft-Windows-Services` | ServiceConfigChange | EtwIngest.feature |
| `Microsoft-Windows-TCPIP` | Ndkpi_Arm_Cq, TcpSwsAvoidanceBegin | EtwIngest.feature |
| `Microsoft-Windows-Sysprep` | RunRegistryDllsStart | EtwIngest.feature |
| `Microsoft-Windows-OobeLdr` | OobeLdrProcessUnattend | EtwIngest.feature |
| `Microsoft-Windows-Windeploy` | LaunchandwaitforexternalprocessStart | EtwIngest.feature |

#### Category 8: EVTX (Windows Event Log)

EVTX files are ingested into a single `WindowsEvents` table with a consistent schema:

| Column | Type |
|--------|------|
| TimeStamp | datetime |
| ProviderName | string |
| LogName | string |
| MachineName | string |
| EventId | int |
| Level | string |
| Keywords | string |
| ProcessId | int |
| Description | string |

---

## Recommended Kusto Queries (Iteration 1)

### Query 1: HostPluginWatchDog Error Events

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.EnsureProcessStartedStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName, correlationVector, name, executablePath, duration, exception
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Level <= 3 captures Critical (1), Error (2), and Warning (3) events. Non-null `exception` field indicates process start failures. Long `duration` values indicate slow process launches.

---

### Query 2: HostPluginWatchDog Process Lifecycle

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.ProcessStarted']
| project TimeStamp, ProcessID, ProcessName, Level, name, executablePath, arguments, workingDirectory
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Shows which processes were started by the watchdog and when. Compare with `FoundProcessAlreadyRunning` to detect restart loops.

---

### Query 3: HostPluginWatchDog Already Running Detection

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.FoundProcessAlreadyRunning']
| summarize Count=count() by bin(TimeStamp, 5m), name
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Frequent "already running" events in short intervals may indicate healthy operation. A drop to zero followed by `ProcessStarted` events indicates a process crash/restart.

---

### Query 4: CloudEngine Action Plan Execution

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-CloudEngine.ActionPlanStart']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Shows when ECE action plans were started. Correlate with `PowerShellRemoteStepStop` events for step completion timing.

---

### Query 5: CloudEngine Remote Step Completion

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-CloudEngine.PowerShellRemoteStepStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Warning/Error level stop events indicate failed action plan steps. Cross-reference with ActionPlanStart timestamps to identify failing phases.

---

### Query 6: Common Infrastructure HTTP Client Errors

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Common-Infrastructure-Http.ClientRequestStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** HTTP client errors from common-infra indicate failed service-to-service calls. Check surrounding events in the same ProcessID for context.

---

### Query 7: Service Fabric Host Initialization

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Common-Infrastructure-HostModel-ServiceFabricHost.InitializeStateSerializersStart']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Shows Service Fabric host process startup. Absence of these events during expected startup windows indicates the host process failed to start.

---

### Query 8: Health Fault State Machine Transitions

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Infrastructure-Health-HealthStorePlugin.FaultSubStateMachine']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Tracks health fault creation, resolution, and state transitions. Level 2 (Error) events indicate new faults being raised.

---

### Query 9: URP/Update Service Lifecycle (HCI)

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-URP-InfraEventSource.StartService']
| union ['ETL-Microsoft-URP-InfraEventSource.StopService']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Shows update service start/stop lifecycle. Frequent stop/start cycles indicate service instability. Gap between stop and next start indicates downtime.

---

### Query 10: URP Health Check Failures (HCI)

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-URP-InfraEventSource.HealthCheckResultDirectoryIsEmptyOrDoesNotExist']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Indicates update content directories are missing or empty -- a common root cause for update discovery failures.

---

### Query 11: Network Controller Firewall Plugin Errors

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft.Windows.NetworkController.HostAgent.FirewallPlugin.OvsdbDbg']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** OVSDB debug events at Warning/Error level indicate firewall rule application failures. High event counts suggest persistent connectivity issues.

---

### Query 12: Network Controller Gateway Plugin Errors

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft.Windows.NetworkController.HostAgent.GatewayPlugin.Error']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** Direct error events from the gateway plugin. Check `UpdatedHealthState` events for gateway tunnel health degradation.

---

### Query 13: Windows Event Log Errors

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
WindowsEvents
| where Level in ("Error", "Critical")
| summarize Count=count() by ProviderName, EventId, Level
| order by Count desc
| limit 1000
```

**How to Interpret:** Aggregates Windows Event Log errors by provider. High counts for a single provider/EventId indicate a recurring system issue.

---

### Query 14: Event Frequency Heatmap by Provider

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.ConfigFilesFound']
| summarize EventCount=count() by bin(TimeStamp, 1h)
| order by TimeStamp asc
| limit 1000
```

**How to Interpret:** Shows event volume over time. Sudden drops indicate service downtime. Spikes may indicate restart storms.

---

### Query 15: Cross-Provider Error Timeline

**Database:** Local Kusto (ETL-ingested)
**Issue:** N/A -- general troubleshooting

```kusto
['ETL-Microsoft-AzureStack-Infrastructure-Health-RunnerService.InvalidOldResult']
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

**How to Interpret:** InvalidOldResult events indicate the health runner detected stale or invalid health check results -- may indicate a timing issue or health check service failure.

---

## Logging Gaps (Updated)

| Area | Gap | Impact |
|------|-----|--------|
| No ETW instrumentation | No `EventSource` subclass in any production project | Cannot trace execution via ETW sessions; must rely on ILogger sinks |
| No OpenTelemetry metrics | No `Meter` or `Counter<T>` instrumentation | No quantitative performance metrics |
| No distributed tracing | No `ActivitySource` usage | Cannot correlate operations across services |
| Dynamic provider discovery | Provider names not hardcoded -- discovered from ETL file content at runtime | Cannot enumerate all possible providers without sample ETL files |
| No schema registry | Event schemas inferred from first event occurrence per type | Schema mismatches possible if first event has null payload fields |

---

Queries generated: 15
