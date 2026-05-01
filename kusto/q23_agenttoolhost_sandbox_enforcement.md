---
name: agenttoolhost_sandbox_enforcement
description: AgentToolHost Sandbox Enforcement — tool allowlist, path traversal, symlink detection
issue: ISS-1 — AgentToolHost has ZERO ILogger calls (logging gap)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects sandbox enforcement actions in `AgentToolHost.cs` — specifically tool allowlist violations, path traversal prevention, and symlink detection. **Today this file has ZERO ILogger calls**, so this query will return no rows until logging is added. Once instrumented, these signals are critical for detecting agents attempting to escape their sandbox.

### Recommended logging to add

```csharp
// Tool allowlist check
_logger.LogWarning("AgentToolHost: tool {ToolName} blocked by allowlist for agent {AgentId}", toolName, agentId);

// Path traversal prevention
_logger.LogWarning("AgentToolHost: path traversal blocked for agent {AgentId}, path {AttemptedPath}", agentId, attemptedPath);

// Symlink detection
_logger.LogWarning("AgentToolHost: symlink detected at {Path} for agent {AgentId}, access denied", path, agentId);
```

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: AgentToolHost.cs — ZERO ILogger calls today; query is ready for when logging is added
// Covers: tool allowlist violations, path traversal attempts, symlink access denials
traces
| where message has "AgentToolHost"
| where message has "blocked" or message has "denied" or message has "traversal" or message has "symlink" or message has "allowlist"
| where severityLevel >= 2
| project timestamp, message, severityLevel,
    customDimensions.AgentId,
    customDimensions.ToolName,
    customDimensions.AttemptedPath
| extend EnforcementType = case(
    message has "allowlist", "ToolAllowlistBlock",
    message has "traversal", "PathTraversalBlock",
    message has "symlink", "SymlinkBlock",
    "Unknown"
  )
| summarize Count=count() by EnforcementType, tostring(customDimensions.AgentId)
| order by Count desc
| limit 1000
```

## How to Interpret Results

- **No rows (current state)** — `AgentToolHost.cs` has zero ILogger calls. This is the most critical logging gap — sandbox enforcement decisions are completely silent.
- **ToolAllowlistBlock** — Agent tried to invoke a tool not in its allowlist. High counts from one agent indicate a misconfigured or adversarial prompt.
- **PathTraversalBlock** — Agent tried to access files outside its allowed directory. This is a security concern.
- **SymlinkBlock** — Agent tried to follow a symlink, which could escape the sandbox boundary.
- **Cluster by AgentId** to identify which agents are repeatedly hitting sandbox boundaries.
