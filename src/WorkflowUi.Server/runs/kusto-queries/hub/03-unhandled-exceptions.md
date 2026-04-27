---
name: unhandled_exceptions
description: Unhandled exceptions caught by the global error handler
database: Hub
cluster: https://aeoprodeastus.eastus.kusto.windows.net
table: "['ETL-Microsoft-WorkflowUi-Server.UnhandledException']"
schema_validated: false
---

> ⚠️ Table not yet created — ETW provider not implemented

## Purpose

List all unhandled exceptions that reached the ProblemDetailsWriter error handler. Each row includes the correlation ID for tracing back to the originating request.

## Query

```kusto
['ETL-Microsoft-WorkflowUi-Server.UnhandledException']
| where OccurrenceTime > ago(1d)
| project OccurrenceTime, correlationId, exceptionType, message, MachineName
| order by OccurrenceTime desc
| limit 1000
```

## How to Interpret Results

Group by `exceptionType` to find the most common failure modes. Use `correlationId` to join with request logs for full context on what triggered the exception.
