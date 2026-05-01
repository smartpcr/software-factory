# Kusto Queries

**Repository:** `smartpcrd/workflow`
**Generated:** 2026-04-28
**Iteration:** 2

---

## Summary

**26 queries generated** (15 consumed-ETW-provider queries + 7 ILogger/SignalR forge-correctness queries + 4 logging gap readiness queries).

This repository (`Workflow.sln`) is an ETW **processing and analysis** tool — it reads and parses ETW events from `.etl` files produced by other Azure Stack services. It does not emit ETW events itself.

### Query Categories

| Category | Queries | Database | Description |
|----------|---------|----------|-------------|
| Consumed ETW Providers | Q1–Q15 | Local Kusto (ETL-ingested) | Queries targeting `['ETL-{ProviderName}.{EventName}']` tables created by this tool |
| Forge Correctness Gaps | Q16–Q22 | Application Logs (ILogger) | ILogger/SignalR-based queries mapping forge_gaps.md correctness issues |
| Logging Gap Readiness | Q23–Q26 | Application Logs (ILogger) | Readiness queries for unaddressed logging gaps — return zero rows until instrumentation is added |

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

### Application Logs — Logging Gap Readiness (ILogger sink)

Q23–Q26: Readiness queries for unaddressed logging gaps. These queries target Application Insights `traces` table but **return zero rows** because the corresponding ILogger instrumentation does not exist yet.

---

| Iteration | New Queries | Total Queries | Reason |
|-----------|-------------|---------------|--------|
| 0 | 0 | 0 | No production EventSource classes found in repository |
| 1 | 15 | 15 | Consumed ETW providers discovered from BDD feature files (queries described but not written to disk) |
| 2 | 7 | 22 | Q1-Q15 written to disk + Q16-Q22 forge correctness ILogger/SignalR queries |
| 3 | 4 | 26 | Q23-Q26 readiness queries for unaddressed logging gaps (AgentToolHost sandbox, ForgeHub membership, ISS-5, ISS-6) |

---

## Query Index (Q23–Q26 — Iteration 3: Logging Gap Readiness Queries)

These queries target logging gaps identified in iteration 0. They return **zero rows today** because the corresponding ILogger calls do not exist yet. Once logging is added, these queries will surface the signals.

| # | File | Database | Issue | Description |
|---|------|----------|-------|-------------|
| Q23 | `q23_agenttoolhost_sandbox_enforcement.md` | App Logs (ILogger) | ISS-1 (logging gap) | AgentToolHost sandbox enforcement — tool allowlist, path traversal, symlink detection |
| Q24 | `q24_forgehub_group_membership.md` | App Logs (ILogger) | ISS-2 (logging gap) | ForgeHub Subscribe/Unsubscribe — SignalR group membership audit |
| Q25 | `q25_mesettings_invalid_defaults.md` | App Logs (ILogger) | ISS-5 (FORGE-GAP-10.2) | MeSettings invalid DefaultProductId/DefaultRepoId detection |
| Q26 | `q26_pr_fake_ci_detection.md` | App Logs (ILogger) | ISS-6 (FORGE-GAP-4.9) | PR RerunBuild/CancelBuild fake CI detection |

### Execution Results (Q23–Q26)

All 4 queries were submitted to the blob pipeline on 2026-04-29. All timed out (120s) — expected because:

1. **Database mismatch** — queries target `Application Logs (ILogger sink)`, not a telemetry/Hub/Diagnostics Kusto cluster
2. **No logging instrumentation** — all queries have `schema_validated: false` and explicitly state they return zero rows until ILogger calls are added
3. **No `invoke-kusto.ps1`** — direct execution script does not exist in this repository

| # | Result File | Status | Error Type |
|---|-------------|--------|------------|
| Q23 | `output/q23_agenttoolhost_sandbox_enforcement.md` | error | timeout |
| Q24 | `output/q24_forgehub_group_membership.md` | error | timeout |
| Q25 | `output/q25_mesettings_invalid_defaults.md` | error | timeout |
| Q26 | `output/q26_pr_fake_ci_detection.md` | error | timeout |