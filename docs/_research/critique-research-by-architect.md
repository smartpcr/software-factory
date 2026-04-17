# Architect's Critique — `workflow-ui-research.md`

> Reviewer: principal architect, accountable for long-term maintainability of the
> ExecutionEngine + UI.
> Reviewed file: `docs/_research/workflow-ui-research.md` (dated 2026-04-16).
> Engine ground-truth references used below: `src/ExecutionEngine/Enums/*.cs`,
> `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs`,
> `src/ExecutionEngine/Events/*.cs`, and the YAML examples under
> `src/ExecutionEngine.Example/Workflows/`.

---

## Strengths

- **Right primary library choice.** `@xyflow/react` v12 is the correct bet for a
  React-first designer with typed ports, subflows, and live overlays; the
  rationale is accurate and the competitors are fairly dismissed.
- **Correct state stack direction.** Zustand + `zundo` (temporal) with an
  execution slice kept out of undo history matches React Flow's own guidance
  and the performance profile we need.
- **Schema-driven property grid.** Picking RJSF + Ajv ties form validation,
  default values, and editor tooling to a single JSON-Schema source of truth —
  the only sustainable approach for 10+ node kinds.
- **Node-registry pattern for extensibility.** Centralizing kind→component
  mapping, and modelling decisions as named output ports rather than edge
  labels, is the right long-term shape.
- **Realistic performance guard-rails.** RAF/16-50 ms batching of execution
  events, per-node status maps (not full-array rewrites), and stable
  `nodeTypes`/`edgeTypes` references are all called out.

---

## Critical issues (must-fix before we commit)

### C1. Execution status vocabulary does not match the engine

§5 proposes:
```
'Ready' | 'Running' | 'Success' | 'Failed' | 'Skipped' | 'Waiting' | 'Paused' | 'Cancelled'
```
The engine's `src/ExecutionEngine/Enums/NodeExecutionStatus.cs` defines exactly:
```
Pending | Running | Completed | Failed | Cancelled | Skipped
```
`Success`, `Ready`, `Waiting`, `Paused` do not exist at the node level.
`Paused` exists at the workflow level (`WorkflowExecutionStatus`). Shipping
this payload will either force a translation layer we did not plan for, or
drift that silently breaks on first integration.
**Fix:** adopt the engine enum verbatim for nodes; isolate workflow-level
`Paused/Pending/Running/Completed/Failed/Cancelled` in a separate
`WorkflowPhaseChanged` event.

### C2. The `RuntimeType` inventory is incomplete and conflated

§2.1 lists six kinds: `task.script.csharp | task.script.powershell |
control.decision | container.parallel | container.loop | subflow`.
`src/ExecutionEngine/Enums/RuntimeType.cs` actually defines **13**:
`Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, IfElse,
ForEach, While, Switch, Subflow, Timer, Container`.

Specific conflations that will bite us:
- **Compiled `CSharp` (assembly + typeName) ≠ `CSharpScript` (inline Roslyn).**
  The engine treats them as different runtimes with different YAML
  (`assemblyPath`+`typeName` vs `scriptContent`). The research collapses both
  to `task.script.csharp`.
- **`Container` ≠ `ForEach`/`While`.** `Container` has `ExecutionMode:
  Sequential|Parallel`, `ChildNodes[]`, `ChildConnections[]` (see
  `ContainerExample.yaml`). Loops are separate control-flow nodes with
  `Next` message semantics. Collapsing them as `container.loop` /
  `container.parallel` hides that difference.
- **`Switch` and `Timer` are missing entirely.**
- **`CSharpTask`/`PowerShellTask` hybrid nodes are missing.**

**Fix:** enumerate every `RuntimeType` value as a NodeKind and give each its
own registry entry, schema, and mapper; do not invent UI-only taxonomies.

### C3. First-class `NodeDefinition` fields are not surfaced at all

`NodeDefinition.cs` carries these properties on every node:
`JoinType (Any|All)`, `Priority`, `MaxConcurrentExecutions`, `RetryPolicy`,
`CircuitBreakerPolicy`, `CompensationNodeId`, `FallbackNodeId`,
`Configuration` (dictionary). The research paper mentions retry once and
ignores the rest. A designer that cannot edit these is a toy; retrofitting
them later means every node schema and every YAML round-trip path changes.
**Fix:** include these in `common/policies.schema.json` shared by every
NodeKind and render them in a grouped section of the property grid.

### C4. `connections[].triggerMessageType` is absent from the model

The engine's connection shape (`Sequential.yaml`, `Parallel.yaml`) has
`sourceNodeId`, `targetNodeId`, `triggerMessageType`, `isEnabled` — the
trigger is one of `Complete | Fail | Next | Cancel | Progress`
(`Enums/MessageType.cs`). This is the **entire semantics of control edges**,
and it is the mechanism by which loops (`Next` per iteration) work. The
research paper never mentions it. A graph model without it cannot round-trip
any workflow that has failure branches or iterator fan-out.
**Fix:** make `controlKind` a mandatory property of every control edge and
render it as the edge type/colour (Complete solid, Fail dashed red, Next
dotted, etc.).

### C5. `activeOutgoingEdgeIds` cannot be emitted by the engine as-is

The suggested SignalR payload includes `activeOutgoingEdgeIds?: string[]`,
but `src/ExecutionEngine/Events/*` only exposes node-level events
(`NodeStartedEvent`, `NodeCompletedEvent`, `NodeFailedEvent`,
`NodeProgressEvent`, `NodeCancelledEvent`) — no edge identity. Edge IDs are
a UI invention. Without a synthesis step this field is either always null or
forces engine changes we have not scoped.
**Fix:** introduce a BFF-side adapter that, on `NodeCompleted|NodeFailed|
NodeCancelled|NodeProgress`, looks up outbound connections whose
`triggerMessageType` matches and emits a derived `EdgeActivated` event with
the UI's edge ID. Document that edge animation is a *derived* signal.

### C6. The backend contract is essentially missing

The paper stops at "ASP.NET Core serves API + SignalR". There is no
endpoint inventory, no auth model, no OpenAPI contract, no ownership of the
YAML mapper, and no statement about where `ExecutionEngine` is invoked
(in-process vs. out-of-process). These are the decisions that determine
whether the UI is maintainable for more than one release.
**Fix:** add a dedicated "BFF design" section with routes
(`GET/POST /api/workflows`, `/validate`, `/start`, `/pause|resume|cancel`,
`/runs/{runId}/state` for resubscribe reconciliation, `/api/schemas/{kind}`,
hub `/hubs/execution`), a generated client (NSwag), and an explicit
statement that **YAML serialization lives server-side** (YamlDotNet) so
there is one YAML parser in the system, not two.

---

## High issues

### H1. Property-grid stack fights the app's chrome stack

§3 picks `@rjsf/mui`; §11 picks Tailwind 4 + shadcn/ui (Radix). That means
MUI + `@emotion/*` + Radix all ship in the bundle, emotion injects runtime
`<style>` tags which forces `style-src 'unsafe-inline'` in CSP, and we take
a permanent ~150–200 kB gzipped tax just for the property panel. The paper
justifies this with "MUI isolated inside the property panel only" but that
is a bundle-size argument, not a deployment-security argument.
**Fix:** use `@rjsf/core` with a thin shadcn/Radix theme, or `@rjsf/chakra-ui`
only if chakra is already present. Do not mix two design systems on first
release.

### H2. Undo granularity is unspecified and the snippet is wrong

The §3 snippet calls `patchNodeConfig(selected.id, e.formData)` on every
RJSF `onChange`, and `temporal(…, { partialize: { nodes, edges } })` wraps
the store. Every keystroke becomes an undo step. The paper even says
"debounced patch commits" in prose — the code contradicts it.
**Fix:** specify coalescing (e.g., `zundo` `handleSet` with a 400 ms
debounce for prop edits; discrete steps for structural changes). Write
this as a policy, not a suggestion.

### H3. Container/loop model doesn't match the engine's container shape

§2.5/§2.6 uses React Flow's `parentId` + `extent: 'parent'` — correct at
the canvas layer — but the serialization plan is silent on
`ContainerExample.yaml`'s shape, where container children are nested inside
the parent node under `ChildNodes:` / `ChildConnections:`, not flattened in
the top-level `nodes:` list. Without an explicit mapper, round-tripping a
container will either explode children to the root (breaking semantics) or
duplicate them.
**Fix:** define the flatten/nest transform in `toEngine`/`fromEngine` per
kind, with a fixture test that compares against `ContainerExample.yaml`
byte-for-byte (modulo the `x-ui:` block).

### H4. Tooling versions are speculative

`Vite 8.x`, `TypeScript 6.x`, `Storybook 10.x`, `Vitest 4.x`, `Playwright
1.59+`, `Tailwind 4`, `pnpm 10.x`. Several of these are either unreleased
or very new relative to the engine's `net8.0` + existing test stacks. Locking
the design document to specific major numbers invites "broken on day 1".
**Fix:** pin to current LTS-adjacent versions at the moment of project
creation (record them in `package.json` and the arch doc), and mark the
doc's version list as *aspirational, not normative*.

### H5. No schema-drift detection between UI, BFF, and engine

The paper argues "canonical JSON in the UI, YAML for the engine", which is
right, but offers no mechanism to detect drift when `NodeDefinition` grows
a field or `RuntimeType` gains a value. In a repo where the engine and UI
live together, the *maintenance* story is: a CI step generates TS types
from `schemas/*.json` **and** C# types via NJsonSchema, and fails the
build on diff.
**Fix:** add a "Contract & drift" subsection mandating this step. Without
it, the JSON-in/YAML-out boundary becomes silently lossy.

### H6. `isValidConnection` is the only validation gate shown

§2.2 checks types/cycles client-side. There is no server-side equivalent,
even though `NodeDefinition.Validate` already exists in the engine and
returns `ValidationResult[]`. The paper's §8 lists semantic rules but
never wires them to the engine path.
**Fix:** define three layers explicitly — L1 client (Ajv + connection
rules), L2 server (same JSON Schemas + cross-workflow checks like subflow
target resolution), L3 engine (`NodeDefinition.Validate`, graph
compilation, retry bounds). Unified `Diagnostic` shape across all three.

---

## Medium issues

### M1. Edge "type" is chosen for aesthetics, not semantics

§2.3 recommends `default/step/smoothstep/bezier` as a styling choice. In
this system, edges have a **channel** (control vs data) and a
**controlKind** (`Complete`/`Fail`/`Next`/`Cancel`/`Progress`). That is the
information the user needs to see. Picking by visual taste first, then
overloading colour for semantics, is backwards.
**Recommendation:** map edge component → `(channel, controlKind)` tuple;
pick one routing style globally (smoothstep) and vary stroke/colour/marker
by semantics.

### M2. "Pro templates" as a selection reason is misleading

§1 cites "Pro templates" as part of ecosystem strength. React Flow Pro is a
paid subscription with its own licensing implications. It should not appear
as a benefit in an architecture selection paragraph without a cost note.
**Recommendation:** drop the reference or mark it as explicitly out of
scope for this project.

### M3. State slice responsibilities are under-specified

§4 lists slices (graph/selection/execution/history/ui) but doesn't say
who owns `onNodesChange`/`onEdgesChange` and how measured dimensions
(which React Flow writes back into each node) interact with `temporal
partialize`. Measured-dimension churn will pollute undo history if we keep
the node object itself in the temporal snapshot.
**Recommendation:** either partialize `nodes.map(stripMeasured)` or move
`measured` to an ephemeral sibling map. Make this explicit.

### M4. `NodeDefinition` has no mapper/validate hooks in the registry

The registry example exposes `{ component, defaultData, schema }` only.
There is no `toEngine`/`fromEngine`/`validate`/`ports(node)` on the spec —
which is exactly where per-kind serialization and cross-field checks
belong. Without these, §7's "translate canonical JSON to YAML" becomes a
giant central switch statement.
**Recommendation:** extend `NodeDefinition` with
`toEngine`, `fromEngine`, `ports(node)`, and `validate(node, graph)`.
Make them required so a new kind can't be half-wired.

### M5. Switch-node port fan-out not addressed

Switch is missing (C2), but also: ports per node depend on configuration
(one output port per case, plus default). The paper's `PortHandle` example
iterates `data.ports`, but there is no guidance on *deriving* ports from
config. For `Switch` and for dynamic fan-out (e.g., `ForEach` producing
both a `Next` and `Complete` port), this is non-negotiable.
**Recommendation:** registry spec exposes `ports(node) => PortDef[]`
rather than storing static port arrays in `NodeData`.

### M6. SignalR resilience is only gestured at

§5 says "good reconnect behavior" but lists no policy: exponential backoff,
resubscribe on reconnect, reconciliation endpoint, UI behaviour during
gap. For a live-execution panel that users will watch for 30-minute
workflows, the gap behaviour matters more than the happy path.
**Recommendation:** specify `withAutomaticReconnect` delays, require
server `GET /runs/{runId}/state` for snapshot reconcile, and define the UI
state ("stale" indicator) during reconnect.

### M7. Copy/paste hotkey library is unspecified

§2.8 uses `hotkeys(...)` without naming a library. There are multiple
(`hotkeys-js`, `mousetrap`, `react-hotkeys-hook`). React Flow already
exposes selection/keyboard hooks; bolting on a second keymap engine will
fight with the canvas's own shortcuts.
**Recommendation:** standardize on `react-hotkeys-hook` (or the built-in
React Flow keyboard config) and confine it to a single `useShortcuts`
hook.

### M8. `produce` / Immer dependency is used but not declared

The §2.7 snippet calls `produce(...)` inside `set`, which implies Immer.
Immer is not in the tooling list. Either the snippet is wrong, or the
dependency should be on the list and its interaction with zundo equality
checks considered (structural sharing makes default referential equality
"work" but some equality configs fail).
**Recommendation:** pick plain immutable updates for the happy path and
reach for Immer only where nested writes are painful.

### M9. Monaco / script editing is not addressed at all

`CSharpScript`, `PowerShellTask.scriptContent`, and `CSharpTask` require a
code editor. The property-grid section talks about RJSF but never mentions
how multi-line code lands in a form. Monaco is a large dependency and
needs lazy loading + `wasm-unsafe-eval` CSP.
**Recommendation:** add a "custom RJSF widgets" subsection (MonacoScript,
Cron, KeyValue) with lazy-load and CSP notes.

---

## Integration concerns with ExecutionEngine / backend

### I1. No ownership decision for YAML parsing

The research says the UI keeps canonical JSON and "translates to/from YAML".
Two candidates: (a) translate in the browser with `js-yaml`, (b) translate
on the BFF with YamlDotNet. Only (b) is defensible long-term: the engine
already consumes YamlDotNet output; every tolerance difference between
`js-yaml` and YamlDotNet becomes a production bug. The paper does not
decide.

### I2. `x-ui:` block tolerance is assumed, not verified

§7 says "place them under an ignored `ui:` section if the engine tolerates
it". The engine uses YamlDotNet; tolerance depends on whether
`IgnoreUnmatchedProperties(true)` is set on the deserializer. I could not
find a guarantee in `src/ExecutionEngine/` from a skim. Treat this as a
required engine-side confirmation with a test fixture before we ship
positions inside the same YAML file.

### I3. Script nodes are privileged and have no gating plan

`CSharpScript`, `PowerShell`, `PowerShellTask`, `CSharpTask` execute
arbitrary code in the engine host. An authoring UI without a
privilege/gating plan *increases* the attack surface, because the UI makes
it easier to add script nodes. The paper is silent on this.

### I4. Subflow reference resolution is not specified

`SubflowNodeDefinition` points to another workflow. The UI must resolve
that reference at save time (exists? version? cyclic?). Not covered.

### I5. `Timer` node needs SSRF/cron hardening

A Timer with a cron schedule plus URL-typed downstream nodes is a common
SSRF vector. Not mentioned.

---

## Missing architectural concerns (high-impact gaps)

### Testing

- **No contract tests.** OpenAPI snapshot + SignalR event-shape fixtures
  between BFF and UI are the single cheapest defense against drift; not
  listed.
- **No visual-regression plan.** Storybook 10 is in the tooling list but
  no stories-per-NodeKind requirement, no Playwright `toHaveScreenshot`
  or Chromatic.
- **No backend tests.** The repo already uses MSTest + Reqnroll — the BFF
  and `YamlMapper` need both unit and integration tests with fixtures
  derived from `src/ExecutionEngine.Example/Workflows/*.yaml`.
- **No YAML round-trip fixture tests.** `fromEngine(toEngine(x)) === x`
  over the four example workflows is the closest thing we have to a
  regression harness; it is not proposed.
- **No performance/load tests.** The 1k-node claim is in the risk table
  with no test strategy to prove it on PRs.

### Security

- **No auth model.** Cookie+CSRF, AAD OBO, or something else — not
  decided. This blocks CSP.
- **No CSP example.** Monaco (wasm), MUI/emotion (`unsafe-inline`), and
  SignalR (`wss:`) each force specific directives; worth writing once.
- **No anti-forgery/rate-limit on state-changing endpoints.**
- **No workflow YAML input hardening** (size cap, `workflowId` character
  set, URL allowlists).
- **No signed-workflow / script-allowlist policy for privileged node kinds.**

### Deployment

- **Serves-from-wwwroot vs. serve-separately is left open.** This is an
  auth and CSP decision, not a preference.
- **No dev proxy spec** (`/api` and `/hubs` with `ws: true`).
- **No CI pipeline.** The type-generation step (§H5) must live here; it
  is the load-bearing drift gate.
- **No packaging story.** The repo is `win-x64` single-platform; should
  the SPA be published inside the engine's self-contained publish, or
  separately?

### Module boundaries / extensibility seams

- **"Add a new NodeKind" is not itemized.** A maintainable registry
  needs a one-page checklist: folder layout, required exports
  (`component, schema, uiSchema, defaultConfig, ports, toEngine,
  fromEngine, validate`), tests, storybook story, YAML fixture.
- **No architectural statement about what `canvas/` is allowed to
  import.** Without it, React-Flow-specific imports will leak
  everywhere and the store becomes un-testable in Node.

---

## Recommended additions (prioritised)

1. **Rewrite the status/event contract** against the engine's actual
   `NodeExecutionStatus`, `MessageType`, and `Events/*` classes (fixes
   C1, C5). Add a BFF-side `EngineEventBridge` that synthesizes
   `EdgeActivated`.
2. **Enumerate every `RuntimeType` value as a NodeKind** with its own
   schema, ports function, and mapper (fixes C2, M5). Explicitly separate
   `Container` from `ForEach`/`While`.
3. **Expose `JoinType`, `RetryPolicy`, `CircuitBreakerPolicy`,
   `CompensationNodeId`, `FallbackNodeId`, `Priority`,
   `MaxConcurrentExecutions`, `Configuration`** in a shared policy
   schema (fixes C3).
4. **Make edges carry `channel` + `controlKind`** and render semantics,
   not aesthetics (fixes C4, M1).
5. **Write the BFF section**: REST inventory, SignalR hub, server-side
   YAML ownership (YamlDotNet), OpenAPI generation, NSwag client, auth
   placeholder (fixes C6, I1).
6. **Extend the registry spec** with `toEngine`, `fromEngine`,
   `ports(node)`, `validate(node, graph)` as required members, and
   document the per-kind folder layout (fixes M4, M5, and the
   "add a kind" gap).
7. **Decide property-grid theming**: single design system, no MUI +
   shadcn mix; `@rjsf/core` with a Radix/shadcn theme (fixes H1).
8. **Specify undo coalescing** (e.g., 400 ms for config edits, discrete
   for structural changes) and `temporal.partialize` of
   dimension-stripped nodes (fixes H2, M3).
9. **Add a "contract & drift" section**: CI step that generates TS and
   C# types from `schemas/*.json`, plus round-trip YAML fixture tests
   against `src/ExecutionEngine.Example/Workflows/*.yaml` (fixes H5,
   testing gaps).
10. **Add a security section**: auth model (cookie+CSRF v1, AAD OBO v2),
    CSP example, script-node gating policy, input hardening (fixes
    I3, I5, all security gaps).
11. **Pin tooling to current LTS** when the project is created; mark the
    research paper's version list as aspirational (fixes H4).
12. **Verify `x-ui:` tolerance** against the engine's YamlDotNet
    deserializer and codify the result as a test (fixes I2). If not
    tolerated, persist UI metadata in a sidecar file, not in the engine
    YAML.
