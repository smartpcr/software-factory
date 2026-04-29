---
name: common_infra_http_errors
description: Common Infrastructure HTTP Client Errors — service-to-service call failures
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Common Infrastructure HTTP ClientRequestStop events at Warning/Error level — failed service-to-service HTTP calls.

## Query

```kusto
['ETL-Microsoft-AzureStack-Common-Infrastructure-Http.ClientRequestStop']
| where Level <= 3
| project TimeStamp, ProcessID, ProcessName, Level, OpcodeName
| order by TimeStamp desc
| limit 1000
```

## How to Interpret Results

HTTP client errors from common-infra indicate failed service-to-service calls. Check surrounding events in the same ProcessID for context.
