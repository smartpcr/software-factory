# Modern Workflow UI — Migration Plan

**Goal:** Migrate `src/WorkflowUI` from its current plain, unstyled appearance to the modern design language and interaction model demonstrated in `docs/examples/flow-studio.html`, while preserving the existing React + `@xyflow/react` + Zustand architecture and engine-contract parity.

**Status:** Draft  
**Reference Design:** `docs/examples/flow-studio.html` (single-file prototype, ~3 400 lines)  
**Current UI:** `src/WorkflowUI` (React/TypeScript SPA, Vite, React Flow v12)

---

## 1 — Gap Analysis

### 1.1 Current State Summary

| Area | Current | Target (flow-studio.html) |
|------|---------|---------------------------|
| **Theming** | No theme system; inline `#fff` / `#3b82f6` / `#e5e7eb` hardcoded | Dual dark/light theme via CSS custom properties; WCAG AA contrast; `prefers-color-scheme` auto-detect |
| **Typography** | Browser defaults | Instrument Sans + JetBrains Mono (Google Fonts); 13px base; antialiased |
| **Layout** | Flexbox `100vh`; fixed sidebar widths | CSS Grid `248px 1fr 320px × 48px 1fr 180px`; named areas (`topbar palette canvas inspector logs`) |
| **Topbar** | None (bare `<h1>` over canvas) | Brand mark, editable workflow title, dirty-dot, toolbar groups (New/Save/Import/Export/Delete), theme toggle, execution indicator, Run/Pause/Resume/Rerun/Cancel |
| **Palette** | Emoji icons; plain list; no descriptions | SVG icons; category sections; icon + name + description; sticky search; workflow list with active indicator & inline delete |
| **Nodes** | White card, colored border, emoji, `minWidth:160` | Gradient card (`bg-3→bg-2`), left accent bar, SVG icon, type badge, monospace config preview; multi-layered shadow; status glow ring + status badge |
| **Ports** | React Flow default handles (top/bottom) | Custom circular ports (in/out/error/branch); hover scale 1.4×; crosshair cursor; positioned in/out center, error right-edge |
| **Edges** | React Flow default bezier | Custom cubic-bezier with arrow markers; error-dash style; selected highlight; active flow animation + glow; edge labels at midpoint |
| **Containers** | Not implemented visually | Dashed border; collapsible; children rendered inline in `container-body`; drag-to-nest; nested node chrome with reduced padding |
| **Branch Nodes** | Not implemented visually | `branch-ports` section below node body; labeled slots with children; per-branch out ports; sibling connectors |
| **Drop Zones** | Canvas drop only | Above/below each node; inside branch slots; inside container body; gap slots between siblings; error-port-adjacent zone; re-parenting via drag over slot |
| **Property Grid** | `react-hook-form` fieldsets; plain `<button>` submit | Styled inspector panel; sectioned fields with labels, hints, type-specific forms (switch cases, AI config, subflow picker); inline delete button |
| **Execution UI** | `executionStatuses` map in store but no visual feedback | Full run/pause/resume/rerun/cancel lifecycle; node status badges; running-pulse animation; edge flow animation; execution indicator; log panel |
| **Logs** | None | Bottom panel; timestamped lines with level (info/warn/error/success), node name, message; clear button; log count badge |
| **Minimap** | React Flow `<MiniMap>` (external) | Custom SVG minimap; viewport indicator rectangle; color-coded node rectangles |
| **Canvas HUD** | React Flow `<Controls>` (external) | Custom zoom controls (in/out/fit/reset); zoom level percentage display (JetBrains Mono) |
| **Toasts** | None | Slide-in toasts; success/error/info variants; auto-dismiss |
| **Modals** | None | Backdrop-blur confirmation dialog; cancel/confirm buttons; danger variant |
| **Keyboard** | None | Ctrl+S save; Ctrl+N new; Del/Backspace delete selection; Esc deselect; +/− zoom; 0 reset; F fit |
| **Accessibility** | Basic `aria-label` | `focus-visible` ring on all interactive elements; `prefers-reduced-motion` support; WCAG AA color contrast |
| **Dark/Light** | No dark mode | Full dual-theme via `data-theme` attribute; separate variable palettes; theme toggle with icon swap |

### 1.2 What Stays Unchanged

- **React + TypeScript + Vite** build toolchain
- **`@xyflow/react` v12** for the canvas graph (custom node/edge types will be restyled, not replaced)
- **Zustand + Zundo** state management with `graphStore.ts`
- **`engine-contract.ts`** DTO types and parity tests
- **`react-hook-form` + `zod`** for property grid validation
- **Existing test infrastructure** (vitest, playwright)

---

## 2 — Design Token System

- [ ] **2.1 CSS Custom Properties**

Port the entire flow-studio.html `:root` / `[data-theme]` variable system into a new CSS file. This is the foundation every other change depends on.

**Variables to port (grouped):**

| Group | Variables |
|-------|-----------|
| Backgrounds | `--bg-0` through `--bg-4`, `--canvas-bg`, `--panel-glass-tint` |
| Strokes | `--stroke-0` through `--stroke-3` |
| Text | `--text-0` through `--text-3` |
| Accent | `--accent`, `--accent-dim`, `--accent-btn-text` |
| Status | `--run`, `--ok`, `--err`, `--paused`, `--cancel` |
| Node colors | `--n-start`, `--n-action`, `--n-if`, `--n-switch`, `--n-loop`, `--n-ai`, `--n-subflow`, `--n-container`, `--n-end` |
| Canvas | `--grid-dot` |
| Shadows | `--node-shadow`, `--shadow-panel`, `--glow-accent`, `--focus-ring` |

Both dark (default) and light palettes must be defined.

- [ ] **2.2 Typography**

Add `Instrument Sans` (400/500/600/700) and `JetBrains Mono` (400/500/600) via Google Fonts link in `index.html`. Define `.mono` utility class.

- [ ] **2.3 Theme Toggle**

- Store preference in `localStorage` under `flow-studio:theme`.
- Apply `data-theme` attribute to `<html>` before first paint (inline script in `index.html`).
- React hook (`useTheme`) to toggle and persist.

---

## 3 — Layout Restructure

- [ ] **3.1 Grid Shell**

Replace the current flexbox layout in `App.tsx` with CSS Grid:

```
grid-template-columns: 248px 1fr 320px;
grid-template-rows: 48px 1fr 180px;
grid-template-areas:
  "topbar  topbar    topbar"
  "palette canvas    inspector"
  "palette logs      inspector";
```

- [ ] **3.2 New Components**

| Component | Grid Area | Responsibility |
|-----------|-----------|----------------|
| `<Topbar>` | `topbar` | Brand, workflow title, toolbar, theme toggle, exec indicator, run controls |
| `<Palette>` | `palette` | Search, workflow list, categorized draggable node items |
| `<CanvasArea>` | `canvas` | React Flow canvas + custom HUD + minimap overlay |
| `<Inspector>` | `inspector` | Node/edge property editing (replaces current `PropertyGrid` sidebar) |
| `<LogPanel>` | `logs` | Timestamped execution log entries |

- [ ] **3.3 Remove**

- Bare `<h1>` in canvas area
- Inline `style=` props on layout divs
- `<WorkspaceTree>` sidebar (integrate workflow list into palette header)
- `<ImportExportDialog>` / `<DuplicateDialog>` (move to topbar actions or modal)
- `<AppFooter>` (replace with log panel)

---

## 4 — Component Migration (by area)

### [x] Phase A — Foundation (no visible feature change)

#### [x] A.1 Design Tokens & Global Styles

**Files to create:**
- [x] `src/ui/theme/tokens.css` — all CSS custom properties (dark + light palettes)
- [x] `src/ui/theme/global.css` — reset, base typography, scrollbar, utility classes
- [x] `src/ui/theme/animations.css` — `pulse`, `runningPulse`, `flowDash`, `dashMove`, `toastIn` keyframes
- [x] `src/ui/theme/useTheme.ts` — hook for theme toggle/persistence

**Files to update:**
- [x] `index.html` — add Google Fonts link, inline theme-detect script
- [x] `src/main.tsx` — import global CSS files

**Mapping to flow-studio:** Lines 20–1114 (CSS section).

#### [x] A.2 SVG Icon System

Replace emoji-based `nodeIcons.ts` with SVG icon components.

**Files to create:**
- [x] `src/ui/theme/icons.tsx` — SVG icon registry keyed by `RuntimeType`, matching flow-studio node icon SVGs
- Map engine `RuntimeType` → flow-studio visual category:
  - `Noop` → action style, `CSharp`/`CSharpScript`/`CSharpTask` → action, `PowerShell`/`PowerShellTask` → action
  - `IfElse` → if, `Switch` → switch, `ForEach`/`While` → loop
  - `Subflow` → subflow, `Timer` → action, `Container` → container

**Files to update:**
- [x] `src/ui/theme/nodeIcons.ts` — replace emoji with SVG references; add `cssColor` field using `--n-*` variables

**Mapping to flow-studio:** Lines 1281–1416 (NodeTypes registry icons/colors).

#### [x] A.3 Layout Shell

**Files to create:**
- [x] `src/ui/layout/AppShell.tsx` — CSS Grid shell component
- [x] `src/ui/layout/AppShell.css` — grid layout styles

**Files to update:**
- [x] `src/App.tsx` — replace inline-style layout with `<AppShell>` children

---

### [x] Phase B — Topbar & Palette Reskin

#### [x] B.1 Topbar Component

**Files to create:**
- [x] `src/ui/topbar/Topbar.tsx` — brand, title input, toolbar groups, theme toggle, exec indicator, run controls
- [x] `src/ui/topbar/Topbar.css` — `.topbar`, `.brand`, `.tb-group`, `.tb-btn`, `.exec-indicator` styles
- [x] `src/ui/topbar/WorkflowTitleInput.tsx` — editable title with dirty-dot indicator
- [x] `src/ui/topbar/ExecutionIndicator.tsx` — status pulse dot + label
- [x] `src/ui/topbar/RunControls.tsx` — Run/Pause/Resume/Rerun/Cancel button group

**Mapping to flow-studio:** Lines 186–291 (CSS), 1120–1192 (HTML), 2577–2629 (App.init/shortcuts).

#### [x] B.2 Palette Reskin

Restyle the existing `<Palette>` component to match flow-studio's design.

**Files to create:**
- [x] `src/ui/palette/Palette.css` — `.palette`, `.palette-search`, `.palette-section`, `.palette-item` styles
- [x] `src/ui/palette/WorkflowList.tsx` — workflow list sub-component (`.wf-item` with active state, inline delete)

**Files to update:**
- [x] `src/ui/palette/Palette.tsx` — replace inline styles; add category grouping with SVG icons + descriptions; integrate `<WorkflowList>`
- [x] `src/ui/palette/paletteCatalog.ts` — add `description` field per entry

**Mapping to flow-studio:** Lines 292–382 (CSS palette), 1194–1209 (HTML), 2168–2203 (Palette JS).

---

### [x] Phase C — Node & Edge Visuals

#### [x] C.1 Node Chrome Redesign

The biggest visual change. Replace the white-card-with-emoji look with flow-studio's gradient card with accent bar, SVG icon, type badge, and config preview body.

**Files to create:**
- [x] `src/ui/nodes/NodeChrome.css` — `.node`, `.node-head`, `.node-head-accent`, `.node-icon`, `.node-title`, `.node-type-label`, `.node-body`, `.node-body-row`, `.node-status-badge`, status ring pseudo-element
- [x] `src/ui/nodes/NodeBodyPreview.tsx` — monospace config summary per runtime type (condition, expression, method/URL, etc.)

**Files to update:**
- [x] `src/ui/nodes/NodeChrome.tsx` — complete rewrite: accent bar, SVG icon, title, type label, body preview; add `data-status` attribute for CSS status rings
- [x] `src/ui/nodes/WorkflowNode.tsx` — pass status to NodeChrome; forward `data-status` to outer wrapper
- [x] `src/ui/nodes/NodeStatusBadge.tsx` — restyle with position, colors, transform animation
- [x] `src/ui/nodes/statusBadgeTokens.ts` — map `NodeExecutionStatus` → CSS classes and label text

**Mapping to flow-studio:** Lines 455–837 (CSS nodes/containers), 1668–1877 (Nodes.render/html/bodyHtml).

#### [x] C.2 Port System

Replace React Flow's default handles with custom circular ports matching flow-studio's in/out/error/branch design.

**Files to create:**
- [x] `src/ui/ports/CustomPort.tsx` — circular port with hover scale, crosshair cursor, position variants (in/out/error)
- [x] `src/ui/ports/CustomPort.css` — `.port`, `.port.in`, `.port.out`, `.port.error`, `.port:hover`, `.port.connecting`

**Files to update:**
- [x] `src/ui/nodes/WorkflowNode.tsx` — replace `<Handle>` with `<CustomPort>`
- [x] `src/ui/ports/portMatrix.ts` — add error port support per node kind

**Mapping to flow-studio:** Lines 543–577 (CSS ports).

#### [x] C.3 Edge Restyling

**Files to create:**
- [x] `src/ui/edges/WorkflowEdge.css` — `.edge-path`, `.edge-path.error-edge`, `.edge-path.selected`, `.edge-path.active`, `.edge-flow`, `.edge-label`, `.drag-edge-preview`

**Files to update:**
- [x] `src/ui/edges/WorkflowEdge.tsx` — custom SVG path with cubic-bezier; arrow markers (normal/error/active); flow animation for active edges; edge labels for branch names
- [x] `src/ui/edges/edgeStyleMap.ts` — update to use CSS variables instead of inline colors
- [x] `src/ui/canvas/edgeTypes.ts` — register updated edge type

**Mapping to flow-studio:** Lines 839–886 (CSS edges), 2038–2166 (Edges JS).

---

### [x] Phase D — Containers, Branches & Advanced Drag-Drop

This is the most complex phase — it introduces nested node rendering, sequential child chains inside slots, and the edge-splice insertion model that don't exist in the current UI. Two critical interaction patterns must be supported:

1. **Drop into branch/container → sequential children:** Dropping palette items into a branch slot or container body creates nested children. Multiple drops build a **sequence** — children are rendered vertically with down-arrow sibling connectors between them, forming an implicit sequential flow. Gap-drop-slots appear between every pair of siblings so new nodes can be inserted at any position in the sequence.

2. **Drop on edge → auto-wire splice:** Dropping a palette item on the drop zone above or below an existing top-level node **splices the new node into the existing edge chain**. If Node A → Node B exists and you drop a new node below A, the result is A → New → B with edges automatically re-routed. This lets users build linear chains quickly without manual wiring.

#### [x] D.1 Container Node Rendering

**Files to create:**
- [x] `src/ui/nodes/ContainerNode.tsx` — collapsible container with `container-body` that renders children inline; collapse button; child count badge when collapsed
- [x] `src/ui/nodes/ContainerNode.css` — `.node.container`, `.container-body`, `.container-hint`, `.collapse-btn`, `.node.nested`, `.container-children`, `.container-child-count`

**Behavior:**
- Container has a single slot (`_body`) that holds an ordered list of child node IDs.
- When empty, shows a "Drop component" hint placeholder.
- When populated, children are rendered as `position: relative` nested nodes stacked vertically inside `.container-children`, with **sibling connectors** (SVG down-arrow dividers) between consecutive children.
- **Gap-drop-slots** appear between every pair of siblings (and at index 0 before the first child) during palette drag. Each slot targets a specific insertion index so the user can drop a new node at any position in the sequence.
- Collapsed state hides the body and shows a child count badge.
- Children inherit a `.node.nested` class that uses reduced padding, smaller ports, and no absolute positioning.

**Design decisions:**
- Container children are rendered as React components inside the parent node's custom component DOM, not as separate top-level React Flow nodes.
- The `graphStore` needs `parentId` / `parentSlot` fields in node metadata to track nesting.
- Nested nodes use `position: relative` (CSS flow layout) instead of `position: absolute` (canvas coordinates).

**Mapping to flow-studio:** Lines 794–837 (CSS), 1689–1722 (renderChildrenInto), 1769–1827 (createElement with container body).

#### [x] D.2 Branch Node Rendering (If/Switch/Loop/Parallel)

Branch nodes (IfElse, Switch, ForEach, While in engine terms) render labeled slot columns. Each slot is itself an ordered-sequence container.

**Files to create:**
- [x] `src/ui/nodes/BranchNode.tsx` — node type that renders:
  - Standard node header (icon + title + type badge)
  - Node body with config preview (condition, expression, etc.)
  - Error port on the body's right edge
  - A `.branch-ports` footer with one `.branch-port` column per branch
  - Each branch column contains: label, `.branch-port-children` (vertical child sequence), and a bottom output port
- [x] `src/ui/nodes/BranchNode.css` — `.branch-ports`, `.branch-port`, `.branch-port-label`, `.branch-port-children`, `.branch-port-empty`, `.sibling-connector`
- [x] `src/ui/nodes/SiblingConnector.tsx` — SVG down-arrow rendered between consecutive children in any slot

**Sequential children in branch slots:**
- Each branch slot (e.g., "true" / "false" on an If/Else) holds an ordered array of child IDs.
- Multiple children in the same slot render vertically as a sequence, connected by `<SiblingConnector>` arrows.
- Gap-drop-slots appear at every insertion point (before first child, between each pair, after last child).
- When empty, a "Drop component" placeholder is shown in the slot.
- The branch's bottom port sits below all children. Edges from this port connect to the next top-level node, representing "branch exits here after the sequence completes."

**Branch slot width auto-sizing:**
- Empty slots have `min-width: 96px`.
- Slots containing children expand to `min-width: 200px` (via CSS `:has(> .branch-port-children > .node)`).

**Mapping to flow-studio:** Lines 579–656 (CSS), 1865–1877 (branchHtml), 1700–1722 (renderChildrenInto with gaps & connectors).

#### [x] D.3 Edge-Splice Insertion (Drop on Node Edge Zones)

When a palette item is dropped on the **above** or **below** drop zone of an existing top-level node, the new node is automatically spliced into the edge chain. This is distinct from plain canvas drops — it creates automatic wiring.

**Behavior — "drop above" (position=before):**
```
BEFORE:  A ──edge──► B     (drop zone "above" B is triggered)
AFTER:   A ──edge──► NEW ──new-edge──► B
```
- Find the incoming edge targeting B (`edge.target === B.id && edge.targetPort === 'in'`).
- Re-route that edge to target NEW instead.
- Create a new edge from NEW's output port to B's input port.
- Position NEW at `(B.x, B.y - 180)`.

**Behavior — "drop below" (position=after):**
```
BEFORE:  A ──edge──► B     (drop zone "below" A is triggered)
AFTER:   A ──edge──► NEW ──new-edge──► B
```
- Find the outgoing edge from A (`edge.source === A.id && edge.sourcePort === 'out'`).
- Re-route that edge: change target to NEW.
- Create a new edge from NEW's output port to the original target B.
- Position NEW at `(A.x, A.y + 180)`.

**Behavior — "drop on error zone" (position=after-error):**
- Same as "drop below" but uses the error port (`edge.sourcePort === 'error'`).

**Behavior for nested nodes:**
- When the reference node is nested (has `parentId`), insertion doesn't splice edges. Instead, it inserts the new node into the parent's children array at the correct index (before or after the reference child). The sequence order *is* the wiring.

**Files to create:**
- [x] `src/ui/nodes/DropZone.tsx` — above/below/error-right drop zones rendered on each node; hidden by default, visible only when `body.dragging-palette` is active; each zone carries `data-node-id` and `data-position` ("before" | "after" | "after-error")
- [x] `src/ui/nodes/GapDropSlot.tsx` — inline "Drop here" slots between sibling children in branch/container slots; carries `data-parent-id`, `data-slot`, `data-index`; visible only during palette drag
- [x] `src/ui/nodes/DropZone.css` — `.drop-zone`, `.gap-drop-slot`, `body.dragging-palette` conditional activation; nested node drop zones are hidden (`.node.nested > .drop-zone { display: none }`) because gap-slots handle insertion
- [x] `src/ui/hooks/useReparentDrag.ts` — pointer-based drag handler for top-level node repositioning that also detects slot hover for re-parenting; uses `elementFromPoint` with `.dragging` class (pointer-events: none on dragged node) to see through to the slot underneath

**Files to update:**
- [x] `src/ui/palette/Palette.tsx` — add `document.body.classList.add('dragging-palette')` on `dragstart`, remove on `dragend`
- [x] `src/ui/state/graphStore.ts` — add these actions:
  - `reparentNode(nodeId, newParentId, newSlot, index?)` — detach from old parent/canvas, attach to new parent's slot at index; also clean up redundant edges
  - `insertNodeRelative(type, refNodeId, position)` — create node and auto-splice into edge chain (for top-level) or insert at correct index (for nested)
  - `createChildNode(type, parentId, slot, index?)` — create node as child of parent at slot[index]
  - Add `parentId` / `parentSlot` fields to sidecar node metadata
  - `isDescendantOf(maybeDescendantId, ancestorId)` — walk children tree to prevent cycles during re-parenting

#### [x] D.4 Re-Parenting via Canvas Drag

Dragging a top-level node over a branch slot or container body highlights the slot (`.reparent-target` class) and, on pointer-up, moves the node from the canvas into that slot as a nested child.

**Behavior:**
1. On `pointermove` during top-level node drag, call `elementFromPoint(clientX, clientY)` to check if the cursor is over a `.branch-port[data-parent-id]` or `.container-body`.
2. If a slot is found (and it's not the dragged node itself, and the target is not a descendant of the dragged node), add `.reparent-target` class for visual feedback.
3. On `pointerup`, if a slot is highlighted, call `reparentNode()` to move the node into the slot.
4. The `.dragging` class on the node sets `pointer-events: none` so `elementFromPoint` can see through it to the slot underneath.
5. Edges from the parent's matching branch port to this node are cleaned up (the parent-child relationship is now implicit in the slot array, not via edges).

**Files:** Handled by `src/ui/hooks/useReparentDrag.ts` (created in D.3).

**Mapping to flow-studio:** Lines 1879–1950 (drag handlers with findSlotAt/reparent), 2684–2718 (reparentNode logic).

#### [x] D.5 Complete Drag-Drop Behavior Matrix

| Drag Source | Drop Target | Result |
|---|---|---|
| Palette item | Empty canvas area | Create top-level node at cursor position |
| Palette item | Empty branch slot | Create nested child in that slot (first child) |
| Palette item | Empty container body | Create nested child in `_body` slot (first child) |
| Palette item | Gap-drop-slot at index *i* | Create nested child at position *i* in that slot's sequence |
| Palette item | Drop zone "above" node (top-level) | Create node + splice into incoming edge: `A→B` becomes `A→New→B` |
| Palette item | Drop zone "below" node (top-level) | Create node + splice into outgoing edge: `A→B` becomes `A→New→B` |
| Palette item | Drop zone "error-right" of node | Create node + splice into error edge |
| Palette item | Drop zone "above"/"below" (nested) | Insert at correct index in parent slot sequence |
| Palette item | Populated branch slot (not on gap) | Append child at end of slot's sequence |
| Palette item | Populated container body (not on gap) | Append child at end of `_body` sequence |
| Top-level node (pointer drag) | Branch slot | Re-parent: move from canvas into slot as nested child |
| Top-level node (pointer drag) | Container body | Re-parent: move from canvas into `_body` as nested child |
| Top-level node (pointer drag) | Empty canvas | Reposition (existing behavior) |

**Cycle prevention:** `isDescendantOf(targetId, draggedId)` — if the target slot's owner is a descendant of the dragged node, the drop is rejected (prevents nesting a node inside its own subtree).

**Edge cleanup on re-parent:** When a node moves into a slot, any explicit edge from the slot's parent to that node (e.g., a branch-port edge) is deleted — the parent-child relationship in the slot array replaces it.

**Mapping to flow-studio:** Lines 679–744 (CSS drop zones/gaps), 1700–1757 (gap slot creation), 1879–2035 (drag handlers), 2672–2757 (reparent/insert/createChild logic).

---

### [x] Phase E — Inspector & Property Grid Reskin

#### [x] E.1 Inspector Panel

Restyle `<PropertyGrid>` as `<Inspector>` matching flow-studio's right panel design.

**Files to create:**
- [x] `src/ui/inspector/Inspector.tsx` — wrapper with sticky header, icon+title, subtitle
- [x] `src/ui/inspector/Inspector.css` — `.inspector`, `.ins-header`, `.ins-section`, `.ins-field`, `.ins-field-label`, `.ins-field-hint`, `.ins-empty`
- [x] `src/ui/inspector/EdgeInspector.tsx` — edge detail view with source/target info and delete button
- [x] `src/ui/inspector/SwitchCaseEditor.tsx` — add/remove/rename cases
- [x] `src/ui/inspector/BranchEditor.tsx` — add/remove/rename parallel branches

**Files to update:**
- [x] `src/ui/property-grid/PropertyGrid.tsx` — refactor into Inspector component; restyle form fields
- [x] `src/ui/property-grid/FieldRenderer.tsx` — apply `.ins-field` styling

**Mapping to flow-studio:** Lines 888–976 (CSS inspector), 2206–2575 (Inspector JS).

---

### [ ] Phase F — Execution UI & Logs

#### [ ] F.1 Execution State & Controls

**Files to create:**
- [ ] `src/ui/execution/ExecutionManager.ts` — run/pause/resume/rerun/cancel state machine; integrates with `graphStore.setExecutionStatus`
- [ ] `src/ui/execution/useExecutionControls.ts` — hook exposing run/pause/resume/rerun/cancel actions + current state
- [ ] `src/ui/topbar/RunControls.tsx` (if not already created in B.1) — button group wired to `useExecutionControls`

**Files to update:**
- [ ] `src/ui/state/graphStore.ts` — ensure `executionStatuses` map drives node `data-status` rendering
- [ ] `src/ui/nodes/NodeChrome.tsx` — render status badge + pass `data-status` to CSS for glow ring
- [ ] `src/ui/edges/WorkflowEdge.tsx` — render `.edge-flow` overlay path when edge is active

**Mapping to flow-studio:** Lines 3175–3400+ (Exec object).

#### [ ] F.2 Log Panel

**Files to create:**
- [ ] `src/ui/logs/LogPanel.tsx` — scrollable log area with header, count badge, clear button
- [ ] `src/ui/logs/LogPanel.css` — `.logs`, `.log-line`, `.log-time`, `.log-level`, `.log-node`, `.log-msg`
- [ ] `src/ui/logs/logStore.ts` — Zustand slice or standalone store for log entries

**Mapping to flow-studio:** Lines 978–1031 (CSS), 3091–3124 (log rendering).

---

### [ ] Phase G — Canvas HUD, Minimap, Toasts, Modals, Keyboard

#### [ ] G.1 Custom Canvas HUD

Replace React Flow's `<Controls>` with a custom zoom control panel.

**Files to create:**
- [ ] `src/ui/canvas/CanvasHUD.tsx` — zoom in/out/fit/reset buttons + zoom percentage display
- [ ] `src/ui/canvas/CanvasHUD.css` — `.canvas-hud`, `.zoom-ctrls`, `.zoom-level`

**Files to update:**
- [ ] `src/ui/canvas/WorkflowCanvas.tsx` — replace `<Controls>` with `<CanvasHUD>`

**Mapping to flow-studio:** Lines 424–453 (CSS), 1225–1241 (HTML).

#### [ ] G.2 Custom Minimap

Optionally replace React Flow's `<MiniMap>` with a custom SVG minimap matching flow-studio's node-colored rectangles + viewport indicator.

**Mapping to flow-studio:** Lines 1103–1113 (CSS), 1632–1666 (Minimap JS).

#### [ ] G.3 Toast System

**Files to create:**
- [ ] `src/ui/toasts/ToastContainer.tsx` — positioned container
- [ ] `src/ui/toasts/toastStore.ts` — Zustand store for toast queue
- [ ] `src/ui/toasts/Toasts.css` — `.toasts`, `.toast`, `.toast.success`, `.toast.error`, `.toast.info`

**Mapping to flow-studio:** Lines 1067–1095 (CSS), 1459–1466 (toast function).

#### [ ] G.4 Modal System

**Files to create:**
- [ ] `src/ui/modals/ConfirmModal.tsx` — backdrop + modal card with cancel/confirm
- [ ] `src/ui/modals/ConfirmModal.css` — `.modal-backdrop`, `.modal`, `.modal-actions`

**Mapping to flow-studio:** Lines 1033–1065 (CSS), 3155–3172 (showModal).

#### [ ] G.5 Keyboard Shortcuts

**Files to create:**
- [ ] `src/ui/hooks/useKeyboardShortcuts.ts` — global keydown handler for Ctrl+S, Ctrl+N, Del, Esc, +/−/0/F

**Mapping to flow-studio:** Lines 3127–3141 (bindShortcuts).

---

### [ ] Phase H — Accessibility & Polish

#### [ ] H.1 Focus Management

- [ ] All interactive elements get `focus-visible` ring styling (from `tokens.css`)
- [ ] `prefers-reduced-motion` disables animations
- [ ] `aria-label` on all buttons and interactive regions

#### [ ] H.2 Light Theme Verification

- [ ] Audit every component in light mode for contrast issues
- [ ] Status badges use `color: #ffffff !important` in light mode (flow-studio pattern)

#### [ ] H.3 Responsive Scrollbars

- [ ] Custom scrollbar styling via `::-webkit-scrollbar` (already in `global.css`)

---

## 5 — Implementation Order & Dependencies

```
Phase A ──────► Phase B ──────► Phase C ──────► Phase D
(tokens,        (topbar,        (nodes,         (containers,
 icons,          palette)        ports,          branches,
 layout)                         edges)          drag-drop)
                                     │
                                     ▼
                                Phase E ──────► Phase F ──────► Phase G ──────► Phase H
                                (inspector)     (execution,     (HUD,           (a11y,
                                                 logs)          toasts,         polish)
                                                                modals,
                                                                keyboard)
```

**Critical path:** A → B → C → D (each phase depends on the previous).  
**Parallel tracks:** E can start after C. F can start after E. G can start after A.

---

## 6 — File Inventory (estimated new/modified)

### New Files (~35)

| File | Phase |
|------|-------|
| `src/ui/theme/tokens.css` | A.1 |
| `src/ui/theme/global.css` | A.1 |
| `src/ui/theme/animations.css` | A.1 |
| `src/ui/theme/useTheme.ts` | A.1 |
| `src/ui/theme/icons.tsx` | A.2 |
| `src/ui/layout/AppShell.tsx` | A.3 |
| `src/ui/layout/AppShell.css` | A.3 |
| `src/ui/topbar/Topbar.tsx` | B.1 |
| `src/ui/topbar/Topbar.css` | B.1 |
| `src/ui/topbar/WorkflowTitleInput.tsx` | B.1 |
| `src/ui/topbar/ExecutionIndicator.tsx` | B.1 |
| `src/ui/topbar/RunControls.tsx` | B.1 |
| `src/ui/palette/Palette.css` | B.2 |
| `src/ui/palette/WorkflowList.tsx` | B.2 |
| `src/ui/nodes/NodeChrome.css` | C.1 |
| `src/ui/nodes/NodeBodyPreview.tsx` | C.1 |
| `src/ui/ports/CustomPort.tsx` | C.2 |
| `src/ui/ports/CustomPort.css` | C.2 |
| `src/ui/edges/WorkflowEdge.css` | C.3 |
| `src/ui/nodes/ContainerNode.tsx` | D.1 |
| `src/ui/nodes/ContainerNode.css` | D.1 |
| `src/ui/nodes/BranchNode.tsx` | D.2 |
| `src/ui/nodes/BranchNode.css` | D.2 |
| `src/ui/nodes/SiblingConnector.tsx` | D.2 |
| `src/ui/nodes/DropZone.tsx` | D.3 |
| `src/ui/nodes/GapDropSlot.tsx` | D.3 |
| `src/ui/nodes/DropZone.css` | D.3 |
| `src/ui/hooks/useReparentDrag.ts` | D.3/D.4 |
| `src/ui/hooks/useEdgeSplice.ts` | D.3 |
| `src/ui/inspector/Inspector.tsx` | E.1 |
| `src/ui/inspector/Inspector.css` | E.1 |
| `src/ui/inspector/EdgeInspector.tsx` | E.1 |
| `src/ui/inspector/SwitchCaseEditor.tsx` | E.1 |
| `src/ui/inspector/BranchEditor.tsx` | E.1 |
| `src/ui/execution/ExecutionManager.ts` | F.1 |
| `src/ui/execution/useExecutionControls.ts` | F.1 |
| `src/ui/logs/LogPanel.tsx` | F.2 |
| `src/ui/logs/LogPanel.css` | F.2 |
| `src/ui/logs/logStore.ts` | F.2 |
| `src/ui/canvas/CanvasHUD.tsx` | G.1 |
| `src/ui/canvas/CanvasHUD.css` | G.1 |
| `src/ui/toasts/ToastContainer.tsx` | G.3 |
| `src/ui/toasts/toastStore.ts` | G.3 |
| `src/ui/toasts/Toasts.css` | G.3 |
| `src/ui/modals/ConfirmModal.tsx` | G.4 |
| `src/ui/modals/ConfirmModal.css` | G.4 |
| `src/ui/hooks/useKeyboardShortcuts.ts` | G.5 |

### Modified Files (~15)

| File | Phase | Change |
|------|-------|--------|
| `index.html` | A.1 | Google Fonts, theme-detect script |
| `src/main.tsx` | A.1 | Import global CSS |
| `src/App.tsx` | A.3 | Replace layout with `<AppShell>` |
| `src/ui/theme/nodeIcons.ts` | A.2 | Replace emoji → SVG; add `cssColor` |
| `src/ui/palette/Palette.tsx` | B.2 | Restyle; add descriptions, icons |
| `src/ui/palette/paletteCatalog.ts` | B.2 | Add `description` field |
| `src/ui/nodes/NodeChrome.tsx` | C.1 | Complete redesign |
| `src/ui/nodes/WorkflowNode.tsx` | C.1/C.2 | Custom ports, status data |
| `src/ui/nodes/NodeStatusBadge.tsx` | C.1 | Restyle |
| `src/ui/nodes/statusBadgeTokens.ts` | C.1 | Map to CSS classes |
| `src/ui/edges/WorkflowEdge.tsx` | C.3 | Custom path, animation |
| `src/ui/edges/edgeStyleMap.ts` | C.3 | Use CSS variables |
| `src/ui/canvas/WorkflowCanvas.tsx` | G.1 | Replace Controls/MiniMap |
| `src/ui/state/graphStore.ts` | D.3 | Add `reparentNode`, `insertNodeRelative`, `createChildNode`, `isDescendantOf`; `parentId`/`parentSlot` metadata |
| `src/ui/canvas/nodeTypes.ts` | D.1/D.2 | Register `containerNode` and `branchNode` custom types |
| `src/ui/property-grid/PropertyGrid.tsx` | E.1 | Refactor to Inspector |
| `src/ui/ports/portMatrix.ts` | C.2 | Error port support |

---

## 7 — Key Design Decisions

### 7.1 CSS Approach

Use **CSS Modules** (`.module.css`) per component alongside the global `tokens.css`. This keeps specificity manageable while allowing the design token system to work via `var()` references. No CSS-in-JS library is needed.

### 7.2 React Flow Customization vs. Replace

Keep `@xyflow/react` but heavily customize:
- Custom node types (`workflowNode`, `containerNode`, `branchNode`) with full visual overhaul
- Custom edge type with cubic-bezier path, markers, and animation
- Custom port components instead of default Handles
- Suppress default Controls/MiniMap in favor of custom HUD

React Flow handles the hard parts (viewport transform, connection validation, selection, drag, layout engine) — replacing it would be counterproductive.

### 7.3 Nested Nodes in React Flow

React Flow doesn't natively support nodes rendered inside other nodes' DOM. Two approaches:

**Option A (recommended):** Render container/branch children as **React portals** inside the parent node's custom component. Children are *not* top-level React Flow nodes. Their positions are managed by CSS flow layout, not canvas coordinates.

**Option B:** Use React Flow's `parentNode` / `extent: 'parent'` feature. Children remain top-level nodes but are constrained to the parent's bounds.

Option A better matches flow-studio's behavior (inline flow layout, gap slots, sibling connectors) but requires more custom rendering logic.

### 7.4 Drag-Drop Architecture

The current UI uses HTML5 Drag and Drop API for palette → canvas. Flow-studio uses the same for palette drops but **pointer events** for canvas node dragging (including re-parenting). This hybrid approach should be preserved:

- **Palette → Canvas/Slot:** HTML5 drag (`dragstart`, `dragover`, `drop`)
- **Node reposition:** Pointer events (React Flow's built-in + custom re-parenting detection)
- **Edge creation:** Pointer events from port → port (React Flow's onConnect or custom)

### 7.5 Engine RuntimeType → Visual Category Mapping

| Engine RuntimeType | flow-studio Visual Category | Color Variable |
|----|----|----|
| `Noop` | Action | `--n-action` |
| `CSharp`, `CSharpScript`, `CSharpTask` | Action | `--n-action` |
| `PowerShell`, `PowerShellTask` | Action | `--n-action` |
| `IfElse` | If/Else | `--n-if` |
| `Switch` | Switch | `--n-switch` |
| `ForEach`, `While` | Loop | `--n-loop` |
| `Subflow` | Subflow | `--n-subflow` |
| `Timer` | Action | `--n-action` |
| `Container` | Container | `--n-container` |

---

## 8 — Testing Strategy

### 8.1 Visual Regression

After each phase, verify visually in both dark and light themes. Consider adding Playwright visual snapshots for key states:
- Canvas with mixed node types
- Container with nested children
- Branch node (if/switch) with slot children
- Active execution with running/success/failed nodes

### 8.2 Existing Tests

All existing vitest unit tests must continue to pass. The `engine-contract` parity tests are the non-negotiable gate.

### 8.3 New Tests

Each phase should include:
- Component render tests for new components (vitest + testing-library)
- Interaction tests for drag-drop, keyboard shortcuts (playwright)
- Theme toggle tests (both themes render without errors)

---

## 9 — Risk Register

| ID | Risk | Impact | Mitigation |
|----|------|--------|------------|
| R1 | React Flow nested node rendering is complex and fragile | HIGH | Prototype Option A vs Option B early in Phase D; fall back to Option B if portal approach has perf issues |
| R2 | CSS variable migration breaks existing test selectors | MEDIUM | Keep `data-testid` attributes stable; only change visual styling |
| R3 | Drop zone system conflicts with React Flow's internal drag handling | HIGH | Use `stopPropagation` carefully; test palette drag vs node drag vs edge drag in isolation |
| R4 | Flow-studio's vanilla JS patterns don't translate cleanly to React | MEDIUM | Adapt patterns to React idioms (hooks, state, refs) rather than porting 1:1 |
| R5 | Performance regression with complex nested node trees | MEDIUM | Memoize node components aggressively; limit re-render scope in Zustand selectors |
| R6 | Google Fonts dependency adds latency | LOW | Use `font-display: swap`; preconnect hints; consider self-hosting |
