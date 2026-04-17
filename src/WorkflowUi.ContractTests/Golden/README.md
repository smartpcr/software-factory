# Golden-File Harness

## Overview

The golden-file harness verifies that fixture triplets round-trip correctly through the DTO mapper and engine serializer. Each fixture consists of three files:

- **`.yaml`** — Canonical workflow YAML
- **`.ui.json`** — Sidecar UI metadata (positions, viewport)
- **`.golden.json`** — Expected hydrated DTO JSON after mapping

## Directory Structure

```
src/WorkflowUi.ContractTests/
├── Fixtures/mapper/
│   ├── task/          (Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask)
│   ├── control/       (IfElse, Switch, ForEach, While)
│   ├── composition/   (Subflow, Timer, Container)
│   └── connections/   (multi-message-type connection fixtures)
└── Golden/
    ├── GoldenFileTests.cs
    ├── GoldenFixtureLoader.cs
    ├── GoldenFixtureManifestTests.cs
    └── GoldenSnapshotModel.cs
```

## Running Tests

```bash
dotnet test src/WorkflowUi.ContractTests --filter "Category=Golden"
```

## How Fixtures Work

1. The harness enumerates all `.yaml` files under `Fixtures/mapper/`
2. For each YAML file, it expects matching `.ui.json` and `.golden.json` companions
3. The golden JSON is compared against mapper output for structural fidelity
4. Sidecar files are validated to ensure they contain only UI metadata
5. Fixture IDs and counts are asserted for stability

## Updating Goldens

See [UpdateGoldenChecklist.md](./UpdateGoldenChecklist.md) for the review workflow.
