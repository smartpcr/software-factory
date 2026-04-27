---
name: appinsights_slow_requests
description: Slow HTTP requests (>5s) parsed from ILogger request timing logs
database: telemetry
cluster: https://api.applicationinsights.io
schema_validated: true
schema_notes: "Uses standard Application Insights 'traces' table with parse operator on structured message."
---

## Purpose

Identifies slow HTTP requests by parsing the `RequestLoggingEnricher` log messages. Extracts method, path, status code, and elapsed time from the structured log message template.

Source log site: `RequestLoggingEnricher` — `"HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms"` (Information level)

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

## Schema Validation

| Element | Status | Notes |
|---------|--------|-------|
| Table `traces` | ✅ Valid | Standard Application Insights table |
| Column `timestamp` | ✅ Valid | Standard column |
| Column `message` | ✅ Valid | Standard column |
| Parsed field `Method` | ⚠️ Parse-dependent | Relies on exact message template format |
| Parsed field `Path` | ⚠️ Parse-dependent | Paths with spaces may break the parse pattern |
| Parsed field `StatusCode` | ⚠️ Parse-dependent | Relies on exact message template format |
| Parsed field `ElapsedMs` | ⚠️ Parse-dependent | Relies on exact message template format |

## Parameter Substitutions

No `metadata.md` found. Query uses relative time (`ago(1d)`) — no substitutions needed.

## How to Interpret Results

Results are ordered by elapsed time descending (slowest first). The `parse` operator extracts fields from the log message — if the message template in `RequestLoggingEnricher` changes, this query will need updating. Consider using `customDimensions["ElapsedMs"]` instead if structured properties are available.
