---
name: mesettings_invalid_defaults
description: MeSettings Invalid Defaults — detect invalid DefaultProductId/DefaultRepoId
issue: ISS-5 — MeSettings validation gap (FORGE-GAP-10.2)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects when users save MeSettings with invalid `DefaultProductId` or `DefaultRepoId` values. `MeSettingsEndpoints.cs` accepts arbitrary strings without validation — **no ILogger calls guard this path today**. This query will return no rows until validation logging is added. Once instrumented, it catches misconfigured user defaults that silently break downstream product/repo resolution.

### Recommended logging to add

```csharp
// DefaultProductId validation
_logger.LogWarning("MeSettings: invalid DefaultProductId {ProductId} for user {UserId} — product not found", productId, userId);

// DefaultRepoId validation
_logger.LogWarning("MeSettings: invalid DefaultRepoId {RepoId} for user {UserId} — repository not found", repoId, userId);

// Optional counter
_meSettingsInvalidCounter.Add(1, new("field", "DefaultProductId"));
```

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: MeSettingsEndpoints.cs — no validation logging exists today
// Covers: invalid DefaultProductId, invalid DefaultRepoId
traces
| where message has "MeSettings" and message has "invalid"
| where severityLevel >= 2
| project timestamp, message, severityLevel,
    customDimensions.UserId,
    customDimensions.ProductId,
    customDimensions.RepoId
| extend InvalidField = case(
    message has "DefaultProductId", "DefaultProductId",
    message has "DefaultRepoId", "DefaultRepoId",
    "Unknown"
  )
| summarize Count=count() by InvalidField, tostring(customDimensions.UserId)
| order by Count desc
| limit 1000
```

## How to Interpret Results

- **No rows (current state)** — No validation logging exists. Invalid settings are accepted silently.
- **DefaultProductId invalid** — User saved a product ID that doesn't exist. Downstream dispatching or filtering will silently use a stale/wrong product context.
- **DefaultRepoId invalid** — User saved a repo ID that doesn't exist. Repository-scoped operations may fail or target the wrong repo.
- **High count for one UserId** — User repeatedly saving invalid settings; may indicate a UI bug or stale dropdown options.
- **Many distinct UserIds** — Systemic issue; the valid product/repo list may have changed and saved settings are now stale.
