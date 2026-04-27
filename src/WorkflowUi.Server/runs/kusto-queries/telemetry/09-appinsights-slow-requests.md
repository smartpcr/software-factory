---
name: appinsights_slow_requests
description: HTTP requests exceeding 5 seconds detected via ILogger traces
database: Telemetry
cluster: https://aeoprodtelemetry.eastus.kusto.windows.net
table: traces
schema_validated: false
---

## Purpose

Identify slow HTTP requests using the `RequestLoggingEnricher` ILogger message. This is the current-stack equivalent of Query 2 (ETW-based slow requests).

## Query

```kusto
traces
| where timestamp > ago(1d)
| where message has "HTTP" and message has "responded"
| parse message with "HTTP " Method " " Path " responded " StatusCode " in " ElapsedMs "ms"
| where toint(ElapsedMs) > 5000
| project timestamp, Method, Path, StatusCode, ElapsedMs
| order by toint(ElapsedMs) desc
```

## How to Interpret Results

Each row is a slow request. `Method` and `Path` identify the endpoint. The `parse` operator extracts structured fields from the log message. Note: this relies on the exact message template from `RequestLoggingEnricher`.
