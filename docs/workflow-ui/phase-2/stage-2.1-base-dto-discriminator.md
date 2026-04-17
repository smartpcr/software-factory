# Stage 2.1 — Base DTO + discriminator

**Phase:** Phase 2 — Node Kinds, Schemas, Round-Trip
**Stage status:** [ ]
**Total effort:** 3 d
**Step count:** 2

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 2.1 establishes the TypeScript foundation every Phase 2 kind-specific step builds on: the `NodeDtoBase` interface + Zod schema reflecting all 11 public properties on the C# `NodeDefinition` base class (per `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:17-97`), a discriminated-union skeleton keyed on `runtimeType: RuntimeType`, and an exhaustive `assertNever` pattern that will type-error at compile time if a new kind lands in the engine without a UI branch. Master plan [§2.2](../../workflow-ui-plan.md#22-node-dto-base) summarises the surface; this stage is the FIRST place it becomes executable TypeScript.

**Engine ground truth:** the base `NodeDefinition` has exactly these public instance properties plus the abstract `RuntimeType`:

| # | Property | CLR type | Nullable | Default | Engine line |
|---|----------|---------|----------|---------|-------------|
| 1 | `NodeId` | `string` | no | `string.Empty` | `NodeDefinition.cs:27` |
| 2 | `NodeName` | `string` | no | `string.Empty` | `NodeDefinition.cs:32` |
| 3 | `Description` | `string?` | yes | `null` | `NodeDefinition.cs:37` |
| 4 | `Configuration` | `Dictionary<string, object>?` | yes | `null` | `NodeDefinition.cs:42` |
| 5 | `JoinType` | `JoinType` | no | `Any` | `NodeDefinition.cs:49` |
| 6 | `Priority` | `NodePriority` | no | `Normal` | `NodeDefinition.cs:57` |
| 7 | `MaxConcurrentExecutions` | `int` | no | `0` | `NodeDefinition.cs:65` |
| 8 | `RetryPolicy` | `Policies.RetryPolicy?` | yes | `null` | `NodeDefinition.cs:72` |
| 9 | `CircuitBreakerPolicy` | `Policies.CircuitBreakerPolicy?` | yes | `null` | `NodeDefinition.cs:79` |
| 10 | `CompensationNodeId` | `string?` | yes | `null` | `NodeDefinition.cs:87` |
| 11 | `FallbackNodeId` | `string?` | yes | `null` | `NodeDefinition.cs:95` |

That's **11** — the master plan's §2.1.1 sentence "9 base properties" is a known undercount (noted as a discrepancy in the evaluator report).

Nested policy shapes are authoritative per Stage 0.2.2 and master plan [§2.2](../../workflow-ui-plan.md#22-node-dto-base): `RetryPolicyDto` = 7 fields, `CircuitBreakerPolicyDto` = 4 fields. **Both are nullable on the base**; `null` means disabled and MUST be preserved distinctly from an empty object (`{}`) on round-trip.

This stage unblocks Stage 2.2 (task-family kinds extend `NodeDtoBase`), Stage 2.3 (control-flow kinds), and Stage 2.4 (composition kinds + YAML round-trip). It also seeds the generator output from Stage 1.2.1 — the facade type union `NodeDto` is initially a single-member union (`NoopNode`) and expands kind-by-kind.

## Prerequisites
- Stage 1.2 complete (contract facade + generator).
- Stage 0.2 parity tests green.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 2.1 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Client
- `client/src/engine-contract.generated.ts` (modified — generator emits `NodeDtoBase`, `RetryPolicyDto`, `CircuitBreakerPolicyDto`).
- `client/src/engine-contract.ts` (modified — re-exports).
- `client/src/schemas/node-base.schema.ts` (new, Step 2.1.1) — Zod schema.
- `client/src/schemas/policy.schema.ts` (new, Step 2.1.1) — Zod for retry + circuit-breaker.
- `client/src/schemas/node-union.ts` (new, Step 2.1.2) — discriminated union stub + `assertNever`.
- `client/src/schemas/__tests__/node-base.schema.test.ts` (new, Step 2.1.1).
- `client/src/schemas/__tests__/node-union.test-d.ts` (new, Step 2.1.2) — `tsd` type-level tests.

Fixtures
- `client/src/__fixtures__/node-base.valid.json` (new) — minimal valid base.
- `client/src/__fixtures__/node-base.with-retry.json` (new).
- `client/src/__fixtures__/node-base.with-circuit-breaker.json` (new).
- `client/src/__fixtures__/node-base.null-policies.json` (new) — both policies `null` explicitly.

Server parity
- `src/WorkflowUi.ContractTests/NodeBaseRoundTripTests.cs` (new) — asserts that DTO round-trips through the engine preserve `null` policy (vs empty object).

## Shared fixtures / fakes
- The 4 base-node JSON fixtures are shared with Stages 2.2/2.3/2.4 as the canonical "starter" node. They are committed LF-normalised.
- `node-union.ts` is reopened by every kind-specific step in Stage 2.2/2.3/2.4 to add one more member to the discriminated union.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="2.1"`, `phaseId="2"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-2.1.plan-read`, `workflow-ui.stage-2.1.apply`, `workflow-ui.stage-2.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_2_1_started_total`, counter `workflow_ui_stage_2_1_failed_total`, histogram `workflow_ui_stage_2_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| schema layer | DTO shape diverges from engine property set | Zod/parity tests fail | update schema from cited engine file and refresh fixtures |
| round-trip API | server canonicalization or validation drift | parse/serialize identity test fails | route through `WorkflowLoader` / `WorkflowSerializer` only |
| sandbox / sidecar store | path traversal or orphan handling bug | endpoint negative tests or warning logs fire | tighten sandbox and preserve orphan metadata explicitly |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| Zod parse (200-node workflow) | 30 ms | 100 ms |
| YAML serialize / validate (200 nodes) | 150 ms | 600 ms |
| sidecar / schema fixture save | 75 ms | 250 ms |

## Security callouts
- Reject path traversal and absolute-path escapes in workflow/sidecar endpoints.
- Treat node names, descriptions, and expressions as untrusted strings; do not render as HTML without escaping.
- Do not introduce a second YAML parser or custom loader; server routes must remain thin wrappers over engine APIs.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 2.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage21` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

## Step 2.1.1 — `NodeDtoBase` type + Zod schema

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 1.2.3

### Goal
Produce a hand-written (but generator-aligned) TypeScript interface `NodeDtoBase` with 11 properties matching the engine table above + the discriminator `runtimeType: RuntimeType`. Author a matching Zod schema `nodeBaseSchema` that parses+validates all 11 fields with correct nullability, defaults, and refinements. The schema MUST preserve the critical `null` vs `absent` distinction for `retryPolicy` / `circuitBreakerPolicy` (engine semantics from master plan [§2.2](../../workflow-ui-plan.md#22-node-dto-base)). The Zod schema is the runtime validation entry point; TS types are derived from it via `z.infer<...>` so drift between type and schema is impossible.

### Depends on
1.2.3.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs` (for property names + types).
- `src/ExecutionEngine/Policies/RetryPolicy.cs` + `CircuitBreakerPolicy.cs`.
- `client/src/engine-contract.ts` from Stage 1.2.3.

### Deliverables
- `client/src/schemas/policy.schema.ts` (new) —
  ```ts
  export const retryStrategySchema = z.enum(['None','Fixed','Exponential','Linear']);
  export const retryPolicySchema = z.object({
    strategy: retryStrategySchema.default('None'),
    maxAttempts: z.number().int().min(1).max(10).default(3),
    initialDelay: z.string().default('00:00:01'),        // hh:mm:ss or ISO-8601 duration
    maxDelay: z.string().default('00:01:00'),
    multiplier: z.number().default(2.0),
    retryOn: z.array(z.string()).default([]),
    doNotRetryOn: z.array(z.string()).default([]),
  }).strict();
  export const circuitBreakerPolicySchema = z.object({
    failureThreshold: z.number().int().min(0).max(100).default(50),
    minimumThroughput: z.number().int().min(1).default(10),
    openDuration: z.string().default('00:00:30'),
    halfOpenSuccesses: z.number().int().min(1).default(3),
  }).strict();
  ```
- `client/src/schemas/node-base.schema.ts` (new) —
  ```ts
  export const nodeBaseSchema = z.object({
    runtimeType: runtimeTypeSchema,
    nodeId: z.string().min(1),
    nodeName: z.string().default(''),
    description: z.string().nullable().optional(),
    configuration: z.record(z.unknown()).nullable().optional(),
    joinType: joinTypeSchema.default('Any'),
    priority: nodePrioritySchema.default('Normal'),
    maxConcurrentExecutions: z.number().int().min(0).max(100).default(0),
    retryPolicy: retryPolicySchema.nullable().optional(),
    circuitBreakerPolicy: circuitBreakerPolicySchema.nullable().optional(),
    compensationNodeId: z.string().nullable().optional(),
    fallbackNodeId: z.string().nullable().optional(),
  });
  export type NodeDtoBase = z.infer<typeof nodeBaseSchema>;
  ```
- `client/src/engine-contract.ts` (modified) — re-export `nodeBaseSchema`, `retryPolicySchema`, `circuitBreakerPolicySchema`, and the inferred types.
- `client/src/__fixtures__/node-base.valid.json` (new) — `{ "runtimeType": "Noop", "nodeId": "n1", "nodeName": "Start", "joinType": "Any", "priority": "Normal", "maxConcurrentExecutions": 0 }`.
- `client/src/__fixtures__/node-base.with-retry.json` (new) — same + non-null retryPolicy.
- `client/src/__fixtures__/node-base.with-circuit-breaker.json` (new) — same + non-null circuitBreakerPolicy.
- `client/src/__fixtures__/node-base.null-policies.json` (new) — `retryPolicy: null, circuitBreakerPolicy: null` EXPLICITLY (not omitted).
- `client/src/schemas/__tests__/node-base.schema.test.ts` (new) — 12+ tests.
- `src/WorkflowUi.ContractTests/NodeBaseRoundTripTests.cs` (new) — null-vs-empty preservation through engine `WorkflowSerializer.ToYaml` and back.

### Acceptance criteria
- [ ] `nodeBaseSchema.parse(fixture)` succeeds for all 4 fixtures.
- [ ] `nodeBaseSchema.parse({})` fails on missing `runtimeType` and `nodeId`.
- [ ] `retryPolicy: null` stays `null` after parse (not coerced to `{}` or `undefined`).
- [ ] `retryPolicy: undefined` stays `undefined` / absent.
- [ ] `retryPolicy: {}` is an error (required fields would be missing after default-apply — a strict object with all defaults applied, so actually `{}` parses to the full defaults object; this behaviour is asserted explicitly so the reviewer understands).
- [ ] `maxConcurrentExecutions: -1` fails validation (range 0..100 per `NodeDefinition.cs:64`).
- [ ] `maxAttempts: 11` fails validation (range 1..10 per `RetryPolicy.cs:47`).
- [ ] `failureThreshold: 150` fails validation (range 0..100 per `CircuitBreakerPolicy.cs:34`).
- [ ] TS type `NodeDtoBase` has exactly 12 known keys (11 engine + `runtimeType`).
- [ ] `configuration` accepts arbitrary `Record<string, unknown>` (including nested objects, arrays, booleans).
- [ ] Round-trip through engine `WorkflowSerializer` preserves `null` vs empty-object for both policies (per `NodeBaseRoundTripTests_NullPolicy_StaysNull_AfterSerialize`).

### Unit tests
1. `NodeBaseSchema_MinimalFixture_Parses`
2. `NodeBaseSchema_MissingRuntimeType_Fails`
3. `NodeBaseSchema_MissingNodeId_Fails`
4. `NodeBaseSchema_NullRetryPolicy_StaysNull`
5. `NodeBaseSchema_UndefinedRetryPolicy_StaysUndefined`
6. `NodeBaseSchema_NullCircuitBreakerPolicy_StaysNull`
7. `NodeBaseSchema_NegativeMaxConcurrent_Fails`
8. `NodeBaseSchema_TooHighMaxConcurrent_101_Fails`
9. `NodeBaseSchema_RetryPolicy_MaxAttempts11_Fails`
10. `NodeBaseSchema_CircuitBreaker_FailureThreshold150_Fails`
11. `NodeBaseSchema_Configuration_Accepts_NestedArbitrary`
12. `NodeBaseSchema_JoinType_DefaultsTo_Any`
13. `NodeBaseSchema_Priority_DefaultsTo_Normal`
14. `NodeBaseSchema_FixtureWithRetry_ParsesAndRoundTrips`
15. `NodeBaseRoundTripTests_NullPolicy_StaysNull_AfterSerialize` (C#)
16. `NodeBaseRoundTripTests_EmptyPolicyObject_NotEqualToNull` (C#)

A/A/A for the three most important:

- `NodeBaseSchema_NullRetryPolicy_StaysNull`
  - Arrange: `const input = { runtimeType: 'Noop', nodeId: 'n', joinType: 'Any', priority: 'Normal', maxConcurrentExecutions: 0, retryPolicy: null, circuitBreakerPolicy: null };`
  - Act: `const parsed = nodeBaseSchema.parse(input);`
  - Assert: `expect(parsed.retryPolicy).toBeNull(); expect(parsed.circuitBreakerPolicy).toBeNull();` — NOT `undefined`.
- `NodeBaseRoundTripTests_NullPolicy_StaysNull_AfterSerialize` (C#)
  - Arrange: `var def = new NoopNodeDefinition { NodeId = "n", RetryPolicy = null };`
  - Act: `var yaml = new WorkflowSerializer().ToYaml(wrapWith(def)); var back = new WorkflowSerializer().FromYaml(yaml);`
  - Assert: `Assert.Null(back.Nodes[0].RetryPolicy);` — engine preserves null.
- `NodeBaseSchema_RetryPolicy_MaxAttempts11_Fails`
  - Arrange: fixture with `retryPolicy: { strategy: 'Fixed', maxAttempts: 11, ... }`.
  - Act: `nodeBaseSchema.safeParse(input)`.
  - Assert: `.success === false` and issue path includes `retryPolicy.maxAttempts`.

### Integration tests
- Cross-language: the C# `NodeBaseRoundTripTests` load a Zod-produced JSON fixture, deserialize via `WorkflowSerializer.FromJson`, re-serialize, and assert the JSON text preserves `retryPolicy: null`.

### Golden-file / parity tests
- **Golden:** `client/src/__fixtures__/node-base.with-retry.json` — round-trips through engine `WorkflowSerializer`; after re-serialize-and-canonicalise, byte-matches the original.
- **Diff contract:** key order preserved, LF line endings, `null` preserved as `null` (not dropped to absent).
- **Parity fixture:** `engine-dto-shape.json` (from Stage 0.2.2) must include `NodeDefinition` with exactly the 11 properties this step implements.

### Edge cases & negative tests
1. Fixture with `retryPolicy: {}` — current schema populates defaults, producing a valid retry object with `strategy: 'None'`. This is acceptable but must be DOCUMENTED: `{}` round-trips differently from `null`. Explicit test asserts this.
2. Engine adds a 12th property (e.g., `Owner: string?`) — the parity test from Stage 0.2.2 fails before this step's schema can be updated; update is tracked via blocker.
3. `configuration: null` — allowed (nullable).
4. `configuration: []` — Zod `z.record(z.unknown())` rejects arrays; refine to accept `null | Record<string, unknown>` only.
5. `priority: 'Critical'` — rejected because `nodePrioritySchema` is a 3-member union (defends master plan v1.1 fix).
6. `joinType: 'Either'` — rejected (not in engine enum).
7. `retryOn: 'System.TimeoutException'` (string, not array) — rejected; must be `string[]`.
8. UTF-8 BOM in fixture — parser strips BOM before JSON parse; fixture committed without BOM.
9. `retryPolicy.strategy: 'Exponential'` with `multiplier: 0` — rejected by a refine (engine validates `Multiplier > 0` per `RetryPolicy.cs:166`).
10. Dictionary with a non-string key (parsed via JSON.parse is impossible; guarded by Zod `z.record`).

### Observability hooks
- Log category (client, via `console.debug` or optional logger injection) `WorkflowUi.Client.NodeBaseSchema` — fires on parse failure with Zod issue path.
- C# tests log category `WorkflowUi.ContractTests.NodeBaseRoundTrip` — fires on null-preservation failure with a unified YAML diff.

### Risks & mitigations
- `null` vs empty-object drift when YamlDotNet serializes — engine preserves per source inspection; defensive fact `NodeBaseRoundTripTests_NullPolicy_StaysNull_AfterSerialize`.
- Zod default-merge surprises (e.g., providing `retryPolicy: {}` fills defaults) — documented and tested; UI mapper in Phase 5 must NOT silently convert `null` to `{}`.
- TypeScript `undefined` vs `null` inference edges — explicit `.nullable().optional()` chain forces both states representable.

### Evaluator gap-analysis prompts
1. Does `NodeDtoBase` include all 11 engine properties plus `runtimeType` — NOT the 9 implied by master plan §2.1.1 (documented discrepancy)?
2. Does `nodeBaseSchema` use `.nullable().optional()` for `retryPolicy` and `circuitBreakerPolicy` so `null` and `undefined` are DISTINCT?
3. Does a round-trip test prove engine `WorkflowSerializer` preserves `null` as `null` (not dropped, not `{}`)?
4. Does `retryPolicySchema` enforce `maxAttempts` in 1..10 matching `RetryPolicy.Validate()` at `src/ExecutionEngine/Policies/RetryPolicy.cs:151`?
5. Does `circuitBreakerPolicySchema` enforce `failureThreshold` in 0..100 matching `CircuitBreakerPolicy.cs:62-65`?
6. Is `priority` schema restricted to exactly `Low | Normal | High` with no `Critical`?
7. Are Zod schemas the source-of-truth with TS types `z.infer<>`-derived (so drift is impossible)?
8. Does `configuration` accept `Record<string, unknown>` AND `null`, but not array or scalar?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.

---

## Step 2.1.2 — Discriminated-union plumbing + `assertNever` exhaustiveness

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 2.1.1

### Goal
Define the initial shape of the `NodeDto` discriminated union in `client/src/schemas/node-union.ts` plus helper machinery that will be filled in by Stages 2.2/2.3/2.4. The union is keyed on `runtimeType` with 13 members (one per engine `RuntimeType`); members not yet implemented in Phase 2 stages 2.2/2.3/2.4 are initially typed `NodeDtoBase & { runtimeType: '<Kind>' }` with a TODO marker. Author an `assertNever` exhaustiveness check pattern so removing or renaming a kind produces a compile-time error. Include a `tsd`-style type-level test that asserts a handler switch must cover all 13 kinds.

### Depends on
2.1.1.

### Inputs
- `client/src/schemas/node-base.schema.ts` from 2.1.1.
- `client/src/engine-contract.ts` with `RUNTIME_TYPES` and `assertNever`.

### Deliverables
- `client/src/schemas/node-union.ts` (new):
  ```ts
  import { z } from 'zod';
  import { nodeBaseSchema } from './node-base.schema';

  // Placeholder per-kind schemas — each later stage replaces these with real shapes.
  const makePlaceholder = <K extends RuntimeType>(k: K) => nodeBaseSchema.extend({ runtimeType: z.literal(k) });

  export const nodeDtoSchema = z.discriminatedUnion('runtimeType', [
    makePlaceholder('Noop'), makePlaceholder('CSharp'), /* ... all 13 */
  ]);
  export type NodeDto = z.infer<typeof nodeDtoSchema>;

  export function handleNode<T>(n: NodeDto, handlers: { [K in NodeDto['runtimeType']]: (n: Extract<NodeDto, { runtimeType: K }>) => T }): T {
    return (handlers[n.runtimeType] as (n: NodeDto) => T)(n);
  }
  ```
- `client/src/schemas/__tests__/node-union.test-d.ts` (new) — type-level assertions using `expectType` / `expectError` from `tsd` (or our local mini-variant).
- `client/src/schemas/__tests__/node-union.test.ts` (new) — runtime assertions.
- `client/src/engine-contract.ts` (modified) — re-export `nodeDtoSchema`, `NodeDto`, `handleNode`.
- `client/src/__fixtures__/node-union.all-13-kinds.json` (new) — array with one node per kind, used by exhaustiveness tests.

### Acceptance criteria
- [ ] `nodeDtoSchema` has exactly 13 discriminated members, one per `RuntimeType` value.
- [ ] `handleNode` forces the caller to provide a handler for every runtime type at compile time.
- [ ] A deliberate removal of one handler fails `tsc --strict` with a type-level error.
- [ ] `assertNever` (from 1.2.3) is the default-branch pattern in any switch; a sample usage is documented.
- [ ] Runtime `parse()` on the union fixture-array succeeds for all 13 placeholder-kind entries.
- [ ] `parse()` fails with a clear Zod error on `runtimeType: 'Mystery'`.
- [ ] The union is re-exported only from the facade; direct imports of `./schemas/node-union` from `engine-contract.generated.ts` are not possible (nothing in the generator points at this file — it's pure UI-land).
- [ ] Type-level test proves `handleNode<string>` return type is `string`.

### Unit tests
1. `NodeUnion_Parse_AllKinds_Valid`
2. `NodeUnion_Parse_Mystery_Fails`
3. `NodeUnion_Discriminator_Is_runtimeType`
4. `NodeUnion_Members_Count_Is_13`
5. `NodeUnion_Missing_runtimeType_Fails`
6. `NodeUnion_HandleNode_DispatchesToCorrectHandler`
7. `NodeUnion_HandleNode_Returns_TypedResult`
8. `NodeUnion_HandleNode_ExhaustivenessError_AtCompileTime` (type-level via `tsd`)
9. `NodeUnion_AssertNever_Throws_WithUnhandledValue`
10. `NodeUnion_FixtureAll13Kinds_LengthIs13`
11. `NodeUnion_FixtureAll13Kinds_Every_runtimeType_Unique`

A/A/A for the three most important:

- `NodeUnion_Parse_AllKinds_Valid`
  - Arrange: `import fixture from '../__fixtures__/node-union.all-13-kinds.json';`
  - Act: `fixture.forEach(n => nodeDtoSchema.parse(n));`
  - Assert: no throw.
- `NodeUnion_HandleNode_DispatchesToCorrectHandler`
  - Arrange: build handlers that return their key, e.g. `{ Noop: () => 'n', CSharp: () => 'c', ... }`.
  - Act: `handleNode(noopNode, handlers)`.
  - Assert: returns `'n'`.
- `NodeUnion_HandleNode_ExhaustivenessError_AtCompileTime`
  - Arrange: a `tsd` snippet calling `handleNode(n, { Noop: ..., /* missing 12 others */ })`.
  - Act: run `tsd`.
  - Assert: error indicating property `CSharp` missing in handler object.

### Integration tests
None — type-level.

### Golden-file / parity tests
- **Parity fixture:** `node-union.all-13-kinds.json` length === `engine-enums.json` `.RuntimeType.members.length` === 13. Asserted by cross-file test `NodeUnion_Fixture_Length_Matches_EngineEnumsJson`.

### Edge cases & negative tests
1. Engine adds a 14th kind — parity test from Stage 0.2.1 fails first (immediate feedback); this step's fixture also mismatches length.
2. Duplicate `runtimeType` in the union (copy-paste mistake) — Zod detects at schema construction.
3. `handleNode` called with a value whose `runtimeType` is legal but no handler provided (TypeScript normally prevents, but a runtime `as` cast bypasses) — returns `undefined` unless defended; add a runtime guard that throws `assertNever`-equivalent.
4. Fixture with only 12 kinds — `NodeUnion_FixtureAll13Kinds_LengthIs13` fails.
5. Two fixture entries sharing `runtimeType` — `NodeUnion_FixtureAll13Kinds_Every_runtimeType_Unique` fails.
6. Zod union member without `.literal('...')` discriminator — Zod `discriminatedUnion` constructor throws.
7. `RuntimeType` string-literal drift (e.g., lowercase) — schema rejects.
8. A stage 2.2 kind upgrade (e.g., `Noop → NoopNode`) replaces a placeholder; test file updates accordingly. Replacement must preserve member count.

### Observability hooks
- Not applicable (compile-time + pure Zod).

### Risks & mitigations
- `z.discriminatedUnion` vs plain `z.union` — must use discriminated for O(1) parse performance and clean errors.
- TS `unknown` leakage via `(handlers[n.runtimeType] as ...)` cast — isolated to the helper; callers see full type safety.
- `tsd` heavy dependency — alternative: a tiny `expectType<T>(value: T)` helper under `client/src/__tests__/type-helpers/`.

### Evaluator gap-analysis prompts
1. Does `nodeDtoSchema` have exactly 13 discriminated members matching the engine `RuntimeType` list?
2. Does `handleNode` enforce exhaustiveness at compile time (removing a handler = tsc error)?
3. Is there a type-level test that asserts the missing-handler error actually fires?
4. Is the union keyed on `runtimeType` (not `kind` or `type`)?
5. Does a runtime fixture with 13 entries (one per kind) parse cleanly?
6. Does parsing a `runtimeType: 'Mystery'` object produce a clear Zod error, not a silent pass?
7. Is `assertNever` imported from the facade and used as the default branch in at least one example switch?
8. Are placeholder-kind schemas authored today in such a way that Stage 2.2/2.3/2.4 replace them without needing to re-export the union?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
