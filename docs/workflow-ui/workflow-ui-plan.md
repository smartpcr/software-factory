# Workflow Designer UI — Definitive Implementation Plan

**Target:** Browser-based designer + live monitor for `src/ExecutionEngine` workflows.
**Status:** v1.6-iter2 — stage-file enhancement pass: readiness gates, stage-wide observability/perf/security guardrails, and template-completeness sweep across all 28 stage specs.
**Scope:** React + TypeScript SPA, ASP.NET Core host, YAML round-trip, SignalR telemetry.

## Changelog
- **v1.6-iter2** — Stage-file enhancement pass (GPT-model audit + broad executability upgrades):
  (1) Added `Definition of Ready`, checkbox legend, deliverable-symbol contract, and stage-wide observability / failure-mode / performance-budget guardrails to all 28 stage files so weaker specs now expose the same execution preconditions as the stronger mapper/monitor stages.
  (2) Filled the missing `Inputs` sections in Stage 2.2 / 2.3 / 2.4 steps (`2.2.2`, `2.2.3`, `2.2.4a`, `2.2.4b`, `2.3.2`, `2.3.3a`, `2.3.3b`, `2.4.2`, `2.4.3`, `2.4.4`, `2.4.5`, `2.4.6`) so every step now conforms to the 16-section template defined in `docs/workflow-ui/README.md`.
  (3) Added stage-wide failure-mode tables, p50/p95 budgets, accessibility checklists (UI-facing stages), security callouts, rollback guidance, and manual-verification focus lists to improve executability and evaluator consistency without changing the 9/28/97/164 structure.
  (4) Stage 6.1 fidelity fix: removed the stale reference to a nonexistent `ExecutionEngine.Events.IEventSource`; the bridge now cites `WorkflowExecutionContext.Events` / `.Progress` as the authoritative engine observables per `src/ExecutionEngine/Contexts/WorkflowExecutionContext.cs:118-125`.
  (5) Re-ran cross-file audits for `Depends on` references, effort totals, banned identifiers, and template completeness; totals remain unchanged at 97 steps / 164 d / 28 stages / 9 phases.
- **v1.5-iter1** — Active-improvement pass (eval-5 remediations + cross-file consistency hardening):
  (1) §2.8 MEDIUM resolved: removed stale sentence claiming `NodeProgressEvent` "carries a `ProgressUpdate` payload" — replaced with accurate description citing `NodeProgressEvent.cs:17-27` direct fields (`NodeInstanceId`, `ProgressPercent`, `Status`); clarified that `ProgressUpdate` is a separate workflow-level aggregate snapshot.
  (2) Step 6.1.2 LOW resolved: `Progress`/`EdgeActivated` wording clarified as a visualization decision (progress-triggered connections preserved for authoring round-trip but runtime monitor intentionally does not animate them as `EdgeActivated`).
  (3) Stage 6.3 — removed stale `ProgressUpdate` reference in Step 6.3.3 run-attach picker; replaced with accurate `NodeProgressEvent` field names.
  (4) §2.8 — added source-file citations for all event classes; added `ProgressUpdate.cs` disambiguation.
  (5) Appendix A Risk Register — added R7 (ProgressUpdate/NodeProgressEvent confusion) and R8 (MessageStatus.Completed vs Removed drift).
  (6) Cross-file audit: verified all 97 step IDs, all "Depends on" references, all engine-name usages across master plan and 28 stage files. Totals unchanged (97 steps / 164 d).
- **v1.4** — Eight cross-check remediations verified against source:
  (1) §2.2 / Step 5.1.1a language updated from "9 base properties" to "**11 base properties + abstract `RuntimeType` discriminator**" matching `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs` (11 settable public props + 1 abstract).
  (2) §2.8 and Step 6.1.1 — `NodeProgressEvent` payload corrected to the actual engine shape `{ NodeInstanceId: Guid, ProgressPercent: int (non-nullable), Status: string (non-nullable) }`; old `{ percent?, message? }` placeholder removed.
  (3) §2.1 — `WorkflowDefinition` namespace corrected to `ExecutionEngine.Workflow.WorkflowDefinition` (file lives at `src/ExecutionEngine/Workflow/WorkflowDefinition.cs`, not under `Nodes/Definitions/`).
  (4) §2.5 — dedicated connection-mapper cross-reference corrected from "§5.1.4" to "§5.1.2" (split already landed in v1.3).
  (5) §4.1 — stray RJSF / `react-jsonschema-form` mention purged; kind-specific form stack pinned to `react-hook-form + zod + shadcn` consistent with §1.1 and §4.4.
  (6) §0.2.2 / §2.2 — Policies folder path verified as `src/ExecutionEngine/Policies/` (not `Nodes/Policies/`); existing references already correct, confirmed no stale path remains.
  (7) §2.3 / Step 2.2.4b — `PowerShellTaskNodeDefinition.ScriptPath` engine quirk documented: declared `string? ScriptPath { get; set; } = string.Empty;` (nullable type **with empty-string default**, not `null`); mapper pins round-trip to preserve default.
  (8) §2.3 / Step 2.2.4a — `RuntimeType` enum confirmed to have **no `PowerShellScript` member** (13 members total: Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, IfElse, ForEach, While, Switch, Subflow, Timer, Container); `PowerShellScriptNodeDefinition` class maps to `RuntimeType.PowerShell` — negative test asserts no `PowerShellScript` literal leaks into TS unions.
  §4.2 panel-partition total relabelled 3 + 5 + 3 = **11 top-level base fields** (aligned with §4.2.3 count assertion). Totals unchanged (97 steps / 164 d).
- **v1.3** — CRITICAL fix: `RetryPolicy` and `CircuitBreakerPolicy` restored to the base `NodeDefinition` parity table (§0.2.2), to `NodeDtoBase` (§2.2), to the Policies panel partition (§4.2), and to the base mapper step (§11 Stage 5.1). HIGH fix: `NodeConnection` DTO rewritten in §2.5 with the full 9-property surface (`SourceNodeId, TargetNodeId, TriggerMessageType, Condition, IsEnabled, Metadata, Priority, SourcePort, TargetPort`); Phase 3 edge UX surfaces `Condition/IsEnabled/Priority`; a dedicated connection mapper step added in Phase 5. HIGH fix: Native engine event surface in §2.8 re-enumerated directly from `src/ExecutionEngine/Events/*.cs` — removed fabricated `WorkflowPaused` and `MessageSent`, added `NodeProgressEvent → NodeProgress`; `NodeSkipped` and `EdgeActivated` clearly labelled SYNTHETIC. All `YamlWorkflowLoader` mentions replaced with real types `WorkflowLoader` (load) and `WorkflowSerializer` (save) in §0.2.3, §1.2, Step 2.4.4, and Phase 5. Derived retry/circuit-breaker source citations: `src/ExecutionEngine/Policies/RetryPolicy.cs`, `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs`. Total steps/days unchanged (97 / 164).
- **v1.2** — CRITICAL fix: per-kind config shapes in §2.3, Phase 2, Phase 5.1.1 now cite exact `*NodeDefinition.cs` property names. HIGH fix: programmatic step/day recount (97 steps / 164 d) applied to phase index + each phase-start table. MEDIUM fixes: `[ ]` checkboxes restored on every phase/stage/step heading with roll-up rule; §0.1 hoists two inherited constraints (≤20 files; 100% unit coverage via coverlet) referenced from §3; audit of 5.2.1 → split into 5.2.1a/b.
- **v1.1** — Removed fabricated `NodePriority.Critical`; uniform step template; split oversized steps (2.2.1, 2.2.4, 2.3.3, 5.1.1); MessageType fidelity across port matrix, edge styling, schemas, fixtures; `NodeSkipped` documented as derived synthetic event.
- **v1.0** — Initial definitive synthesis of research + architecture + cross-critiques.

---

## §0 Ground Rules (apply to every phase/stage/step)

### §0.1 Uniform step template
Every `#### Step` body contains exactly these labelled paragraphs in this order:

- **Deliverables** — concrete code/doc artefacts produced.
- **Acceptance** — observable behaviour when the step is done.
- **Unit tests** — xUnit/MSTest cases; must compile and pass locally.
- **Integration tests** — end-to-end or Playwright cases when the step touches UI or crosses boundaries.
- **Depends on** — upstream step IDs.
- **Risks** — concrete failure modes and their mitigation.
- **Rubric** — 1-line self-score guide (e.g. "pass if enum parity test green, else 0").

**Two inherited constraints apply to every step (do not repeat inline):**
1. **File budget:** ≤ 20 files touched per step. If natural work exceeds this, split the step into `a/b/c…` sub-steps.
2. **Coverage:** 100% line coverage on new production code, enforced by coverlet threshold in CI.

### §0.1.1 Checkbox roll-up rule
Every `## §N Phase`, `### Stage X.Y`, and `#### Step X.Y.Z` header begins with one of `[ ]` (not started), `[~]` (in progress), `[x]` (done). **Roll-up:** a stage flips to `[x]` only when every child step is `[x]`; a phase flips to `[x]` only when every child stage is `[x]`. PRs touching this plan must update the markers atomically with the work.

### §0.2 Quality gates (global, before any phase can start)

#### §0.2.1 Engine-parity tests (CRITICAL, blocker for everything else)
A parity test fixture in `src/WorkflowUi.ContractTests` enumerates the following via reflection and asserts name-for-name / value-for-value equality with the UI's `engine-contract.ts`:
- `RuntimeType` → 13 members: `Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, IfElse, ForEach, While, Switch, Subflow, Timer, Container`.
- `NodeExecutionStatus` → 6 members: `Pending, Running, Completed, Failed, Cancelled, Skipped`.
- `WorkflowExecutionStatus` → 6 members: `Pending, Running, Completed, Failed, Cancelled, Paused`.
- `MessageType` → 5 members: `Complete, Fail, Progress, Next, Cancel`.
- `NodePriority` → 3 members: `Low, Normal, High` (no `Critical`).
- `JoinType` → 2 members: `Any, All`.
- `ExecutionMode` → 2 members: `Sequential, Parallel`.

If engine adds a value, the test fails red and blocks merge until `engine-contract.ts` is regenerated.

#### §0.2.2 DTO-parity tests
Same fixture reflects over every `*NodeDefinition.cs` in `src/ExecutionEngine/Nodes/Definitions/` and asserts each public property (name + CLR type) appears in the matching TypeScript DTO. Current authoritative shapes:

| C# type | Public properties (engine v-current) |
|---|---|
| `NodeDefinition` (base) | `NodeId, NodeName, Description, Configuration, JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy, CompensationNodeId, FallbackNodeId` |
| `Policies.RetryPolicy` (nested on base) | `Strategy (RetryStrategy), MaxAttempts (int), InitialDelay (TimeSpan), MaxDelay (TimeSpan), Multiplier (double), RetryOn (List<Type>), DoNotRetryOn (List<Type>)` |
| `Policies.CircuitBreakerPolicy` (nested on base) | `FailureThreshold (int), MinimumThroughput (int), OpenDuration (TimeSpan), HalfOpenSuccesses (int)` |
| `NoopNodeDefinition` | *(no extra props)* |
| `CSharpNodeDefinition` | `AssemblyPath, TypeName` |
| `CSharpScriptNodeDefinition` | `ScriptPath, ScriptContent` |
| `CSharpTaskNodeDefinition` | `ScriptContent, ExecutorTypeName, ExecutorAssemblyPath` |
| `PowerShellScriptNodeDefinition` *(RuntimeType.PowerShell)* | `ScriptPath, RequiredModules, ModulePaths` |
| `PowerShellTaskNodeDefinition` | `ScriptPath, ScriptContent, RequiredModules, ModulePaths` |
| `IfElseNodeDefinition` | `Condition` |
| `SwitchNodeDefinition` | `Expression, Cases` |
| `ForEachNodeDefinition` | `CollectionExpression, ItemVariableName` |
| `WhileNodeDefinition` | `ConditionExpression, MaxIterations` |
| `SubflowNodeDefinition` | `WorkflowFilePath, WorkflowDefinition, InputMappings, OutputMappings, Timeout, SkipValidation` |
| `TimerNodeDefinition` | `Schedule` |
| `ContainerNodeDefinition` | `ChildNodes, ChildConnections, ExecutionMode` |

**Note on retry/circuit-breaker:** these ARE on `NodeDefinition` (confirmed against `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs`) — both nullable (null = policy disabled). Their sub-fields are frozen by the nested parity rows above; `RetryPolicy.RetryOn`/`DoNotRetryOn` carry `List<Type>` which the UI surfaces as free-form string lists of fully-qualified type names (the server round-trips the strings through `Type.GetType`; the DTO hides that detail).

#### §0.2.3 YAML round-trip gate
Golden corpus in `src/WorkflowUi.ContractTests/Fixtures/*.yaml` must deserialize with engine `WorkflowLoader` and re-serialize via engine `WorkflowSerializer` (both in `src/ExecutionEngine/Workflow/`). The UI server never implements its own YAML; it is a thin wrapper around these two classes with identical YamlDotNet options. Canonicalised diff is comment-tolerant and preserves engine's key order. Blocks Phase 2 merge.

---

## §1 Executive Summary

Designer UI for ExecutionEngine workflows, grounded in-source. Two surfaces:
1. **Authoring**: draggable DAG, schema-driven property grid, live YAML round-trip.
2. **Monitoring**: attach to a running `WorkflowExecutor`, stream node/edge telemetry over SignalR.

### §1.1 Architecture at a glance
- **Client:** React 18 + TypeScript + React Flow + Zustand + Monaco (YAML). No inline CSS — all in Tailwind tokens driven by a theme file.
- **Server:** ASP.NET Core 8 minimal API + SignalR hub + `YamlDotNet` (same options as engine). The server is a thin adapter: it hosts the engine, never forks logic.
- **Contract:** `engine-contract.ts` generated from reflection during CI; drift caught by §0.2.1/§0.2.2 parity tests.
- **Sidecar:** `*.ui.json` holds UI-only state (positions, colours, collapsed groups). YAML remains pure engine.

### §1.2 Non-goals (v1)
- No multi-user collaborative editing (single-author, file-locked).
- No engine feature work (e.g. retry policies); UI only surfaces what engine has.
- No in-browser engine execution — always via server.

---

## §2 Data Model

### §2.1 Workflow DTO
Mirrors `ExecutionEngine.Workflow.WorkflowDefinition` 1:1 (source: `src/ExecutionEngine/Workflow/WorkflowDefinition.cs`). UI never introduces fields the engine can't round-trip.

### §2.2 Node DTO base
`NodeDtoBase` in `engine-contract.ts` = C# `NodeDefinition` base:
`nodeId, nodeName, description?, configuration?, joinType, priority, maxConcurrentExecutions, retryPolicy?, circuitBreakerPolicy?, compensationNodeId?, fallbackNodeId?` plus discriminator `runtimeType: RuntimeType`.

**Nested policy DTOs** (exact shapes from `src/ExecutionEngine/Policies/`):

```ts
type RetryStrategy = 'None' | 'Fixed' | 'Exponential' | 'Linear';

interface RetryPolicyDto {
  strategy: RetryStrategy;              // default 'None'
  maxAttempts: number;                  // 1..10, default 3
  initialDelay: string;                 // TimeSpan, default '00:00:01'
  maxDelay: string;                     // TimeSpan, default '00:01:00'
  multiplier: number;                   // default 2.0 (Exponential only)
  retryOn: string[];                    // FQTNs of exception types
  doNotRetryOn: string[];               // FQTNs; takes precedence
}

interface CircuitBreakerPolicyDto {
  failureThreshold: number;             // 0..100 percent, default 50
  minimumThroughput: number;            // default 10
  openDuration: string;                 // TimeSpan, default '00:00:30'
  halfOpenSuccesses: number;            // default 3
}
```

`retryPolicy` / `circuitBreakerPolicy` are both **nullable** on the base DTO; `null` means "policy disabled" (engine semantics). UI must preserve `null` ↔ absent-key on round-trip; an empty object is NOT equivalent to `null`.

### §2.3 Per-kind DTOs (exact engine shapes)

TypeScript discriminated union keyed on `runtimeType`. **Every shape below is derived from the matching `*NodeDefinition.cs` — do not invent fields.**

```ts
// RuntimeType.Noop — no extra fields
type NoopNode = NodeDtoBase & { runtimeType: 'Noop' };

// RuntimeType.CSharp — compiled assembly
type CSharpNode = NodeDtoBase & {
  runtimeType: 'CSharp';
  assemblyPath?: string;
  typeName?: string;
};

// RuntimeType.CSharpScript — Roslyn inline or file
type CSharpScriptNode = NodeDtoBase & {
  runtimeType: 'CSharpScript';
  scriptPath: string;     // engine default: ''
  scriptContent: string;  // engine default: ''
};

// RuntimeType.CSharpTask — inline script OR compiled executor (mutually exclusive)
type CSharpTaskNode = NodeDtoBase & {
  runtimeType: 'CSharpTask';
  scriptContent?: string;
  executorTypeName?: string;
  executorAssemblyPath?: string;
};

// RuntimeType.PowerShell → PowerShellScriptNodeDefinition (path-only)
type PowerShellNode = NodeDtoBase & {
  runtimeType: 'PowerShell';
  scriptPath: string;                            // REQUIRED in engine
  requiredModules?: string[];
  modulePaths?: Record<string, string>;
};

// RuntimeType.PowerShellTask — inline OR file
type PowerShellTaskNode = NodeDtoBase & {
  runtimeType: 'PowerShellTask';
  scriptPath?: string;
  scriptContent?: string;
  requiredModules?: string[];
  modulePaths?: Record<string, string>;
};

type IfElseNode = NodeDtoBase & {
  runtimeType: 'IfElse';
  condition: string;       // engine: single expression string
};

type SwitchNode = NodeDtoBase & {
  runtimeType: 'Switch';
  expression: string;
  cases: Record<string, string>;  // case-value -> target nodeId
};

type ForEachNode = NodeDtoBase & {
  runtimeType: 'ForEach';
  collectionExpression: string;
  itemVariableName: string;       // engine default: 'item'
};

type WhileNode = NodeDtoBase & {
  runtimeType: 'While';
  conditionExpression: string;
  maxIterations: number;          // engine default: 1000
};

type SubflowNode = NodeDtoBase & {
  runtimeType: 'Subflow';
  workflowFilePath: string;       // engine default: ''
  workflowDefinition?: WorkflowDto;  // inline override
  inputMappings: Record<string, string>;
  outputMappings: Record<string, string>;
  timeout: string;                // TimeSpan — ISO-8601 or hh:mm:ss; engine default '00:05:00'
  skipValidation: boolean;        // engine default false
};

type TimerNode = NodeDtoBase & {
  runtimeType: 'Timer';
  schedule: string;               // cron/interval string per engine parser
};

type ContainerNode = NodeDtoBase & {
  runtimeType: 'Container';
  childNodes: NodeDto[];
  childConnections: NodeConnectionDto[];
  executionMode: 'Sequential' | 'Parallel';
};
```

**UI-only extras** (e.g. `itemVariableDisplayName`, palette grouping hints, node colour) MUST live in the `*.ui.json` sidecar, never in the engine DTO.

### §2.4 Sidecar schema (`*.ui.json` v1)
```ts
interface UiSidecar {
  schemaVersion: 1;
  nodePositions: Record<string /*nodeId*/, { x: number; y: number }>;
  nodeColors?: Record<string, string /*hex*/>;
  collapsedContainers?: string[];
  viewport?: { x: number; y: number; zoom: number };
  // kind-agnostic overflow bag for UI-only labels
  labels?: Record<string, Record<string, string>>;
}
```

### §2.5 Connection DTO — 1:1 with engine `NodeConnection`

Confirmed against `src/ExecutionEngine/Workflow/NodeConnection.cs`. Full nine-property surface:

```ts
interface NodeConnectionDto {
  sourceNodeId: string;                 // required
  targetNodeId: string;                 // required
  triggerMessageType: MessageType;      // default 'Complete'
  condition?: string;                   // optional expression; empty == always active
  isEnabled: boolean;                   // default true; false = edge ignored at runtime
  metadata?: Record<string, unknown>;   // free-form bag round-tripped untouched
  priority: number;                     // default 0; higher = evaluated first
  sourcePort?: string;                  // null/empty = default output port
  targetPort?: string;                  // null/empty = default input port
}
```

UI implications:
- The canvas edge data object stores every one of these fields. None may be dropped on round-trip.
- `sourcePort` / `targetPort` replace the prior `sourcePortId` / `targetPortId` names — the wire name matches the engine property.
- `condition`, `isEnabled`, `priority` are editable in the edge property popover (Phase 3, Step 3.3.3).
- `metadata` is opaque to the UI; it survives round-trip via a passthrough mapper unit test.
- §5.1.2 adds a dedicated connection-mapper step asserting every field round-trips.

### §2.6 Port matrix (authoritative)
Every node kind has exactly one input port `in` and a fixed set of output ports keyed by `MessageType`:

| Kind | Output ports |
|---|---|
| Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, Timer | `Complete, Fail, Progress, Cancel` |
| IfElse | `Complete` (true), `Fail`, `Cancel`, + `Next` mapped to else branch via label |
| Switch | `Complete`, `Fail`, `Cancel` + one synthetic labelled port per case key |
| ForEach, While | `Next` (per iteration), `Complete` (end), `Fail`, `Cancel`, `Progress` |
| Subflow, Container | `Complete, Fail, Progress, Cancel` |

### §2.7 Edge styling
Solid for `Complete`, dashed for `Next`, red for `Fail`, amber for `Progress`, grey for `Cancel`.

### §2.8 Engine event surface (read-only in UI)
Emitted by `EngineEventBridge` → SignalR group per workflow instance. Enumerated from `src/ExecutionEngine/Events/*.cs`:

- **Native workflow-level** (base `WorkflowEvent`): `WorkflowStartedEvent, WorkflowCompletedEvent, WorkflowFailedEvent, WorkflowCancelledEvent`.
- **Native node-level** (base `NodeEvent`): `NodeStartedEvent, NodeCompletedEvent, NodeFailedEvent, NodeCancelledEvent, NodeProgressEvent`. `NodeProgressEvent` exposes `NodeInstanceId (Guid)`, `ProgressPercent (int)`, and `Status (string)` directly on the class (see `src/ExecutionEngine/Events/NodeProgressEvent.cs:17-27`) — it does **not** carry a `ProgressUpdate` payload. (`ProgressUpdate` is a separate workflow-level aggregate snapshot class in `src/ExecutionEngine/Events/ProgressUpdate.cs`.)
- **Synthetic (derived by the bridge, not emitted by the engine)**:
  - `NodeSkipped` — derived by the bridge when an IfElse/Switch branch is not taken (engine has no native `NodeSkippedEvent` or `Skipped` status transition on `NodeCompletedEvent`).
  - `EdgeActivated` — derived by matching each source terminal event's `MessageType` to each outgoing `NodeConnection.TriggerMessageType`.
- **Explicitly NOT present in the engine** (must not appear in any DTO, bridge switch, or test fixture): `WorkflowPausedEvent`, `MessageSentEvent`, `NodeSkippedEvent`. If the UI needs pause semantics it uses the `WorkflowExecutionStatus.Paused` enum value observed in state snapshots, not an event.
- `NodeCompletedEvent` carries no pass/fail status payload; the bridge treats it as `Completed` unless the synthetic skip rule fires.
- `NodeProgressEvent` is mapped to a `NodeProgress` telemetry DTO with `{ nodeId, nodeInstanceId: Guid, progressPercent: number (int, non-nullable), status: string (non-nullable), timestamp }` — fields taken verbatim from `src/ExecutionEngine/Events/NodeProgressEvent.cs` (`NodeInstanceId`, `ProgressPercent`, `Status`). No nullable `percent?` / `message?` — engine emits concrete values.

---

## §3 Quality Gate (per step)

Every step must, **in addition to the inherited constraints from §0.1**, satisfy:
- Engine-parity tests (§0.2.1, §0.2.2) still green on CI.
- YAML round-trip test (§0.2.3) still green for all fixtures whose surface this step touches.
- No test skipped, no `Assert.Inconclusive`. Flaky tests must be quarantined by issue number, not by `[Ignore]`.

---

## §4 Property Grid Field Partition

### §4.1 General panel (3 fields)
`nodeId` (read-only), `nodeName`, `description`.

### §4.2 Policies panel (5 fields — matches engine reality)
`joinType, priority, maxConcurrentExecutions, retryPolicy, circuitBreakerPolicy`. `retryPolicy` and `circuitBreakerPolicy` each open a nested editor (modal or inline collapsible) with the sub-fields frozen in §2.2 and §0.2.2; `null`/absent is preserved (empty ≠ null). Field count is 5 at the top level of the panel; nested policy editors don't count toward the partition total.

### §4.3 References & Configuration panel (3 fields)
`compensationNodeId, fallbackNodeId, configuration`.

General + Policies + References/Configuration = **11 top-level base fields** (3 + 5 + 3) — matches the 11 settable properties on `NodeDefinition` and is asserted by §4.2.3 count test.

### §4.4 Kind-specific panel
Rendered from the per-kind DTO (§2.3) via JSON Schema. One section per property, with dependent-visibility for mutually-exclusive pairs (e.g. `CSharpTask` script vs executor).

---

## §5 Phase Index (VERIFIED — 97 steps / 164 d)

| Phase | Title | Stages | Steps | Days |
|---|---|---:|---:|---:|
| 0 | Foundations & gates | 3 | 9 | 14 |
| 1 | Host, contract, CI | 3 | 9 | 16 |
| 2 | Node kinds, schemas, round-trip | 4 | 18 | 29 |
| 3 | Canvas, palette, connections | 3 | 10 | 17 |
| 4 | Property grid & validation | 3 | 9 | 18 |
| 5 | Mapper, fixtures, golden files | 3 | 12 | 10 |
| 6 | SignalR telemetry & live monitor | 3 | 9 | 19 |
| 7 | Auth, persistence, file ops | 3 | 11 | 22 |
| 8 | Packaging, docs, release | 3 | 10 | 19 |
| **Total** | | **28** | **97** | **164** |

Every phase-start table below restates its row and the rolling total so reviewers can re-verify without scrolling.

---

## §6 [ ] Phase 0 — Foundations & Gates

| Stage | Steps | Days |
|---|---:|---:|
| 0.1 Repo scaffolding | 3 | 4 |
| 0.2 Parity harness | 3 | 6 |
| 0.3 CI skeleton | 3 | 4 |

### [ ] Stage 0.1 — Repo scaffolding
#### [ ] Step 0.1.1 (2 d)
- **Deliverables:** `src/WorkflowUi.Server/` ASP.NET Core 8 skeleton, `src/WorkflowUI/` Vite+React+TS skeleton, `src/WorkflowUi.ContractTests/` xUnit project wired to `ExecutionEngine`.
- **Acceptance:** `dotnet build Workflow.sln` green; `npm run build` in `src/WorkflowUI/` green.
- **Unit tests:** one placeholder test per new project that asserts assembly loads.
- **Integration tests:** none.
- **Depends on:** nothing.
- **Risks:** solution filter regressions — mitigated by adding each new project to `Workflow.sln` and running `dotnet sln list`.
- **Rubric:** pass iff both builds succeed.

#### [ ] Step 0.1.2 (1 d)
- **Deliverables:** `docs/workflow-ui-plan.md` (this file) linked from root `readme.md`; `.editorconfig` coverage rule extended to new projects.
- **Acceptance:** markdown lint clean; editorconfig rules applied to new `.cs`.
- **Unit tests:** editorconfig self-check via `dotnet format --verify-no-changes`.
- **Integration tests:** none.
- **Depends on:** 0.1.1.
- **Risks:** none.
- **Rubric:** pass iff `dotnet format --verify-no-changes` returns 0.

#### [ ] Step 0.1.3 (1 d)
- **Deliverables:** prettier + eslint + vitest for `src/WorkflowUI/`.
- **Acceptance:** `npm run lint` and `npm run test` both pass on empty suite.
- **Unit tests:** one passing placeholder.
- **Integration tests:** none.
- **Depends on:** 0.1.1.
- **Risks:** none.
- **Rubric:** pass iff lint+test both return 0.

### [ ] Stage 0.2 — Parity harness
#### [ ] Step 0.2.1 (2 d)
- **Deliverables:** reflection-based enum parity test (`EnumParityTests.cs`) covering the 7 enums listed in §0.2.1.
- **Acceptance:** test compares engine enum to a checked-in TS fixture JSON produced by `npm run gen:contract`.
- **Unit tests:** one fact per enum; parameterised over members.
- **Integration tests:** none.
- **Depends on:** 0.1.1.
- **Risks:** alphabetic order mismatch — tests compare as sets, then separately assert ordinal values for `NodePriority`, `JoinType`.
- **Rubric:** pass iff `dotnet test --filter Category=EnumParity` green.

#### [ ] Step 0.2.2 (2 d)
- **Deliverables:** reflection-based DTO parity test covering every `*NodeDefinition.cs`; emits TypeScript stubs into `src/WorkflowUI/src/engine-contract.generated.ts`.
- **Acceptance:** generator + diff check fails if a property is added on the C# side but not reflected in TS.
- **Unit tests:** one fact per node definition class.
- **Integration tests:** none.
- **Depends on:** 0.2.1.
- **Risks:** nullable-reference-type differences — test uses `NullabilityInfoContext` and asserts optional ↔ `?:` mapping.
- **Rubric:** pass iff parity test green + generated file byte-equal to checked-in copy.

#### [ ] Step 0.2.3 (2 d)
- **Deliverables:** YAML round-trip test reading `Fixtures/noop-minimal.yaml` through engine loader and server serializer.
- **Acceptance:** canonicalised byte-match.
- **Unit tests:** one fact for fixture.
- **Integration tests:** none.
- **Depends on:** 0.2.2.
- **Risks:** YamlDotNet default options diverge — enforce same options via shared static.
- **Rubric:** pass iff round-trip test green.

### [ ] Stage 0.3 — CI skeleton
#### [ ] Step 0.3.1 (2 d)
- **Deliverables:** GitHub Actions workflow `ci-workflow-ui.yml` running dotnet build/test, npm build/test, coverage threshold (100% on new projects).
- **Acceptance:** CI red if any gate fails.
- **Unit tests:** meta-test that parses workflow YAML for required steps.
- **Integration tests:** one PR-preview run.
- **Depends on:** 0.2.1, 0.2.2, 0.2.3.
- **Risks:** coverlet 100% threshold flaky — exclude generated files explicitly.
- **Rubric:** pass iff CI green on empty PR.

#### [ ] Step 0.3.2 (1 d)
- **Deliverables:** pre-commit hook (`lefthook.yml` or husky) running lint + format check on staged files.
- **Acceptance:** hook blocks on failed lint.
- **Unit tests:** not applicable.
- **Integration tests:** manual hook test logged in PR description.
- **Depends on:** 0.1.3.
- **Risks:** Windows cmd incompatibility — use node-launched hook runner.
- **Rubric:** pass iff hook fails on intentional lint violation.

#### [ ] Step 0.3.3 (1 d)
- **Deliverables:** branch-protection rule doc snippet + codeowners entry for `docs/workflow-ui-plan.md`.
- **Acceptance:** merged to main only with codeowner approval.
- **Unit tests:** not applicable.
- **Integration tests:** not applicable.
- **Depends on:** 0.3.1.
- **Risks:** codeowners rule scope drift — cover by `workflow-ui-plan.md` path.
- **Rubric:** pass iff codeowners entry present.

---

## §7 [ ] Phase 1 — Host, Contract, CI

| Stage | Steps | Days |
|---|---:|---:|
| 1.1 Server host | 3 | 6 |
| 1.2 Contract generation | 3 | 5 |
| 1.3 CI wiring | 3 | 5 |

### [ ] Stage 1.1 — Server host
#### [ ] Step 1.1.1 (2 d)
- **Deliverables:** minimal API endpoints `GET /api/health`, `GET /api/engine/version`.
- **Acceptance:** returns engine assembly version.
- **Unit tests:** controller tests via `WebApplicationFactory`.
- **Integration tests:** none.
- **Depends on:** 0.1.1.
- **Risks:** assembly version resolution differs per build — use `typeof(WorkflowExecutor).Assembly.GetName().Version`.
- **Rubric:** pass iff `/api/engine/version` returns non-empty string.

#### [ ] Step 1.1.2 (2 d)
- **Deliverables:** DI registration of `IWorkflowLoader`, `IWorkflowExecutorFactory` from engine.
- **Acceptance:** ASP.NET DI graph boots.
- **Unit tests:** service-provider resolution test per registered interface.
- **Integration tests:** none.
- **Depends on:** 1.1.1.
- **Risks:** engine has internal statics — guard with scoped lifetimes.
- **Rubric:** pass iff DI tests green.

#### [ ] Step 1.1.3 (2 d)
- **Deliverables:** problem-details error middleware, correlation id, request logging.
- **Acceptance:** 500 errors return RFC-7807 payloads.
- **Unit tests:** middleware unit tests with injected throwing handler.
- **Integration tests:** one `WebApplicationFactory` test.
- **Depends on:** 1.1.2.
- **Risks:** middleware ordering bugs — assert ordering via integration test.
- **Rubric:** pass iff problem-details response validated.

### [ ] Stage 1.2 — Contract generation
#### [ ] Step 1.2.1 (1 d)
- **Deliverables:** `tools/GenContract` console app reflecting engine enums + DTOs → TS.
- **Acceptance:** `dotnet run --project tools/GenContract` overwrites `src/WorkflowUI/src/engine-contract.generated.ts`.
- **Unit tests:** generator unit test produces stable sorted output.
- **Integration tests:** none.
- **Depends on:** 0.2.2.
- **Risks:** non-deterministic ordering — sort by metadata token.
- **Rubric:** pass iff re-run is idempotent (diff empty).

#### [ ] Step 1.2.2 (2 d)
- **Deliverables:** CI step that re-runs generator and fails if diff non-empty.
- **Acceptance:** contract drift breaks build.
- **Unit tests:** meta-test asserting CI step exists.
- **Integration tests:** intentional-drift PR test.
- **Depends on:** 1.2.1.
- **Risks:** false positives from whitespace — generator emits with `lf` newlines.
- **Rubric:** pass iff drift PR fails CI.

#### [ ] Step 1.2.3 (2 d)
- **Deliverables:** hand-written `engine-contract.ts` facade re-exporting generated types and adding UI-only helpers.
- **Acceptance:** all UI imports go through facade.
- **Unit tests:** `eslint` rule banning direct import of generated file.
- **Integration tests:** none.
- **Depends on:** 1.2.1.
- **Risks:** developer bypass — eslint rule must be error-level.
- **Rubric:** pass iff eslint fails on direct import.

### [ ] Stage 1.3 — CI wiring
#### [ ] Step 1.3.1 (2 d)
- **Deliverables:** unit coverage report publishing (coverlet → lcov → codecov action).
- **Acceptance:** PR comment shows coverage.
- **Unit tests:** meta-test validates yml.
- **Integration tests:** none.
- **Depends on:** 0.3.1.
- **Risks:** codecov token scope — store as environment secret, not per-workflow.
- **Rubric:** pass iff coverage comment present on PR.

#### [ ] Step 1.3.2 (2 d)
- **Deliverables:** Playwright project scaffold + first smoke test.
- **Acceptance:** Playwright runs headless in CI.
- **Unit tests:** n/a.
- **Integration tests:** smoke test navigates to `/health` UI page.
- **Depends on:** 1.1.1.
- **Risks:** browser download flake — pin Playwright cache via GH action.
- **Rubric:** pass iff smoke test green.

#### [ ] Step 1.3.3 (1 d)
- **Deliverables:** status badges in `readme.md`.
- **Acceptance:** badges render on GitHub UI.
- **Unit tests:** n/a.
- **Integration tests:** n/a.
- **Depends on:** 1.3.1, 1.3.2.
- **Risks:** wrong branch reference — parameterise by repo default branch.
- **Rubric:** pass iff badges show live status.

---

## §8 [ ] Phase 2 — Node Kinds, Schemas, Round-Trip

| Stage | Steps | Days |
|---|---:|---:|
| 2.1 Base DTO + discriminator | 2 | 3 |
| 2.2 Task-family kinds (Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask) | 6 | 7.5 |
| 2.3 Control-flow kinds (IfElse, Switch, ForEach, While) | 4 | 7.5 |
| 2.4 Composition kinds + round-trip (Subflow, Timer, Container, YAML, sidecar, port matrix) | 6 | 11 |

> File-budget discipline (§0.1 constraint 1): each step below touches at most one engine kind's schema + one UI schema + tests. If a kind's real property surface exceeds that, it is pre-split into `a/b`.

### [ ] Stage 2.1 — Base DTO + discriminator
#### [ ] Step 2.1.1 (1 d)
- **Deliverables:** `NodeDtoBase` TS type matching the **11 base properties + abstract `RuntimeType` discriminator** from `NodeDefinition.cs` (`NodeId, NodeName, Description, Configuration, JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy, CompensationNodeId, FallbackNodeId` + `RuntimeType`); Zod schema.
- **Acceptance:** Zod parse round-trips a fixture JSON.
- **Unit tests:** parse + stringify fact per required field.
- **Integration tests:** none.
- **Depends on:** 1.2.3.
- **Risks:** Configuration dict needs `Record<string, unknown>` — add Zod `z.record(z.unknown())`.
- **Rubric:** pass iff base parity fact green.

#### [ ] Step 2.1.2 (2 d)
- **Deliverables:** discriminated-union plumbing keyed on `runtimeType` + exhaustive `assertNever` helper.
- **Acceptance:** TS compile fails if a new kind is added without a branch.
- **Unit tests:** type-level tests via `tsd`.
- **Integration tests:** none.
- **Depends on:** 2.1.1.
- **Risks:** none.
- **Rubric:** pass iff exhaustiveness test green.

### [ ] Stage 2.2 — Task-family kinds
#### [ ] Step 2.2.1a — Noop (1 d)
- **Deliverables:** `NoopNode` DTO (base only, no extra props), Zod schema, palette entry.
- **Acceptance:** round-trip YAML fixture `noop-minimal.yaml` byte-match.
- **Unit tests:** Zod parse; parity reflection test covers zero extra props.
- **Integration tests:** UI drag from palette → canvas renders Noop tile.
- **Depends on:** 2.1.2.
- **Risks:** none.
- **Rubric:** pass iff fixture round-trips.

#### [ ] Step 2.2.1b — CSharp (compiled assembly) (1 d)
- **Deliverables:** `CSharpNode` with `assemblyPath`, `typeName` only (per `CSharpNodeDefinition.cs`).
- **Acceptance:** property grid shows exactly those two fields in the kind-specific panel.
- **Unit tests:** Zod schema allows both fields optional; DTO parity test enumerates `AssemblyPath`, `TypeName`.
- **Integration tests:** Playwright asserts the two inputs appear on selection.
- **Depends on:** 2.2.1a.
- **Risks:** developers confuse this with `CSharpTask` — label the palette entry "CSharp (compiled DLL)".
- **Rubric:** pass iff panel shows 2 kind-specific fields.

#### [ ] Step 2.2.2 — CSharpScript (1.5 d)
- **Deliverables:** `CSharpScriptNode` with `scriptPath` and `scriptContent` (both strings per `CSharpScriptNodeDefinition.cs`, engine defaults `""`).
- **Acceptance:** Monaco C# editor opens when `scriptContent` selected; file-picker for `scriptPath`.
- **Unit tests:** Zod both strings with empty-string default; serializer omits empty strings only if engine does.
- **Integration tests:** round-trip fixture with both fields populated.
- **Depends on:** 2.2.1b.
- **Risks:** engine treats `scriptContent` + `scriptPath` as both-allowed; UI must not silently blank one.
- **Rubric:** pass iff both fields independently editable and preserved.

#### [ ] Step 2.2.3 — CSharpTask (1 d)
- **Deliverables:** `CSharpTaskNode` with `scriptContent?`, `executorTypeName?`, `executorAssemblyPath?` (per `CSharpTaskNodeDefinition.cs`); UI enforces "inline script XOR executor" via dependent visibility.
- **Acceptance:** toggle between "inline" and "executor" tabs.
- **Unit tests:** Zod refine ensures at least one of the two modes populated.
- **Integration tests:** fixture per mode.
- **Depends on:** 2.2.2.
- **Risks:** engine allows both populated — UI warns but does not block.
- **Rubric:** pass iff warning shows when both set.

#### [ ] Step 2.2.4a — PowerShell (script path) (1.5 d)
- **Deliverables:** `PowerShellNode` with **`scriptPath` (required)**, `requiredModules?`, `modulePaths?` — exact shape of `PowerShellScriptNodeDefinition.cs`. No inline-script field.
- **Acceptance:** file picker required; module-list grid.
- **Unit tests:** Zod requires non-empty `scriptPath`; schema rejects inline script. **Negative parity test:** asserts no `PowerShellScript` literal in any generated TS union — the `RuntimeType` enum (13 members: `Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, IfElse, ForEach, While, Switch, Subflow, Timer, Container`) has **no `PowerShellScript` member**; `PowerShellScriptNodeDefinition` maps to `RuntimeType.PowerShell`.
- **Integration tests:** fixture with 2 modules + 1 modulePath entry.
- **Depends on:** 2.2.3.
- **Risks:** palette ambiguity with `PowerShellTask` — tooltip clarifies "path-only".
- **Rubric:** pass iff parity test passes and schema forbids unknown props.

#### [ ] Step 2.2.4b — PowerShellTask (1.5 d)
- **Deliverables:** `PowerShellTaskNode` with `scriptPath?`, `scriptContent?`, `requiredModules?`, `modulePaths?` (per `PowerShellTaskNodeDefinition.cs`). **Engine quirk pinned:** `ScriptPath` is declared `public string? ScriptPath { get; set; } = string.Empty;` — nullable type with **empty-string default**. Round-trip must preserve the default (omitted in YAML → `""` on load → omitted on save if untouched), never silently coerce to `null`.
- **Acceptance:** "inline script" OR "script path" chooser; either allowed, engine validates at run.
- **Unit tests:** Zod refine — at least one of the two must be set. Quirk test: default-constructed DTO serialises `ScriptPath` identical to engine's C# default (empty string, not null).
- **Integration tests:** fixture per mode.
- **Depends on:** 2.2.4a.
- **Risks:** user sets both — UI warns on save.
- **Rubric:** pass iff both modes round-trip distinctly.

### [ ] Stage 2.3 — Control-flow kinds
#### [ ] Step 2.3.1 — IfElse (2 d)
- **Deliverables:** `IfElseNode` with single `condition: string` (per `IfElseNodeDefinition.cs`). Branch targets expressed only via edges, never as DTO fields.
- **Acceptance:** property grid has exactly one expression field plus Monaco snippet helper.
- **Unit tests:** Zod rejects any `trueBranch`/`falseBranch` DTO props (parity lock).
- **Integration tests:** fixture with true + false edges using `Complete` vs `Next` message types.
- **Depends on:** 2.1.2.
- **Risks:** legacy diagrams included branch-id fields — migration doc notes removal.
- **Rubric:** pass iff parity test enumerates only `Condition`.

#### [ ] Step 2.3.2 — Switch (2.5 d)
- **Deliverables:** `SwitchNode` with `expression: string` and `cases: Record<string, string>` where value = target nodeId (per `SwitchNodeDefinition.cs`).
- **Acceptance:** property grid has an expression field + editable dictionary (add/remove case rows).
- **Unit tests:** Zod enforces value of each case references an existing nodeId (refine at workflow-validation time, not at DTO-parse time).
- **Integration tests:** fixture with 3 cases + default fallback via `Fail` edge.
- **Depends on:** 2.3.1.
- **Risks:** UI and engine differ on case-insensitive matching — UI surfaces engine behaviour via tooltip.
- **Rubric:** pass iff parity test + fixture round-trip.

#### [ ] Step 2.3.3a — ForEach (1.5 d)
- **Deliverables:** `ForEachNode` with `collectionExpression, itemVariableName` (engine default `"item"`), per `ForEachNodeDefinition.cs`. No `bodyNodeId` on the DTO; the body is expressed by the `Next` edge target.
- **Acceptance:** property grid shows two fields; palette tooltip explains "body = node reachable via Next edge".
- **Unit tests:** Zod default for `itemVariableName` = `"item"`; parity reflection enumerates exactly these.
- **Integration tests:** fixture iterates 3-item array; SignalR emits `Next` 3 times then `Complete`.
- **Depends on:** 2.3.2.
- **Risks:** users drag "body" shapes into grid expecting a slot — palette instead forces the `Next` edge pattern.
- **Rubric:** pass iff parity + fixture tests green.

#### [ ] Step 2.3.3b — While (1.5 d)
- **Deliverables:** `WhileNode` with `conditionExpression` + `maxIterations: number` (engine default `1000`), per `WhileNodeDefinition.cs`.
- **Acceptance:** property grid has condition + numeric spinner; Zod caps at `int32`.
- **Unit tests:** Zod default 1000; schema requires non-empty expression.
- **Integration tests:** fixture loops 5 times then exits; `Next` emitted 5 times.
- **Depends on:** 2.3.3a.
- **Risks:** infinite loop if `maxIterations` mis-set — warning below 1 or above 1e6.
- **Rubric:** pass iff parity + fixture green.

### [ ] Stage 2.4 — Composition, round-trip, sidecar
#### [ ] Step 2.4.1 — Subflow (3 d)
- **Deliverables:** `SubflowNode` with `workflowFilePath`, `workflowDefinition?` (inline override), `inputMappings`, `outputMappings`, `timeout` (ISO-8601 TimeSpan string, engine default `"00:05:00"`), `skipValidation` (default `false`). All names from `SubflowNodeDefinition.cs`.
- **Acceptance:** property grid has a file picker (disabled when inline populated), mapping grids, duration input, boolean toggle.
- **Unit tests:** Zod TimeSpan parser accepts `hh:mm:ss` and ISO-8601 durations.
- **Integration tests:** fixture with external file; fixture with inline definition; both round-trip.
- **Depends on:** 2.3.3b.
- **Risks:** inline vs file conflict — UI blocks save if both set.
- **Rubric:** pass iff parity + two fixtures.

#### [ ] Step 2.4.2 — Timer (2 d)
- **Deliverables:** `TimerNode` with single `schedule: string` (per `TimerNodeDefinition.cs`). No `cron` vs `delayMs` split at DTO level — string is opaque to UI; tooltip shows engine parser examples.
- **Acceptance:** property grid shows one string input plus a helper for cron or interval syntax.
- **Unit tests:** Zod requires non-empty; parity test enumerates exactly `Schedule`.
- **Integration tests:** fixture with cron `"*/5 * * * *"` and fixture with interval `"00:00:30"`.
- **Depends on:** 2.4.1.
- **Risks:** UI previews wrong next-run time — call engine `/api/timer/preview` endpoint (Phase 6) instead of re-implementing parser.
- **Rubric:** pass iff parity + two fixtures.

#### [ ] Step 2.4.3 — Container (1 d)
- **Deliverables:** `ContainerNode` with `childNodes`, `childConnections`, `executionMode: 'Sequential'|'Parallel'` (per `ContainerNodeDefinition.cs`, engine default `Sequential`).
- **Acceptance:** container node rendered as group on canvas with expand/collapse.
- **Unit tests:** Zod recursive types using `z.lazy`.
- **Integration tests:** fixture with 2 child nodes in Parallel mode.
- **Depends on:** 2.4.2.
- **Risks:** recursion blow-up — depth-limit at 16 with explicit error.
- **Rubric:** pass iff parity + fixture.

#### [ ] Step 2.4.4 — YAML round-trip server (2 d)
- **Deliverables:** `/api/workflows/{id}/yaml` GET/PUT; GET delegates to engine `WorkflowSerializer`, PUT delegates to engine `WorkflowLoader` (both in `src/ExecutionEngine/Workflow/`). No YAML parsing or emission in UI server code.
- **Acceptance:** PUT of GET output is idempotent (canonicalised compare); server startup asserts the loader/serializer types exist via reflection.
- **Unit tests:** per-kind round-trip fact; reflection probe for `WorkflowLoader` + `WorkflowSerializer` type identity.
- **Integration tests:** all fixtures cycle through server.
- **Depends on:** 2.4.1, 2.4.2, 2.4.3.
- **Risks:** YamlDotNet option drift — both ends share the engine's options surface via `WorkflowSerializer`/`WorkflowLoader` static configuration.
- **Rubric:** pass iff all fixtures idempotent and reflection probe green.

#### [ ] Step 2.4.5 — Sidecar I/O (2 d)
- **Deliverables:** `/api/workflows/{id}/ui` GET/PUT for `*.ui.json`.
- **Acceptance:** missing sidecar returns empty schema-valid object; PUT validates against §2.4 schema.
- **Unit tests:** schema-validation + default-populate.
- **Integration tests:** reorder nodes on canvas → save → reload → positions preserved.
- **Depends on:** 2.4.4.
- **Risks:** sidecar divergence — server rejects unknown keys.
- **Rubric:** pass iff Playwright reload test preserves positions.

#### [ ] Step 2.4.6 — Port matrix enforcement (1 d)
- **Deliverables:** port-matrix table (§2.6) materialised as a const in `src/WorkflowUI/src/ports.ts`; UI hides/shows ports per node kind.
- **Acceptance:** attempting to create a `Next` edge from a `Noop` is blocked.
- **Unit tests:** matrix snapshot test.
- **Integration tests:** Playwright tries invalid edge, receives inline error.
- **Depends on:** 2.4.3.
- **Risks:** matrix drift from engine — unit test asserts matrix keys == `MessageType` members.
- **Rubric:** pass iff invalid-edge attempt blocked.

---

## §9 [ ] Phase 3 — Canvas, Palette, Connections

| Stage | Steps | Days |
|---|---:|---:|
| 3.1 Canvas primitives | 4 | 6 |
| 3.2 Palette & drag | 3 | 6 |
| 3.3 Connections | 3 | 5 |

### [ ] Stage 3.1 — Canvas primitives
#### [ ] Step 3.1.1 (1 d)
- **Deliverables:** React Flow wrapper component with pan/zoom disabled scroll-jacking.
- **Acceptance:** canvas renders empty grid with snap.
- **Unit tests:** renders without crash.
- **Integration tests:** Playwright pan/zoom smoke.
- **Depends on:** 2.1.2.
- **Risks:** browser scroll hijack — guard at wrapper level.
- **Rubric:** pass iff pan/zoom test green.

#### [ ] Step 3.1.2 (2 d)
- **Deliverables:** custom node renderer reading kind-specific icon from registry.
- **Acceptance:** each of 13 kinds has distinct icon.
- **Unit tests:** snapshot per kind.
- **Integration tests:** visual diff baseline.
- **Depends on:** 3.1.1.
- **Risks:** icon licence — use open-source Lucide set only.
- **Rubric:** pass iff 13 snapshots stable.

#### [ ] Step 3.1.3 (2 d)
- **Deliverables:** per-node status badge bound to `NodeExecutionStatus`.
- **Acceptance:** badge colour matches status.
- **Unit tests:** one per status value.
- **Integration tests:** Playwright toggles state via devtools.
- **Depends on:** 3.1.2.
- **Risks:** colour-blind accessibility — dual-encode via icon.
- **Rubric:** pass iff 6 status snapshots pass.

#### [ ] Step 3.1.4 (1 d)
- **Deliverables:** minimap + fit-view controls.
- **Acceptance:** fit-view recenters on all nodes.
- **Unit tests:** n/a.
- **Integration tests:** Playwright.
- **Depends on:** 3.1.1.
- **Risks:** none.
- **Rubric:** pass iff recenters.

### [ ] Stage 3.2 — Palette & drag
#### [ ] Step 3.2.1 (3 d)
- **Deliverables:** palette listing all 13 kinds grouped Task / Control / Composition.
- **Acceptance:** drag-and-drop creates matching DTO with engine defaults.
- **Unit tests:** factory per kind returns schema-valid DTO.
- **Integration tests:** Playwright drops every kind, saves, reloads — all 13 round-trip.
- **Depends on:** 2.4.6.
- **Risks:** default values drift from engine — factory reads from shared defaults const.
- **Rubric:** pass iff all 13 round-trip.

#### [ ] Step 3.2.2 (2 d)
- **Deliverables:** keyboard creation (hotkey `N`) with kind picker.
- **Acceptance:** hotkey opens command palette.
- **Unit tests:** key-handler unit test.
- **Integration tests:** Playwright hotkey test.
- **Depends on:** 3.2.1.
- **Risks:** hotkey conflict — scope to canvas focus.
- **Rubric:** pass iff hotkey test green.

#### [ ] Step 3.2.3 (1 d)
- **Deliverables:** palette search filter.
- **Acceptance:** typing filters entries.
- **Unit tests:** filter logic.
- **Integration tests:** Playwright.
- **Depends on:** 3.2.1.
- **Risks:** none.
- **Rubric:** pass iff filter test green.

### [ ] Stage 3.3 — Connections
#### [ ] Step 3.3.1 (1 d)
- **Deliverables:** edge creation with port-matrix enforcement.
- **Acceptance:** only valid source→target ports connect.
- **Unit tests:** per-kind matrix.
- **Integration tests:** Playwright invalid-edge rejection.
- **Depends on:** 2.4.6, 3.2.1.
- **Risks:** none.
- **Rubric:** pass iff invalid-edge rejected.

#### [ ] Step 3.3.2 (2 d)
- **Deliverables:** edge styling per §2.7.
- **Acceptance:** colour/dash matches MessageType.
- **Unit tests:** snapshot per MessageType.
- **Integration tests:** visual diff baseline.
- **Depends on:** 3.3.1.
- **Risks:** colour clash — verify against theme tokens.
- **Rubric:** pass iff 5 snapshots pass.

#### [ ] Step 3.3.3 (2 d)
- **Deliverables:** edge re-routing + deletion with undo; edge property popover surfacing `condition` (expression string), `isEnabled` (toggle), `priority` (int). `metadata` is passthrough (not editable in v1, round-trips untouched); `sourcePort`/`targetPort` are read-only (derived from port matrix); `triggerMessageType` is styled (§2.7) not edited here.
- **Acceptance:** Ctrl+Z restores edge; condition/isEnabled/priority persist through YAML round-trip via sidecar-free `NodeConnection` fields.
- **Unit tests:** undo store; edge-property round-trip fact (all 9 `NodeConnectionDto` fields preserved including untouched `metadata`).
- **Integration tests:** Playwright undo; Playwright edits condition/isEnabled/priority → save → reload → values preserved.
- **Depends on:** 3.3.2.
- **Risks:** undo over non-undoable operation — whitelist operations; condition expression parsing is server-side (`ConditionEvaluator`) — UI validates non-empty only.
- **Rubric:** pass iff undo test green AND edge-property round-trip test green.

---

## §10 [ ] Phase 4 — Property Grid & Validation

| Stage | Steps | Days |
|---|---:|---:|
| 4.1 Grid framework | 3 | 7 |
| 4.2 Panel partition | 3 | 7 |
| 4.3 Validation surface | 3 | 4 |

### [ ] Stage 4.1 — Grid framework
#### [ ] Step 4.1.1 (3 d)
- **Deliverables:** kind-specific form stack = `react-hook-form + zod + shadcn` (same as §1.1 / §4.4) driven by the per-kind DTO (§2.3) — NOT `react-jsonschema-form`. Zod schema is the single source of truth; form fields are rendered via a thin shadcn-based renderer keyed on the discriminator.
- **Acceptance:** schema per DTO renders correct widgets.
- **Unit tests:** per-kind render snapshot.
- **Integration tests:** Playwright edit + save round-trip.
- **Depends on:** 2.4.6.
- **Risks:** avoid heavy schema-form libs (RJSF rejected) — keep the Zod-driven shadcn renderer minimal.
- **Rubric:** pass iff 13 kinds render.

#### [ ] Step 4.1.2 (2 d)
- **Deliverables:** dependent-visibility rules (CSharpTask modes, Subflow path-vs-inline).
- **Acceptance:** toggling mode hides/shows correct fields.
- **Unit tests:** rule engine unit tests.
- **Integration tests:** Playwright toggles.
- **Depends on:** 4.1.1.
- **Risks:** rule explosion — keep rules in per-kind config files, not in code.
- **Rubric:** pass iff rule tests green.

#### [ ] Step 4.1.3 (2 d)
- **Deliverables:** Monaco editors for expression/script fields.
- **Acceptance:** syntax highlight for C#/PowerShell/YAML.
- **Unit tests:** editor mount test.
- **Integration tests:** Playwright type into editor.
- **Depends on:** 4.1.1.
- **Risks:** Monaco bundle size — code-split by kind.
- **Rubric:** pass iff editors mount lazily.

### [ ] Stage 4.2 — Panel partition
#### [ ] Step 4.2.1 (4 d)
- **Deliverables:** Policies panel (5 top-level fields: JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy). `RetryPolicy` opens a nested editor with 7 sub-fields (§2.2); `CircuitBreakerPolicy` opens a nested editor with 4 sub-fields (§2.2). `null` vs empty-object distinction preserved.
- **Acceptance:** tabbed section "Policies" appears for every kind; nested editors open on demand; `null` policy round-trips to absent key.
- **Unit tests:** top-level panel field count == 5 per kind; nested RetryPolicy editor renders 7 fields; nested CircuitBreakerPolicy editor renders 4 fields; `null` ↔ absent-key contract test.
- **Integration tests:** Playwright edits each top-level field and each nested sub-field, round-trips via YAML gate.
- **Depends on:** 4.1.1.
- **Risks:** nested editor UX — use collapsible with "Disabled (null)" toggle at the top.
- **Rubric:** pass iff 5-field top-level assertion + nested sub-field counts green.

#### [ ] Step 4.2.2 (2 d)
- **Deliverables:** References & Configuration panel (3 fields: CompensationNodeId, FallbackNodeId, Configuration).
- **Acceptance:** tabbed section shows 3 fields; node-id pickers validate existence.
- **Unit tests:** panel field count == 3; picker rejects invalid id.
- **Integration tests:** Playwright sets comp/fallback, round-trips.
- **Depends on:** 4.2.1.
- **Risks:** node-id existence must be live — picker reads Zustand store.
- **Rubric:** pass iff 3-field + picker tests green.

#### [ ] Step 4.2.3 (1 d)
- **Deliverables:** count-assertion test covering partition totals (3 general + 5 policies top-level + 3 refs/config = 11 top-level fields, plus kind-specific variable; nested RetryPolicy = 7, nested CircuitBreakerPolicy = 4 covered in separate assertions).
- **Acceptance:** test fails if any section's top-level count drifts or if nested policy editor sub-field counts drift.
- **Unit tests:** one per section + one per nested policy editor.
- **Integration tests:** n/a.
- **Depends on:** 4.2.2.
- **Risks:** count tests brittle — cover only the invariant sections and nested sub-field cardinality.
- **Rubric:** pass iff all counts match.

### [ ] Stage 4.3 — Validation surface
#### [ ] Step 4.3.1 (1 d)
- **Deliverables:** inline error badges on invalid fields.
- **Acceptance:** error text derived from Zod issues.
- **Unit tests:** error-mapping unit tests.
- **Integration tests:** Playwright produces invalid input.
- **Depends on:** 4.1.1.
- **Risks:** none.
- **Rubric:** pass iff error shown within 100 ms.

#### [ ] Step 4.3.2 (2 d)
- **Deliverables:** workflow-level validation via `/api/workflows/{id}/validate` calling engine validator.
- **Acceptance:** save disabled while errors present.
- **Unit tests:** controller test for valid/invalid payload.
- **Integration tests:** Playwright flow.
- **Depends on:** 2.4.4.
- **Risks:** engine validator throws vs returns — wrap both.
- **Rubric:** pass iff Save disabled on invalid.

#### [ ] Step 4.3.3 (1 d)
- **Deliverables:** validation error panel with deep-linking to faulting node.
- **Acceptance:** click error jumps camera to node.
- **Unit tests:** n/a.
- **Integration tests:** Playwright jump test.
- **Depends on:** 4.3.2.
- **Risks:** none.
- **Rubric:** pass iff jump-to-node works.

---

## §11 [ ] Phase 5 — Mapper, Fixtures, Golden Files

| Stage | Steps | Days |
|---|---:|---:|
| 5.1 DTO ↔ engine mapper | 7 | 5 |
| 5.2 Fixture corpus (split per audit) | 3 | 3 |
| 5.3 Golden-file harness | 2 | 2 |

### [ ] Stage 5.1 — DTO ↔ engine mapper (`EngineDtoMapper`)

Each sub-step covers exactly one kind-family. Total 7 steps; all share a single mapper class with partial methods per kind, so each sub-step touches ≤ 3 files (mapper partial + unit test + fixture).

#### [ ] Step 5.1.1a — Base + Task-family mapper (Noop, CSharp) (0.5 d)
- **Deliverables:** `EngineDtoMapper.MapBase` (handles every `NodeDefinition` field including `RetryPolicy` ↔ `RetryPolicyDto` with all 7 sub-fields and `CircuitBreakerPolicy` ↔ `CircuitBreakerPolicyDto` with all 4 sub-fields; preserves `null` ↔ absent-key for both policies; round-trips `CompensationNodeId`, `FallbackNodeId`); plus `MapNoop`, `MapCSharp` handling `AssemblyPath`, `TypeName`. All per-kind mappers below compose with `MapBase`.
- **Acceptance:** YAML → DTO → YAML byte-match for both kinds; a policy-populated fixture and a policy-null fixture both round-trip; `RetryPolicy.RetryOn`/`DoNotRetryOn` `List<Type>` ↔ `string[]` of FQTNs round-trips.
- **Unit tests:** round-trip per kind; base policy round-trip facts (non-null + null); policy sub-field count parity with §2.2.
- **Integration tests:** n/a.
- **Depends on:** 2.2.1b, 2.4.4.
- **Risks:** `List<Type>` serialisation — document `Type.GetType` contract and reject unknown FQTNs at load time with a clear error.
- **Rubric:** pass iff all round-trip facts green and policy parity tests green.

#### [ ] Step 5.1.1b — Script/task mapper (CSharpScript, CSharpTask) (0.75 d)
- **Deliverables:** mapper partials for both kinds handling `ScriptPath`/`ScriptContent` and `ScriptContent`/`ExecutorTypeName`/`ExecutorAssemblyPath`.
- **Acceptance:** inline and executor modes round-trip.
- **Unit tests:** 2 facts per kind (inline, file/executor).
- **Integration tests:** n/a.
- **Depends on:** 2.2.2, 2.2.3.
- **Risks:** mutual-exclusion divergence — mapper passes through whatever engine deserialised.
- **Rubric:** pass iff 4 facts green.

#### [ ] Step 5.1.1c — PowerShell mapper (0.5 d)
- **Deliverables:** partials for `PowerShell` (path-only) and `PowerShellTask` (path or inline). Uses exact property names `ScriptPath`, `ScriptContent`, `RequiredModules`, `ModulePaths`.
- **Acceptance:** fixtures for both kinds round-trip; missing inline on `PowerShell` produces no extra key in YAML.
- **Unit tests:** 2 facts.
- **Integration tests:** n/a.
- **Depends on:** 2.2.4a, 2.2.4b.
- **Risks:** none.
- **Rubric:** pass iff 2 facts green.

#### [ ] Step 5.1.1d — Control-flow mapper (IfElse, Switch, ForEach, While) (0.75 d)
- **Deliverables:** partials handling `Condition`; `Expression, Cases`; `CollectionExpression, ItemVariableName`; `ConditionExpression, MaxIterations`.
- **Acceptance:** fixtures round-trip.
- **Unit tests:** 1 fact per kind × 4.
- **Integration tests:** n/a.
- **Depends on:** 2.3.1–2.3.3b.
- **Risks:** engine default `MaxIterations=1000` must survive round-trip — mapper writes explicit value only if non-default.
- **Rubric:** pass iff 4 facts green.

#### [ ] Step 5.1.1e — Composition mapper (Subflow, Timer, Container) (0.5 d)
- **Deliverables:** partials handling `WorkflowFilePath, WorkflowDefinition, InputMappings, OutputMappings, Timeout, SkipValidation`; `Schedule`; `ChildNodes, ChildConnections, ExecutionMode`.
- **Acceptance:** Timespan round-trip preserves `hh:mm:ss`; Container Parallel mode preserved.
- **Unit tests:** 1 fact per kind × 3.
- **Integration tests:** n/a.
- **Depends on:** 2.4.1, 2.4.2, 2.4.3.
- **Risks:** TimeSpan serialisation differs between YamlDotNet defaults — force ISO-8601 via converter or match engine format.
- **Rubric:** pass iff 3 facts green.

#### [ ] Step 5.1.2 — Connection mapper + Sidecar merger (1.5 d)
- **Deliverables:** `ConnectionMapper` mapping `NodeConnection` ↔ `NodeConnectionDto` across all 9 properties (`SourceNodeId, TargetNodeId, TriggerMessageType, Condition, IsEnabled, Metadata, Priority, SourcePort, TargetPort`); `Metadata` `Dictionary<string,object>?` passthrough preserves keys and scalar types exactly; plus `SidecarMerger` combining `*.yaml` + `*.ui.json` into hydrated UI state and stripping UI-only fields on save.
- **Acceptance:** UI-only props never leak into YAML; all 9 connection fields round-trip including `null` ↔ absent-key for `Condition`, `Metadata`, `SourcePort`, `TargetPort`; default `IsEnabled=true` and `Priority=0` and `TriggerMessageType=Complete` round-trip to absent keys (canonical form).
- **Unit tests:** leak test (random UI-only prop added, assert absent in serialised YAML); connection round-trip fact per `MessageType` (5 facts); connection defaults-omission fact; `Metadata` passthrough fact with mixed scalar types.
- **Integration tests:** Playwright round-trip preserves positions and edge conditions/priorities/metadata.
- **Depends on:** 5.1.1e, 2.4.5.
- **Risks:** key collisions — sidecar schema forbids reserved engine keys; `Metadata` value-type drift across YamlDotNet versions — pin version and add a mixed-type regression fixture.
- **Rubric:** pass iff all round-trip facts + leak test green.

#### [ ] Step 5.1.3 — Mapper registry & kind factory (0.5 d)
- **Deliverables:** registry keyed on `RuntimeType`; `assertNever` exhaustiveness check.
- **Acceptance:** missing kind = compile error.
- **Unit tests:** exhaustiveness test.
- **Integration tests:** n/a.
- **Depends on:** 5.1.1a–e.
- **Risks:** none.
- **Rubric:** pass iff missing branch fails to compile.

### [ ] Stage 5.2 — Fixture corpus (split per audit)

Fixture corpus has ~25 YAML + ~25 sidecar files = ~50 files, exceeding the §0.1 20-file cap. Split into three sub-steps of ≤ 20 files each.

#### [ ] Step 5.2.1a — Task-family fixtures (1 d)
- **Deliverables:** 6 fixture pairs covering Noop, CSharp, CSharpScript, CSharpTask (inline + executor), PowerShell, PowerShellTask (inline + path). 12 files total.
- **Acceptance:** all fixtures pass `/validate` and round-trip.
- **Unit tests:** data-driven round-trip theory.
- **Integration tests:** none.
- **Depends on:** 5.1.1a–c.
- **Risks:** none.
- **Rubric:** pass iff theory green.

#### [ ] Step 5.2.1b — Control-flow fixtures (1 d)
- **Deliverables:** 5 fixture pairs covering IfElse, Switch (3-case), ForEach, While, and one corrupt/invalid case for negative testing. 10 files.
- **Acceptance:** round-trip + validator rejects the corrupt fixture.
- **Unit tests:** data-driven.
- **Integration tests:** none.
- **Depends on:** 5.1.1d.
- **Risks:** negative fixture must not accidentally parse — explicit assert on validator error code.
- **Rubric:** pass iff theory green and negative fixture rejected.

#### [ ] Step 5.2.1c — Composition fixtures (1 d)
- **Deliverables:** 4 fixture pairs covering Subflow (file), Subflow (inline), Timer (cron), Container (Parallel with 2 children). 8 files.
- **Acceptance:** fixtures round-trip.
- **Unit tests:** data-driven.
- **Integration tests:** none.
- **Depends on:** 5.1.1e.
- **Risks:** none.
- **Rubric:** pass iff theory green.

### [ ] Stage 5.3 — Golden-file harness
#### [ ] Step 5.3.1 (1 d)
- **Deliverables:** `GoldenFileTests.cs` using `Verify` to snapshot the canonicalised YAML + hydrated DTO JSON.
- **Acceptance:** drift surfaces as diff-review in CI.
- **Unit tests:** covers all fixtures from 5.2.1a–c.
- **Integration tests:** n/a.
- **Depends on:** 5.2.1c.
- **Risks:** Verify diffs on line-endings — normalize to LF in Verify settings.
- **Rubric:** pass iff snapshots stable.

#### [ ] Step 5.3.2 (1 d)
- **Deliverables:** doc section explaining "how to update a golden".
- **Acceptance:** team member can update golden without guidance.
- **Unit tests:** n/a.
- **Integration tests:** n/a.
- **Depends on:** 5.3.1.
- **Risks:** none.
- **Rubric:** pass iff doc review signed off.

---

## §12 [ ] Phase 6 — SignalR Telemetry & Live Monitor

| Stage | Steps | Days |
|---|---:|---:|
| 6.1 Engine event bridge | 3 | 7 |
| 6.2 Hub + client | 3 | 7 |
| 6.3 Live monitor UI | 3 | 5 |

### [ ] Stage 6.1 — Engine event bridge
#### [ ] Step 6.1.1 (2 d)
- **Deliverables:** `EngineEventBridge` subscribing to engine event source; normalises each native event class in `src/ExecutionEngine/Events/` to a `TelemetryEvent` DTO.
- **Acceptance:** all 9 native engine events (`WorkflowStartedEvent, WorkflowCompletedEvent, WorkflowFailedEvent, WorkflowCancelledEvent, NodeStartedEvent, NodeCompletedEvent, NodeFailedEvent, NodeCancelledEvent, NodeProgressEvent`) plus 2 synthetic (`NodeSkipped, EdgeActivated`) emitted as `TelemetryEvent`. No `WorkflowPausedEvent` or `MessageSentEvent` branches exist (those types are not in the engine).
- **Unit tests:** one per native event type (9) + one per synthetic (2); a negative test that asserts the bridge does NOT handle `WorkflowPausedEvent` or `MessageSentEvent` (type not referenced).
- **Integration tests:** run a fixture workflow, assert event trace; `NodeProgressEvent` carries `NodeInstanceId (Guid)`, `ProgressPercent (int, non-nullable)`, `Status (string, non-nullable)` — per `src/ExecutionEngine/Events/NodeProgressEvent.cs`.
- **Depends on:** 5.1.3.
- **Risks:** `NodeCompletedEvent` has no status field — bridge treats as `Completed` unless synthetic-skip rule fires; pause semantics come from `WorkflowExecutionStatus.Paused` observed in state snapshots, not from any event.
- **Rubric:** pass iff trace matches expected sequence and negative test green.

#### [ ] Step 6.1.2 (3 d)
- **Deliverables:** synthetic `EdgeActivated` emitter that matches terminal `MessageType` to outgoing `NodeConnection.triggerMessageType`.
- **Acceptance:** for each terminal event, at most one `EdgeActivated` per outgoing edge whose trigger matches.
- **Unit tests:** matrix test over MessageType × connection triggers.
- **Integration tests:** fixture with multi-edge node asserts correct subset activates.
- **Depends on:** 6.1.1.
- **Risks:** `Progress` is a valid `MessageType` and valid `TriggerMessageType` on `NodeConnection`, so progress-triggered connections are preserved in the authored graph for round-trip fidelity. However, the runtime monitor intentionally does **not** animate `Progress`-triggered connections as `EdgeActivated` because `Progress` is a non-terminal heartbeat — only terminal events (`Complete`, `Fail`, `Cancel`) and loop-iteration `Next` drive edge activation in the live UI. This is a **visualization decision**, not an engine-fidelity constraint.
- **Rubric:** pass iff matrix test green.

#### [ ] Step 6.1.3 (2 d)
- **Deliverables:** synthetic `NodeSkipped` derivation (on IfElse/Switch branches not taken).
- **Acceptance:** skipped node receives synthetic event within 50 ms of branch decision.
- **Unit tests:** IfElse true-branch + false-branch scenarios.
- **Integration tests:** Switch 3-case fixture asserts 2 skips.
- **Depends on:** 6.1.2.
- **Risks:** over-emission on retry — cap one skip per (nodeId, runId).
- **Rubric:** pass iff IfElse + Switch tests green.

### [ ] Stage 6.2 — Hub + client
#### [ ] Step 6.2.1 (2 d)
- **Deliverables:** SignalR hub `/hubs/telemetry` with per-run groups.
- **Acceptance:** multiple clients see same stream.
- **Unit tests:** hub tests via `HubConnection`.
- **Integration tests:** two clients attach to same run.
- **Depends on:** 6.1.3.
- **Risks:** group-leak on reconnect — server tracks subscription state.
- **Rubric:** pass iff two-client test green.

#### [ ] Step 6.2.2 (2 d)
- **Deliverables:** TS client wrapper with auto-reconnect + replay-from-last-sequence.
- **Acceptance:** after forced disconnect, client resumes at last seen event.
- **Unit tests:** sequence-gap detection.
- **Integration tests:** Playwright kills WS, observes recovery.
- **Depends on:** 6.2.1.
- **Risks:** duplicate events — idempotency keyed by `(runId, seq)`.
- **Rubric:** pass iff recovery test green.

#### [ ] Step 6.2.3 (3 d)
- **Deliverables:** run-history ring buffer in server (last 1000 events per run).
- **Acceptance:** late-joining client catches up.
- **Unit tests:** ring-buffer unit test.
- **Integration tests:** late-join test.
- **Depends on:** 6.2.1.
- **Risks:** memory pressure — size-capped per run, spill to disk in Phase 7.
- **Rubric:** pass iff late-join test green.

### [ ] Stage 6.3 — Live monitor UI
#### [ ] Step 6.3.1 (2 d)
- **Deliverables:** canvas overlays drawing node/edge status from telemetry stream.
- **Acceptance:** live fixture run shows animated status changes.
- **Unit tests:** overlay state store.
- **Integration tests:** Playwright runs fixture, observes updates.
- **Depends on:** 6.2.2, 3.1.3.
- **Risks:** animation jank — throttle overlays to 60 Hz max.
- **Rubric:** pass iff animation smooth under 100-event burst.

#### [ ] Step 6.3.2 (1 d)
- **Deliverables:** event-log side panel with filters (kind, nodeId, MessageType).
- **Acceptance:** filter updates within 50 ms.
- **Unit tests:** filter logic.
- **Integration tests:** Playwright filter combos.
- **Depends on:** 6.3.1.
- **Risks:** none.
- **Rubric:** pass iff filter tests green.

#### [ ] Step 6.3.3 (2 d)
- **Deliverables:** run-attach UI (pick active run, subscribe).
- **Acceptance:** selecting a run subscribes client.
- **Unit tests:** subscribe/unsubscribe.
- **Integration tests:** Playwright.
- **Depends on:** 6.3.1.
- **Risks:** none.
- **Rubric:** pass iff subscribe test green.

---

## §13 [ ] Phase 7 — Auth, Persistence, File Ops

| Stage | Steps | Days |
|---|---:|---:|
| 7.1 Auth | 4 | 9 |
| 7.2 Persistence | 4 | 7 |
| 7.3 File ops | 3 | 6 |

### [ ] Stage 7.1 — Auth
#### [ ] Step 7.1.1 (2 d)
- **Deliverables:** JWT bearer auth scaffold; dev-mode bypass flag.
- **Acceptance:** unauthenticated hits `/api/*` return 401.
- **Unit tests:** middleware tests.
- **Integration tests:** `WebApplicationFactory` auth flow.
- **Depends on:** 1.1.3.
- **Risks:** dev-bypass leaks to prod — guard via environment variable and warning log.
- **Rubric:** pass iff 401 emitted.

#### [ ] Step 7.1.2 (3 d)
- **Deliverables:** OpenID Connect integration (Azure AD or generic provider).
- **Acceptance:** browser login flow redirects and returns token.
- **Unit tests:** mocked OIDC discovery.
- **Integration tests:** Playwright OAuth mock.
- **Depends on:** 7.1.1.
- **Risks:** provider lock-in — use OIDC generic library.
- **Rubric:** pass iff login round-trip.

#### [ ] Step 7.1.3 (2 d)
- **Deliverables:** role-based policies (`Editor`, `Viewer`).
- **Acceptance:** Viewer cannot PUT workflow.
- **Unit tests:** policy tests per role.
- **Integration tests:** forbidden-endpoint test.
- **Depends on:** 7.1.2.
- **Risks:** none.
- **Rubric:** pass iff forbidden test green.

#### [ ] Step 7.1.4 (2 d)
- **Deliverables:** audit log of write endpoints.
- **Acceptance:** every PUT/DELETE produces a row.
- **Unit tests:** log writer.
- **Integration tests:** n/a.
- **Depends on:** 7.1.3.
- **Risks:** PII leakage — log principal id only.
- **Rubric:** pass iff log row per write.

### [ ] Stage 7.2 — Persistence
#### [ ] Step 7.2.1 (2 d)
- **Deliverables:** workflow repository abstraction (filesystem-backed in v1).
- **Acceptance:** CRUD over `/workflows` paths.
- **Unit tests:** repository tests with tmp dir.
- **Integration tests:** end-to-end via API.
- **Depends on:** 2.4.4, 2.4.5.
- **Risks:** path traversal — canonicalise and assert prefix.
- **Rubric:** pass iff traversal rejected.

#### [ ] Step 7.2.2 (2 d)
- **Deliverables:** optimistic concurrency via `ETag` on sidecar/yaml.
- **Acceptance:** concurrent PUT second writer gets 412.
- **Unit tests:** etag-mismatch test.
- **Integration tests:** Playwright two-tab edit.
- **Depends on:** 7.2.1.
- **Risks:** file-system mtime resolution — use hash.
- **Rubric:** pass iff 412 emitted.

#### [ ] Step 7.2.3 (1 d)
- **Deliverables:** run-history persistence (JSONL per run).
- **Acceptance:** run log survives restart.
- **Unit tests:** log writer.
- **Integration tests:** restart test.
- **Depends on:** 6.2.3.
- **Risks:** disk fill — rotate at 10 MB.
- **Rubric:** pass iff restart test green.

#### [ ] Step 7.2.4 (2 d)
- **Deliverables:** background job for run cleanup (retain 30 days).
- **Acceptance:** old runs removed.
- **Unit tests:** cleanup logic with synthetic clock.
- **Integration tests:** n/a.
- **Depends on:** 7.2.3.
- **Risks:** clock drift — inject `IClock`.
- **Rubric:** pass iff cleanup test green.

### [ ] Stage 7.3 — File ops
#### [ ] Step 7.3.1 (2 d)
- **Deliverables:** workspace browser API + UI (list/create/rename/delete workflows).
- **Acceptance:** UI shows tree, inline rename works.
- **Unit tests:** API tests.
- **Integration tests:** Playwright.
- **Depends on:** 7.2.1.
- **Risks:** none.
- **Rubric:** pass iff tree ops test green.

#### [ ] Step 7.3.2 (2 d)
- **Deliverables:** import/export zip of workflow + sidecar.
- **Acceptance:** exported zip reimports cleanly.
- **Unit tests:** round-trip.
- **Integration tests:** Playwright import.
- **Depends on:** 7.3.1.
- **Risks:** zip-slip — sanitise entry names.
- **Rubric:** pass iff round-trip green.

#### [ ] Step 7.3.3 (2 d)
- **Deliverables:** duplicate + template workflows.
- **Acceptance:** duplicate copies YAML + sidecar with new id.
- **Unit tests:** id-rewrite logic.
- **Integration tests:** Playwright.
- **Depends on:** 7.3.1.
- **Risks:** id collisions — generator retries on collision.
- **Rubric:** pass iff collision-retry test green.

---

## §14 [ ] Phase 8 — Packaging, Docs, Release

| Stage | Steps | Days |
|---|---:|---:|
| 8.1 Packaging | 4 | 6 |
| 8.2 Docs | 3 | 7 |
| 8.3 Release | 3 | 6 |

### [ ] Stage 8.1 — Packaging
#### [ ] Step 8.1.1 (3 d)
- **Deliverables:** Dockerfile for server + static UI, multi-stage.
- **Acceptance:** `docker run` serves UI + API.
- **Unit tests:** n/a.
- **Integration tests:** smoke via Testcontainers.
- **Depends on:** 7.3.3.
- **Risks:** base-image CVEs — pin and scan.
- **Rubric:** pass iff container smoke test green.

#### [ ] Step 8.1.2 (1 d)
- **Deliverables:** helm chart (optional deploy).
- **Acceptance:** `helm template` renders valid yaml.
- **Unit tests:** helm unittest.
- **Integration tests:** n/a.
- **Depends on:** 8.1.1.
- **Risks:** none.
- **Rubric:** pass iff template test green.

#### [ ] Step 8.1.3 (1 d)
- **Deliverables:** self-contained `dotnet publish` single-file binary.
- **Acceptance:** single exe runs on Windows server.
- **Unit tests:** n/a.
- **Integration tests:** smoke on Windows runner.
- **Depends on:** 8.1.1.
- **Risks:** none.
- **Rubric:** pass iff smoke test green.

#### [ ] Step 8.1.4 (1 d)
- **Deliverables:** version stamp into assembly + UI footer.
- **Acceptance:** footer matches tag.
- **Unit tests:** version-string test.
- **Integration tests:** Playwright snapshot footer.
- **Depends on:** 8.1.1.
- **Risks:** none.
- **Rubric:** pass iff footer matches.

### [ ] Stage 8.2 — Docs
#### [ ] Step 8.2.1 (3 d)
- **Deliverables:** user guide in `docs/workflow-ui-user-guide.md`.
- **Acceptance:** screenshots + walk-through for each kind.
- **Unit tests:** markdown link checker.
- **Integration tests:** n/a.
- **Depends on:** 7.3.3.
- **Risks:** screenshot drift — auto-capture via Playwright.
- **Rubric:** pass iff link checker green.

#### [ ] Step 8.2.2 (2 d)
- **Deliverables:** developer guide — how to add a new kind (see Appendix B).
- **Acceptance:** new kind checklist matches appendix.
- **Unit tests:** n/a.
- **Integration tests:** dog-food test (add a dummy kind following the checklist).
- **Depends on:** 8.2.1.
- **Risks:** appendix drifts from code — CI link-checks appendix against code markers.
- **Rubric:** pass iff dog-food test green.

#### [ ] Step 8.2.3 (2 d)
- **Deliverables:** API reference generated from OpenAPI.
- **Acceptance:** `/api/openapi.json` served; redoc site embedded.
- **Unit tests:** openapi schema validation.
- **Integration tests:** redoc renders.
- **Depends on:** 1.1.3.
- **Risks:** none.
- **Rubric:** pass iff schema validates.

### [ ] Stage 8.3 — Release
#### [ ] Step 8.3.1 (3 d)
- **Deliverables:** release pipeline producing tagged container + signed single-file exe.
- **Acceptance:** tag triggers build+publish.
- **Unit tests:** pipeline meta-test.
- **Integration tests:** dry-run release.
- **Depends on:** 8.1.3.
- **Risks:** signing key handling — use GH OIDC to KMS.
- **Rubric:** pass iff dry-run succeeds.

#### [ ] Step 8.3.2 (2 d)
- **Deliverables:** changelog automation from PR labels.
- **Acceptance:** release notes populated.
- **Unit tests:** n/a.
- **Integration tests:** n/a.
- **Depends on:** 8.3.1.
- **Risks:** none.
- **Rubric:** pass iff notes generated.

#### [ ] Step 8.3.3 (1 d)
- **Deliverables:** post-release checklist (parity-tests re-run against released engine version).
- **Acceptance:** checklist passes.
- **Unit tests:** n/a.
- **Integration tests:** parity tests green on release tag.
- **Depends on:** 8.3.1.
- **Risks:** engine version bump — re-run parity, open PR to regenerate contract.
- **Rubric:** pass iff parity tests green on tag.

---

## Appendix A — Risk Register
- **R1 — Engine adds a property** → parity tests fail; regenerate `engine-contract.ts`, add DTO field + mapper partial + fixture. Owner: contract lead.
- **R2 — MessageType extension** → port matrix fails test; extend matrix + edge styling + synthetic edge derivation.
- **R3 — YAML option drift** → round-trip fails; reconcile options with engine shared constant.
- **R4 — SignalR flakiness** → recovery test fails; tune heartbeat + sequence gaps.
- **R5 — Large container recursion** → depth-limit triggers; raise limit or refactor container type.
- **R6 — File-budget overshoot** (§0.1) → split step into `a/b/…` before merge; reviewer enforces.
- **R7 — `ProgressUpdate` / `NodeProgressEvent` confusion** → `ProgressUpdate` (`src/ExecutionEngine/Events/ProgressUpdate.cs`) is a workflow-level aggregate snapshot (percent, node counts, ETA); `NodeProgressEvent` (`src/ExecutionEngine/Events/NodeProgressEvent.cs`) is a per-node progress event with `NodeInstanceId`, `ProgressPercent`, `Status`. The UI bridge maps `NodeProgressEvent` only; `ProgressUpdate` is not surfaced via the event bridge. If these are conflated, telemetry DTOs will drift. Mitigation: parity test asserts the two types have no overlapping field names.
- **R8 — `MessageStatus` enum drift** → engine uses `Ready, InFlight, Completed, Superseded` (not `Removed` or `Abandoned` as some early docs stated). UI should never reference `MessageStatus` directly (it's an internal queue concern), but if it leaks into a DTO, parity test catches it.

## Appendix B — Add-a-kind Checklist
1. Add `*NodeDefinition.cs` in engine.
2. Parity tests fail until: `engine-contract.ts` regenerated, per-kind DTO added in §2.3, port matrix entry added in §2.6.
3. Palette registry entry + icon.
4. Mapper partial added in Stage 5.1 (create a new 5.1.1X sub-step).
5. Golden fixture pair added under Stage 5.2.
6. Property-grid schema file in `src/WorkflowUI/src/schemas/<kind>.json`.
7. User-guide section + screenshot (Step 8.2.1).
8. Update §0.2.2 table row.
9. Update phase index totals if step count changes; keep roll-up checkboxes consistent with §0.1.1.
