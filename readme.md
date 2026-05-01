# Workflow

A .NET 8.0 monorepo with three major subsystems: **Forge** (AI-powered software factory), **ExecutionEngine** (DAG-based workflow orchestration), and **ETW/EVTX processing** (Windows event log ingestion into Kusto).

---

## Forge — Software Factory

Forge is an AI-agent-powered engineering management system that connects to ADO/GitHub, syncs work items and PRs, dispatches tasks to AI coding agents, and tracks builds end-to-end. It runs as a local two-process stack: ASP.NET Core API + React SPA.

```
┌────────────────────┐       ┌──────────────────┐
│  Forge.UI (React)  │──────▶│  Forge.Api       │──────▶  ADO / GitHub REST
│  Vite · port 5181  │ REST  │  ASP.NET 8       │
│                    │◀──────│  port 5180       │
│                    │SignalR │  ┌────────────┐  │  background jobs
└────────────────────┘       │  │ SQLite DB  │  │  ├─ WorkItemSyncJob (20 min)
                             │  │ forge.db   │  │  ├─ PrSyncJob (20 min)
                             │  └────────────┘  │  ├─ BuildSyncJob (20 min)
                             └──────────────────┘  └─ CrewQueueWorker
```

### Key capabilities

| Area | What it does |
|------|-------------|
| **Products & repos** | Attach ADO/GitHub repos, configure area paths, build/test commands |
| **Work item sync** | Scheduled delta sync from ADO, agent-ready classification by team |
| **PR sync** | Pull requests with comment threads, build status, diff stats |
| **Build validation** | Detect build pipeline, run init → restore → build → test locally |
| **Agent dispatch** | Route tasks to AI agents (Copilot Coding Agent), track transcripts |
| **Crew management** | Day/night shifts, per-crew quotas, agent roster, cost tracking |
| **Sync history** | Persistent run log for all sync/build operations with per-entry detail |

### Quick start

```bash
# Build & run both API and UI
.\scripts\run-forge-ui.ps1 -Rebuild

# Or run separately
dotnet run --project src/forge/Forge.Api/Forge.Api.csproj     # API on :5180
cd src/forge/Forge.UI && npm run dev                           # UI on :5181

# Run tests (stop Forge.Api first — exe is file-locked)
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj
```

### Documentation

- **[docs/forge/](docs/forge/)** — User docs (setup, providers, daily flow, keyboard, FAQ)
- **[docs/forge/architecture.md](docs/forge/architecture.md)** — API/UI shape, request flow, telemetry
- **[docs/forge_implementation.md](docs/forge_implementation.md)** — Phase-by-phase implementation plan

---

## Execution Engine

A DAG-based workflow orchestration engine with per-node message queues, lock-free circular buffers, and support for C#/PowerShell task execution.

### Key features

- Graph-based workflow definition (YAML) with nodes and edges
- Container nodes: sequential or parallel execution of children
- Subflow composition for workflow reusability
- State persistence — pause, resume, cancel workflows
- Progress tracking with Gantt-style timeline rendering

### Example output

```
✓   root   (10.0s)                                ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  100%
└── ✓ azs deployment (S 8.9s)                     ━━━━━━━━━━━━━━━━━━━━━━━━━━━━    100%
    ├── ✓ pre deployment checks (S 1.7s)          ━━━━━━                           100%
    ├── ✓ node deployments (P 5.5s)                     ━━━━━━━━━━━━━━━━━━━━       100%
    └── ✓ health checks (S 1.7s)                                        ━━━━━━━   100%
```

### Quick start

```bash
dotnet run --project src/ExecutionEngine.Example/ExecutionEngine.Example.csproj
dotnet test src/ExecutionEngine.UnitTests/ExecutionEngine.UnitTests.csproj
```

### Documentation

- **[src/ExecutionEngine/design.md](src/ExecutionEngine/design.md)** — Architecture, node model, message queues
- **[src/ExecutionEngine/implementation.md](src/ExecutionEngine/implementation.md)** — Phase roadmap, test strategy
- **[src/ExecutionEngine/usage_guide.md](src/ExecutionEngine/usage_guide.md)** — Examples, integration patterns

---

## Workflow Designer UI

A browser-based visual editor for building, editing, and monitoring ExecutionEngine workflows. ASP.NET Core 8 server + React 18 SPA with real-time telemetry via SignalR.

```bash
dotnet run --project src/WorkflowUi.Server/WorkflowUi.Server.csproj   # Server :5226
cd src/WorkflowUI && npm run dev                                       # UI :5555
```

- **[docs/workflow-ui-plan.md](docs/workflow-ui-plan.md)** — Implementation plan
- **[docs/workflow-ui/](docs/workflow-ui/)** — Per-stage specs

---

## ETW/EVTX Processing Pipeline

Processes Windows Event Tracing (ETW) and Event Log (EVTX) files for ingestion into Azure Data Explorer (Kusto).

**Pipeline**: Parse → Schema Inference → Table Creation → CSV Export → Kusto Ingestion

```bash
dotnet test src/EtwIngest/EtwIngest.csproj                              # BDD tests
dotnet test src/EtwIngest/EtwIngest.csproj --filter "Category=parser"   # By tag
dotnet run --project src/EtlIterator/EtlIterator.csproj                 # Batch processor
```

Key packages: `Microsoft.Diagnostics.Tracing.TraceEvent`, `evtx`, `Microsoft.Azure.Kusto.Data`

---

## Solution structure

```
src/
├── forge/                          # Forge software factory
│   ├── Forge.Api/                  #   ASP.NET 8 API + SQLite + background jobs
│   ├── Forge.Api.Tests/            #   xUnit tests
│   └── Forge.UI/                   #   React 18 + Vite + TypeScript SPA
├── ExecutionEngine/                # DAG workflow engine core library
├── ExecutionEngine.Example/        # Example workflows (Azure Stack deployment)
├── ExecutionEngine.UnitTests/      # Reqnroll BDD tests
├── ExecutionEngine.IntegrationTests/
├── ProgressTree/                   # Gantt-style progress rendering
├── WorkflowUI/                     # Workflow designer React SPA
├── WorkflowUi.Server/              # Workflow designer ASP.NET host
├── EtwIngest/                      # ETW/EVTX BDD test project
├── EtlIterator/                    # Batch ETL processor
├── EtwEventReader/                 # Event reader with formatters
├── Common.Diagnostics.EtwParser/   # Core ETW parsing library
└── Unzip/                          # Recursive ZIP extraction utility
```

## Build

```bash
dotnet build Workflow.sln
```
