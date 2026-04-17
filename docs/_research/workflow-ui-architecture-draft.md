# Workflow Designer UI — Architecture Draft

> Status: **Draft for review**. Scope: architecture only; no phased delivery plan.
> Engine anchor: `src/ExecutionEngine/` (net8.0). YAML shape anchored to
> `src/ExecutionEngine.Example/Workflows/*.yaml` and `NodeDefinition.cs`.
> Canvas library: **`@xyflow/react` v12** (React Flow 12).

---

## 1. Goals & Non-Goals

### Goals
- **Authoring**: drag-and-drop graph construction over every `RuntimeType` the
  engine currently supports (`Noop`, `CSharp`, `CSharpScript`, `CSharpTask`,
  `PowerShell`, `PowerShellTask`, `IfElse`, `ForEach`, `While`, `Switch`,
  `Subflow`, `Timer`, `Container`).
- **Decision trees**: first-class `IfElse` and `Switch` nodes with typed,
  labelled output ports (`true`/`false`, per-case).
- **Loops**: `ForEach`/`While` rendered as iterator containers with a distinct
  `Next`-message port and an aggregate `Complete` port.
- **Subflows / Containers**: navigable nesting; a container node can be entered
  to edit its child graph inline, matching `ContainerNode.ChildNodes` /
  `ChildConnections` YAML shape.
- **Property editing** via a schema-driven property grid that mirrors
  `NodeDefinition` subclasses (retry/circuit-breaker/compensation policies
  included).
- **Typed edges with validation**: control (`MessageType`) and typed data
  channels, enforced at connect time and at save time.
- **Multiple handles per node** (inbound/outbound; control vs. data).
- **Live execution** overlay: node status badges, animated active edges,
  per-node progress, pause/resume/cancel from the toolbar.
- **Undo/redo**, **save/load**, **YAML round-trip** with the engine.
- **Long-term maintainability**: typed contracts, schema-first, testable
  modules, no business logic in React components.

### Non-Goals (for this doc)
- Multi-user real-time co-editing (CRDT) — deferred.
- Visual debugger with breakpoints/step — deferred; live visualization only.
- In-browser C#/PowerShell execution — scripts are opaque text until the
  engine runs them server-side.
- A new workflow engine; we target the existing `ExecutionEngine` verbatim.
- Plugin marketplace / dynamic assembly upload from the browser.

---

## 2. High-Level Architecture

```
 ┌────────────────────────────────────────────────────────────────────────┐
 │                          Browser (SPA, Vite build)                     │
 │                                                                        │
 │  ┌────────────┐  ┌──────────────┐  ┌─────────────┐  ┌──────────────┐   │
 │  │  Palette   │  │   Canvas     │  │  Property   │  │  Toolbar +   │   │
 │  │ (NodeKinds)│  │ (@xyflow/    │  │    Grid     │  │  Run Panel   │   │
 │  │            │  │  react v12)  │  │  (RJSF)     │  │              │   │
 │  └─────┬──────┘  └──────┬───────┘  └──────┬──────┘  └──────┬───────┘   │
 │        └────────────────┴─────────────────┴────────────────┘           │
 │                              │                                         │
 │                  ┌───────────▼────────────┐                            │
 │                  │   Zustand store        │  (graph, selection,        │
 │                  │   + zundo (history)    │   history, execution,      │
 │                  │                        │   palette, clipboard)      │
 │                  └───────────┬────────────┘                            │
 │                              │                                         │
 │   ┌───────────┐  ┌──────────▼──────────┐  ┌──────────────┐             │
 │   │ YAML/JSON │  │ SignalR client      │  │  Validation  │             │
 │   │  io/      │  │ (@microsoft/        │  │  (Ajv + rules)│            │
 │   │           │  │  signalr)           │  │              │             │
 │   └─────┬─────┘  └──────────┬──────────┘  └──────┬───────┘             │
 └─────────┼─────────────────────────────────────────┼────────────────────┘
           │ HTTPS (JSON)                            │ WS (SignalR)
           ▼                                         ▼
 ┌────────────────────────────────────────────────────────────────────────┐
 │                 ASP.NET Core BFF (Minimal API, net8.0)                 │
 │                                                                        │
 │   REST:  /api/workflows, /validate, /start, /pause, /resume, /cancel   │
 │   Hub:   /hubs/execution   (NodeStatusChanged, EdgeActivated, …)       │
 │                                                                        │
 │   ┌───────────────┐   ┌──────────────────┐   ┌──────────────────┐      │
 │   │ YamlMapper    │   │ WorkflowService  │   │ ExecutionBridge  │      │
 │   │ (UI ⇄ Engine) │   │ (CRUD+validate)  │   │ (IWorkflowEngine │      │
 │   │               │   │                  │   │  events ⇒ Hub)   │      │
 │   └──────┬────────┘   └─────────┬────────┘   └────────┬─────────┘      │
 │          │                      │                     │                │
 │          └───────────┬──────────┴─────────┬───────────┘                │
 │                     ▼                    ▼                             │
 │          ┌─────────────────────┐   ┌─────────────────────┐             │
 │          │  ExecutionEngine    │   │  Persistence        │             │
 │          │ (existing, net8.0)  │   │  (IWorkflowStore:   │             │
 │          │  IWorkflowEngine,   │   │   FS/SQLite/Blob)   │             │
 │          │  WorkflowEngine,    │   │                     │             │
 │          │  NodeFactory, …)    │   │                     │             │
 │          └─────────────────────┘   └─────────────────────┘             │
 └────────────────────────────────────────────────────────────────────────┘
```

**Process boundary**: BFF runs in-process with `ExecutionEngine` in v1 (same
`net8.0` host). Later, the engine can be split behind a gRPC boundary without
changing the browser contract.

---

## 3. Frontend Module Layout

```
web/                                     # new sibling to src/, Vite + React 18 + TS 5
├── index.html
├── vite.config.ts
├── package.json
├── tsconfig.json
├── src/
│   ├── app/                             # app shell, routing, providers
│   │   ├── App.tsx
│   │   ├── routes.tsx                   # /, /workflows/:id, /workflows/:id/run/:runId
│   │   ├── providers/
│   │   │   ├── QueryProvider.tsx        # @tanstack/react-query
│   │   │   ├── SignalRProvider.tsx
│   │   │   └── ThemeProvider.tsx
│   │   └── layout/ (Sidebar, Header, StatusBar)
│   │
│   ├── canvas/                          # @xyflow/react host + glue
│   │   ├── WorkflowCanvas.tsx           # <ReactFlow ...> composition root
│   │   ├── CanvasContextMenu.tsx
│   │   ├── selection.ts                 # marquee, keyboard selection
│   │   ├── copyPaste.ts
│   │   └── viewport.ts                  # fitView, focus node, zoom-to-selection
│   │
│   ├── nodes/                           # one folder per NodeKind
│   │   ├── registry.ts                  # NodeKindRegistry (see §5)
│   │   ├── BaseNode.tsx                 # shared chrome (header, badge, ports)
│   │   ├── task/        (TaskNode.tsx, schema.ts, mapper.ts, rules.ts)
│   │   ├── decision/    (DecisionNode.tsx, …)  # IfElse
│   │   ├── switch/
│   │   ├── loop/        (ForEachNode, WhileNode)
│   │   ├── container/
│   │   ├── subflow/
│   │   ├── timer/
│   │   ├── script-csharp/
│   │   ├── script-powershell/
│   │   ├── start/
│   │   └── end/
│   │
│   ├── edges/
│   │   ├── ControlEdge.tsx              # default, Complete
│   │   ├── FailEdge.tsx                 # dashed red
│   │   ├── NextEdge.tsx                 # loop Next
│   │   ├── DataEdge.tsx                 # typed data channel
│   │   └── AnimatedRunEdge.tsx          # wraps during live run
│   │
│   ├── ports/
│   │   ├── TypedHandle.tsx              # wraps <Handle> with PortDef metadata
│   │   ├── portCatalog.ts               # per-NodeKind ports (pure data)
│   │   └── portStyles.ts
│   │
│   ├── property-grid/
│   │   ├── PropertyGrid.tsx             # RJSF host
│   │   ├── widgets/                     # MonacoScriptWidget, CronWidget, KeyValueWidget
│   │   ├── fields/                      # RetryPolicyField, CircuitBreakerField
│   │   └── formContext.ts               # bidirectional bind to store
│   │
│   ├── palette/
│   │   ├── Palette.tsx                  # draggable NodeKinds, grouped
│   │   ├── search.ts
│   │   └── drag.ts                      # onDragStart sets DataTransfer('application/x-wf-node')
│   │
│   ├── toolbar/
│   │   ├── Toolbar.tsx                  # Save, Validate, Run, Pause, Resume, Cancel
│   │   ├── UndoRedo.tsx
│   │   └── LayoutButton.tsx             # triggers ELK auto-layout
│   │
│   ├── minimap/
│   │   └── MiniMap.tsx                  # thin wrapper around <MiniMap />
│   │
│   ├── state/                           # Zustand slices (see §8)
│   │   ├── store.ts                     # createStore + zundo middleware
│   │   ├── graphSlice.ts
│   │   ├── selectionSlice.ts
│   │   ├── historySlice.ts
│   │   ├── executionSlice.ts
│   │   ├── paletteSlice.ts
│   │   ├── clipboardSlice.ts
│   │   └── selectors.ts
│   │
│   ├── io/                              # YAML/JSON serializers
│   │   ├── yaml.ts                      # js-yaml, SCHEMA_VERSION
│   │   ├── toEngine.ts                  # UiGraph  -> EngineWorkflow
│   │   ├── fromEngine.ts                # EngineWorkflow -> UiGraph
│   │   ├── uiMeta.ts                    # positions/comments under `x-ui:` block
│   │   └── migrations/                  # v1→v2 etc.
│   │
│   ├── execution/                       # SignalR client + animation
│   │   ├── hubClient.ts                 # HubConnectionBuilder, reconnect policy
│   │   ├── events.ts                    # discriminated unions (§9)
│   │   ├── animationAdapter.ts          # events → edge.animated / node.data.status
│   │   └── runController.ts             # start/pause/resume/cancel API
│   │
│   ├── validation/
│   │   ├── schemaValidator.ts           # Ajv instance, shared JSON Schemas
│   │   ├── connectionRules.ts           # isValidConnection matrix (§6)
│   │   ├── graphRules.ts                # cycles (outside loops), orphans, required ports
│   │   └── index.ts                     # runAll(graph) => Diagnostic[]
│   │
│   ├── layout/                          # ELK-based auto layout
│   │   ├── elkLayout.ts                 # elkjs, layered + orthogonal routing
│   │   └── index.ts
│   │
│   ├── hooks/
│   │   ├── useGraphStore.ts             # typed selectors
│   │   ├── useSelection.ts
│   │   ├── useShortcuts.ts              # Ctrl+Z/Y, Del, Ctrl+D, Ctrl+S
│   │   └── useExecutionStream.ts
│   │
│   ├── lib/                             # framework-agnostic helpers
│   │   ├── id.ts                        # nanoid + slug
│   │   ├── graphAlgos.ts                # topo sort, cycle detection, bfs
│   │   └── types.ts
│   │
│   ├── schemas/                         # JSON Schemas for each NodeKind
│   │   ├── task.schema.json
│   │   ├── decision.schema.json
│   │   ├── switch.schema.json
│   │   ├── forEach.schema.json
│   │   ├── while.schema.json
│   │   ├── container.schema.json
│   │   ├── subflow.schema.json
│   │   ├── timer.schema.json
│   │   ├── script-csharp.schema.json
│   │   ├── script-powershell.schema.json
│   │   ├── common/policies.schema.json  # retry, circuit-breaker
│   │   └── workflow.schema.json         # root graph
│   │
│   └── tests/                           # Vitest + RTL
│       ├── unit/
│       ├── components/
│       └── fixtures/                    # canonical yaml ⇄ json
└── e2e/                                 # Playwright
```

**Boundary rules**
- `nodes/*/mapper.ts` owns *only* UI ⇄ engine translation for that kind; never
  touches React.
- `state/*` never imports from `@xyflow/react`; it holds pure data.
- `canvas/`, `nodes/`, `edges/`, `ports/` are the only React-Flow-aware areas.
- `io/` is pure, isomorphic, unit-tested. No DOM.
- `execution/` depends on `state/executionSlice` through a thin adapter so we
  can unit-test the mapper without a live hub.

---

## 4. Data Model (TypeScript)

```ts
// src/lib/types.ts
export const SCHEMA_VERSION = '1.0.0' as const;

export enum NodeKind {
  Start            = 'start',
  End              = 'end',
  Task             = 'task',              // RuntimeType.CSharp (compiled)
  CSharpScript     = 'script-csharp',     // RuntimeType.CSharpScript
  PowerShell       = 'script-powershell', // RuntimeType.PowerShell / PowerShellTask
  Decision         = 'decision',          // RuntimeType.IfElse
  Switch           = 'switch',            // RuntimeType.Switch
  ForEach          = 'forEach',           // RuntimeType.ForEach
  While            = 'while',             // RuntimeType.While
  Container        = 'container',         // RuntimeType.Container
  Subflow          = 'subflow',           // RuntimeType.Subflow
  Timer            = 'timer',             // RuntimeType.Timer
  Noop             = 'noop',
}

export type PortDirection = 'in' | 'out';
export type PortChannel   = 'control' | 'data';

// Control channel uses engine MessageType; data channel carries a TS-side type tag.
export type ControlKind = 'Complete' | 'Fail' | 'Next' | 'Cancel' | 'Progress';

export interface PortDef {
  id: string;                   // stable within node; e.g. 'out:true', 'in:item'
  direction: PortDirection;
  channel: PortChannel;
  label?: string;
  controlKind?: ControlKind;    // when channel === 'control'
  dataType?: string;            // when channel === 'data', e.g. 'string', 'int', 'T'
  maxConnections?: number;      // default: in=∞, out=∞ except Decision out:true/false = 1
  required?: boolean;
}

export interface NodeDef<TConfig = Record<string, unknown>> {
  id: string;                   // engine NodeId
  kind: NodeKind;
  name: string;                 // engine NodeName
  description?: string;
  position: { x: number; y: number };
  size?:    { width: number; height: number };
  ports: PortDef[];             // computed from registry + config (e.g. Switch cases)
  config: TConfig;              // schema-shaped; validated against schemas/<kind>.schema.json
  joinType?: 'Any' | 'All';
  priority?: 'Low' | 'Normal' | 'High' | 'Critical';
  maxConcurrentExecutions?: number;
  retryPolicy?: RetryPolicy;
  circuitBreakerPolicy?: CircuitBreakerPolicy;
  compensationNodeId?: string;
  fallbackNodeId?: string;
  parentId?: string;            // for container children
  runtime: {                    // engine-facing; authored by mapper
    runtimeType: string;        // 'CSharp' | 'CSharpScript' | …
    assemblyPath?: string;
    typeName?: string;
    scriptContent?: string;
  };
}

export interface EdgeDef {
  id: string;
  source: string;                         // node id
  sourceHandle: string;                   // port id
  target: string;
  targetHandle: string;
  channel: PortChannel;
  controlKind?: ControlKind;              // mirrors source port
  label?: string;
  isEnabled: boolean;                     // maps to connection.isEnabled
}

export interface WorkflowGraph {
  schemaVersion: typeof SCHEMA_VERSION;
  workflowId: string;
  workflowName: string;
  description?: string;
  defaultVariables?: Record<string, unknown>;
  nodes: NodeDef[];
  edges: EdgeDef[];
  // Container children live in nodes[] with parentId set; Subflow targets are by id.
}

export enum NodeStatus {
  Idle      = 'idle',
  Pending   = 'pending',
  Running   = 'running',
  Completed = 'completed',
  Failed    = 'failed',
  Cancelled = 'cancelled',
  Skipped   = 'skipped',
}
```

`NodeStatus` is a 1:1 superset of the engine's `NodeExecutionStatus`
(`Pending/Running/Completed/Failed/Cancelled/Skipped`) plus `Idle` for
design-time.

---

## 5. Node Extensibility — NodeKind Registry

A **NodeKind** is registered once and bundles five concerns: the React
component, the JSON Schema for the property grid, the port catalog, the
UI⇄engine mapper, and kind-specific validation rules. Nothing in
`canvas/`/`palette/` references concrete kinds; both iterate the registry.

```ts
// src/nodes/registry.ts
import type { JSONSchema7 } from 'json-schema';
import type { ComponentType } from 'react';
import type { NodeDef, PortDef, WorkflowGraph } from '@/lib/types';

export interface NodeKindSpec<TConfig = any> {
  kind: NodeKind;
  displayName: string;
  category: 'Flow' | 'Control' | 'Script' | 'Loop' | 'Composition' | 'IO';
  icon: ComponentType;
  defaultConfig: TConfig;
  ports: (node: NodeDef<TConfig>) => PortDef[];   // may depend on config (Switch cases)
  component: ComponentType<{ node: NodeDef<TConfig> }>;
  schema: JSONSchema7;
  uiSchema?: Record<string, unknown>;             // RJSF ui hints
  validate?: (node: NodeDef<TConfig>, g: WorkflowGraph) => Diagnostic[];
  toEngine: (n: NodeDef<TConfig>) => EngineNodeYaml;
  fromEngine: (y: EngineNodeYaml) => NodeDef<TConfig>;
}

const reg = new Map<NodeKind, NodeKindSpec>();
export const registerNodeKind = (s: NodeKindSpec) => reg.set(s.kind, s);
export const getNodeKind      = (k: NodeKind) => {
  const s = reg.get(k);
  if (!s) throw new Error(`Unknown NodeKind: ${k}`);
  return s;
};
export const listNodeKinds = () => [...reg.values()];
```

### Complete example: Decision (IfElse) node

```ts
// src/nodes/decision/schema.ts
import type { JSONSchema7 } from 'json-schema';
export const decisionSchema: JSONSchema7 = {
  $id: 'https://workflow/schemas/decision.json',
  type: 'object',
  required: ['condition'],
  properties: {
    condition: {
      type: 'string',
      title: 'Condition (C# expression)',
      description: 'Evaluated against WorkflowExecutionContext. e.g. ctx.Get<int>("count") > 0'
    },
    description: { type: 'string' },
  },
  additionalProperties: false,
};
```

```ts
// src/nodes/decision/ports.ts
import type { PortDef, NodeDef } from '@/lib/types';
export const decisionPorts = (_n: NodeDef): PortDef[] => ([
  { id: 'in',        direction: 'in',  channel: 'control', controlKind: 'Complete', required: true },
  { id: 'out:true',  direction: 'out', channel: 'control', controlKind: 'Complete', label: 'true',  maxConnections: 1 },
  { id: 'out:false', direction: 'out', channel: 'control', controlKind: 'Complete', label: 'false', maxConnections: 1 },
  { id: 'out:fail',  direction: 'out', channel: 'control', controlKind: 'Fail',     label: 'on fail' },
]);
```

```tsx
// src/nodes/decision/DecisionNode.tsx
import { memo } from 'react';
import { BaseNode } from '../BaseNode';
import { TypedHandle } from '@/ports/TypedHandle';
import { decisionPorts } from './ports';

export const DecisionNode = memo(({ node }: { node: NodeDef }) => (
  <BaseNode node={node} shape="diamond">
    {decisionPorts(node).map(p => <TypedHandle key={p.id} port={p} />)}
  </BaseNode>
));
```

```ts
// src/nodes/decision/mapper.ts — UI ⇄ engine YAML
export const toEngine = (n: NodeDef): EngineNodeYaml => ({
  nodeId: n.id,
  nodeName: n.name,
  runtimeType: 'IfElse',
  description: n.description,
  configuration: { condition: (n.config as any).condition },
  joinType: n.joinType ?? 'Any',
});
export const fromEngine = (y: EngineNodeYaml): NodeDef => ({
  id: y.nodeId,
  kind: NodeKind.Decision,
  name: y.nodeName ?? y.nodeId,
  description: y.description,
  position: uiMeta(y).position ?? { x: 0, y: 0 },
  ports: [],                                   // filled by registry.ports(node)
  config: { condition: y.configuration?.condition ?? '' },
  runtime: { runtimeType: 'IfElse' },
});
```

```ts
// src/nodes/decision/index.ts — registration
registerNodeKind({
  kind: NodeKind.Decision,
  displayName: 'Decision (If/Else)',
  category: 'Control',
  icon: DiamondIcon,
  defaultConfig: { condition: '' },
  ports: decisionPorts,
  component: DecisionNode,
  schema: decisionSchema,
  uiSchema: { condition: { 'ui:widget': 'MonacoScriptWidget', 'ui:options': { language: 'csharp' } } },
  validate: (n, g) => {
    const outs = g.edges.filter(e => e.source === n.id);
    const ds: Diagnostic[] = [];
    if (!outs.some(e => e.sourceHandle === 'out:true'))
      ds.push({ level: 'warn', nodeId: n.id, message: "Decision has no 'true' branch" });
    if (!outs.some(e => e.sourceHandle === 'out:false'))
      ds.push({ level: 'warn', nodeId: n.id, message: "Decision has no 'false' branch" });
    return ds;
  },
  toEngine, fromEngine,
});
```

Adding a new kind = drop a folder under `src/nodes/<kind>/` with these five
files and one `register…()` call in the barrel. No changes to canvas/palette.

---

## 6. Ports & Connection Rules

Ports are metadata, not React. `TypedHandle` renders a `<Handle>` (from
`@xyflow/react`) and forwards `isValidConnection` to a central rule engine.

### Compatibility matrix

| Source →        | Target (control) | Target (data&lt;T&gt;) | Target (data&lt;U≠T&gt;) |
|-----------------|------------------|------------------------|--------------------------|
| control         | ✅ (message kinds routed) | ❌                     | ❌                       |
| data&lt;T&gt;   | ❌               | ✅                     | ❌                       |
| data&lt;any&gt; | ❌               | ✅                     | ✅                       |

Additional rules:
1. Source must be `direction: 'out'`, target `'in'`.
2. Self-connections forbidden.
3. `maxConnections` per port enforced on both sides.
4. No cycles **unless** the cycle is contained within a `ForEach`/`While`
   iterator body (detected by walking `parentId` chain).
5. Cross-container edges disallowed except through the container's own in/out
   ports.
6. Control edges carry `controlKind`; we ignore this during compatibility but
   persist it to YAML as `triggerMessageType`.

```ts
// src/validation/connectionRules.ts
import type { Connection } from '@xyflow/react';
export const isValidConnection = (
  c: Connection, g: WorkflowGraph
): boolean => {
  if (!c.source || !c.target || c.source === c.target) return false;
  const s = findPort(g, c.source!, c.sourceHandle!);
  const t = findPort(g, c.target!, c.targetHandle!);
  if (!s || !t) return false;
  if (s.direction !== 'out' || t.direction !== 'in') return false;
  if (s.channel !== t.channel) return false;
  if (s.channel === 'data' &&
      t.dataType !== 'any' && s.dataType !== 'any' &&
      s.dataType !== t.dataType) return false;
  if (countEdgesOnPort(g, c.source!,  c.sourceHandle!) >= (s.maxConnections ?? Infinity)) return false;
  if (countEdgesOnPort(g, c.target!,  c.targetHandle!) >= (t.maxConnections ?? Infinity)) return false;
  if (createsIllegalCycle(g, c)) return false;
  return true;
};
```

Wired into React Flow:

```tsx
<ReactFlow
  nodes={nodes} edges={edges}
  onNodesChange={onNodesChange} onEdgesChange={onEdgesChange}
  onConnect={onConnect}
  isValidConnection={(c) => isValidConnection(c, graph)}
  nodeTypes={nodeTypes} edgeTypes={edgeTypes}
  fitView
>
  <Background /><MiniMap /><Controls /><Panel position="top-left">…</Panel>
</ReactFlow>
```

---

## 7. Property Grid

**Choice**: `@rjsf/core` + `@rjsf/validator-ajv8`. Rationale: drives forms
directly from the JSON Schemas in `src/schemas/`, supports nested objects
(retry/circuit-breaker policies), custom widgets (Monaco for scripts,
cron editor for `Timer.Schedule`), and is the same Ajv instance used by
`validation/schemaValidator.ts` — one source of truth.

- **Schema origin**: each `NodeKindSpec.schema` is authored in
  `src/schemas/<kind>.schema.json` and imported into the registry entry.
  These schemas are *also* published by the BFF at `/api/schemas/{kind}` and
  consumed by tests for contract drift checks.
- **Live-reactive binding**: the grid subscribes to a *single* node slice
  (`selectors.selectedNodeConfig(nodeId)`). On change it dispatches
  `graph.updateNodeConfig(id, patch)`. The store applies RFC-6902 patches so
  zundo records minimal diffs.
- **Custom widgets**:
  - `MonacoScriptWidget` — C# / PowerShell, syntax only, no IntelliSense v1.
  - `CronWidget` — timer schedule.
  - `RetryPolicyField` — combines `RetryStrategy`, delays, maxAttempts from
    `ExecutionEngine.Policies.RetryPolicy`.
  - `KeyValueWidget` — `configuration: Dictionary<string,object>`.
- **Debouncing**: 120 ms on text fields; instant on booleans/enums.
- **Validation feedback**: Ajv errors render inline; cross-node errors from
  `validation/graphRules.ts` bubble to a Problems panel keyed by nodeId.

---

## 8. State Management (Zustand)

Single store, composed of slices, wrapped in `zundo` for temporal/undo-redo.
Selectors are hand-written and memoized with `useShallow` to avoid re-renders
on unrelated changes — React Flow is re-render-sensitive with large graphs.

```ts
// src/state/store.ts
import { create } from 'zustand';
import { temporal } from 'zundo';
import { devtools } from 'zustand/middleware';
import { graphSlice, GraphSlice } from './graphSlice';
import { selectionSlice, SelectionSlice } from './selectionSlice';
import { executionSlice, ExecutionSlice } from './executionSlice';
import { paletteSlice, PaletteSlice } from './paletteSlice';
import { clipboardSlice, ClipboardSlice } from './clipboardSlice';

export type AppState = GraphSlice & SelectionSlice & ExecutionSlice
                     & PaletteSlice & ClipboardSlice;

export const useAppStore = create<AppState>()(
  devtools(
    temporal(
      (set, get, api) => ({
        ...graphSlice(set, get, api),
        ...selectionSlice(set, get, api),
        ...executionSlice(set, get, api),
        ...paletteSlice(set, get, api),
        ...clipboardSlice(set, get, api),
      }),
      {
        // Only graph edits are undoable. Execution/selection are NOT.
        partialize: (s) => ({ nodes: s.nodes, edges: s.edges,
                              workflowId: s.workflowId, workflowName: s.workflowName }),
        limit: 200,
        equality: (a, b) => a === b,
      }
    )
  )
);
export const useTemporal = useAppStore.temporal; // .undo(), .redo(), .clear()
```

### Slice responsibilities

| Slice       | Owns                                                                 |
|-------------|----------------------------------------------------------------------|
| `graph`     | `nodes`, `edges`, `workflowId`, `workflowName`, `defaultVariables`, CRUD ops, patch-based updates |
| `selection` | `selectedNodeIds`, `selectedEdgeIds`, hover state (non-undoable)     |
| `history`   | Exposed through `useTemporal`; thin wrapper for toolbar              |
| `execution` | `runId`, per-node `status`, per-edge `active`, `phase`, server errors |
| `palette`   | Palette search text, collapsed categories                            |
| `clipboard` | Copied subgraph, offset for paste placement                          |

### Selector patterns (avoid full-tree re-renders)

```ts
// src/state/selectors.ts
import { useShallow } from 'zustand/react/shallow';

export const useNodeById = (id: string) =>
  useAppStore(useShallow(s => s.nodes.find(n => n.id === id)));

export const useEdgeIdsForNode = (id: string) =>
  useAppStore(useShallow(
    s => s.edges.filter(e => e.source === id || e.target === id).map(e => e.id)
  ));

// React Flow integration: feed it *arrays* only when necessary.
// Status changes are applied as node-data patches, not new arrays, so
// unchanged nodes keep identity.
```

Execution updates mutate `node.data.status` / `edge.data.active` in place via
a dedicated `executionSlice.applyEvent(evt)` reducer; that slice is outside
`temporal.partialize`, so live runs don't pollute undo history.

---

## 9. Execution Visualization

### SignalR event contract (server → client)

```ts
// src/execution/events.ts
export type RunEvent =
  | { type: 'WorkflowPhaseChanged'; runId: string; phase: 'Pending'|'Running'|'Paused'|'Completed'|'Failed'|'Cancelled'; at: string }
  | { type: 'NodeStatusChanged';    runId: string; nodeId: string;
                                    status: 'Pending'|'Running'|'Completed'|'Failed'|'Cancelled'|'Skipped';
                                    attempt: number; at: string; error?: { message: string; stack?: string } }
  | { type: 'NodeProgress';         runId: string; nodeId: string; percent?: number; message?: string; at: string }
  | { type: 'EdgeActivated';        runId: string; edgeId: string;
                                    messageType: 'Complete'|'Fail'|'Next'|'Cancel'|'Progress'; at: string }
  | { type: 'VariableChanged';      runId: string; key: string; valuePreview: string; at: string };
```

- Client opens `/hubs/execution` and invokes `Subscribe(runId)`.
- `animationAdapter` maps events to store mutations:
  - `NodeStatusChanged` → `node.data.status` + badge color + spinner overlay.
  - `EdgeActivated` → toggles `edge.animated = true`, starts a 1.2 s pulse then
    decays to `data.lastActivatedAt` tint (React Flow v12 natively supports
    `animated: true` on edges; we wrap it in `AnimatedRunEdge` for richer
    styling).
  - `WorkflowPhaseChanged` updates toolbar + status bar; on `Completed/Failed`
    the client calls `Unsubscribe`.
- Reconnect policy: exponential backoff, resubscribe on reconnect; on a gap,
  the client pulls `/api/workflows/{id}/runs/{runId}/state` to reconcile
  (server returns current `NodeStatus[]`).

### Engine-side changes (additive, non-breaking)

- Existing events (`NodeStartedEvent`, `NodeCompletedEvent`,
  `NodeFailedEvent`, `NodeCancelledEvent`, `NodeProgressEvent`,
  `WorkflowStartedEvent`, `WorkflowCompletedEvent`, `WorkflowFailedEvent`,
  `WorkflowCancelledEvent`) already exist in `src/ExecutionEngine/Events/`.
- Add an `IWorkflowEventPublisher` port in the engine and a default
  in-process implementation. The BFF registers an adapter that translates
  engine events to `RunEvent` and calls `IHubContext<ExecutionHub>`.
- `EdgeActivated` does **not** exist in the engine; we synthesize it in the
  BFF from `NodeCompleted/NodeFailed/Message…` by looking up outbound
  connections whose `triggerMessageType` matches the completion kind.

```csharp
// src/Workflow.Api/Execution/ExecutionHub.cs
public sealed class ExecutionHub : Hub
{
    public Task Subscribe(string runId)   => Groups.AddToGroupAsync(Context.ConnectionId, runId);
    public Task Unsubscribe(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
```

```csharp
// src/Workflow.Api/Execution/EngineEventBridge.cs
internal sealed class EngineEventBridge : IWorkflowEventSubscriber
{
    private readonly IHubContext<ExecutionHub> hub;
    public EngineEventBridge(IHubContext<ExecutionHub> hub) { this.hub = hub; }

    public Task OnNodeStatus(NodeEvent e, CancellationToken ct) =>
        this.hub.Clients.Group(e.RunId).SendAsync("event", new {
            type   = "NodeStatusChanged",
            runId  = e.RunId,
            nodeId = e.NodeId,
            status = e.Status.ToString(),
            attempt = e.Attempt,
            at     = e.Timestamp,
            error  = e.Error is null ? null : new { message = e.Error.Message }
        }, ct);
}
```

---

## 10. Backend (BFF) Design

- **Host**: `src/Workflow.Api/` — ASP.NET Core Minimal API, `net8.0`, project
  references `ExecutionEngine`.
- **Serialization**: `System.Text.Json` (camelCase) for REST; YamlDotNet for
  engine YAML.
- **Store**: `IWorkflowStore` abstraction. Default: filesystem under
  `%WORKFLOWS_ROOT%`. SQLite variant for runs/history. Blob later.

### Endpoints

| Verb  | Route                                  | Purpose                                         |
|-------|----------------------------------------|-------------------------------------------------|
| GET   | `/api/workflows`                       | List (id, name, schemaVersion, updatedAt)       |
| GET   | `/api/workflows/{id}`                  | Full `WorkflowDto` (JSON, UI-native shape)      |
| GET   | `/api/workflows/{id}/yaml`             | Engine YAML                                     |
| POST  | `/api/workflows`                       | Create (JSON)                                   |
| PUT   | `/api/workflows/{id}`                  | Upsert JSON                                     |
| PUT   | `/api/workflows/{id}/yaml`             | Upsert YAML (for CLI parity)                    |
| POST  | `/api/workflows/{id}/validate`         | Returns `Diagnostic[]` (schema + engine checks) |
| POST  | `/api/workflows/{id}/start`            | Returns `runId`                                 |
| POST  | `/api/workflows/{id}/runs/{runId}/pause`  | -                                            |
| POST  | `/api/workflows/{id}/runs/{runId}/resume` | -                                            |
| POST  | `/api/workflows/{id}/runs/{runId}/cancel` | -                                            |
| GET   | `/api/workflows/{id}/runs/{runId}/state`  | Current node statuses (reconcile after WS gap) |
| GET   | `/api/schemas/{kind}`                  | JSON Schema for a NodeKind (drift detection)    |
| WS    | `/hubs/execution`                      | SignalR hub (§9)                                |

### YAML production / consumption

- `YamlMapper` (C#) is the authority for the **engine** YAML shape. It uses
  YamlDotNet with a custom `INamingConvention` to match the lower-camel
  `nodeId` / `runtimeType` / `connections` casing observed in the examples.
- The BFF exposes **two** forms:
  - `WorkflowDto` (JSON) — UI-native, includes `position`, `schemaVersion`,
    `ports`. The Vite app speaks this everywhere.
  - Engine YAML — produced on demand; UI-only fields (positions, collapsed
    containers, colors, comments) are preserved under a top-level
    `x-ui:` block that YamlDotNet serializes after `connections:` (engine
    ignores unknown keys; we assert this with an integration test).

### Why JSON for the UI and YAML for the engine?

- The UI shouldn't parse YAML in the browser for the happy path; JSON is
  cheaper, faster, and type-safe with our OpenAPI contract. YAML round-trip
  is used when importing/exporting files, running from CLI, or when the user
  explicitly views YAML in a side panel.

---

## 11. YAML Round-Trip

### Compatibility

The engine consumes YAML with this top-level shape (observed in
`Sequential.yaml`, `Parallel.yaml`, `ContainerExample.yaml`,
`etl-workflow-with-kusto.yaml`):

```yaml
workflowId: …
workflowName: …
description: …
defaultVariables: { … }
nodes:
  - nodeId: …
    nodeName: …
    runtimeType: CSharp | CSharpScript | IfElse | ForEach | Container | …
    assemblyPath: …          # CSharp only
    typeName: …              # CSharp only
    scriptContent: |         # script kinds
    configuration: { … }
    ExecutionMode: Parallel  # Container only
    ChildNodes: [ … ]        # Container only
    ChildConnections: [ … ]  # Container only
connections:
  - sourceNodeId: …
    targetNodeId: …
    triggerMessageType: Complete | Fail | Next | Cancel | Progress
    isEnabled: true
```

### Our additions

```yaml
schemaVersion: "1.0.0"        # NEW — tolerated by engine (unknown key)
x-ui:                         # NEW — UI-only metadata block
  positions:
    validate-order: { x: 120, y: 80 }
    process-payment: { x: 360, y: 80 }
  collapsedContainers: [validation-container]
  edgeLabels:
    e-1: "happy path"
  palette: { theme: "dark" }
```

- **Preservation of comments**: **not guaranteed**. js-yaml / YamlDotNet do
  not round-trip comments reliably. We explicitly document this; if users
  need comments they keep them in source control and edit YAML by hand.
- **Versioning**: `schemaVersion` is required in the UI JSON form and
  optional in YAML (defaulting to `"1.0.0"` on read). `io/migrations/` holds
  pure functions `migrate_1_0_to_1_1(doc) → doc` invoked by `fromEngine()`.
- **Fidelity test**: `tests/fixtures/*.yaml` ↔ `*.json` pairs drive a
  Vitest suite `io/roundtrip.test.ts` that asserts `fromEngine(toEngine(x))
  === x` for the UI model (positions and all).

---

## 12. Validation Layers

```
┌──────────────────────────────┐   Ajv against src/schemas/*.json
│ L1  Client schema            │   + uniqueness of nodeId, port maxConnections
│   (src/validation)           │   Inline errors in property grid, problems panel
└──────────┬───────────────────┘
           │ POST /api/workflows/{id}/validate
┌──────────▼───────────────────┐   Same JSON Schemas published at
│ L2  Server schema            │   /api/schemas/{kind} (authoritative copy)
│   (FluentValidation + Ajv    │   + cross-workflow checks (Subflow target exists)
│    parity via NJsonSchema)   │
└──────────┬───────────────────┘
           │
┌──────────▼───────────────────┐   NodeDefinition.Validate(…) (already in engine)
│ L3  Engine semantic          │   Graph compilation (NodeFactory), cycle rules,
│   (ExecutionEngine)          │   JoinType reachability, retry policy bounds.
└──────────────────────────────┘
```

- L1 is the fast path: every edit runs L1 locally; there is no network call.
- L2 is run on **Save** and **Start**; it is the canonical contract.
- L3 is run only on **Start**; failures surface as `RunEvent` (`phase:
  Failed`) with a `diagnostics[]` payload.
- All three return a common `Diagnostic { level, nodeId?, edgeId?, path?,
  code, message }` so the UI Problems panel is uniform.

---

## 13. Testing Architecture

| Layer            | Tool                                           | What it covers                                              |
|------------------|------------------------------------------------|-------------------------------------------------------------|
| Unit (frontend)  | **Vitest**                                     | `io/` round-trip, `validation/`, `state/` reducers, `lib/`  |
| Component        | **@testing-library/react** + **Storybook 8** interaction tests | Each NodeKind component, PropertyGrid, Palette DnD |
| E2E              | **Playwright**                                 | Authoring flows, save/load, start+SignalR overlays          |
| Contract         | **OpenAPI** (NSwag generated client) + **Pact** for SignalR event fixtures | BFF ⇄ UI drift |
| Backend unit     | **MSTest** (+ Reqnroll where a BDD shape fits) | `YamlMapper`, `WorkflowService`, `EngineEventBridge`        |
| Backend integ.   | **MSTest** + `WebApplicationFactory<Program>`  | REST + Hub end-to-end against in-memory engine              |

- Storybook is the design-time fixture source; every `NodeKindSpec` ships at
  least one story and a visual regression snapshot (Chromatic or Playwright
  `toHaveScreenshot`).
- The `schemas/` folder is the shared contract: generated TypeScript types
  (`json-schema-to-typescript`) AND C# types (`NJsonSchema`) are produced in
  CI; drift fails the build.
- Reqnroll feature files for backend mirror the conventions already in
  `src/EtwIngest/Features/` so the team has one BDD flavor.

---

## 14. Performance Targets

| Metric                                         | Target             | Approach                                                                     |
|------------------------------------------------|--------------------|------------------------------------------------------------------------------|
| Pan/zoom at 1 000 nodes / 2 000 edges          | 60 fps on laptop   | Memoized node components; `onlyRenderVisibleElements` in React Flow v12; avoid array rebuilds |
| Edge update latency (drag connect → render)    | < 50 ms            | `onConnect` calls `applyEdgeChanges` synchronously; no remote call           |
| Property change → canvas reflect               | < 100 ms           | Patch-based store updates; selectors keyed by nodeId; debounce 120 ms on text only |
| Initial load 1 000-node workflow               | < 800 ms TTI       | Code-split per NodeKind; `React.lazy` for heavy widgets (Monaco)             |
| Live event throughput                          | 500 events/s sustained | Batch events into 16 ms animation frames; coalesce by nodeId            |
| Bundle size (main chunk, gz)                   | < 350 kB           | Monaco lazy-loaded; RJSF lazy-loaded with property grid; no moment/lodash    |

Concretely: node components are `memo`'d on shallow `node.data` equality;
status updates mutate `node.data.status` and we notify React Flow via
`applyNodeChanges([{ type: 'replace', … }])` so only the affected nodes
re-render.

---

## 15. Security

- **Auth**: v1 cookie auth + anti-forgery tokens on state-changing routes,
  SameSite=Lax, HttpOnly. The SignalR hub requires the same cookie.
  v2 plan: **AAD OBO** — BFF swaps the frontend token for a downstream
  engine token; `/hubs/execution` accepts a bearer via `accessTokenFactory`.
- **Authorization**: role claims `Workflow.Viewer | Editor | Operator`.
  Editor may PUT; Operator may Start/Pause/Cancel; Viewer is read-only.
- **CSP**:
  ```
  default-src 'self';
  script-src  'self' 'wasm-unsafe-eval';       # Monaco uses wasm
  style-src   'self' 'unsafe-inline';           # RF + RJSF inline styles
  connect-src 'self' wss://<host>;
  img-src     'self' data:;
  font-src    'self' data:;
  frame-ancestors 'none';
  ```
- **Workflow YAML sanitization**: scripts (`CSharpScript`, `PowerShell`,
  `CSharpTask.scriptContent`) are opaque text **and privileged**. Two gates:
  1. **Save-time**: flagged nodes require `Workflow.Scripts.Author` claim;
     the BFF rejects writes from users without it and records the author.
  2. **Run-time**: execution of script nodes is off by default in shared
     environments. `IScriptExecutionPolicy` (engine-side hook) consults a
     per-environment allowlist + signed-workflow requirement (SHA256 of the
     canonical YAML signed by a CI key). Unsigned workflows run only in
     `Development`.
- **Input hardening**: `workflowId` restricted to `[a-z0-9-]{1,64}`; YAML
  size capped at 2 MB; deserialization uses a strict YamlDotNet builder with
  `IgnoreUnmatchedProperties(false)` for known blocks (except `x-ui:`).
- **Anti-SSRF** on engine: Timer cron and any URL-typed fields are validated
  against an allowlist policy at Start time.

---

## 16. Deployment

- Two csproj adjacent to existing engine:
  - `src/Workflow.Api/Workflow.Api.csproj` (`net8.0`), references
    `ExecutionEngine.csproj`.
  - `src/Workflow.Api/web/` (Vite project) builds to
    `src/Workflow.Api/wwwroot/` via `npm run build`.
- **Vite config** (prod):
  - `base: '/'`, `build.outDir: '../wwwroot'`, `build.emptyOutDir: true`.
- **Vite dev proxy**: `server.proxy` routes `/api` and `/hubs` to
  `http://localhost:5000` (Kestrel). `ws: true` on `/hubs/*`.
- **ASP.NET serves SPA**: `app.UseStaticFiles(); app.MapFallbackToFile
  ("index.html");` after routing REST + hubs.
- **CI** (GitHub Actions):
  1. `actions/setup-node@v4` → `npm ci && npm run build` in `web/`.
  2. `actions/setup-dotnet@v4` (net8.0) → `dotnet build Workflow.sln` →
     `dotnet test`.
  3. Contract step: regenerate TS + C# types from `schemas/`, fail on diff.
  4. Playwright e2e against `dotnet run` + the built SPA.
- **Packaging**: single self-contained publish of `Workflow.Api` (win-x64
  matches the rest of the repo) including `wwwroot`.

---

## 17. Open Questions (top 10, with recommended defaults)

1. **Workflow storage backend.** *Default:* filesystem keyed by
   `workflowId`, JSON for UI shape + derived YAML on demand; introduce
   SQLite for runs/history once we need filtering.
2. **Multi-tenant isolation.** *Default:* single-tenant v1; tenant claim
   gates store path later.
3. **Subflow reference resolution.** *Default:* `subflow.workflowRef` is a
   `workflowId` in the same store; cross-store refs via URI later.
4. **Container navigation model.** *Default:* "zoom-into" a container opens
   an inner canvas in a breadcrumb-routed view; children still live in the
   same `nodes[]` array with `parentId`.
5. **Comment preservation in YAML.** *Default:* **do not** preserve; document
   clearly; revisit if users complain.
6. **Expression language for Decision/Switch.** *Default:* C# expression
   evaluated server-side (matches existing `IfElseNodeDefinition.Condition`);
   no client-side evaluation.
7. **Monaco vs CodeMirror for script editing.** *Default:* Monaco (better
   C#/PowerShell syntax), lazy-loaded to keep the core bundle under 350 kB.
8. **Undo granularity.** *Default:* coalesce property edits within a 400 ms
   window as a single undo step; structural edits (add/remove node/edge)
   are always discrete.
9. **Auto-layout engine.** *Default:* `elkjs` layered algorithm; triggered
   on explicit user action, never implicitly after load.
10. **Versioning strategy for published workflows.** *Default:* immutable
    on save — writing creates a new `version` while `workflowId` is stable;
    runs pin the version they started against.

---

### Cross-references

- Engine code read: `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs`,
  `…/IfElseNodeDefinition.cs`, `src/ExecutionEngine/Enums/RuntimeType.cs`,
  `…/Enums/MessageType.cs`, `…/Enums/NodeExecutionStatus.cs`,
  `src/ExecutionEngine/Events/*`, `src/ExecutionEngine/design.md`.
- YAML anchors: `src/ExecutionEngine.Example/Workflows/Sequential.yaml`,
  `Parallel.yaml`, `ContainerExample.yaml`, `etl-workflow-with-kusto.yaml`.
- Conventions: `CLAUDE.md`, `.editorconfig` (C# `this.` qualifier, file
  headers, 4-space C#, 2-space TS/JSON/YAML).
