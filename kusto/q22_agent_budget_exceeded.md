---
name: agent_budget_exceeded
description: Agent Budget Exceeded Events — daily crew budget breaches
issue: FORGE-GAP-11.10 — Budget enforcement observability
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects when agents breach their daily crew budget. `AgentsEndpoints.cs` (line 334) broadcasts `agent.budget_exceeded` via SignalR and `AgentToolHost.Session.RecordTokens()` (line 108) throws `AgentBudgetExceededException`. Neither site has explicit ILogger calls, but the exception propagation through PumpEvents triggers the LogError at line 463.

## Forge Gap Reference

**[11.10]** Budget enforcement is implemented (402 status + SignalR event) but budget breach events are not logged at the application level — they're only observable through SignalR broadcasts or the 402 HTTP response. A dedicated log entry would improve operational visibility.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: AgentsEndpoints.cs:334 — broadcasts "agent.budget_exceeded"
// Also: AgentToolHost.Session:108 — throws AgentBudgetExceededException
traces
| where message has "budget_exceeded" or message has "AgentBudgetExceededException" or message has "pump crashed"
| where customDimensions.ExceptionType == "AgentBudgetExceededException" or message has "budget"
| project timestamp, message, customDimensions.agentId, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **"pump crashed" + AgentBudgetExceededException** — Budget was exceeded mid-stream; the invocation was terminated. This is the expected enforcement behavior.
- **"budget_exceeded"** — SignalR broadcast logged by middleware. Confirms the UI should have received the notification.
- **No rows** — Either no agents have hit their budget limits, or the budget logging gap means breaches are happening silently.
