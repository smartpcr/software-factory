---
name: claude_api_retries
description: Claude API HTTP Retry Events — transient failures during SSE streaming
issue: ISS-1 — ClaudeAgentRunner SSE reliability (FORGE-GAP-11.5)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors Claude API retry behavior in `AgentRunnerDispatcher.cs` (lines 250, 259). Captures both network exceptions and HTTP error status codes during SSE streaming.

## Query

```kusto
traces
| where message has "Claude HTTP failed" or message has "Claude returned"
| where severityLevel >= 2
| project timestamp, message, customDimensions.Attempt, customDimensions.Status, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- "Claude HTTP failed" = network exception on attempt N
- "Claude returned {Status}" = 429 (rate limited), 503 (unavailable), 529 (overloaded)
- Attempt > 3 = approaching max retries; exhaustion leads to pump crash (Q1)
