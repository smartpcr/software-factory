# Critique of `workflow-ui-architecture-draft.md` (from research perspective)

## Strengths

- Strong primary choice: **`@xyflow/react` v12 + Zustand + ELKjs + SignalR** is a credible modern stack for a .NET-backed workflow designer.
- The draft correctly separates **UI-native JSON** from **engine YAML**, which is the right long-term contract boundary for versioning and migrations.
- The **NodeKind registry** idea is solid: schema + renderer + ports + mapper + validation per kind is maintainable.
- Validation is treated as layered (**client / server / engine**), which is exactly what a production workflow editor needs.
- The plan recognizes important workflow-editor concerns that many drafts miss: typed handles, subflows/containers, execution overlays, undo/redo, and YAML round-trip.

## Critical issues (must-fix, with cited evidence)

### 1) The custom node component contract is wrong for React Flow v12

The draft registers node components as `ComponentType<{ node: NodeDef<TConfig> }>` and renders `DecisionNode({ node })` (architect draft §5, lines 367-379 and 431-434). That is **not** the React Flow node API. React Flow injects **`NodeProps`** (`id`, `data`, selection/drag/connect props, etc.) into custom node components, not a custom `{ node }` prop.  

**Evidence**
- Architect draft: `component: ComponentType<{ node: NodeDef<TConfig> }>` and `DecisionNode = memo(({ node }: { node: NodeDef }) => ...)`
- React Flow docs: custom nodes are React components that receive injected node props; `nodeTypes` maps to components using `NodeProps` ([custom nodes docs](https://reactflow.dev/learn/customization/custom-nodes))

**Why this matters**
- The registry shape as written will not plug cleanly into `nodeTypes`.
- It also hides standard React Flow node props you need for selection state, dragging, focusability, and measured layout.

### 2) The execution-update strategy relies on in-place mutation, which is wrong for controlled React Flow

The draft says status updates “mutate `node.data.status` / `edge.data.active` in place” and then keep array identity stable (architect draft §8, lines 649-656; §14, lines 903-906). That is a **bad fit** for controlled React Flow state. React Flow’s own Zustand guide explicitly says it is important to create a **new node object** to inform React Flow about the change.

**Evidence**
- Architect draft: “Status changes are applied as node-data patches, not new arrays” and “mutate `node.data.status` / `edge.data.active` in place”
- React Flow state-management guide: “it’s important to create a new object here, to inform React Flow about the changes” ([state management guide](https://reactflow.dev/learn/advanced-use/state-management))

**Why this matters**
- In-place mutation risks stale renders, broken memoization, and hard-to-debug desync between the store and the canvas.
- For runtime overlays, the safer pattern is either:
  1. immutable node/edge replacement for affected elements only, or
  2. a **separate execution map** keyed by node/edge ID, rendered as overlays without rewriting graph structure.

### 3) The draft misses `useUpdateNodeInternals` for dynamic handles

The registry explicitly says ports can depend on config (e.g. `Switch` cases) (architect draft §5, line 373), but the draft never calls out **`useUpdateNodeInternals(id)`**. React Flow requires that hook whenever the number or position of handles changes programmatically.

**Evidence**
- Architect draft: dynamic ports for `Switch` / loop-like nodes
- React Flow handles docs: “If you are programmatically changing the position or number of handles in your custom node, you need to update the node internals with the `useUpdateNodeInternals` hook” ([handles docs](https://reactflow.dev/learn/customization/handles))

**Why this matters**
- Without it, handle hitboxes and edge anchors drift out of sync after case/port changes.
- This becomes a production bug fast in decision/switch editors.

### 4) ELK “orthogonal routing” is overstated; the draft only specifies node layout, not routed-edge rendering

The draft says `elkLayout.ts` will use “layered + orthogonal routing” (architect draft §3, lines 203-205). ELK can compute routed edges, but React Flow’s built-in edge types do **not** automatically consume ELK bend points/sections. The official ELK example mostly uses ELK to position nodes and then feeds plain React Flow edges back into the canvas.

**Evidence**
- Architect draft: “elkLayout.ts # elkjs, layered + orthogonal routing”
- React Flow layout docs: ELK supports node layout + edge routing, but it is a separate external system ([layout docs](https://reactflow.dev/learn/layouting/layouting))
- React Flow ELK example: the example maps laid-out nodes back to `position` and returns edges, but does not establish an ELK-section-aware custom edge renderer ([ELK example](https://reactflow.dev/examples/layout/elkjs))

**Why this matters**
- As written, the draft will likely produce **ELK-positioned nodes with ordinary React Flow edges**, not true orthogonal ELK-routed polylines.
- If orthogonal routing is a requirement, the architecture must specify a custom edge that renders ELK sections/bend points.

## High issues (should-fix)

### 1) The performance targets are optimistic, and some proposed optimizations are misframed

The draft targets **60 fps at 1k nodes / 2k edges** using `onlyRenderVisibleElements` plus memoization (architect draft §14, lines 896-900). React Flow docs are more cautious: `onlyRenderVisibleElements` can help, but it **also adds overhead**, and complex CSS/animations can significantly hurt performance.

**Evidence**
- React Flow API: `onlyRenderVisibleElements` “might improve performance … but also adds an overhead” ([ReactFlow API](https://reactflow.dev/api-reference/react-flow))
- React Flow performance guide: complex animations/shadows/gradients can significantly impact performance ([performance guide](https://reactflow.dev/learn/advanced-use/performance))

**Research view**
- 1k/2k is possible for a stripped-down canvas, but **not a safe default promise** once you add:
  - animated run edges,
  - status badges,
  - nested containers,
  - rich custom nodes,
  - Monaco-backed widgets nearby.
- Treat this as a benchmark goal, not an architectural assumption.

### 2) The selector examples still lean on scanning `nodes[]` / `edges[]`

The draft’s example selectors use `find`/`filter` over the full arrays (architect draft §8, lines 641-646). React Flow’s performance guide specifically warns against components depending directly on `nodes` or `edges` because those arrays change frequently.

**Evidence**
- Architect draft: `useNodeById = useAppStore(... s.nodes.find(...))`, `useEdgeIdsForNode = ... s.edges.filter(...)`
- React Flow performance guide: avoid accessing `nodes`/`edges` in components; store derived state separately when possible ([performance guide](https://reactflow.dev/learn/advanced-use/performance))

**Correction**
- Keep canonical arrays for React Flow, but add **index maps / derived maps** (`nodeById`, `edgeIdsByNodeId`, `selectedNodeIds`) for frequent lookup paths.

### 3) RJSF is defensible, but the draft overstates it as the obvious best choice

The draft chooses `@rjsf/core` + Ajv (architect draft §7, lines 553-577). That is a reasonable **ship-fast** choice, but not automatically the best long-term choice for a workflow editor. RJSF’s own docs say that if you already know your data and want a toolkit for building forms, you “might look elsewhere.”

**Evidence**
- RJSF docs: if you have *a priori* knowledge of your data and want a toolkit, “you might look elsewhere” ([RJSF docs](https://rjsf-team.github.io/react-jsonschema-form/docs/))
- uniforms positions itself as flexible across schemas/themes and is used in data-heavy apps ([uniforms site](https://uniforms.tools/))
- TanStack Form emphasizes granular reactive performance and TS-first headless control ([TanStack Form](https://tanstack.com/form/latest))

**Research view**
- **RJSF is best when**: speed, schema generation, and low custom-form investment matter most.
- **uniforms is better when**: you want schema-driven forms but more layout/control flexibility.
- **TanStack Form is better when**: forms are known, hand-authored, and performance/UX consistency outweigh schema auto-generation.

### 4) SignalR is a good default, but the draft should explicitly separate “best default” from “best possible”

The draft uses SignalR for the live run stream (architect draft §9). That is a solid .NET-default choice. Microsoft explicitly positions SignalR for real-time dashboards/monitoring and notes it handles connection management and transport fallback automatically.

**Evidence**
- Microsoft docs: SignalR is a good fit for high-frequency updates, dashboards, and monitoring; it supports WebSockets, SSE, and Long Polling with automatic fallback ([SignalR intro](https://learn.microsoft.com/aspnet/core/signalr/introduction))

**Research view**
- **Keep SignalR** as the primary choice.
- But clarify trade-offs:
  - **Raw WebSocket** is leaner if you want a custom protocol and do not need SignalR hubs/groups/reconnect semantics.
  - **SSE** is simpler if the stream is strictly server→client and all commands remain REST.
- The architecture should preserve a transport abstraction so the app is not SignalR-shaped all the way down.

### 5) Some tooling choices are dated versus late-2025/early-2026 norms

- The draft still says **Storybook 8** (architect draft §13, line 875). Current versions are already beyond that; this should be updated.
- The draft uses **npm** everywhere, while the research recommendation favored **pnpm** for workspace/monorepo ergonomics and faster installs. Not a blocker, but worth reconsidering.

## Medium issues

### 1) The draft misses a few production React Flow patterns

Compared with `workflow-ui-research.md`, the draft is missing or underplaying:

- **`useUpdateNodeInternals`** for dynamic ports
- **`prefers-reduced-motion`** handling for animated execution edges
- explicit use of **React Flow-level `isValidConnection`** as the preferred validation path for performance (the docs prefer this over per-handle validation)
- a recommendation to keep **runtime overlay state separate from graph-edit state**
- a clear plan for **collapse/expand summaries** that preserve port visibility and runtime roll-up state

### 2) “EdgeActivated” may under-deliver the requested “active data flow” visualization

The draft synthesizes `EdgeActivated` from completion/failure/message events (architect draft §9, lines 699-701). That will give a pulse, but not necessarily a faithful visualization of **message-in-flight** semantics for the engine’s per-node queues. If “active data flow” really matters, the backend likely needs explicit **router/queue events** rather than inferring everything from node completion.

### 3) The `x-ui:` YAML strategy is plausible, but the draft should call out the trade-off more bluntly

The engine serializer skips unknown keys today via the reflection converter’s `SkipValue` behavior (`ReflectionYamlConverter.cs`, lines 39-47), so `x-ui:` is probably safe. But the architecture should state that **engine-owned YAML is still the primary contract**, and UI metadata preservation must be covered by regression tests across engine upgrades.

## Specific corrections (before/after)

### A) Fix the node component contract

**Before**

```ts
component: ComponentType<{ node: NodeDef<TConfig> }>;
```

```tsx
export const DecisionNode = memo(({ node }: { node: NodeDef }) => (
  <BaseNode node={node} shape="diamond">
    {decisionPorts(node).map(p => <TypedHandle key={p.id} port={p} />)}
  </BaseNode>
));
```

**After**

```ts
import type { Node, NodeProps } from '@xyflow/react';

export type WorkflowNodeData<TConfig = unknown> = {
  specKind: NodeKind;
  config: TConfig;
  ports: PortDef[];
  status?: NodeStatus;
};

component: ComponentType<NodeProps<Node<WorkflowNodeData<TConfig>>>>;
```

```tsx
export const DecisionNode = memo(
  ({ id, data, selected }: NodeProps<Node<WorkflowNodeData<DecisionConfig>>>) => (
    <BaseNode id={id} selected={selected} shape="diamond">
      {data.ports.map((p) => <TypedHandle key={p.id} nodeId={id} port={p} />)}
    </BaseNode>
  )
);
```

### B) Stop mutating node/edge objects in place for runtime updates

**Before**

```ts
node.data.status = evt.status;
edge.data.active = true;
```

**After (preferred)**

```ts
execution: {
  nodeStatusById: Record<string, RuntimeNodeState>;
  activeEdgeIds: Record<string, number>;
}
```

```tsx
const status = useAppStore((s) => s.execution.nodeStatusById[id]);
return <NodeStatusBadge status={status} />;
```

If you do write runtime state into nodes/edges, do it immutably:

```ts
set((s) => ({
  nodes: s.nodes.map((n) =>
    n.id === evt.nodeId ? { ...n, data: { ...n.data, status: evt.status } } : n
  ),
}));
```

### C) Add `useUpdateNodeInternals` for dynamic ports

**Before**

```ts
ports: (node: NodeDef<TConfig>) => PortDef[];
```

**After**

```tsx
import { useUpdateNodeInternals } from '@xyflow/react';

function SwitchNode({ id, data }: NodeProps<Node<WorkflowNodeData<SwitchConfig>>>) {
  const updateNodeInternals = useUpdateNodeInternals();

  useEffect(() => {
    updateNodeInternals(id);
  }, [id, data.config.cases, updateNodeInternals]);

  return <RenderedSwitchNode id={id} ports={data.ports} />;
}
```

### D) Narrow the ELK claim, or add a real routed-edge plan

**Before**

```ts
// elkLayout.ts # elkjs, layered + orthogonal routing
```

**After (honest default)**

```ts
// elkLayout.ts # elkjs for node placement and container-aware layout
// Edge routing remains React Flow smoothstep by default.
```

**Or**, if orthogonal routing is truly required:

```ts
type RoutedEdgeData = {
  sections: Array<{ startPoint: XY; bendPoints?: XY[]; endPoint: XY }>;
};
```

```tsx
export function ElkPolylineEdge({ data }: EdgeProps<RoutedEdgeData>) {
  // render SVG polyline/path from ELK sections
}
```

## Additional recommendations not in architect draft

1. **Prototype the property-grid decision before locking it in.**  
   Build 3 real nodes first: `Switch`, `Timer`, and a script node with retry/circuit-breaker fields. If RJSF becomes too rigid or visually inconsistent, pivot early to **uniforms** or a **TanStack Form**-based custom panel.

2. **Keep execution rendering out of `node.data` by default.**  
   This is the cleanest way to avoid undo-history pollution and graph-array churn.

3. **Add a transport abstraction now.**  
   Something like `ExecutionStreamClient` with a SignalR implementation lets you downgrade to SSE later if the hub model proves unnecessary.

4. **Add a measured-node/layout pipeline.**  
   ELK layout is much more reliable if you cache measured node dimensions and relayout only on structural changes.

5. **Add reduced-motion support as a first-class requirement.**  
   Animated run edges should disable themselves under `prefers-reduced-motion`.

6. **Add explicit index maps in the store.**  
   `nodeById`, `edgeById`, `edgeIdsBySource`, `edgeIdsByTarget`, `selectedNodeIds` will matter more than clever selector memoization once graphs grow.

7. **Be careful with Monaco.**  
   It is fine, but it should mount lazily and ideally only when the property panel for a script node is actually open.

8. **Update the draft against the research report.**  
   The biggest gaps versus `workflow-ui-research.md` are:
   - dynamic-handle lifecycle (`useUpdateNodeInternals`)
   - stronger caution on RJSF
   - more realistic performance framing
   - clearer ELK edge-routing nuance
   - stronger separation of execution overlay state from graph-edit state
