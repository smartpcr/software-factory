# Stage 6.3 — Live monitor UI

**Phase:** §12 Phase 6 — SignalR Telemetry & Live Monitor
**Stage status:** [ ]
**Total effort:** 5 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

The live monitor is the browser surface that consumes the `TelemetryClient` (Stage 6.2) and
animates the designer canvas (Phase 3) with real-time status. It has three parts: canvas overlays
(Step 6.3.1) that colour nodes and edges by the latest `RunEvent` seen; an event-log side panel
(Step 6.3.2) with filters by kind, nodeId and MessageType; and a run-attach picker (Step 6.3.3)
that lets the user select an active run from the host API and subscribe through the hub. None of
these steps introduce new DTOs — they consume the `RunEvent` / `RunEventKind` shapes fixed in
Stage 6.1 and §2.8 of the master plan.

Critical parity constraints: the overlay can colour a node `Skipped` only when `RunEvent.kind ==
NodeSkipped AND synthetic == true` (enforced by §2.8 rule that `NodeSkipped` is derived).
`NodeCompletedEvent` has NO status discriminator (`src/ExecutionEngine/Events/NodeCompletedEvent.
cs:12-23`), so a node is `Completed` unless a synthetic skip arrives first. The overlay MUST NOT
attempt to render `Paused` as a node status — `Paused` is a workflow-level status only
(`WorkflowExecutionStatus.cs:42`), rendered in the header chip, not on nodes.

The UI uses **Zustand** for the telemetry store (matches Phase 3 canvas store pattern) and
**React Flow** for the canvas itself. Tailwind tokens from Phase 3 provide the colour palette
(`node-status-pending/running/completed/failed/cancelled/skipped`). Animation jank is managed by
batching overlay updates to at most one paint per `requestAnimationFrame` (≈60 Hz).

## Prerequisites
- Stage 6.2 complete.
- Stage 3.1.3 (canvas render).
- External deps already in project from Phase 3: React 18, React Flow 11, Zustand 4,
  Tailwind 3, Vitest, Playwright.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 6.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/ui/src/live-monitor/store/telemetryStore.ts` (new)
- `src/ui/src/live-monitor/store/overlayProjector.ts` (new — RunEvent → {nodeStates, edgeStates})
- `src/ui/src/live-monitor/components/CanvasOverlay.tsx` (new)
- `src/ui/src/live-monitor/components/EventLogPanel.tsx` (new)
- `src/ui/src/live-monitor/components/EventRow.tsx` (new)
- `src/ui/src/live-monitor/components/RunFilterBar.tsx` (new)
- `src/ui/src/live-monitor/components/RunAttachPicker.tsx` (new)
- `src/ui/src/live-monitor/api/runsApi.ts` (new — GET /api/runs)
- `src/ui/src/live-monitor/hooks/useLiveRun.ts` (new)
- `src/ui/src/live-monitor/__tests__/telemetryStore.test.ts` (new)
- `src/ui/src/live-monitor/__tests__/overlayProjector.test.ts` (new)
- `src/ui/src/live-monitor/__tests__/EventLogPanel.test.tsx` (new)
- `src/ui/src/live-monitor/__tests__/RunAttachPicker.test.tsx` (new)
- `src/ui/e2e/live-monitor-overlay.spec.ts` (new)
- `src/ui/e2e/live-monitor-filters.spec.ts` (new)
- `src/ui/e2e/live-monitor-attach.spec.ts` (new)
- `src/host/WorkflowUi.Host/Controllers/RunsController.cs` (new — minimal, drives picker)

## Shared fixtures / fakes
- **`scriptedRun.json`** — 60-event scripted run covering every `RunEventKind` at least once,
  used by overlay + log tests for deterministic playback.
- **`FakeTelemetryClient`** — implements the same API as `TelemetryClient.ts` but replays events
  from a scripted array on a tick, used by Vitest and Playwright (via `@playwright/test`
  `page.addInitScript`).
- **100-event burst fixture** — `burstRun.json` used to assert 60 Hz batching in overlay.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="6.3"`, `phaseId="6"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-6.3.plan-read`, `workflow-ui.stage-6.3.apply`, `workflow-ui.stage-6.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_6_3_started_total`, counter `workflow_ui_stage_6_3_failed_total`, histogram `workflow_ui_stage_6_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| event bridge | native event mapping drifts from engine | bridge parity test or contract golden fails | re-read cited event class and update adapter |
| SignalR hub | reconnect or backlog handling duplicates events | seq/order tests fail | dedupe by `(runId, seq)` and replay from last ack |
| live monitor store | high-frequency updates tear UI state | render/perf tests or dropped-frame budget fails | batch store updates and coalesce transient fields |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| monitor attach / replay hydrate | 250 ms | 750 ms |
| 100 telemetry events -> UI render | 100 ms | 300 ms |
| SignalR reconnect | 1 s | 5 s |

## Accessibility checklist
- [ ] Live monitor timeline updates are announced politely without overwhelming screen readers.
- [ ] Run-status chips and edge animations have text equivalents and reduced-motion fallbacks.
- [ ] Attach/reconnect controls remain keyboard reachable during transient loading states.
- [ ] Color-coded telemetry severities include icon/text redundancy.
- [ ] Focus is preserved when streaming updates rerender list rows or panels.

## Security callouts
- SignalR payloads must exclude secrets, bearer tokens, and machine-local paths.
- Reconnect state should use bounded backoff and drop stale subscriptions cleanly to avoid ghost listeners.
- Synthetic event derivation must not trust client input; derive from engine events / workflow graph only.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 6.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage63` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Attach the live monitor to a busy run, disconnect the network, reconnect, and confirm event ordering by `(runId, seq)`.
- Run with a screen reader/reduced-motion setting and verify updates remain understandable.
- Leave the monitor open for 10 minutes and confirm no duplicate subscriptions or memory blow-up.

---

## Step 6.3.1 — Canvas overlays from telemetry stream (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Build a Zustand `telemetryStore` that maintains per-run `nodeStates: Record<string, NodeStatus>`
and `edgeStates: Record<string, EdgeActivation>` derived from the `RunEvent` stream. Wrap the
canvas in `<CanvasOverlay>` which reads the store and applies Tailwind status classes to nodes
and edge stroke colours. Overlay updates are batched per `requestAnimationFrame` to cap paint
rate at ~60 Hz even under 100-event bursts.

### 4. Depends on
- Step 6.2.2
- Step 3.1.3 (canvas)

### 5. Inputs
- `TelemetryClient` from Stage 6.2.
- React Flow node + edge model from Phase 3.
- Status palette tokens from Tailwind config.

### 6. Deliverables
- `telemetryStore.ts` — Zustand store with:
  ```ts
  interface TelemetryState {
    runId: string | null;
    lastSeq: number | null;
    nodeStates: Record<string, NodeStatus>;
    edgeStates: Record<string, EdgeActivation>;
    attach(runId: string): Promise<void>;
    detach(): Promise<void>;
    ingest(ev: RunEvent): void;          // only called from client subscription
    reset(): void;
  }
  type NodeStatus = 'Pending'|'Running'|'Completed'|'Failed'|'Cancelled'|'Skipped';
  interface EdgeActivation { triggerMessageType: MessageType; atSeq: number; }
  ```
- `overlayProjector.ts` — pure reducer `(state, ev) => state` used by tests AND the store.
- `CanvasOverlay.tsx` — wraps React Flow `<ReactFlow>` with `nodeClassName` and `edgeClassName`
  derived from store selectors using shallow equality.
- `requestAnimationFrame` batching via a `pendingEvents: RunEvent[]` buffer flushed on rAF.

### 7. Acceptance criteria
- [ ] Every `RunEventKind` maps to at most one state mutation — idempotent on replay.
- [ ] `NodeSkipped` (synthetic) renders the node with the `skipped` class.
- [ ] `EdgeActivated` (synthetic) sets the edge to the colour matching its `triggerMessageType`
      (`Complete=green`, `Fail=red`, `Next=blue`, `Cancel=gray`, `Progress=neutral`).
- [ ] `RunCompleted`/`RunFailed`/`RunCancelled` DO NOT recolor nodes — node status is independent
      of run-terminal events.
- [ ] Under a 100-event burst in < 100 ms, overlay paints ≤ 6 frames (≤ 60 Hz).
- [ ] Late `NodeStarted` for an already-`Completed` node is IGNORED (monotonic FSM guard).
- [ ] Store `reset()` clears node/edge states and re-attaches cleanly.

### 8. Unit tests (Vitest)
1. `projector_RunStarted_setsRunIdAndClearsStates`.
2. `projector_NodeStarted_transitionsPendingToRunning`.
3. `projector_NodeCompleted_transitionsToCompleted`.
4. `projector_NodeFailed_transitionsToFailed`.
5. `projector_NodeCancelled_transitionsToCancelled`.
6. `projector_NodeSkipped_transitionsToSkipped_synthetic`.
7. `projector_EdgeActivated_setsEdgeStateWithTrigger`.
8. `projector_NodeStartedAfterCompleted_isIgnored` (monotonic FSM).
9. `projector_RunCompleted_doesNotMutateNodeStates`.
10. `store_ingest_batches_rAF_flush` (fake rAF: emit 100 events, assert one commit).
11. `store_reset_clearsAllState`.

Three AAA sketches:

```ts
// #6 projector_NodeSkipped_transitionsToSkipped_synthetic
const s0: TelemetryState = initialState('run-1');
const ev: RunEvent = { runId:'run-1', seq:5, kind:'NodeSkipped',
  synthetic:true, payload:{nodeId:'f', reason:'BranchNotTaken'}, ...};
// Act
const s1 = project(s0, ev);
// Assert
expect(s1.nodeStates['f']).toBe('Skipped');
expect(s1.lastSeq).toBe(5);

// #8 projector_NodeStartedAfterCompleted_isIgnored
let s = initialState('run-1');
s = project(s, completed('n1', 4));
const ev: RunEvent = { kind:'NodeStarted', payload:{nodeId:'n1'}, seq:5, ...};
// Act
const s2 = project(s, ev);
// Assert
expect(s2.nodeStates['n1']).toBe('Completed');   // guard prevents regression

// #10 store_ingest_batches_rAF_flush
vi.useFakeTimers();
const rafSpy = vi.spyOn(window, 'requestAnimationFrame')
  .mockImplementation(cb => { setTimeout(() => cb(performance.now()), 16); return 1; });
const store = createTelemetryStore(fakeClient);
for (let i=0;i<100;i++) store.getState().ingest(makeNodeStarted(i));
vi.advanceTimersByTime(20);
// Assert — store committed once, not 100 times
expect(store.__commitCount).toBe(1);
expect(Object.keys(store.getState().nodeStates)).toHaveLength(100);
```

### 9. Integration tests (Playwright)
- `live-monitor-overlay.spec.ts`:
  1. Attach to scripted run with `FakeTelemetryClient` seeded with `scriptedRun.json`.
  2. Step through events, assert React Flow node class transitions at each milestone
     (`Pending → Running → Completed`).
  3. Burst test: ingest 100 events, assert `performance.now()` delta between first and last paint
     ≥ 83 ms (indicating ≥ 5 frame budget, not synchronous flood).
  4. Screenshot comparison after final event to pinned baseline under
     `e2e/__snapshots__/overlay-final.png`.

### 10. Golden-file / parity tests
- Projector reducer golden: `overlayProjector.golden.json` records expected end-state for
  `scriptedRun.json` — any behavioural drift shows as JSON diff.

### 11. Edge cases & negative tests
- Event for `nodeId` not in the current workflow graph — store records state anyway (engine may
  have included dynamic subflows); overlay ignores unknown nodes via React Flow's iteration over
  known nodes.
- Rapid attach/detach cycles — no leaked subscriptions (unit test asserts single active
  subscription).
- Out-of-order events (seq 5 arrives after seq 6): dropped with a warning because 6.2.2 dedup
  already catches this, but projector is also monotonic-FSM safe.
- `RunEvent` with unknown `kind` (forward-compat) — logged, ignored, does not break store.
- Re-attaching to the same runId without detach — store resets to prevent mixed history.

### 12. Observability hooks
- `window.__workflowUi.telemetry.debug()` devtool returns `{runId, lastSeq, nodeStates,
  edgeStates}` snapshot.
- Playwright trace attached on failure.
- Perf mark `telemetry-flush` around rAF commit so DevTools Performance panel shows batching.

### 13. Risks & mitigations
- **R6.3.1-A — memory blow-up on long runs**: store only tracks *latest* state per node, not
  history — O(nodes), not O(events).
- **R6.3.1-B — React Flow re-renders on every event**: use Zustand shallow selectors + memo.
- **R6.3.1-C — rAF suspended when tab hidden**: acceptable — store still ingests, visual
  catches up on resume.

### 14. Evaluator gap-analysis prompts
1. Is the projector a pure function (no side effects, easy to snapshot-test)?
2. Does the FSM guard prevent `Completed → Running` transitions?
3. Does the burst test truly verify frame-batching (not just final count)?
4. Is the edge colour table covering all 5 `MessageType` values?
5. Is `synthetic` flag routed to a distinct CSS class for debug visibility?
6. Does the detach path cleanly unsubscribe?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, burst Playwright spec green, golden projector file stable.

---

## Step 6.3.2 — Event-log side panel with filters (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Add a virtualised event-log side panel that renders the `RunEvent` feed in reverse-chronological
order with three live filters: **kind** (multiselect of 11 `RunEventKind` values), **nodeId**
(text / autocomplete from current workflow nodes), and **MessageType** (multiselect of 5 values;
only meaningful for `EdgeActivated` rows). Filter changes must apply within 50 ms on a 10k-event
log — achieved by pre-computed indexes in the store rather than per-row filter functions.

### 4. Depends on
- Step 6.3.1

### 5. Inputs
- `telemetryStore` from 6.3.1 extended with a capped `eventsLog: RunEvent[]` (10k cap, FIFO drop).

### 6. Deliverables
- `EventLogPanel.tsx` — React Flow-adjacent side panel using `react-window` for virtualisation.
- `EventRow.tsx` — single-row component with kind chip, timestamp, nodeId, and a JSON-peek
  expander.
- `RunFilterBar.tsx` — three filter controls; state stored in Zustand (`filterKinds: Set<Kind>`,
  `filterNodeId: string | null`, `filterMessageTypes: Set<MessageType>`).
- Store extension: `filteredEvents` derived via selector using pre-built
  `eventsByKind: Record<Kind, number[]>` and `eventsByNodeId: Record<string, number[]>` index
  arrays (indices into `eventsLog`).
- Vitest for filter logic; Playwright for combo filters.

### 7. Acceptance criteria
- [ ] Filter updates within 50 ms (assert via `performance.now()` in test) on 10k-row log.
- [ ] Kind filter excludes unchecked kinds even when other filters are empty.
- [ ] NodeId filter matches exactly (no substring) — because node ids are engine-provided.
- [ ] MessageType filter applies only to `EdgeActivated` rows; non-edge rows ALWAYS visible
      regardless of MessageType selection.
- [ ] Cleared filters restore full log immediately.
- [ ] Virtualised list renders no more than `visibleRows + overscan` DOM nodes at any time.
- [ ] Selecting an event in the panel highlights its source node in the canvas (scroll-into-view).

### 8. Unit tests (Vitest)
1. `filter_kinds_includesOnlySelectedKinds`.
2. `filter_nodeId_matchesExactly`.
3. `filter_messageType_appliesOnlyToEdgeActivated`.
4. `filter_combined_andSemantics`.
5. `filter_cleared_restoresFullLog`.
6. `index_updatedOnIngest`.
7. `index_evictedOnLogCapOverflow`.
8. `filter_latency_under50ms_10kEvents` (perf).

Three AAA sketches:

```ts
// #3 filter_messageType_appliesOnlyToEdgeActivated
const log = [
  nodeCompleted('n1'),   // kind=NodeCompleted, no MessageType
  edgeActivated('n1','n2','Complete'),
  edgeActivated('n1','n3','Fail'),
];
store.setFilterMessageTypes(new Set(['Complete']));
// Act
const visible = store.getFiltered();
// Assert
// nodeCompleted row is visible (not an edge event)
// Complete edge visible; Fail edge hidden
expect(visible.map(e => e.kind)).toEqual(['NodeCompleted','EdgeActivated']);

// #6 index_updatedOnIngest
const s = store.getState();
s.ingest(nodeStarted('n1'));
s.ingest(nodeCompleted('n1'));
// Assert
expect(s.eventsByKind['NodeStarted']).toHaveLength(1);
expect(s.eventsByNodeId['n1']).toHaveLength(2);

// #8 filter_latency_under50ms_10kEvents
for (let i=0;i<10000;i++) s.ingest(synthEvent(i));
const t0 = performance.now();
s.setFilterKinds(new Set(['NodeCompleted']));
const filtered = s.getFiltered();
const dt = performance.now() - t0;
// Assert
expect(dt).toBeLessThan(50);
expect(filtered.length).toBeGreaterThan(0);
```

### 9. Integration tests (Playwright)
- `live-monitor-filters.spec.ts`: ingest scripted run, toggle filter combinations, assert DOM
  row counts and that "jump-to-node" scroll behaviour activates the right React Flow viewport.

### 10. Golden-file / parity tests
- Not required (UI-only behavioural).

### 11. Edge cases & negative tests
- Log at exact cap (10k) — new ingest evicts oldest, indexes rebuild partial.
- Filter with empty selected kinds set: treat as "show all" (classic UX convention).
- NodeId input with whitespace: trimmed before match.
- Very long JSON payload in `EventRow` peek: truncated at 1 KB with "show more" expander.
- Attempting to jump-to-node for a `nodeId` not in the current graph: no-op, toast warning.

### 12. Observability hooks
- `window.__workflowUi.eventLog.stats()` returns `{ logSize, filteredSize, indexSizes }`.
- Trace mark `event-log-filter` around filter selector.

### 13. Risks & mitigations
- **R6.3.2-A — filter recomputation stutter**: pre-indexed arrays and Set intersections keep
  filter work O(smallest index), not O(log).
- **R6.3.2-B — runaway log memory**: 10k cap with explicit eviction.

### 14. Evaluator gap-analysis prompts
1. Does MessageType filter correctly special-case edge vs non-edge rows?
2. Is virtualisation actually enabled (DOM count bounded)?
3. Is the 50 ms latency proven by measurement, not just claimed?
4. Are filters additive (AND semantics)?
5. Is the log cap enforced on the store not just the view?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, Playwright filter combos green, perf test < 50 ms.

---

## Step 6.3.3 — Run-attach picker UI (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Add a `RunAttachPicker` dialog listing active + recently-completed runs (last 24 h) obtained from
`GET /api/runs`. Selecting a run calls `telemetryStore.attach(runId)` which in turn invokes
`TelemetryClient.attach(runId)` and (via 6.2.3) triggers replay. The picker shows runId short
form, workflow name, status, start time, node progress summary (derived from the latest
`NodeProgressEvent` fields — `ProgressPercent`, `Status` — per `src/ExecutionEngine/Events/NodeProgressEvent.cs:17-27`, if a snapshot is
available), and a search box.

### 4. Depends on
- Step 6.3.1

### 5. Inputs
- Host-side `RunsController` (new) exposing `GET /api/runs` → `RunSummaryDto[]` sourced from the
  run-history buffer (active runs) + on-disk JSONL index (completed runs; implemented in Phase
  7.2.3 — in this step the endpoint returns only active runs).
- `useLiveRun()` hook coordinating picker → store → client.

### 6. Deliverables
- `RunsController.cs`:
  ```csharp
  [ApiController, Route("api/runs")]
  public class RunsController : ControllerBase {
      [HttpGet] public ActionResult<IReadOnlyList<RunSummaryDto>> List() => ...;
  }
  public record RunSummaryDto(Guid RunId, string WorkflowId, string WorkflowName,
      WorkflowExecutionStatus Status, DateTime StartedUtc, int NodesCompleted, int TotalNodes);
  ```
- `runsApi.ts` — thin `fetch` wrapper with AbortController.
- `RunAttachPicker.tsx` — modal list + search + attach button.
- `useLiveRun.ts` — wires picker choice through store + client; handles detach on unmount.
- Unit tests for the picker (Vitest + React Testing Library).
- Playwright `live-monitor-attach.spec.ts`.

### 7. Acceptance criteria
- [ ] `GET /api/runs` returns 200 with array (possibly empty).
- [ ] Unauthenticated request returns 401 (when 7.1 lands; in this step asserted via
      `[Authorize]` attribute presence).
- [ ] Picker refreshes every 5 s while open (polling); stops polling on close.
- [ ] Selecting a run attaches and causes the canvas overlay to begin updating within 500 ms.
- [ ] Closing the picker without selection leaves current attachment untouched.
- [ ] Switching from run A to run B cleanly detaches A then attaches B (no event bleed).
- [ ] Keyboard navigation (↑/↓/Enter) works for accessibility.

### 8. Unit tests (Vitest + RTL)
1. `picker_rendersList_whenApiReturnsRuns`.
2. `picker_rendersEmptyState_whenApiReturnsEmpty`.
3. `picker_searchBox_filtersListByWorkflowName`.
4. `picker_select_callsAttach`.
5. `picker_polling_startsOnOpen_stopsOnClose`.
6. `picker_401_showsAuthError`.
7. `picker_network_error_showsRetryButton`.
8. `picker_keyboardArrows_moveSelection`.
9. `useLiveRun_switching_detachesPrev_attachesNext`.

Three AAA sketches:

```ts
// #4 picker_select_callsAttach
const attach = vi.fn();
render(<RunAttachPicker attach={attach} runs={[summary('r-1','wf')]} />);
fireEvent.click(screen.getByText(/wf/));
fireEvent.click(screen.getByRole('button', { name: /attach/i }));
expect(attach).toHaveBeenCalledWith('r-1');

// #5 picker_polling_startsOnOpen_stopsOnClose
const fetchSpy = vi.spyOn(global, 'fetch').mockResolvedValue(jsonOk([]));
const { rerender } = render(<RunAttachPicker open={true} attach={vi.fn()} />);
vi.advanceTimersByTime(5000);
expect(fetchSpy).toHaveBeenCalledTimes(2);   // initial + 1 poll
rerender(<RunAttachPicker open={false} attach={vi.fn()} />);
vi.advanceTimersByTime(10000);
expect(fetchSpy).toHaveBeenCalledTimes(2);   // no new calls after close

// #9 useLiveRun_switching_detachesPrev_attachesNext
const { result } = renderHook(() => useLiveRun(client, store));
await act(() => result.current.select('r-1'));
await act(() => result.current.select('r-2'));
expect(client.detach).toHaveBeenCalledWith('r-1');
expect(client.attach).toHaveBeenCalledWith('r-2');
expect(store.getState().runId).toBe('r-2');
```

### 9. Integration tests (Playwright)
- `live-monitor-attach.spec.ts`:
  1. Seed host with two scripted runs.
  2. Open picker, assert 2 rows.
  3. Select first run, assert canvas overlay begins updating.
  4. Reopen picker, select second run, assert overlay resets and new run streams.

### 10. Golden-file / parity tests
- `RunSummaryDto.json` golden shape. `Status` enum values match
  `WorkflowExecutionStatus` 6-member set (incl. `Paused`) — enforced via server-side
  `EnumMemberJsonConverter` reflected against the engine enum.

### 11. Edge cases & negative tests
- API returns 500 — picker shows retry button.
- Very long workflow name truncated with tooltip.
- Two pickers rendered simultaneously (misuse) — only one polls at a time via ref-counted hook.
- Selecting a run that was evicted between list fetch and attach (race) — server returns 404 on
  `Replay`; picker surfaces "run no longer available" error.
- Clock skew between server `StartedUtc` and browser time — UI shows relative "started 2m ago"
  using server clock to avoid negative durations.

### 12. Observability hooks
- `GET /api/runs` logs `{ActiveRunsCount}` structured field.
- UI emits `ui.event.attach_selected` analytics event with `runId` (hashed).
- Polling loop uses `AbortController` to cleanly cancel on unmount.

### 13. Risks & mitigations
- **R6.3.3-A — thundering herd on polling**: jitter the 5 s interval ± 500 ms.
- **R6.3.3-B — selected run disappears before attach**: surface as error + refresh list.
- **R6.3.3-C — auth not yet implemented**: `[Authorize]` attribute present; test uses bypass
  (Phase 7.1 turns on real auth).

### 14. Evaluator gap-analysis prompts
1. Is the list endpoint actually backed by `RunHistoryBuffer` (Stage 6.2.3)?
2. Is the `Status` field wire-value matching `WorkflowExecutionStatus` exactly (string enum)?
3. Does `useLiveRun` cleanly detach on previous selection?
4. Does the picker handle 401 when Phase 7.1 lands?
5. Is polling cancelled on unmount?
6. Is keyboard navigation WCAG-compliant?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, Playwright attach spec green, `RunSummaryDto` parity test green.
