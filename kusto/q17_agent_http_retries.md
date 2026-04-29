---
name: agent_http_retries
description: Claude API HTTP Retries — transient failures during agent invocations
issue: FORGE-GAP-11.5 — ClaudeAgentRunner SSE reliability
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects retry events in `AgentRunnerDispatcher.RunClaudeSdk()` (lines 250, 259). The dispatcher logs WARNING on HTTP exceptions and 429/503/529 status codes before retrying with exponential backoff. High retry rates indicate Anthropic API instability or rate limiting.

## Forge Gap Reference

**[11.5]** ClaudeAgentRunner uses exponential backoff for HTTP retries. Monitoring retry frequency is critical — if retries exceed the max attempt count, the invocation silently fails with no ToolCall events emitted.

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: AgentRunnerDispatcher.cs:250 — LogWarning(ex, "Claude HTTP failed (attempt {Attempt}); retrying", attempt)
// Source: AgentRunnerDispatcher.cs:259 — LogWarning("Claude returned {Status} (attempt {Attempt}); retrying", status, attempt)
traces
| where message has "Claude HTTP failed" or message has "Claude returned"
| where severityLevel >= 2
| project timestamp, message, customDimensions.Attempt, customDimensions.Status, customDimensions.ExceptionType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **"Claude HTTP failed"** — Network-level exception (DNS, TLS, timeout). Check `ExceptionType` for root cause.
- **"Claude returned {Status}"** — HTTP status code returned. 429 = rate limited, 503 = service unavailable, 529 = overloaded.
- **Attempt > 3** — Approaching max retries; likely imminent invocation failure.
- Cluster these by time to detect Anthropic API outage windows.
