---
name: workflow_engine_failures
description: Workflow Engine Node Failures — DAG execution errors
issue: General — execution engine reliability
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Tracks workflow DAG node failures in `WorkflowEngine.cs`.

## Query

```kusto
traces
| where message has "Node execution failed" or message has "Fatal error creating node" or message has "Unhandled node failure"
| where severityLevel >= 3
| project timestamp, message, customDimensions.NodeId, customDimensions.ErrorMessage
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- "Fatal error creating node" = node definition/type resolution failure
- "Unhandled node failure" = no error handler configured, workflow will fail
- "Node execution failed" = runtime error in node logic
