# Kusto Queries — WorkflowUi.Server

> **Generated:** 2026-04-27 | **Iteration:** 0 (initial run) | **AppendMode:** false

## Overview

10 queries extracted from [`etw_events.md`](../../etw_events.md). No `metadata.md` was found, so parameter placeholders were not substituted (queries use relative `ago()` time ranges).

> ⚠️ **No ETW provider is implemented yet.** Queries 1–7 target the *proposed* `Microsoft-WorkflowUi-Server` EventSource. All hub tables are annotated as not yet created. Queries 8–10 target Application Insights `traces` and work with the current ILogger-based observability stack.

---

## Hub Database (ETW — hypothetical)

| # | File | Table | Description |
|---|------|-------|-------------|
| 1 | [01-failed-workflow-runs.md](hub/01-failed-workflow-runs.md) | `ETL-Microsoft-WorkflowUi-Server.RunFailed` | Failed workflow runs from ETW RunFailed events |
| 2 | [02-slow-http-requests.md](hub/02-slow-http-requests.md) | `ETL-Microsoft-WorkflowUi-Server.RequestStop` | HTTP requests taking longer than 5 seconds |
| 3 | [03-unhandled-exceptions.md](hub/03-unhandled-exceptions.md) | `ETL-Microsoft-WorkflowUi-Server.UnhandledException` | Unhandled exceptions caught by global error handler |
| 4 | [04-node-failures-by-workflow.md](hub/04-node-failures-by-workflow.md) | `ETL-Microsoft-WorkflowUi-Server.NodeFailed` | Aggregate node failure counts per workflow run |
| 5 | [05-audit-trail-write-operations.md](hub/05-audit-trail-write-operations.md) | `ETL-Microsoft-WorkflowUi-Server.AuditEvent` | Audit trail for all mutating HTTP operations |
| 6 | [06-signalr-client-activity.md](hub/06-signalr-client-activity.md) | `ETL-Microsoft-WorkflowUi-Server.ClientAttached` | SignalR client attachment counts by run and hour |
| 7 | [07-run-duration-distribution.md](hub/07-run-duration-distribution.md) | `ETL-Microsoft-WorkflowUi-Server.RunCompleted` | Completed workflow run durations over past 7 days |

## Telemetry Database (Application Insights / ILogger)

| # | File | Table | Description |
|---|------|-------|-------------|
| 8 | [08-appinsights-failed-runs.md](telemetry/08-appinsights-failed-runs.md) | `traces` | Failed runs via ILogger in Application Insights |
| 9 | [09-appinsights-slow-requests.md](telemetry/09-appinsights-slow-requests.md) | `traces` | Slow HTTP requests (>5s) via ILogger traces |
| 10 | [10-appinsights-audit-events.md](telemetry/10-appinsights-audit-events.md) | `traces` | Audit trail for write operations via ILogger traces |
