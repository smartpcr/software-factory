---
name: agent_budget_exceeded
description: Agent Budget Exceeded Events — daily crew budget breaches
issue: ISS-3 — Budget enforcement observability (FORGE-GAP-11.10)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects agent budget breaches. `AgentsEndpoints.cs` (line 334) broadcasts `agent.budget_exceeded` via SignalR. `RecordTokens()` throws `AgentBudgetExceededException` without explicit logging.

## Query

```kusto
traces
| where message has "budget_exceeded" or message has "AgentBudgetExceededException" or message has "pump crashed"
| where customDimensions.ExceptionType == "AgentBudgetExceededException" or message has "budget"
| project timestamp, message, customDimensions.agentId, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- "pump crashed" + AgentBudgetExceededException = budget exceeded mid-stream
- "budget_exceeded" = SignalR broadcast sent
- No rows = no breaches or silent enforcement gap
