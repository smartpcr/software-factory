# Kusto Queries

**Repository:** `Workflow.sln`
**Generated:** 2026-04-26

---

## No ETW-Based Queries Available

This repository is an ETW **consumer and analysis tool** — it reads, parses, and ingests ETW events from `.etl`/`.evtx` files produced by other Azure Stack services into Kusto. It does **not** emit ETW events itself (no production `EventSource` subclasses were found).

### Query Count by Database

| Database | Count |
|----------|-------|
| Diagnostics | 0 |
| Telemetry | 0 |
| Hub | 0 |

---

## Alternative Observability Queries

Since this repository uses `ILogger<T>`, SignalR, and custom CLR events (not ETW), the following alternative query approaches are recommended for monitoring its runtime behavior:

### 1. ILogger-Based Traces (Application Insights / Log Analytics)

If the services are configured with an Application Insights or Azure Monitor sink, query `traces` or `AppTraces`:

```kusto
// WorkflowUi.Server ILogger traces
AppTraces
| where AppRoleName == "WorkflowUi.Server"
| where TimeGenerated between (ago(1h) .. now())
| project TimeGenerated, Message, SeverityLevel, Properties
| order by TimeGenerated desc
| take 200
```

```kusto
// ExecutionEngine workflow execution logs
AppTraces
| where Properties has "WorkflowEngine" or Properties has "NodeFactory"
| where TimeGenerated between (ago(1h) .. now())
| project TimeGenerated, Message, SeverityLevel, Properties
| order by TimeGenerated desc
```

### 2. SignalR Hub Telemetry

If Application Insights is enabled, SignalR connection events appear in `dependencies` and `requests`:

```kusto
// SignalR TelemetryHub connection lifecycle
requests
| where name has "TelemetryHub"
| where timestamp between (ago(1h) .. now())
| project timestamp, name, success, duration, resultCode
| order by timestamp desc
```

```kusto
// SignalR hub invocation failures
AppExceptions
| where AppRoleName == "WorkflowUi.Server"
| where ExceptionType has "Hub" or Properties has "SignalR"
| where TimeGenerated between (ago(1h) .. now())
| project TimeGenerated, ExceptionType, OuterMessage, InnermostMessage
| order by TimeGenerated desc
```

### 3. Tables Ingested by This Tool

This tool dynamically creates Kusto tables from source ETL files using the naming convention:

```
['ETL-{ProviderName}.{EventMethodName}']
```

Example:
```
['ETL-Microsoft-AzureStack-Compute-Pipeline.VmPipelineCriticalError']
```

To discover tables ingested by this tool, query the target Kusto database:

```kusto
.show tables
| where TableName startswith "ETL-"
| project TableName, DocString, Folder
| order by TableName asc
```

### 4. Logging Gaps to Consider

| Gap | Impact | Recommendation |
|-----|--------|----------------|
| No ETW instrumentation | Cannot trace via ETW sessions | Add `EventSource` if ETW-based diagnostics are needed |
| No OpenTelemetry metrics | No latency/throughput counters | Add `Meter`/`Counter<T>` for performance monitoring |
| No distributed tracing | Cannot correlate across services | Add `ActivitySource` for W3C trace context propagation |
