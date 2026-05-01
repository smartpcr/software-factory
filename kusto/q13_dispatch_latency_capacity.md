---
name: dispatch_latency_capacity
description: Dispatch Latency and Queue Capacity — OTel metrics monitoring
issue: General — agent queue health
database: OTel Metrics (Prometheus / OTLP sink)
schema_validated: false
score: 0
---

## Purpose

Monitors agent dispatch latency and queue capacity via OTel metrics from `LeadAutoDispatcher.cs` and `CrewQueueWorker.cs`.

## Query (PromQL)

```promql
# p95 dispatch latency
histogram_quantile(0.95, rate(forge_dispatch_lead_to_crew_latency_ms_bucket[5m]))

# Ack timeout rate
sum(rate(forge_queue_ack_timeout_total[5m])) by (targetKind)

# Queue strain events
sum(rate(forge_queue_strain_fired_total[5m])) by (crewId)
```

## How to Interpret Results

- p95 dispatch latency > 30s = capacity issues
- Rising ack_timeout rate = queue strain
- `forge.queue.strain.fired` = system detected overload
