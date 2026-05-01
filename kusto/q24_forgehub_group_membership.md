---
name: forgehub_group_membership
description: ForgeHub Subscribe/Unsubscribe — SignalR group membership audit
issue: ISS-2 — ForgeHub has ZERO ILogger calls for Subscribe/Unsubscribe (logging gap)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Audits SignalR group membership changes in `ForgeHub.cs`. The `Subscribe()` (line 24) and `Unsubscribe()` (line 27) methods add/remove clients from SignalR groups (`agent.{agentId}`, `task.{workItemId}`) but **have ZERO ILogger calls today**. This query will return no rows until logging is added. Once instrumented, it enables auditing which clients are receiving real-time agent/task events.

### Recommended logging to add

```csharp
// Subscribe
_logger.LogInformation("ForgeHub: client {ConnectionId} subscribed to group {GroupName}", Context.ConnectionId, groupName);

// Unsubscribe
_logger.LogInformation("ForgeHub: client {ConnectionId} unsubscribed from group {GroupName}", Context.ConnectionId, groupName);
```

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: ForgeHub.cs:24 Subscribe(), ForgeHub.cs:27 Unsubscribe()
// Today: ZERO ILogger calls — query ready for when logging is added
traces
| where message has "ForgeHub"
| where message has "subscribed" or message has "unsubscribed"
| project timestamp, message,
    customDimensions.ConnectionId,
    customDimensions.GroupName
| extend Action = iff(message has "unsubscribed", "Unsubscribe", "Subscribe")
| summarize
    SubscribeCount=countif(Action == "Subscribe"),
    UnsubscribeCount=countif(Action == "Unsubscribe")
    by bin(timestamp, 1h), tostring(customDimensions.GroupName)
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **No rows (current state)** — `ForgeHub.cs` has no ILogger calls in Subscribe/Unsubscribe. Cannot audit group membership.
- **SubscribeCount >> UnsubscribeCount** — Clients connecting but not cleanly disconnecting. Potential connection leak or UI tab left open.
- **UnsubscribeCount > 0 with SubscribeCount = 0** — Stale group cleanup without new subscriptions.
- **GroupName pattern `agent.{id}`** — Clients receiving real-time agent events (ISS-2 fan-out verification).
- **GroupName pattern `task.{id}`** — Clients receiving real-time task events.
- Cross-reference with q04/q19 (TelemetryHub attach/detach) to build a complete picture of client connectivity.
