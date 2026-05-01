---
name: pr_fake_ci_detection
description: PR Fake CI Operations — distinguish RerunBuild/CancelBuild fake from real CI
issue: ISS-6 — PR RerunBuild/CancelBuild are fake (FORGE-GAP-4.9)
database: Application Logs (ILogger sink)
schema_validated: false
score: 0
---

## Purpose

Detects fake CI operations (RerunBuild, CancelBuild) on PR endpoints. The current implementation flips cached state and synthesizes log tails without invoking any real CI provider, then returns 202 — **indistinguishable from a real CI call**. No ILogger calls mark these as fake today. This query will return no rows until logging is added. Once instrumented, it prevents operators from mistakenly believing CI was actually triggered.

### Recommended logging to add

```csharp
// RerunBuild (fake)
_logger.LogWarning("PR: fake RerunBuild for PR {PrId} — no CI provider invoked, returning synthesized 202", prId);

// CancelBuild (fake)
_logger.LogWarning("PR: fake CancelBuild for PR {PrId} — no CI provider invoked, returning synthesized 202", prId);

// Optional: tag the audit trail
// AuditRow.IsFakeCi = true
```

## Query

```kusto
// Target: structured ILogger output (Application Insights / Seq / console JSON)
// Source: PR endpoints — RerunBuild/CancelBuild handlers
// Today: no logging distinguishes fake from real CI operations
traces
| where message has "fake" and (message has "RerunBuild" or message has "CancelBuild")
| where severityLevel >= 2
| project timestamp, message, severityLevel,
    customDimensions.PrId,
    customDimensions.Operation
| extend CiOperation = case(
    message has "RerunBuild", "RerunBuild",
    message has "CancelBuild", "CancelBuild",
    "Unknown"
  )
| summarize Count=count() by CiOperation, bin(timestamp, 1h)
| order by timestamp desc
| limit 1000
```

## Alternative Detection (without new logging)

Until explicit fake-CI logging is added, you can infer fake operations by correlating the audit trail with actual CI provider calls:

```kusto
// Cross-reference: audit log shows RerunBuild/CancelBuild actions
// but no corresponding CI webhook callback was received
traces
| where message has "Audit:" and (message has "RerunBuild" or message has "CancelBuild")
| project timestamp, message, customDimensions.Path, customDimensions.UserId, customDimensions.OutcomeCode
| join kind=leftanti (
    traces
    | where message has "CI webhook" or message has "build callback"
    | project timestamp, message
  ) on timestamp
| order by timestamp desc
| limit 1000
```

## How to Interpret Results

- **No rows (current state)** — No logging distinguishes fake from real CI. The 202 response looks identical to a real CI call.
- **"fake RerunBuild"** — Confirms the build was not actually triggered. The PR status was only flipped in cache with synthesized logs.
- **"fake CancelBuild"** — Confirms the cancel was not actually sent to a CI provider.
- **High frequency** — Many users triggering fake CI thinking it's real. UI should surface a warning or disable these actions until a real CI provider is configured.
- **Alternative query results** — Audit entries for RerunBuild/CancelBuild with no matching CI webhook confirm fake operations even without explicit logging.
