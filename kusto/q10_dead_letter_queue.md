---
name: dead_letter_queue
description: Dead Letter Queue Activity — message routing failures
issue: General — message routing reliability
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Monitors messages landing in the dead letter queue from `DeadLetterQueue.cs`.

## Query

```kusto
traces
| where message has "dead letter queue" or message has "Dead letter"
| project timestamp, message, severityLevel, customDimensions.Reason, customDimensions.MessageType
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- Each row = a message that could not be delivered to its target node
- `Reason` field explains why (node not found, queue full, exception)
- High volume = systemic routing issue
