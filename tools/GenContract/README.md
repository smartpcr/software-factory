# GenContract — Engine-to-TypeScript contract generator

## Purpose

Generates `src/WorkflowUI/src/engine-contract.generated.ts` by reflecting over the `ExecutionEngine` assembly. The output is byte-stable across runs and platforms (LF line endings, alphabetical property order).

## Usage

```bash
# From the repository root:
dotnet run --project tools/GenContract -- --output src/WorkflowUI/src/engine-contract.generated.ts
```

The CI `contract-drift` job runs this generator and diffs the output against the committed file. If they diverge, CI fails with a message telling you to regenerate and commit.

## What it emits

1. **Enum string-literal unions** — 8 enums (ExecutionMode, JoinType, MessageType, NodeExecutionStatus, NodePriority, RetryStrategy, RuntimeType, WorkflowExecutionStatus).
2. **Policy DTOs** — `RetryPolicyDto` (7 props), `CircuitBreakerPolicyDto` (4 props).
3. **Node DTOs** — `NodeDtoBase` (11 props + discriminator), 13 per-kind interfaces extending the base, and a `NodeDto` discriminated union.
4. **NodeConnectionDto** — 9 properties.
5. **WorkflowDefinitionDto** — forward reference for `SubflowNodeDefinition`.

## Tests

```bash
dotnet test tools/GenContract.Tests
```
