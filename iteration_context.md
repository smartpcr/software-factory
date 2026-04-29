## Previous Query Results (Iteration 2)

| # | Query | Issue | Database | Score | Rows | Key Finding |
|---|-------|-------|----------|-------|------|-------------|
| Q1 | q01_hostplugin_watchdog_errors | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q2 | q02_hostplugin_watchdog_lifecycle | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q3 | q03_hostplugin_already_running | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q4 | q04_cloudengine_action_plan | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q5 | q05_cloudengine_remote_step | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q6 | q06_common_infra_http_errors | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q7 | q07_sf_host_initialization | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q8 | q08_health_fault_state_machine | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q9 | q09_urp_service_lifecycle | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q10 | q10_urp_health_check_failures | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q11 | q11_nc_firewall_plugin_errors | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q12 | q12_nc_gateway_plugin_errors | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q13 | q13_windows_event_log_errors | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q14 | q14_event_frequency_heatmap | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q15 | q15_health_runner_invalid_results | N/A | Local Kusto | 0 | 0 | Not yet executed — written to disk this iteration |
| Q16 | q16_agent_pump_crashes | FORGE-GAP-11.6 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q17 | q17_agent_http_retries | FORGE-GAP-11.5 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q18 | q18_signalr_agent_events | FORGE-GAP-11.7 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q19 | q19_telemetry_hub_connections | FORGE-GAP-11.7 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q20 | q20_engine_event_bridge_unknown | FORGE-GAP-11.6 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q21 | q21_run_event_persistence_failures | FORGE-GAP-11.7 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |
| Q22 | q22_agent_budget_exceeded | FORGE-GAP-11.10 | App Logs | 0 | 0 | Not yet executed — new forge correctness query |

### Gaps to Address

- **Q1–Q15**: Consumed ETW provider queries now written to disk; awaiting execution against local Kusto with ingested ETL data
- **Q16–Q22**: Forge correctness queries targeting ILogger/SignalR call sites; require Application Insights or structured log sink
- **FORGE-GAP-11.6**: `ClaudeAgentRunner` does not invoke `IAgentToolSession` — `tool_use` SSE parsing absent (Q16, Q20)
- **FORGE-GAP-11.7**: SignalR fan-out works server-side but no UI consumer is wired (Q18, Q19, Q21)
- **FORGE-GAP-11.5**: Claude API retry monitoring (Q17) — no visibility into retry exhaustion
- **Logging gap**: `AgentToolHost` sandbox enforcement (tool allowlist, path traversal, budget) has no ILogger calls

### Next Query Number: 23

### Existing Query Files

- `kusto/q01_hostplugin_watchdog_errors.md`
- `kusto/q02_hostplugin_watchdog_lifecycle.md`
- `kusto/q03_hostplugin_already_running.md`
- `kusto/q04_cloudengine_action_plan.md`
- `kusto/q05_cloudengine_remote_step.md`
- `kusto/q06_common_infra_http_errors.md`
- `kusto/q07_sf_host_initialization.md`
- `kusto/q08_health_fault_state_machine.md`
- `kusto/q09_urp_service_lifecycle.md`
- `kusto/q10_urp_health_check_failures.md`
- `kusto/q11_nc_firewall_plugin_errors.md`
- `kusto/q12_nc_gateway_plugin_errors.md`
- `kusto/q13_windows_event_log_errors.md`
- `kusto/q14_event_frequency_heatmap.md`
- `kusto/q15_health_runner_invalid_results.md`
- `kusto/q16_agent_pump_crashes.md`
- `kusto/q17_agent_http_retries.md`
- `kusto/q18_signalr_agent_events.md`
- `kusto/q19_telemetry_hub_connections.md`
- `kusto/q20_engine_event_bridge_unknown.md`
- `kusto/q21_run_event_persistence_failures.md`
- `kusto/q22_agent_budget_exceeded.md`

### Already-Recommended Library Queries (do NOT re-recommend)

_(none — this repository has no external library queries)_

---

## Recommended Next Action

1. **Execute Q1–Q15** against local Kusto databases with ingested ETL data to validate consumed provider queries
2. **Wire ILogger structured logging sink** (Application Insights or Seq) to enable Q16–Q22 query execution
3. **Prioritize FORGE-GAP-11.6 fix** — wire `tool_use` SSE parsing → `IAgentToolSession` calls → emit `ToolCall`/`ToolResult` events. This unblocks the agent runtime from chat-only mode.

---

_Generated: 2026-04-28 | Iteration: 2 | Status: QUERIES WRITTEN (22 total — 15 ETW + 7 ILogger/SignalR)_
