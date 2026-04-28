# Kusto Queries — Local Kusto (ETL-ingested)

> Generated from `etw_events.md` (Iteration 1). All queries target dynamically-created ETL tables in a local Kusto database.

| # | File | Title | Description |
|---|------|-------|-------------|
| 1 | [Q01.md](Q01.md) | HostPluginWatchDog Error Events | Critical/Error/Warning events from process start watchdog |
| 2 | [Q02.md](Q02.md) | HostPluginWatchDog Process Lifecycle | Processes started by the watchdog with paths and arguments |
| 3 | [Q03.md](Q03.md) | HostPluginWatchDog Already Running Detection | 5-minute bucketed counts of already-running detections |
| 4 | [Q04.md](Q04.md) | CloudEngine Action Plan Execution | ECE action plan start events |
| 5 | [Q05.md](Q05.md) | CloudEngine Remote Step Completion | Failed remote PowerShell step completions |
| 6 | [Q06.md](Q06.md) | Common Infrastructure HTTP Client Errors | Failed service-to-service HTTP calls |
| 7 | [Q07.md](Q07.md) | Service Fabric Host Initialization | SF host process startup events |
| 8 | [Q08.md](Q08.md) | Health Fault State Machine Transitions | Health fault creation/resolution tracking |
| 9 | [Q09.md](Q09.md) | URP/Update Service Lifecycle (HCI) | Update service start/stop lifecycle |
| 10 | [Q10.md](Q10.md) | URP Health Check Failures (HCI) | Missing/empty update content directories |
| 11 | [Q11.md](Q11.md) | Network Controller Firewall Plugin Errors | OVSDB firewall rule application failures |
| 12 | [Q12.md](Q12.md) | Network Controller Gateway Plugin Errors | Gateway plugin error events |
| 13 | [Q13.md](Q13.md) | Windows Event Log Errors | Aggregated EVTX errors by provider/EventId |
| 14 | [Q14.md](Q14.md) | Event Frequency Heatmap by Provider | Hourly event volume for downtime detection |
| 15 | [Q15.md](Q15.md) | Cross-Provider Error Timeline | Stale health check result detection |

## Table Naming Convention

```
ETL tables:  ['ETL-{ProviderName}.{EventName}']
EVTX table:  WindowsEvents
```

## Standard Columns (all ETL tables)

| Column | Type | Description |
|--------|------|-------------|
| `TimeStamp` | datetime | Event timestamp |
| `ProcessID` | int | Source process ID |
| `ProcessName` | string | Source process name |
| `Level` | int | 0=Always, 1=Critical, 2=Error, 3=Warning, 4=Info, 5=Verbose |
| `Opcode` | int | ETW opcode value |
| `OpcodeName` | string | ETW opcode name |
