# Kusto Queries

**Repository:** `smartpcrd/workflow`
**Generated:** 2026-04-28
**Iteration:** 2

---

## Summary

**22 queries generated** (15 consumed-ETW-provider queries + 7 ILogger/SignalR forge-correctness queries).

This repository (`Workflow.sln`) is an ETW **processing and analysis** tool — it reads and parses ETW events from `.etl` files produced by other Azure Stack services. It does not emit ETW events itself.

### Query Categories

| Category | Queries | Database | Description |
|----------|---------|----------|-------------|
| Consumed ETW Providers | Q1–Q15 | Local Kusto (ETL-ingested) | Queries targeting `['ETL-{ProviderName}.{EventName}']` tables created by this tool |
| Forge Correctness Gaps | Q16–Q22 | Application Logs (ILogger) | ILogger/SignalR-based queries mapping forge_gaps.md correctness issues |

### Observability patterns used

| Pattern | Details |
|---------|---------|
| `ILogger<T>` | Microsoft.Extensions.Logging throughout all services |
| SignalR | Real-time telemetry streaming via `TelemetryHub` + `ForgeHub` |
| CLR events | Custom `WorkflowEvent` system (in-process, not ETW) |

### Querying data ingested by this tool

The repository **creates** Kusto tables dynamically from source `.etl` files using the naming convention:

```
['ETL-{ProviderName}.{EventMethodName}']
```

To query data ingested by this tool, use the table names derived from the source ETL files in the target Kusto database.

---

## Databases

### Local Kusto (ETL-ingested)

Q1–Q15: Consumed ETW provider queries using standard columns (`TimeStamp`, `ProcessID`, `ProcessName`, `Level`, `Opcode`, `OpcodeName`).

### Application Logs (ILogger sink)

Q16–Q22: Forge correctness queries targeting structured ILogger output (Application Insights / Seq / console JSON).

---

| Iteration | New Queries | Total Queries | Reason |
|-----------|-------------|---------------|--------|
| 0 | 0 | 0 | No production EventSource classes found in repository |
| 1 | 15 | 15 | Consumed ETW providers discovered from BDD feature files (queries described but not written to disk) |
| 2 | 7 | 22 | Q1-Q15 written to disk + Q16-Q22 forge correctness ILogger/SignalR queries |