# Kusto Queries

**Repository:** `smartpcr/workflow`
**Generated:** 2026-04-26

---

## Summary

**No Kusto queries were generated for this repository.**

This repository (`Workflow.sln`) is an ETW **processing and analysis** tool — it reads and parses ETW events from `.etl` files produced by other Azure Stack services. It does not emit ETW events itself, so no ETW-provider-based Kusto queries apply.

### Observability patterns used

| Pattern | Details |
|---------|---------|
| `ILogger<T>` | Microsoft.Extensions.Logging throughout all services |
| SignalR | Real-time telemetry streaming via `TelemetryHub` |
| CLR events | Custom `WorkflowEvent` system (in-process, not ETW) |

### Querying data ingested by this tool

The repository **creates** Kusto tables dynamically from source `.etl` files using the naming convention:

```
['ETL-{ProviderName}.{EventMethodName}']
```

To query data ingested by this tool, use the table names derived from the source ETL files in the target Kusto database.

---

## Databases

### Diagnostics

_No queries._

### Telemetry

_No queries._

### Hub

_No queries._

---

| Iteration | New Queries | Total Queries | Reason |
|-----------|-------------|---------------|--------|
| 0 | 0 | 0 | No production EventSource classes found in repository |

---

## Query Execution

**Execution skipped** -- 2026-04-26

No query `.md` files were found under `kusto/telemetry/`, `kusto/diagnostics/`, or `kusto/hub/`. The `execute-incident-queries` step had nothing to execute. No calls were made to `invoke-kusto.ps1` and no blobs were uploaded to the pipeline.