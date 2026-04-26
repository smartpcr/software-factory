# ETW Events Discovery Report

**Repository:** `E:\work\github\crp\workflow` (Workflow.sln)
**Generated:** 2026-04-26
**Iteration:** 1

---

## Summary

**No production EventSource classes were found in this repository.**

This repository is an ETW **processing and analysis** tool -- it reads and parses ETW events from `.etl` files produced by other Azure Stack services. It does not emit ETW events itself.

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
| **CodeIntelligence** | AST analyzer including `ObservabilityAnalyzer` that detects ETW patterns in other repos |

---

## Discovered EventSource Classes

### Production EventSource Classes: **NONE**

No classes inheriting from `System.Diagnostics.Tracing.EventSource` were found in production code.

### Test-Only EventSource Classes (not production)

The following EventSource classes exist **only in unit test code** as fixtures for the `ObservabilityAnalyzer`:

| Class | File | Purpose |
|-------|------|---------|
| `OrderEventSource` | `src/ast/CodeIntelligence.UnitTests/Analyzers/ObservabilityAnalyzerTests.cs` | Test fixture for ETW provider name extraction |
| `MyEventSource` | `src/ast/CodeIntelligence.UnitTests/Analyzers/ObservabilityAnalyzerTests.cs` | Test fixture for ETW event detection |

These are inline test examples (string literals in test methods) used to verify the `ObservabilityAnalyzer` can detect and catalog EventSource patterns. They are not compiled into any production assembly.

---

## Observability Patterns Actually Used

This repository uses standard .NET observability patterns (not ETW):

### 1. ILogger (Microsoft.Extensions.Logging)

Used throughout all production services:

| Component | Logger Usage |
|-----------|-------------|
| `WorkflowUi.Server` | `ILogger<T>` injected via DI in all endpoints, hubs, and services |
| `ExecutionEngine` | `ILogger<T>` for workflow execution tracing |
| `EngineEventBridge` | `ILogger<EngineEventBridge>` for event mapping diagnostics |
| `TelemetryHub` | `ILogger<TelemetryHub>` for SignalR connection lifecycle |
| `Forge.Api` | `ILogger<T>` in auth, Git, and provider services |

### 2. SignalR Telemetry (Real-Time UI Events)

| Class | Namespace | Description |
|-------|-----------|-------------|
| `TelemetryHub` | `WorkflowUi.Server.Hubs` | SignalR hub for streaming per-run workflow events to UI clients |
| `TelemetryHubSink` | `WorkflowUi.Server.Hubs` | Publishes `RunEvent` DTOs to SignalR groups |
| `EngineEventBridge` | `WorkflowUi.Server.Telemetry` | Adapts engine `WorkflowEvent` CLR events into `RunEvent` transport DTOs |

### 3. Custom WorkflowEvent System

The `ExecutionEngine` uses a custom CLR event system (`WorkflowEvent` base class) for engine lifecycle events -- these are **not** ETW events but in-process .NET events consumed by the `EngineEventBridge`.

---

## Recommended Kusto Queries

Since this repository does not emit ETW events, there are no ETW-based Kusto queries to recommend.

However, the repository **processes** ETW events from other Azure Stack services. The key Kusto tables it creates and populates are dynamically named based on the ETW provider and event method from the source `.etl` files:

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
| No ETW instrumentation | The workflow engine and UI server have no ETW provider | Cannot trace execution via ETW sessions; must rely on ILogger output and SignalR events |
| No OpenTelemetry metrics | No `Meter` or `Counter<T>` instrumentation | No quantitative performance metrics (latency histograms, throughput counters) |
| No distributed tracing | No `ActivitySource` usage | Cannot correlate operations across services via W3C trace context |

---

## Query Generation Log

| Iteration | New Queries | Total Queries | Max Score | Reason |
|-----------|-------------|---------------|-----------|--------|
| 0 | 0 | 0 | N/A | No production EventSource classes found in repository |
| 1 | 0 | 0 | N/A | Re-scanned all src/ projects (forge, viz, agents, CodeIntelligence, WorkflowUi.Server, ExecutionEngine). No new EventSource subclasses, DiagnosticSource, ActivitySource, OpenTelemetry SDK, ETW manifests (.man/.wprp), or System.Diagnostics.Tracing package refs found. All EventSource/ActivitySource/Meter hits remain test fixtures in ObservabilityAnalyzerTests.cs. Repository remains an ETW consumer only. |

Queries generated: 0
