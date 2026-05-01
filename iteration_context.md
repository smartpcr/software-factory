## Previous Query Results (Iteration 0)

| # | Query | Issue | Database | Score | Rows | Key Finding |
|---|-------|-------|----------|-------|------|-------------|
| Q1 | agent_pump_crashes | ISS-1 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q2 | claude_api_retries | ISS-1 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q3 | signalr_agent_events | ISS-2 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q4 | telemetry_hub_connections | ISS-2 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q5 | engine_event_bridge_unknown | ISS-1 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q6 | run_event_persistence | ISS-2 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q7 | agent_budget_exceeded | ISS-3 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q8 | copilot_server_lifecycle | ISS-1 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q9 | workflow_engine_failures | General | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q10 | dead_letter_queue | General | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q11 | audit_trail_analysis | ISS-4 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q12 | taskchannel_broadcast_failures | ISS-2 | App Logs | 0 | 0 | Not yet executed — written to disk this iteration |
| Q13 | dispatch_latency_capacity | General | OTel Metrics | 0 | 0 | Not yet executed — requires OTel metrics sink |
| Q23 | agenttoolhost_sandbox_enforcement | ISS-1 (gap) | App Logs | 0 | 0 | Not yet executed — readiness query (zero logging today) |
| Q24 | forgehub_group_membership | ISS-2 (gap) | App Logs | 0 | 0 | Not yet executed — readiness query (zero logging today) |
| Q25 | mesettings_invalid_defaults | ISS-5 | App Logs | 0 | 0 | Not yet executed — readiness query (zero logging today) |
| Q26 | pr_fake_ci_detection | ISS-6 | App Logs | 0 | 0 | Not yet executed — readiness query (zero logging today) |

### Gaps to Address

- **ISS-1 (FORGE-GAP-11.6)**: `ClaudeAgentRunner` does not invoke `IAgentToolSession` — `tool_use` SSE parsing absent. Q1, Q2, Q5, Q8 target this gap from different angles.
- **ISS-2 (FORGE-GAP-11.7)**: SignalR fan-out works server-side but no UI consumer is wired. Q3, Q4, Q6, Q12 target this gap.
- **ISS-3 (FORGE-GAP-11.10)**: Budget enforcement logging gap — RecordTokens throws without explicit logging. Q7 targets this.
- **ISS-4 (FORGE-GAP-10.8)**: `Supervising` audit column never written — always `false`. Q11 can confirm this gap.
- **ISS-5 (FORGE-GAP-10.2)**: MeSettings validation gap — no logging for invalid DefaultProductId/DefaultRepoId. No query can target this; requires code fix.
- **ISS-6 (FORGE-GAP-4.9)**: PR RerunBuild/CancelBuild are fake — no logging distinguishes fake from real CI. No query can target this; requires code fix.
- **Logging gap**: `AgentToolHost` sandbox enforcement has ZERO ILogger calls — tool allowlist, path traversal, symlink detection, budget checks are all completely silent.
- **Logging gap**: `ForgeHub.cs` Subscribe/Unsubscribe have no ILogger calls — cannot audit SignalR group membership.

### Next Query Number: 27

### Existing Query Files

- `kusto/q01_agent_pump_crashes.md` (ISS-1)
- `kusto/q02_claude_api_retries.md` (ISS-1)
- `kusto/q03_signalr_agent_events.md` (ISS-2)
- `kusto/q04_telemetry_hub_connections.md` (ISS-2)
- `kusto/q05_engine_event_bridge_unknown.md` (ISS-1)
- `kusto/q06_run_event_persistence.md` (ISS-2)
- `kusto/q07_agent_budget_exceeded.md` (ISS-3)
- `kusto/q08_copilot_server_lifecycle.md` (ISS-1)
- `kusto/q09_workflow_engine_failures.md` (General)
- `kusto/q10_dead_letter_queue.md` (General)
- `kusto/q11_audit_trail_analysis.md` (ISS-4)
- `kusto/q12_taskchannel_broadcast_failures.md` (ISS-2)
- `kusto/q13_dispatch_latency_capacity.md` (General)
- `kusto/q23_agenttoolhost_sandbox_enforcement.md` (ISS-1, logging gap)
- `kusto/q24_forgehub_group_membership.md` (ISS-2, logging gap)
- `kusto/q25_mesettings_invalid_defaults.md` (ISS-5)
- `kusto/q26_pr_fake_ci_detection.md` (ISS-6)

### Already-Recommended Library Queries (do NOT re-recommend)

_(none — this repository has no external library queries)_

---

## Recommended Next Action

1. **Configure structured logging sink** — Connect ILogger output to Application Insights, Seq, or a JSON file sink to enable Q1-Q12 query execution
2. **Configure OTel metrics export** — Set up OTLP exporter or Prometheus endpoint to enable Q13 (metric queries)
3. **Prioritize ISS-1 fix (FORGE-GAP-11.6)** — Wire `tool_use` SSE parsing → `IAgentToolSession` calls → emit ToolCall/ToolResult events. This unblocks agents from chat-only mode.
4. **Add ILogger to AgentToolHost.cs** — Critical logging gap; sandbox enforcement is completely silent. Add LogWarning for blocked tools, path traversal attempts, and budget violations.
5. **Add ILogger to ForgeHub.cs** — Add LogInformation for Subscribe/Unsubscribe to audit group membership.

---

_Generated: 2026-04-29 | Iteration: 1 | Status: QUERIES WRITTEN (17 total — 12 ILogger + 1 OTel + 4 readiness queries for logging gaps)_
