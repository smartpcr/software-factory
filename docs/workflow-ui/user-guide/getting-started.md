# Workflow UI — User Guide

## Getting Started

### Prerequisites

- .NET 8.0 SDK
- Node.js 20+
- Docker (optional, for container deployment)

### Quick Start

1. Clone the repository:
   ```bash
   git clone https://github.com/smartpcr/workflow.git
   cd workflow
   ```

2. Build the solution:
   ```bash
   dotnet build Workflow.sln
   ```

3. Build the UI:
   ```bash
   cd src/WorkflowUI
   npm ci
   npm run build
   ```

4. Run the server:
   ```bash
   dotnet run --project src/WorkflowUi.Server
   ```

5. Open `http://localhost:5226` in your browser.

### Docker Quick Start

```bash
docker build -t workflow-ui .
docker run -p 8080:8080 \
  -e WorkflowUi__Auth__AllowDevBypass=true \
  -e ASPNETCORE_ENVIRONMENT=Development \
  workflow-ui
```

Open `http://localhost:8080` in your browser.

## Designer Tour

The Workflow UI designer consists of several panels:

### Workspace Tree (left sidebar)
Browse, create, rename, delete, import/export, and duplicate workflows.

### Palette
Drag node kinds onto the canvas to add them to the workflow.

### Canvas (center)
The main editing area where you connect nodes to build the workflow graph.

### Property Grid (right sidebar)
Configure properties for the selected node.

### Validation Bar (bottom)
Run validation checks on the current workflow and jump to problem nodes.

### Footer
Displays the current version and commit SHA.

## Node Kinds

The following node kinds are available:

- [Noop](kinds/noop.md) — No-operation placeholder
- [CSharp](kinds/csharp.md) — C# code execution
- [CSharpScript](kinds/csharp-script.md) — C# script execution
- [CSharpTask](kinds/csharp-task.md) — C# task-based execution
- [PowerShell](kinds/powershell.md) — PowerShell script execution
- [PowerShellTask](kinds/powershell-task.md) — PowerShell task-based execution
- [IfElse](kinds/ifelse.md) — Conditional branching
- [Switch](kinds/switch.md) — Multi-way branching
- [ForEach](kinds/foreach.md) — Collection iteration
- [While](kinds/while.md) — Loop with condition
- [Subflow](kinds/subflow.md) — Nested workflow execution
- [Timer](kinds/timer.md) — Timed delay
- [Container](kinds/container.md) — Sequential/parallel container

## Authentication

The server supports JWT bearer authentication with an optional dev-mode bypass.

### Configuration

Set the following environment variables or appsettings:

| Setting | Description |
|---------|-------------|
| `WorkflowUi:Auth:Authority` | OIDC authority URL |
| `WorkflowUi:Auth:Audience` | Expected audience claim |
| `WorkflowUi:Auth:AllowDevBypass` | Enable dev-mode bypass (Development only) |

### Roles

- **Editor** — Can create, edit, delete workflows
- **Viewer** — Can list and view workflows

## Persistence

Workflows are stored as YAML + JSON sidecar file pairs under the configured `WorkflowsRoot` directory.

### Configuration

| Setting | Description | Default |
|---------|-------------|---------|
| `WorkflowUi:Persistence:WorkflowsRoot` | Root directory for workflow files | `workflows` |
| `WorkflowUi:Persistence:RunsRoot` | Root directory for run history | `runs` |
| `WorkflowUi:Persistence:RunRetentionDays` | Days to keep run history | `30` |
