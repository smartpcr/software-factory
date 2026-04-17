# Stage 6.1 — Engine event bridge

**Phase:** §12 Phase 6 — SignalR Telemetry & Live Monitor
**Stage status:** [ ]
**Total effort:** 7 d
**Step count:** 3

## Steps

- [ ] **Step 1**: Native event adapter (2 d)
- [ ] **Step 2**: Synthetic `EdgeActivated` derivation (3 d)
- [ ] **Step 3**: Synthetic `NodeSkipped` derivation (2 d)

---
## Context

The ExecutionEngine publishes a fixed set of native CLR events (`WorkflowEvent` subclasses and
`NodeEvent` subclasses) through its internal event source (`src/ExecutionEngine/Events/*.cs`). The
Designer UI never subscribes to those CLR types directly; instead the host process runs an
**`EngineEventBridge`** that adapts each engine event into a transport-neutral `RunEvent` /
`TelemetryEvent` DTO shape and hands it to the SignalR hub (Stage 6.2). This stage is the single
point where engine truth is translated into UI truth — every downstream surface (live monitor,
run history, audit log) is keyed off the DTOs emitted here.

The bridge is strict about three fidelity rules from the master plan (§0.2.1, §2.8, §12):
(a) only the nine native events actually declared under `src/ExecutionEngine/Events/` may be
handled — no fabricated `WorkflowPausedEvent`, `MessageSentEvent`, or `NodeSkippedEvent` type;
(b) `NodeSkipped` and `EdgeActivated` are **synthetic** and must be explicitly flagged as such in
both the DTO (`Synthetic: true`) and log enrichers; (c) `NodeProgressEvent` carries the engine's
own progress shape (`ProgressPercent: int`, `Status: string`) which the bridge surfaces as a
well-typed `progress: { percent: number, status: string }` payload, NOT the workflow-level
`ProgressUpdate` aggregate which is a separate snapshot type.

Ground truth for this stage:
- `src/ExecutionEngine/Events/WorkflowEvent.cs` (base, lines 13–29) supplies
  `WorkflowInstanceId`, `Timestamp`, `Metadata`.
- `src/ExecutionEngine/Events/NodeEvent.cs` (base, lines 13–24) adds `NodeId`, `NodeName`.
- `src/ExecutionEngine/Events/NodeProgressEvent.cs` (lines 12–28) — `NodeInstanceId`,
  `ProgressPercent`, `Status`.
- `src/ExecutionEngine/Events/NodeCompletedEvent.cs` (lines 12–23) — `NodeInstanceId`, `Duration`.
  (Note: no `Status` discriminator — the bridge derives `Skipped` from correlated branch data.)
- `src/ExecutionEngine/Enums/MessageType.cs` — `Complete, Fail, Progress, Next, Cancel`.
- `src/ExecutionEngine/Enums/NodeExecutionStatus.cs` — 6 members including `Skipped` (node-level).
- `src/ExecutionEngine/Enums/WorkflowExecutionStatus.cs` — 6 members including `Paused`
  (workflow-level only; NOT an event).

## Prerequisites
- §0.2 engine-parity tests green (§0.2.1, §0.2.2, §0.2.3).
- Phase 1 host skeleton complete (`src/host/WorkflowUi.Host` exists with DI container).
- Phase 5.1 mapper layer complete (Step 5.1.3): `IWorkflowMapper` is available so the bridge can
  correlate `NodeId` strings against the last-loaded workflow's `NodeConnection` graph.
- External deps: **SignalR 8.0** (`Microsoft.AspNetCore.SignalR` 8.0.x), **Serilog 4.x**
  (`Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Formatting.Compact`), **System.Reactive
  6.0** (engine already exposes `IObservable<WorkflowEvent>`). OpenTelemetry 1.9 for span export.
- Dev container includes `.NET 8 SDK` and a fixture workflow corpus under
  `src/WorkflowUi.ContractTests/Fixtures/` (established in §0.2.3).

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/host/WorkflowUi.Host/Telemetry/EngineEventBridge.cs` (new)
- `src/host/WorkflowUi.Host/Telemetry/IRunEventSink.cs` (new)
- `src/host/WorkflowUi.Host/Telemetry/RunEvent.cs` (new)
- `src/host/WorkflowUi.Host/Telemetry/RunEventKind.cs` (new — enum covering 9 native + 2 synthetic)
- `src/host/WorkflowUi.Host/Telemetry/EdgeActivationDeriver.cs` (new, Step 6.1.2)
- `src/host/WorkflowUi.Host/Telemetry/SkipDeriver.cs` (new, Step 6.1.3)
- `src/host/WorkflowUi.Host/Telemetry/SequenceAllocator.cs` (new — monotonic `(runId, seq)`)
- `src/host/WorkflowUi.Host/Telemetry/BridgeLoggingExtensions.cs` (new — Serilog enricher)
- `src/host/WorkflowUi.Host/DependencyInjection/TelemetryServiceCollectionExtensions.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Telemetry/EngineEventBridgeTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Telemetry/EdgeActivationDeriverTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Telemetry/SkipDeriverTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Telemetry/UnknownEventNegativeTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Telemetry/BridgeRoundTripTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Fixtures/ifelse-true-branch.yaml` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Fixtures/switch-three-cases.yaml` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Fixtures/fanout-multiedge.yaml` (new)
- `src/host/WorkflowUi.Host.ContractTests/EventShapeParityTests.cs` (new — golden parity matrix)
- `src/host/WorkflowUi.Host.ContractTests/Fixtures/RunEvent-shape.json` (new — golden file)

## Shared fixtures / fakes
- **`FakeEngineEventSource`** — an `IObservable<WorkflowEvent>` test double exposing
  `Emit(WorkflowEvent e)` used by unit tests to inject each native event class deterministically.
- **`InMemoryRunEventSink`** — `IRunEventSink` implementation that appends to a
  `ConcurrentQueue<RunEvent>`; used to assert mapped output without spinning up SignalR.
- **Seed workflows**: `ifelse-true-branch.yaml` (one IfElse node, true edge), `switch-three-cases.yaml`
  (Switch with `cases: { a: n1, b: n2, c: n3 }`), `fanout-multiedge.yaml` (one Noop with three
  outgoing connections differing by `TriggerMessageType: Complete | Fail | Next`).
- **Golden file `RunEvent-shape.json`** — canonical JSON rendering of each of the 11 event kinds
  (9 native + 2 synthetic), checked against parity tests byte-for-byte after normalisation.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="6.1"`, `phaseId="6"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-6.1.plan-read`, `workflow-ui.stage-6.1.apply`, `workflow-ui.stage-6.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_6_1_started_total`, counter `workflow_ui_stage_6_1_failed_total`, histogram `workflow_ui_stage_6_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 6.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage61` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Attach the live monitor to a busy run, disconnect the network, reconnect, and confirm event ordering by `(runId, seq)`.
- Run with a screen reader/reduced-motion setting and verify updates remain understandable.
- Leave the monitor open for 10 minutes and confirm no duplicate subscriptions or memory blow-up.

---

### Step 1 — Native event adapter (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Build `EngineEventBridge`, an adapter that subscribes to the engine's `IObservable<WorkflowEvent>`
stream and emits a `RunEvent` DTO per incoming event. The bridge must handle **exactly the nine
native event classes** present under `src/ExecutionEngine/Events/` (`WorkflowStartedEvent`,
`WorkflowCompletedEvent`, `WorkflowFailedEvent`, `WorkflowCancelledEvent`, `NodeStartedEvent`,
`NodeCompletedEvent`, `NodeFailedEvent`, `NodeCancelledEvent`, `NodeProgressEvent`) and refuse to
compile against any fabricated type. Each `RunEvent` carries `{ runId, seq, timestampUtc, kind,
nodeId?, payload, synthetic=false }`. A monotonically increasing `seq` is allocated per `runId`
via `SequenceAllocator`.

### 4. Depends on
- Step 5.1.3 (mapper)
- Step 1.1.3 (host DI)

### 5. Inputs
- `src/ExecutionEngine/Contexts/WorkflowExecutionContext.cs:118-125` — authoritative `Events` / `Progress` observables exposed by the engine execution context; the bridge subscribes to these rather than a nonexistent `ExecutionEngine.Events.IEventSource`.
- Workflow snapshot from mapper (only used by Steps 6.1.2/6.1.3; present here only for runId
  correlation).
- Contract DTO definitions in this stage's `RunEvent.cs`.

### 6. Deliverables
- `EngineEventBridge.cs` — implements `IHostedService`, subscribes on start, disposes on stop.
- `IRunEventSink.cs` — sink abstraction (`Task PublishAsync(RunEvent, CancellationToken)`).
- `RunEvent.cs` — immutable record with JSON-serialisable shape.
- `RunEventKind.cs` — enum with 11 values, `RunStarted, RunCompleted, RunFailed, RunCancelled,
  NodeStarted, NodeCompleted, NodeFailed, NodeCancelled, NodeProgress, NodeSkipped, EdgeActivated`.
- `SequenceAllocator.cs` — `long Next(Guid runId)` using per-run `Interlocked.Increment`.
- `TelemetryServiceCollectionExtensions.AddEngineEventBridge()` — registers hosted service + sink.
- Unit test file `EngineEventBridgeTests.cs` (≥ 9 cases).
- Negative test file `UnknownEventNegativeTests.cs` (≥ 2 cases).
- Contract parity fixture `RunEvent-shape.json`.

### 7. Acceptance criteria
- [ ] All nine native event classes map to a distinct `RunEventKind` with `synthetic=false`.
- [ ] `NodeProgressEvent` payload renders exactly `{ "progressPercent": <int>, "status": "<s>",
      "nodeInstanceId": "<guid>" }`; field names match engine PascalCase via camelCase
      naming policy applied once at JSON boundary.
- [ ] Bridge ignores (does NOT throw) event types it does not know, and emits a single Serilog
      `Warning` with property `EventType = typeof(event).FullName`.
- [ ] `SequenceAllocator.Next(runId)` is strictly monotonic under 10-thread stress (100k calls).
- [ ] Negative compile-time test: `typeof(WorkflowEvent).Assembly.GetType("ExecutionEngine.Events.
      WorkflowPausedEvent")` returns `null` (asserted in `UnknownEventNegativeTests`).
- [ ] Bridge registers as `IHostedService` and is started/stopped by generic host.
- [ ] 100% line coverage on `EngineEventBridge`, `SequenceAllocator`, `RunEvent`.

### 8. Unit tests (xUnit + FluentAssertions)
1. `WorkflowStartedEvent_MapsTo_RunStarted` — emit event, assert `kind == RunStarted`, `runId`,
   `workflowId`, `workflowName`, `totalNodes` preserved.
2. `WorkflowCompletedEvent_MapsTo_RunCompleted`.
3. `WorkflowFailedEvent_MapsTo_RunFailed` — exception message propagates.
4. `WorkflowCancelledEvent_MapsTo_RunCancelled`.
5. `NodeStartedEvent_MapsTo_NodeStarted` — `nodeId`, `nodeInstanceId` preserved.
6. `NodeCompletedEvent_MapsTo_NodeCompleted` — `duration` rendered as ISO-8601 `TimeSpan`.
7. `NodeFailedEvent_MapsTo_NodeFailed` — exception class name in payload.
8. `NodeCancelledEvent_MapsTo_NodeCancelled`.
9. `NodeProgressEvent_MapsTo_NodeProgress` — `progressPercent`, `status` echoed.
10. `SequenceAllocator_IsMonotonic_UnderStress` — 10 threads × 10k increments.
11. `UnknownEventType_LogsWarning_AndIsIgnored` — emit a locally-defined `DummyEvent : WorkflowEvent`
    subclass; assert warning logged, no sink publish.
12. `NullEvent_IsDropped_WithDebugLog`.

Three detailed AAA sketches:

```csharp
// #1 WorkflowStartedEvent_MapsTo_RunStarted
// Arrange
var sink = new InMemoryRunEventSink();
var src  = new FakeEngineEventSource();
var bridge = new EngineEventBridge(src, sink, new SequenceAllocator(),
                NullLogger<EngineEventBridge>.Instance);
await bridge.StartAsync(CancellationToken.None);
var wid = Guid.NewGuid();
// Act
src.Emit(new WorkflowStartedEvent {
    WorkflowInstanceId = wid, WorkflowId = "wf-1",
    WorkflowName = "Sample", TotalNodes = 4 });
// Assert
sink.Events.Should().ContainSingle()
    .Which.Should().BeEquivalentTo(new {
        RunId = wid, Kind = RunEventKind.RunStarted, Synthetic = false,
        Payload = new { WorkflowId = "wf-1", WorkflowName = "Sample", TotalNodes = 4 }
    }, o => o.ExcludingMissingMembers());

// #9 NodeProgressEvent_MapsTo_NodeProgress
// Arrange
var ev = new NodeProgressEvent {
    WorkflowInstanceId = wid, NodeId = "n1", NodeName = "Work",
    NodeInstanceId = Guid.NewGuid(), ProgressPercent = 42,
    Status = "halfway" };
// Act
src.Emit(ev);
// Assert
var re = sink.Events.Single(e => e.Kind == RunEventKind.NodeProgress);
re.Payload.Should().BeEquivalentTo(new {
    ProgressPercent = 42, Status = "halfway",
    NodeInstanceId = ev.NodeInstanceId });
re.Synthetic.Should().BeFalse();

// #11 UnknownEventType_LogsWarning_AndIsIgnored
// Arrange
var logger = new ListLogger<EngineEventBridge>();
var bridge = new EngineEventBridge(src, sink, new SequenceAllocator(), logger);
await bridge.StartAsync(default);
// Act
src.Emit(new DummyEvent { WorkflowInstanceId = wid });   // local subclass
// Assert
sink.Events.Should().BeEmpty();
logger.Entries.Should().ContainSingle(e =>
    e.Level == LogLevel.Warning &&
    e.Properties["EventType"].ToString()!.EndsWith("DummyEvent"));
```

### 9. Integration tests
- `BridgeRoundTripTests.NativeEvents_From_FakeEngine_EndToEnd` — use `WebApplicationFactory` to
  bring up host DI, inject `FakeEngineEventSource`, publish all 9 native events in sequence,
  assert `InMemoryRunEventSink` received 9 DTOs with strictly increasing `seq` and matching
  `runId`.

### 10. Golden-file / parity tests
- `EventShapeParityTests.AllNativeKinds_MatchGoldenJson` — for each of the 9 native events,
  build a canonical instance, map via bridge, serialise to JSON with shared options, compare to
  `Fixtures/RunEvent-shape.json` key-by-key (ignoring timestamps). Golden file is the authoritative
  mapping table:

  | Native class            | RunEventKind     | Payload keys                                    |
  |-------------------------|------------------|-------------------------------------------------|
  | `WorkflowStartedEvent`  | `RunStarted`     | `workflowId`, `workflowName`, `totalNodes`      |
  | `WorkflowCompletedEvent`| `RunCompleted`   | (base only)                                     |
  | `WorkflowFailedEvent`   | `RunFailed`      | `errorMessage`, `exceptionType`                 |
  | `WorkflowCancelledEvent`| `RunCancelled`   | `reason`                                        |
  | `NodeStartedEvent`      | `NodeStarted`    | `nodeInstanceId`                                |
  | `NodeCompletedEvent`    | `NodeCompleted`  | `nodeInstanceId`, `duration`                    |
  | `NodeFailedEvent`       | `NodeFailed`     | `nodeInstanceId`, `errorMessage`, `exceptionType`|
  | `NodeCancelledEvent`    | `NodeCancelled`  | `nodeInstanceId`                                |
  | `NodeProgressEvent`     | `NodeProgress`   | `nodeInstanceId`, `progressPercent`, `status`   |

### 11. Edge cases & negative tests
- Bridge receives `null` via `OnNext(null)` — dropped, debug log only.
- Bridge receives an event with `WorkflowInstanceId == Guid.Empty` — dropped with warning
  (runId of empty is not routable downstream).
- Subscribe → dispose → emit: post-dispose events must NOT reach sink.
- Two overlapping runs on the same bridge instance: `seq` sequences are independent per `runId`.
- Engine emits 10k events/second burst — bridge does not drop; backpressure handled by
  `Channel<RunEvent>` with unbounded writer for v1 (cap enforced in Step 6.2.3 ring buffer).
- Reflection check: `typeof(WorkflowEvent).Assembly.DefinedTypes.Where(t =>
  typeof(WorkflowEvent).IsAssignableFrom(t)).Count()` equals exactly 9 (base + 8 terminal +
  `NodeEvent` abstract = 10 types total, 9 concrete non-abstract).

### 12. Observability hooks
- Serilog enricher `BridgeLoggingExtensions.EnrichFromRunEvent` adds `WorkflowId`, `RunId`,
  `NodeId`, `EventKind` as structured log properties.
- OpenTelemetry span `workflow.event.translate` per emit, attributes: `event.kind`,
  `event.synthetic`.
- Metric counter `workflow_ui_bridge_events_total{kind=...}` (Prometheus / OTel).
- Metric counter `workflow_ui_bridge_unknown_events_total` for negative path.

### 13. Risks & mitigations
- **R6.1.1-A — engine adds a new event type**: parity test under §0.2.1 fails red, blocking merge;
  bridge logs warning until enum extended.
- **R6.1.1-B — high-throughput burst starves SignalR**: move sink behind a bounded
  `Channel<RunEvent>` in 6.2.3; here we establish the contract but keep unbounded.
- **R6.1.1-C — dual subscription leak**: bridge is hosted-service scoped singleton; start/stop
  tested under DI lifecycle.

### 14. Evaluator gap-analysis prompts
1. Does the bridge reference any event type NOT in `src/ExecutionEngine/Events/*.cs`?
2. Is every enum value of `RunEventKind` covered by at least one unit test?
3. Does the `NodeProgress` payload expose the engine's actual field names
   (`ProgressPercent`, `Status`) rather than plan-v1.0 fabrications (`percent`, `message`)?
4. Is `SequenceAllocator` thread-safe, and is thread safety asserted under ≥ 10-thread stress?
5. Does the unknown-event path log AND drop (vs. log OR throw)?
6. Are disposal semantics asserted (post-dispose emits are ignored)?
7. Does the golden JSON file include all 9 native kinds?
8. Is `synthetic: false` explicitly serialised for native events (not omitted)?

### 15. Scoring rubric (weights 25/25/20/10/10/10)
| Dimension                            | Weight |
|--------------------------------------|-------:|
| Deliverable completeness             |     25 |
| Test coverage & assertion quality    |     25 |
| Contract/parity fidelity             |     20 |
| Edge-case handling                   |     10 |
| Observability                        |     10 |
| Clarity & traceability               |     10 |

### 16. Definition of done
Evaluator score ≥ 90, zero CRITICAL/HIGH findings, all acceptance criteria checked, parity golden
file committed, CI coverage report shows 100% line coverage on new `EngineEventBridge` code.

---

### Step 2 — Synthetic `EdgeActivated` derivation (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
Extend the bridge with `EdgeActivationDeriver`, a side-effect component that — on every terminal
`NodeCompletedEvent` / `NodeFailedEvent` / `NodeCancelledEvent` and every `NodeProgressEvent`
representing loop `Next` output — emits zero or more **synthetic** `EdgeActivated` `RunEvent`s,
one per outgoing `NodeConnection` whose `TriggerMessageType` matches the terminal event's
`MessageType` projection. `Progress` (non-terminal heartbeat) NEVER triggers `EdgeActivated`;
only `Next` (loop-iteration heartbeat) does, per §2.6 port matrix. Each synthetic event carries
`synthetic: true`, `edge: { sourceNodeId, targetNodeId, triggerMessageType, condition?, priority }`.

### 4. Depends on
- Step 6.1.1

### 5. Inputs
- Last-loaded `WorkflowDefinition` snapshot (connections graph, keyed by `sourceNodeId`) provided
  by `IWorkflowSnapshotProvider` (injected; Phase 5 surface).
- Native `NodeCompleted/Failed/Cancelled/Progress` events from Step 6.1.1.

### 6. Deliverables
- `EdgeActivationDeriver.cs` — stateless component with
  `IEnumerable<RunEvent> Derive(RunEvent terminal, WorkflowDefinition graph)`.
- Wired into `EngineEventBridge.Handle` so every terminal emit is followed by synthetic edges.
- Event kind → message type projection table:
  - `NodeCompleted` → `MessageType.Complete`
  - `NodeFailed`    → `MessageType.Fail`
  - `NodeCancelled` → `MessageType.Cancel`
  - `NodeProgress`  → `MessageType.Next` IFF engine marks the progress as loop-next (detected via
    `Metadata["messageType"] == "Next"`, per engine loop-node convention documented in
    `src/ExecutionEngine/Enums/MessageType.cs` lines 30–36).
- `EdgeActivationDeriverTests.cs` — matrix tests (≥ 10 cases).
- Fixture `fanout-multiedge.yaml`.

### 7. Acceptance criteria
- [ ] For a node with N outgoing edges, exactly `count(edges where trigger == projectedType)`
      synthetic `EdgeActivated` events are emitted per terminal.
- [ ] `Progress` (non-terminal) produces zero synthetic edges.
- [ ] `Next` (loop iteration) produces synthetic edges only for edges with
      `TriggerMessageType == Next`.
- [ ] Synthetic events carry `synthetic: true` and unique `seq` continuing the run's sequence.
- [ ] Edge `IsEnabled == false` connections are filtered out.
- [ ] Edges with `Condition` present are still emitted (UI evaluates conditions later); the deriver
      does NOT evaluate conditions.
- [ ] Ordering stable by `Priority` descending then `TargetNodeId` ascending.

### 8. Unit tests
1. `Complete_SingleMatchingEdge_EmitsOne`.
2. `Complete_NoMatchingEdge_EmitsZero`.
3. `Complete_TwoMatchingEdges_EmitsTwo_InPriorityOrder`.
4. `Fail_MatchesOnlyFailTriggers`.
5. `Cancel_MatchesOnlyCancelTriggers`.
6. `Progress_NonTerminal_EmitsZero`.
7. `Next_FromLoopNode_MatchesNextTriggers_OnlyNext`.
8. `DisabledEdge_IsSkipped`.
9. `EdgeWithCondition_StillEmitted_ConditionEchoedInPayload`.
10. `UnknownSourceNode_EmitsZero_AndLogsWarning`.
11. `SeqContinuesFromTerminalSeq_Incremented`.

Three AAA sketches:

```csharp
// #3 Complete_TwoMatchingEdges_EmitsTwo_InPriorityOrder
var graph = WorkflowBuilder.WithConnections(
    new NodeConnection { SourceNodeId="a", TargetNodeId="b",
        TriggerMessageType=MessageType.Complete, Priority=10, IsEnabled=true },
    new NodeConnection { SourceNodeId="a", TargetNodeId="c",
        TriggerMessageType=MessageType.Complete, Priority=5,  IsEnabled=true });
var deriver = new EdgeActivationDeriver();
var terminal = RunEventFactory.NodeCompleted("a", seq:10);
// Act
var edges = deriver.Derive(terminal, graph).ToList();
// Assert
edges.Should().HaveCount(2);
edges[0].Payload.As<EdgePayload>().TargetNodeId.Should().Be("b"); // pri 10 first
edges[1].Payload.As<EdgePayload>().TargetNodeId.Should().Be("c");
edges.Should().OnlyContain(e => e.Synthetic);

// #6 Progress_NonTerminal_EmitsZero
var terminal = RunEventFactory.NodeProgress("a", percent:50, status:"ok");
deriver.Derive(terminal, graph).Should().BeEmpty();

// #7 Next_FromLoopNode_MatchesNextTriggers_OnlyNext
var loopGraph = WorkflowBuilder.WithConnections(
    new NodeConnection { SourceNodeId="loop", TargetNodeId="item",
        TriggerMessageType=MessageType.Next, IsEnabled=true },
    new NodeConnection { SourceNodeId="loop", TargetNodeId="after",
        TriggerMessageType=MessageType.Complete, IsEnabled=true });
var terminal = RunEventFactory.NodeProgress("loop", percent:10, status:"iter",
    metadata: new() { ["messageType"]="Next" });
// Act
var edges = deriver.Derive(terminal, loopGraph).ToList();
// Assert
edges.Should().ContainSingle()
     .Which.Payload.As<EdgePayload>().TargetNodeId.Should().Be("item");
```

### 9. Integration tests
- `BridgeRoundTripTests.Fanout_MultiEdge_EmitsMatchingSubset` — load
  `fanout-multiedge.yaml` (edges with `Complete`, `Fail`, `Next`), run synthetic `NodeCompleted`,
  assert exactly 1 synthetic `EdgeActivated` (the Complete edge).

### 10. Golden-file / parity tests
- `EventShapeParityTests.EdgeActivated_Payload_Golden` — canonical payload shape:
  ```json
  { "sourceNodeId":"a", "targetNodeId":"b", "triggerMessageType":"Complete",
    "condition":null, "priority":10, "sourcePort":"out", "targetPort":"in" }
  ```

### 11. Edge cases & negative tests
- Node with zero outgoing connections → zero synthetic events.
- Terminal for a node whose id is not in the snapshot graph (stale load) → zero events, warning log.
- Snapshot graph is `null` (no workflow loaded) → deriver short-circuits, warning log at most once
  per minute (rate-limited).
- Cyclic graph: deriver does NOT recurse — emits only one hop per terminal.
- Duplicate edge definitions in YAML (same src/tgt/trigger): both emitted, UI must de-dupe.

### 12. Observability hooks
- Metric `workflow_ui_bridge_synthetic_edges_total{trigger=...}`.
- Span `workflow.event.edge_derive` with attributes `source.node.id`, `edges.emitted`.
- Log enricher adds `Synthetic=true` to every derived event's structured log.

### 13. Risks & mitigations
- **R6.1.2-A — snapshot staleness**: deriver takes snapshot per-event, not cached reference —
  prevents visibility of graph edits mid-run (workflow is immutable per run in engine).
- **R6.1.2-B — `Progress` mistaken for `Next`**: unit test #7 pins detection rule on `Metadata
  ["messageType"]` string.

### 14. Evaluator gap-analysis prompts
1. Does the deriver distinguish `Progress` (non-terminal) from `Next` (loop-terminal)?
2. Is `IsEnabled=false` filtering explicit and tested?
3. Are synthetic edges marked `synthetic: true` and excluded from the native parity golden?
4. Does ordering deterministically break ties on `TargetNodeId`?
5. Is the projection table `Completed→Complete, Failed→Fail, Cancelled→Cancel` codified in a
   single place (switch statement with exhaustive enum handling)?
6. Does the deriver handle snapshot-missing without crashing?
7. Is `Priority` actually read from the engine `NodeConnection` (§2.5) rather than invented?

### 15. Scoring rubric
25/25/20/10/10/10 as per stage header.

### 16. Definition of done
Evaluator ≥ 90, zero CRITICAL/HIGH, matrix test green, synthetic-flag assertions present.

---

### Step 3 — Synthetic `NodeSkipped` derivation (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Derive a synthetic `NodeSkipped` `RunEvent` for every node that the engine's branching semantics
implicitly skip: (a) `IfElse` nodes' non-chosen branch, (b) `Switch` cases whose key does not
match the runtime expression. The derivation fires within **50 ms** of the branch's chosen edge
being activated (so it always fires AFTER the `EdgeActivated` for the taken branch). The engine
does NOT emit a `NodeSkippedEvent` class (confirmed against `src/ExecutionEngine/Events/*.cs` — no
such file); this is why the event is SYNTHETIC and MUST carry `synthetic: true`.

### 4. Depends on
- Step 6.1.2

### 5. Inputs
- Workflow snapshot (same source as 6.1.2).
- A stream of `RunEvent` with `EdgeActivated` already flagged.

### 6. Deliverables
- `SkipDeriver.cs` — component with `IEnumerable<RunEvent> Derive(RunEvent taken,
  WorkflowDefinition graph)`; observes every `EdgeActivated` whose source is an `IfElse` or
  `Switch` node, enumerates sibling edges, and emits `NodeSkipped` for each sibling's target.
- Cap rule: at most one `NodeSkipped` per `(runId, nodeId)` — enforced with a
  `ConcurrentHashSet<(Guid,string)>` scoped to `runId`.
- `SkipDeriverTests.cs`.
- Fixtures `ifelse-true-branch.yaml`, `switch-three-cases.yaml`.

### 7. Acceptance criteria
- [ ] IfElse true-branch run emits exactly one `NodeSkipped` for the false-branch node.
- [ ] Switch with 3 cases and 1 matching emits 2 `NodeSkipped` events.
- [ ] Repeat IfElse evaluation under retry policy still emits at most one skip per (runId, nodeId).
- [ ] Synthetic flag `synthetic: true`; `payload.reason = "BranchNotTaken" | "SwitchCaseNoMatch"`.
- [ ] Skip event `timestampUtc` is within 50 ms of the triggering `EdgeActivated`.
- [ ] Only `IfElse` and `Switch` sources trigger skip derivation (Noop, ForEach siblings do NOT).
- [ ] When there are zero non-taken siblings (degenerate IfElse with only the taken edge defined),
      emits zero events.

### 8. Unit tests
1. `IfElse_TrueBranchTaken_SkipsFalseBranch`.
2. `IfElse_FalseBranchTaken_SkipsTrueBranch`.
3. `Switch_ThreeCases_TwoSkipped`.
4. `Switch_AllCasesMatch_Impossible_NoSkips` (defensive — engine disallows).
5. `Retry_SecondAttempt_NoDuplicateSkip` (cap rule).
6. `Noop_Source_EmitsNoSkips` (non-branching source).
7. `IfElse_MissingFalseEdge_EmitsZero`.
8. `CrossRun_SameNodeId_DoesNotDeDupe` (cap is per-run).
9. `TimestampWithin50ms_OfTriggeringEdge`.

Three AAA sketches:

```csharp
// #1 IfElse_TrueBranchTaken_SkipsFalseBranch
var graph = WorkflowBuilder.IfElse("cond", trueTarget:"t", falseTarget:"f");
var deriver = new SkipDeriver();
var edge = RunEventFactory.EdgeActivated("cond", "t", MessageType.Complete);
// Act
var skips = deriver.Derive(edge, graph).ToList();
// Assert
skips.Should().ContainSingle();
var payload = skips[0].Payload.As<SkipPayload>();
payload.NodeId.Should().Be("f");
payload.Reason.Should().Be("BranchNotTaken");
skips[0].Synthetic.Should().BeTrue();

// #3 Switch_ThreeCases_TwoSkipped
var graph = WorkflowBuilder.Switch("sw",
    cases: new() { ["a"]="n1", ["b"]="n2", ["c"]="n3" });
var edge = RunEventFactory.EdgeActivated("sw", "n2", MessageType.Complete);
// Act
var skips = deriver.Derive(edge, graph).ToList();
// Assert
skips.Select(s => s.Payload.As<SkipPayload>().NodeId)
     .Should().BeEquivalentTo(new[] { "n1", "n3" });

// #5 Retry_SecondAttempt_NoDuplicateSkip
var deriver = new SkipDeriver();
var edge1 = RunEventFactory.EdgeActivated("cond","t",runId:R, seq:3);
var edge2 = RunEventFactory.EdgeActivated("cond","t",runId:R, seq:9); // retry
// Act
var first  = deriver.Derive(edge1, graph).ToList();
var second = deriver.Derive(edge2, graph).ToList();
// Assert
first.Should().ContainSingle();
second.Should().BeEmpty();                // cap rule
```

### 9. Integration tests
- `BridgeRoundTripTests.IfElse_TrueBranch_EmitsSkipForFalseBranch` — load fixture,
  run synthetic IfElse completion, assert exactly one `NodeSkipped` reaches sink.
- `BridgeRoundTripTests.Switch_ThreeCases_EmitsTwoSkips`.

### 10. Golden-file / parity tests
- `EventShapeParityTests.NodeSkipped_Payload_Golden` —
  ```json
  { "nodeId":"f", "reason":"BranchNotTaken", "correlatedTakenEdge":
    { "sourceNodeId":"cond", "targetNodeId":"t" } }
  ```
- Confirms `synthetic: true` and enum value `RunEventKind.NodeSkipped`.

### 11. Edge cases & negative tests
- IfElse where `ThenTarget == ElseTarget` (degenerate) — zero skips.
- Switch with duplicate case targets — skips de-duped by `NodeId`.
- Dispose-during-derive: cap-set is cleared when run terminates (`RunCompleted` / `RunFailed`
  / `RunCancelled`) to prevent memory leak across workflow lifetimes.
- Over-emission guard: property-based test with 1000 random retries, asserts exactly one skip.
- Race condition on `runId` cap: 10-thread concurrent `Derive` calls same `(runId, skippedId)`
  yields exactly one emission.

### 12. Observability hooks
- Metric `workflow_ui_bridge_synthetic_skips_total{reason=...}`.
- Log line per skip with `Reason`, `CorrelatedEdge.SourceNodeId/TargetNodeId`.
- Span `workflow.event.skip_derive` with `cap.hit: bool`.

### 13. Risks & mitigations
- **R6.1.3-A — cap memory leak**: bind cap set to `runId`, evict on terminal run event.
- **R6.1.3-B — missed skip when branch node has no explicit else edge**: documented acceptable
  behaviour; skip only emitted for explicitly defined sibling targets.
- **R6.1.3-C — engine later adds `NodeSkippedEvent` class**: §0.2.1 parity test flags type,
  deriver can then be retired in favour of native mapping.

### 14. Evaluator gap-analysis prompts
1. Does the deriver run after `EdgeActivated` ordering (skip cannot precede taken edge)?
2. Is the cap rule keyed on `(runId, nodeId)` and per-run cleared on terminal?
3. Is this event explicitly marked `synthetic: true`?
4. Does the reason enum cover both `BranchNotTaken` and `SwitchCaseNoMatch`?
5. Does the deriver ignore non-branching source nodes?
6. Is 50 ms latency asserted in integration or only unit tests?
7. Does the golden file pin `correlatedTakenEdge` structure?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, zero CRITICAL/HIGH, IfElse + Switch fixtures pass, cap-rule property test green.
