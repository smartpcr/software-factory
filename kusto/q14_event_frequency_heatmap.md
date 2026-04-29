---
name: event_frequency_heatmap
description: Event Frequency Heatmap by Provider — volume trends over time
issue: N/A — general troubleshooting (consumed ETW provider)
database: Local Kusto (ETL-ingested)
schema_validated: true
score: 0
---

## Purpose

Event frequency heatmap — shows event volume over time for the HostPluginWatchDog ConfigFilesFound provider. Sudden drops indicate service downtime; spikes may indicate restart storms.

## Query

```kusto
['ETL-Microsoft-AzureStack-Compute-HostPluginWatchDog.ConfigFilesFound']
| summarize EventCount=count() by bin(TimeStamp, 1h)
| order by TimeStamp asc
| limit 1000
```

## How to Interpret Results

Shows event volume over time. Sudden drops indicate service downtime. Spikes may indicate restart storms.
