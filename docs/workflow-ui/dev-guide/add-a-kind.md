# Developer Guide — Adding a New Node Kind

This guide walks through adding a new node kind to the Workflow UI, covering the engine,
server mapping, UI registration, and testing.

## Checklist

Follow these steps in order. Each step must be completed before moving to the next.

### 1. Engine — Define the Node Kind

- [ ] Add a new `NodeKind` enum value in `ExecutionEngine/Nodes/Definitions/NodeKind.cs`
- [ ] Create a new node class implementing `INode` in `ExecutionEngine/Nodes/`
- [ ] Register the node in `NodeFactory.Create()` switch expression
- [ ] Add YAML deserialization support in `WorkflowLoader`

### 2. Server — DTO and Mapper

- [ ] Add the new kind to `NodeKindDto` enum in the contract project
- [ ] Create a mapper entry in `EngineDtoMapper` for the new kind
- [ ] Map engine-specific properties to/from the DTO
- [ ] Add round-trip parity test in `WorkflowUi.ContractTests`

### 3. UI — Schema and Palette

- [ ] Add the kind to `engine-contract.ts` TypeScript enum
- [ ] Create a Zod schema for the node's properties in `src/schemas/`
- [ ] Register the kind in `nodeKindRegistry.tsx` with icon, color, and label
- [ ] Add the kind to the palette entries

### 4. UI — Property Grid

- [ ] Add property grid fields for kind-specific properties
- [ ] Wire validation for required fields
- [ ] Add form tests verifying field rendering and validation

### 5. Testing

- [ ] Engine unit test: node executes correctly with valid config
- [ ] Contract test: round-trip YAML → DTO → YAML preserves all fields
- [ ] UI test: palette shows the new kind
- [ ] UI test: property grid renders kind-specific fields
- [ ] Lint, build, and type-check pass

### 6. Documentation

- [ ] Add a kind page in `docs/workflow-ui/user-guide/kinds/{kind}.md`
- [ ] Update the user guide index to include the new kind
- [ ] Add any kind-specific notes to the developer guide

## Architecture Overview

```
Engine (C#)                    Server (C#)                   UI (TypeScript)
┌──────────────┐              ┌──────────────┐              ┌──────────────┐
│ NodeKind     │──────────────│ NodeKindDto  │──────────────│ NodeKind     │
│ INode impl   │  Mapper      │ NodeDto      │  Contract    │ Zod schema   │
│ WorkflowDef  │──────────────│ WorkflowDto  │──────────────│ Registry     │
└──────────────┘              └──────────────┘              └──────────────┘
```

## Common Pitfalls

1. **Forgetting the mapper**: The server won't serialize the node without a mapper entry
2. **Missing Zod schema**: The property grid won't render without a schema
3. **Enum mismatch**: The TypeScript enum must match the C# enum name exactly
4. **Missing palette entry**: The kind won't appear in the palette without registration
