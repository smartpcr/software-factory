---
name: copilot_server_lifecycle
description: Copilot Server Lifecycle — SDK server start/stop/restart
issue: ISS-1 — Agent runtime infrastructure
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors shared Copilot SDK server lifecycle in `CopilotServerManager.cs`.

## Query

```kusto
traces
| where message has "Copilot server" or message has "Shared Copilot"
| project timestamp, message, severityLevel, customDimensions.Port
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- "ping failed, restarting" = server died and was auto-recovered
- "Failed to start" (Error level) = infrastructure problem
- "started on port {Port}" = successful startup
