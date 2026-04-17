# Workflow UI Research: React Flow / @xyflow/react for Workflow Design + Execution Visualization

_Checked: 2026-04-16. Versions and stars are from npm/GitHub at time of writing._

## Executive recommendation

Build the designer on **`@xyflow/react` v12** with **Zustand**, **ELKjs**, **SignalR**, **Tailwind CSS**, and a **schema-driven property grid**.  
For this repo, the best primary stack is:

- **Canvas**: `@xyflow/react` `12.10.2`
- **State**: `zustand` `5.0.12`
- **Layout**: `elkjs` `0.11.1`
- **Property grid**: **RJSF** + `@rjsf/mui` + `@rjsf/validator-ajv8` (`6.4.2`)
- **Transport for live execution**: **SignalR** from ASP.NET Core / .NET 8
- **Shell styling**: **Tailwind CSS 4** + **shadcn/ui**

This is the most maintainable option because it is React-first, typed, MIT-licensed, has a strong ecosystem, and already supports nearly every workflow-editor primitive you need without fighting the library.

---

## 1) Library choice

### Primary choice: `@xyflow/react` (React Flow v12)

**Recommendation: choose this.**

Why it fits this ExecutionEngine use case:

- Mature, actively maintained, and well documented with a real ecosystem around examples, Pro templates, and community content
- Natural fit for **DAG + containers + subflows + typed ports**
- Excellent support for **custom nodes, handles, edges, groups, labels, selection, keyboard a11y**
- React-first architecture: easy to integrate with property panels, app state, and .NET-backed live execution
- Strong documentation, examples, and public showcase
- MIT licensed
- Production users are publicly visible, which is rare in this category

### Comparison table

| Library | Version | License | GitHub stars | Last release | Public production evidence | Verdict |
|---|---:|---|---:|---|---|---|
| **@xyflow/react / React Flow** | `12.10.2` | MIT | ~36.2k | 2026-03-27 | **Carto, DoubleLoop, Hubql, OneSignal, Stripe, Typeform** via official showcase/case studies | **Best choice** |
| **Rete.js** | `2.0.6` | MIT | ~12.0k | 2025-06-30 | Official examples/showcases; fewer named public enterprise users | Strong alternative if you want a heavier “editor framework” |
| **litegraph.js** | `0.7.18` | MIT | ~8.0k | 2024-01-08 | **ComfyUI, WebGLStudio** and others publicly referenced | Good for canvas-heavy node editors, weaker React ergonomics |
| **Drawflow** | `0.0.60` | MIT | ~6.0k | 2024-09-03 | Popular OSS, but named production users are sparse | Fine for simpler editors; weaker long-term architecture |
| **Reaflow** | `5.4.1` | Apache-2.0 | ~2.5k | 2025-04-08 | Limited public production evidence | Better as a diagramming lib than a workflow IDE foundation |
| **Flume** | `1.2.0` | MIT | ~1.6k | 2025-11-05 | Mostly developer/community usage; limited public enterprise evidence | Nice config-editor UX, but too narrow for execution visualization |

### Opinionated take

- **Pick React Flow** if your priority is a **production React app** with custom UI, live status overlays, typed ports, and long-term maintainability.
- **Pick Rete.js** only if you want a more opinionated visual-programming framework and are willing to accept more framework gravity.
- Do **not** choose Drawflow, Reaflow, or Flume as the primary foundation for this repo.

---

## 2) Core patterns

### 2.1 Custom node types registry pattern

Keep a **typed registry** that owns rendering, schema, iconography, port definitions, and serializer hooks.

```tsx
// node-registry.ts
import type { Node, NodeProps, NodeTypes } from '@xyflow/react';

export type WorkflowNodeKind =
  | 'task.script.csharp'
  | 'task.script.powershell'
  | 'control.decision'
  | 'container.parallel'
  | 'container.loop'
  | 'subflow';

export interface WorkflowNodeData {
  title: string;
  config: Record<string, unknown>;
  ports: Array<{ id: string; name: string; direction: 'in' | 'out'; dataType: string }>;
}

export interface NodeDefinition {
  kind: WorkflowNodeKind;
  component: React.ComponentType<NodeProps<Node<WorkflowNodeData>>>;
  defaultData: WorkflowNodeData;
  schema: object;
}

export const nodeDefinitions: Record<WorkflowNodeKind, NodeDefinition> = { /* ... */ };

export const nodeTypes: NodeTypes = Object.fromEntries(
  Object.values(nodeDefinitions).map((d) => [d.kind, d.component]),
);
```

**Rule:** UI should never switch on ad-hoc strings all over the app. Route everything through this registry.

### 2.2 Multiple typed handles/ports with validation

Use unique handle IDs and validate both in `isValidConnection` and the store-level `onConnect`.

```tsx
import { Handle, Position, type Connection } from '@xyflow/react';

const canConnect = (c: Connection, graph: GraphModel) => {
  const sourcePort = graph.getPort(c.source!, c.sourceHandle!);
  const targetPort = graph.getPort(c.target!, c.targetHandle!);
  if (!sourcePort || !targetPort) return false;
  if (sourcePort.direction !== 'out' || targetPort.direction !== 'in') return false;
  if (sourcePort.dataType !== targetPort.dataType && targetPort.dataType !== 'any') return false;
  if (wouldCreateCycle(graph, c) && !isInsideLoopContainer(graph, c)) return false;
  return true;
};

export function PortHandle(props: {
  nodeId: string; portId: string; type: 'source' | 'target'; position: Position;
}) {
  return (
    <Handle
      id={props.portId}
      type={props.type}
      position={props.position}
      isValidConnection={(c) => canConnect(c, useWorkflowStore.getState().graph)}
    />
  );
}
```

**Rule:** never trust the canvas alone; re-check connection validity in the store reducer before persisting.

### 2.3 Edge types + custom animated runtime edge

Use:

- `default` for generic links
- `step` for control-flow
- `smoothstep` for readable orchestration graphs
- `bezier` only when aesthetics matter more than routing clarity

For live execution, add a custom edge that animates only when active.

```tsx
import { BaseEdge, getSmoothStepPath, type EdgeProps } from '@xyflow/react';

export function RuntimeEdge(props: EdgeProps<{ active?: boolean; label?: string }>) {
  const [path] = getSmoothStepPath(props);
  return (
    <>
      <BaseEdge path={path} markerEnd={props.markerEnd} style={{ strokeWidth: 2 }} />
      {props.data?.active && (
        <path
          d={path}
          fill="none"
          stroke="var(--color-flow-active)"
          strokeWidth={4}
          strokeDasharray="10 6"
          className="wf-edge-active"
        />
      )}
    </>
  );
}
```

```css
.wf-edge-active {
  animation: wf-edge-flow 0.8s linear infinite;
}
@keyframes wf-edge-flow {
  from { stroke-dashoffset: 0; }
  to   { stroke-dashoffset: -16; }
}
```

### 2.4 Decision / branch node pattern

Model a decision as **N named output ports**, not as magical edge labels.

```tsx
export function DecisionNode({ id, data }: NodeProps<Node<WorkflowNodeData>>) {
  return (
    <div className="wf-node wf-node-decision">
      <PortHandle nodeId={id} portId="in" type="target" position={Position.Left} />
      <div className="font-medium">Decision</div>
      {data.ports.filter((p) => p.direction === 'out').map((p) => (
        <div key={p.id}>
          <span>{p.name}</span>
          <PortHandle
            nodeId={id}
            portId={p.id}
            type="source"
            position={Position.Right}
          />
        </div>
      ))}
    </div>
  );
}
```

**Rule:** persist the branch condition on the port/edge metadata so YAML round-trips cleanly.

### 2.5 Loop / iterator container pattern

Use a **container node** for loop scope and child nodes with `parentId` + `extent: 'parent'`.

```ts
const nodes = [
  {
    id: 'loop:1',
    type: 'container.loop',
    data: { title: 'For Each Item', config: { collection: '$.items' }, ports: [] },
    position: { x: 100, y: 100 },
    style: { width: 520, height: 320 },
  },
  {
    id: 'task:inside-loop',
    type: 'task.script.csharp',
    parentId: 'loop:1',
    extent: 'parent',
    position: { x: 40, y: 80 },
    data: { title: 'Transform Item', config: {}, ports: [] },
  },
];
```

**Why:** it maps cleanly to engine concepts like scope, iteration context, retries, and pause/resume at container level.

### 2.6 Subflow / grouping with collapse-expand

Use group/subflow nodes as first-class model objects, not just visual rectangles.

```ts
function toggleCollapse(groupId: string) {
  useWorkflowStore.setState((s) => {
    const collapsed = !s.ui.collapsedGroups[groupId];
    s.ui.collapsedGroups[groupId] = collapsed;
    s.nodes = s.nodes.map((n) =>
      n.parentId === groupId ? { ...n, hidden: collapsed } : n
    );
  });
}
```

**Pattern:** when collapsed, render a summarized node with port aggregates and runtime roll-up state.

### 2.7 Undo / redo

Start with **Zustand + temporal middleware** for local editing.  
Add **Yjs** only if you later need real-time collaboration.

```ts
import { temporal } from 'zundo';

export const useWorkflowStore = create<WorkflowState>()(
  temporal(
    (set, get) => ({
      nodes: [],
      edges: [],
      applyGraphPatch: (patch) => set(produce((s) => patch(s))),
    }),
    {
      partialize: (s) => ({ nodes: s.nodes, edges: s.edges }),
      limit: 100,
    },
  ),
);
```

### 2.8 Copy/paste, multi-select, keyboard shortcuts

Support this on day 1:

- marquee multi-select
- `Ctrl/Cmd+C`, `Ctrl/Cmd+V`
- `Delete`
- `Ctrl/Cmd+Z`, `Shift+Ctrl/Cmd+Z`
- duplicate
- frame selection
- auto-layout

```ts
hotkeys('ctrl+c,cmd+c', () => store.copySelection());
hotkeys('ctrl+v,cmd+v', () => store.pasteSelectionAtViewportCenter());
hotkeys('del,backspace', () => store.deleteSelection());
hotkeys('ctrl+z,cmd+z', () => store.undo());
hotkeys('shift+ctrl+z,shift+cmd+z', () => store.redo());
```

---

## 3) Property grid

### Primary choice: **RJSF + `@rjsf/mui` + `@rjsf/validator-ajv8`**

**Choose this unless/until it becomes visually inconsistent enough to justify a custom bridge.**

Why:

- Best fit for **node-type-specific JSON Schema**
- Ajv gives reliable schema validation, defaults, enums, formats, and `if/then/else`
- Easy to switch schemas based on selected node kind
- Faster to ship than TanStack Form / Formik for this use case
- More mature and lower-effort for generated property grids than uniforms

Why **not** the others:

- **uniforms**: flexible, but you write more bridging and field plumbing
- **Formik + ajv**: too manual for a schema-driven node editor
- **TanStack Form**: excellent for hand-authored forms, wrong default for 20+ dynamic node types

### Pattern

- Each `NodeDefinition` owns:
  - `schema`
  - `uiSchema`
  - `defaultData`
  - custom field/widget registry
- Property panel binds to the selected node only
- Save with **debounced patch commits**, not on every keystroke into global history

```tsx
import Form from '@rjsf/mui';
import validator from '@rjsf/validator-ajv8';

function NodePropertiesPanel() {
  const selected = useWorkflowStore((s) => s.selection.primaryNode);
  if (!selected) return <EmptyState />;

  const def = nodeDefinitions[selected.type as WorkflowNodeKind];

  return (
    <Form
      schema={def.schema}
      uiSchema={def.uiSchema}
      formData={selected.data.config}
      validator={validator}
      liveValidate={false}
      onChange={(e) => useWorkflowStore.getState().patchNodeConfig(selected.id, e.formData)}
    />
  );
}
```

### Conditional fields

Use JSON Schema `dependencies` / `if-then-else` for things like:

- retry policy fields only when `retry.enabled = true`
- branch expression editor only when `mode = expression`
- loop batch-size only when `executionMode = parallel`

---

## 4) State management

### Primary choice: **Zustand**

This matches React Flow’s own guidance and mental model.

Recommended store slices:

- **graph slice**: nodes, edges, viewport, connect/disconnect, serialize
- **selection slice**: selected nodes/edges, inspector state
- **execution slice**: runtime status map, elapsed times, active edges, retries
- **history slice**: undo/redo snapshots, clipboard
- **ui slice**: sidebars, collapsed groups, command palette, search

Why **not Redux** here:

- Too much ceremony for highly interactive canvas state
- Boilerplate encourages over-normalization before you know the pain points
- Zustand selectors are simpler for fine-grained subscriptions
- Canvas apps want **small, direct actions** and **low re-render pressure**

Use Redux only if the wider product already standardizes on it and mandates shared tooling.

---

## 5) Execution visualization

### Transport

Use **SignalR** over WebSocket semantics from the .NET 8 backend.

Why:

- Native fit for ASP.NET Core
- Good reconnect behavior
- Easy topic/channel model per workflow instance
- Works well with pause/resume/cancel and state replay

Suggested event payload:

```ts
type NodeStatus = 'Ready' | 'Running' | 'Success' | 'Failed' | 'Skipped' | 'Waiting' | 'Paused' | 'Cancelled';

interface WorkflowExecutionEvent {
  workflowInstanceId: string;
  nodeId: string;
  status: NodeStatus;
  startedAt?: string;
  completedAt?: string;
  elapsedMs?: number;
  retryCount?: number;
  activeOutgoingEdgeIds?: string[];
  message?: string;
}
```

### Node badge overlays

Render status as a memoized overlay layer, not as part of every node’s editable content.

```tsx
const NodeStatusBadge = memo(({ nodeId }: { nodeId: string }) => {
  const status = useWorkflowStore((s) => s.execution.nodeStatus[nodeId]);
  if (!status) return null;
  return (
    <div className={`wf-badge wf-${status.status.toLowerCase()}`}>
      <StatusIcon status={status.status} />
      <span>{status.retryCount ?? 0}r</span>
      <span>{formatMs(status.elapsedMs)}</span>
    </div>
  );
});
```

### Edge activation

- Set `edge.data.active = true` for short windows while a message is in-flight
- Fade back to inactive after completion
- For high-frequency traffic, aggregate by frame

### Performance rules

- Batch execution events through `requestAnimationFrame` or a 16-50ms throttle
- Update only per-node/per-edge maps, not the whole graph array
- Use `useStore(selector, shallow)` and `memo`
- Keep runtime state separate from edit state
- Avoid re-creating `nodeTypes`, `edgeTypes`, handlers, and schemas

**Important:** do not call `setNodes([...])` on every status event. That will kill performance at scale.

---

## 6) Layout / auto-arrange

### Recommendation: **ELKjs**

Use ELKjs for production auto-layout because you need:

- compound/group/container layout
- better handling of mixed node sizes
- edge routing options
- future-proofing for loop containers and subflows

Use **dagre** only as a lightweight fast-path for simple top-to-bottom DAG layout.  
Use **d3-hierarchy** only if you truly constrain the graph to a single-root tree.

```ts
import ELK from 'elkjs/lib/elk.bundled.js';

const elk = new ELK();

export async function autoLayout(nodes: Node[], edges: Edge[]) {
  const graph = {
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'RIGHT',
      'elk.layered.spacing.nodeNodeBetweenLayers': '60',
      'elk.spacing.nodeNode': '40',
    },
    children: nodes.map((n) => ({
      id: n.id,
      width: n.measured?.width ?? 220,
      height: n.measured?.height ?? 80,
    })),
    edges: edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };

  const layout = await elk.layout(graph);
  return nodes.map((n) => {
    const ln = layout.children!.find((c) => c.id === n.id)!;
    return { ...n, position: { x: ln.x!, y: ln.y! } };
  });
}
```

---

## 7) Serialization

### Recommendation

Use a **canonical graph JSON document in the frontend**, then translate that to/from the engine’s YAML.

Why:

- The UI should not mutate YAML directly
- JSON is easier for history, validation, testing, migrations, and collaboration
- YAML becomes a persistence/export format and engine contract

Suggested model:

- `WorkflowDocument`
  - `version`
  - `metadata`
  - `nodes[]`
  - `edges[]`
  - `groups[]`
  - `ui` (optional editor-only layout metadata)

### Versioning strategy

- Add `documentVersion: "1.x"`
- Keep frontend migrators: `v1 -> v2 -> v3`
- Store `engineMinVersion` when backend schema introduces breaking concepts
- Strip editor-only fields when exporting YAML for runtime, or place them under an ignored `ui:` section if the engine tolerates it

---

## 8) Validation

Run **two layers** of validation:

1. **Schema validation**  
   Ajv on node config and workflow document shape
2. **Graph semantic validation**  
   Custom rules:
   - cycle detection unless inside explicit loop container
   - missing required ports
   - unresolved `sourceHandle` / `targetHandle`
   - invalid type matches
   - unreachable nodes
   - missing branch default path
   - container boundary violations

**Rule:** validation results should be rendered both in the property panel and on-canvas.

---

## 9) Testing strategy

### Unit / component

- **Vitest** + **React Testing Library**
- Test:
  - reducers/store actions
  - validators
  - serializers
  - node registry contracts
  - property-panel schema switching

### Canvas interaction / E2E

- **Playwright**
- Test:
  - drag node
  - connect/disconnect ports
  - multi-select
  - copy/paste
  - undo/redo
  - collapse/expand groups
  - live execution overlays

### React Flow-specific notes

- In RTL/jsdom, set explicit container width/height
- Wrap graph-state mutations in `act(...)`
- Prefer testing **store actions** directly for unit-level confidence
- Use Playwright for true drag/drop and pointer geometry
- For alternate connect modes or modifier-based behavior, synthesize the modifier key (`Alt`, `Shift`, etc.) in Playwright instead of trying to fake SVG geometry in jsdom

---

## 10) Accessibility

React Flow already gives you useful baseline support. Keep it on and build on it.

Use:

- `nodesFocusable`
- `edgesFocusable`
- `ariaLabelConfig`
- `domAttributes`
- semantic `ariaRole` for custom nodes when appropriate

Recommendations:

- Keep every custom node operable with keyboard
- Provide visible focus rings on node wrappers and handles
- Use high-contrast status colors plus icon/text redundancy
- Ensure animated edges respect `prefers-reduced-motion`
- Add screen-reader labels for status badges: `Running`, `Failed after 2 retries`, etc.

---

## 11) Styling

### Recommendation: **Tailwind CSS 4** + **shadcn/ui** for app chrome

Why:

- Fastest way to build a serious workspace UI
- Easy dark mode and theme-token wiring
- Good match for canvas-heavy applications where custom layout is constant
- Easier to keep lean than a full component framework

Suggested styling split:

- **Tailwind**: layout, spacing, canvas overlays, badges, panels
- **shadcn/ui**: dialogs, dropdowns, command palette, menus, sheets
- **RJSF/MUI**: isolated inside the property panel only

Theme tokens to define up front:

- canvas background
- node surface / selected surface
- port colors by type
- status colors: ready/running/success/failed/waiting/paused
- edge colors: normal/active/error

---

## 12) Project tooling

Recommended stack:

- **Vite** `8.x`
- **TypeScript** `6.x`
- **pnpm** `10.x`
- **ESLint** with `typescript-eslint` strict mode
- **Prettier**
- **Vitest** `4.x`
- **Playwright** `1.59+`
- **Storybook** `10.x`

Also add:

- `msw` for SignalR/event mocking where practical
- `zod` or pure JSON Schema typing helpers for editor-only APIs
- `changesets` if the UI becomes a multi-package workspace

---

## 13) Monorepo layout recommendation

### Recommended layout inside this repo

```text
src/
  ExecutionEngine/
  ...
  ui/
    workflow-designer/
      package.json
      vite.config.ts
      src/
        app/
        components/
        features/workflow-canvas/
        features/property-grid/
        features/execution-visualization/
        lib/
        store/
        schemas/
```

### Coexistence with .NET backend

Use the React app as a **separate frontend project** but keep it in-repo.

Recommended shipping model:

1. Build frontend with Vite
2. Emit static assets to `wwwroot` of an ASP.NET Core host, **or**
3. Serve separately in dev and behind the same reverse proxy in prod

Preferred production model:

- **ASP.NET Core serves API + SignalR**
- Static SPA assets served either by the same host or a CDN

Avoid Electron unless there is a hard offline/desktop requirement.

---

## 14) Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| 1k+ nodes causes sluggish pan/zoom/re-render | High | Separate runtime state from graph state; memoize nodes; selectors; avoid full-array rewrites; virtualize side panels |
| Too much per-event UI churn from execution telemetry | High | Batch updates with `requestAnimationFrame` / throttle; coalesce duplicate events |
| Memory leaks from timers / SignalR subscriptions | High | Centralize subscriptions in execution service; strict cleanup on unmount/workflow switch |
| Layout jitter when node sizes change | Medium | Measure nodes once; cache dimensions; relayout only on structural changes |
| YAML/UI model drift | High | Canonical JSON graph + tested YAML translators + versioned migrators |
| Complex node forms become inconsistent | Medium | Registry-owned schemas/widgets; strict field naming; design review for new node kinds |
| Browser compatibility issues for large SVG/canvas operations | Medium | Test Chromium/Edge/Firefox; keep animation optional; reduced-motion support |

---

## 15) Reference links

### High-signal official docs

- React Flow docs: https://reactflow.dev/  
  _Best starting point; official._
- Custom nodes: https://reactflow.dev/learn/customization/custom-nodes  
  _High signal for node registry pattern._
- Handles / multiple handles: https://reactflow.dev/learn/customization/handles  
  _High signal for typed-port rendering._
- State management with Zustand: https://reactflow.dev/learn/advanced-use/state-management  
  _High signal; explicitly recommends Zustand as the guide choice._
- Accessibility: https://reactflow.dev/learn/advanced-use/accessibility  
  _High signal; confirms keyboard/ARIA support._
- Layouting overview: https://reactflow.dev/learn/layouting/layouting  
  _High signal; good ELK vs dagre summary._
- Sub-flows example: https://reactflow.dev/examples/grouping/sub-flows  
  _High signal for `parentId` / `extent`._

### Market / production evidence

- React Flow showcase: https://reactflow.dev/showcase  
  _Official showcase; credible but marketing-curated._
- React Flow case studies: https://reactflow.dev/pro/case-studies  
  _Strong evidence for public production users._
- xyflow site: https://xyflow.com/  
  _Official project site._
- Rete.js examples/showcase: https://retejs.org/examples/  
  _Official; good evidence for capabilities._
- litegraph.js repo: https://github.com/jagenjo/litegraph.js  
  _Official repo; README lists notable uses._
- Drawflow repo: https://github.com/jerosoler/Drawflow  
  _Official repo; weaker production proof._
- Reaflow repo: https://github.com/reaviz/reaflow  
  _Official repo; limited workflow-editor depth._
- Flume docs/site: https://flume.dev  
  _Official; more config-editor-oriented than execution-visualization-oriented._

### Supporting libraries

- ELKjs: https://github.com/kieler/elkjs  
  _Best layout engine here._
- Dagre: https://github.com/dagrejs/dagre  
  _Simple DAG layout fallback._
- Zustand: https://github.com/pmndrs/zustand  
  _Primary app-state library._
- RJSF: https://rjsf-team.github.io/react-jsonschema-form/  
  _Primary property-grid choice._
- ASP.NET Core SignalR: https://learn.microsoft.com/aspnet/core/signalr/introduction  
  _Primary live execution transport._

---

## Bottom line

For this repo, build the workflow design experience on **React Flow v12**.  
It is the best match for a **React + .NET** product that needs both a **visual designer** and **live runtime execution overlays** without locking the team into a niche editor framework.
