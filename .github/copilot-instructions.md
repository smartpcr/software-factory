# Copilot Instructions for Workflow Repository

## Build and Test Commands

```bash
# Build the solution
dotnet build Workflow.sln

# Run all tests for a specific project
dotnet test src/EtwIngest/EtwIngest.csproj
dotnet test src/ExecutionEngine.UnitTests/ExecutionEngine.UnitTests.csproj
dotnet test src/ProgressTree.UnitTests/ProgressTree.UnitTests.csproj

# Run a single test by name
dotnet test src/ExecutionEngine.UnitTests/ExecutionEngine.UnitTests.csproj --filter "DisplayName~Simple sequential workflow"

# Run tests by category (BDD scenario tags)
dotnet test src/EtwIngest/EtwIngest.csproj --filter "Category=parser"
dotnet test src/EtwIngest/EtwIngest.csproj --filter "Category=schema"
dotnet test src/EtwIngest/EtwIngest.csproj --filter "Category=extract"
dotnet test src/EtwIngest/EtwIngest.csproj --filter "Category=ingest"

# Run integration tests
dotnet test src/EtwEventReader.IntegrationTests/EtwEventReader.IntegrationTests.csproj --filter "TestCategory=Integration"
dotnet test src/ExecutionEngine.IntegrationTests/ExecutionEngine.IntegrationTests.csproj

# Run Forge.Api tests
# IMPORTANT: The running Forge.Api server must be stopped first — the test
# build copies Forge.Api.exe which is file-locked by the server process.
# Always restart the server after tests complete.
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj
```

## Architecture Overview

This is a .NET 8.0 Windows-only solution with two major subsystems:

### ETW/EVTX Processing Pipeline

Processes Event Tracing for Windows (ETW) and Windows Event Log (EVTX) files for ingestion into Azure Data Explorer (Kusto):

1. **Parsing** - `EtlFile.Parse()` extracts event schemas using Microsoft.Diagnostics.Tracing.TraceEvent
2. **Schema Inference** - Maps .NET CLR types to Kusto column types via `KustoExtension.ToKustoColumnType()`
3. **Table Creation** - Generates Kusto DDL commands matching event schemas
4. **CSV Export** - `EtlFile.Process()` converts events to CSV with proper escaping
5. **Ingestion** - CSV files ingested into Kusto tables

Key projects: `EtwIngest`, `EtlIterator`, `EtwEventReader`, `Common.Diagnostics.EtwParser`

### ExecutionEngine (Workflow Orchestration)

A DAG-based workflow execution engine with:

- **Graph-based workflows** - Nodes (tasks, control flow, subflows) connected by edges
- **Per-node message queues** - Lock-free circular buffers with visibility timeout pattern
- **C# and PowerShell execution** - Dynamic node loading from assemblies
- **State persistence** - Pause, resume, cancel workflows
- **Containers** - Sequential or parallel execution of child nodes

Workflow definitions use YAML format. See `src/ExecutionEngine/design.md` for architecture details.

## Code Conventions

### Required File Header

```csharp
// -----------------------------------------------------------------------
// <copyright file="{fileName}" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
```

### C# Style (enforced via .editorconfig)

- **`this.` qualifier** - Always use for instance members: `this.fieldName`, `this.PropertyName`, `this.MethodName()`
- **`using` directives** - Place inside namespace, sort `System.*` first
- **`var` preference** - Use when type is apparent
- **Indentation** - 4 spaces for C#, 2 spaces for other files
- **Field naming**:
  - Const fields: `PascalCase`
  - Public/internal static: `PascalCase`
  - Private/protected static: `camelCase`
  - Private/protected instance: `camelCase`

### BDD Testing Pattern

Tests use Reqnroll (SpecFlow successor) with MSTest:
- Feature files in `*/Features/*.feature`
- Step definitions in `*/Steps/*Steps.cs`
- Tag scenarios with categories like `@parser`, `@schema`, `@extract`, `@ingest`

### Kusto Table Naming

Tables follow pattern: `ETL-{ProviderName}.{EventName}` where slashes in event names are removed (e.g., "StartWatchDog/Stop" → "StartWatchDogStop").

## Important Implementation Details

### Timeout Protection

`EtlFile.Parse()` and `EtlFile.Process()` include a 10-second timeout - if no events are received within 10 seconds, processing stops. This prevents hanging on corrupted ETL files.

### CSV Escaping

String fields with special characters (quotes, commas, spaces, newlines):
1. Wrapped in double quotes
2. Internal quotes escaped by doubling (`"` → `""`)

### Message Queue Design

ExecutionEngine uses lock-free circular buffers with CAS operations for thread safety. Status transitions: `Ready → InFlight → Removed` (or `Abandon/Retry` back to `Ready`).
