# Stage 6.2 — Hub + Client

**Phase:** §12 Phase 6 — SignalR Telemetry & Live Monitor
**Stage status:** [ ]
**Total effort:** 7 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 6.1 produced a uniform `RunEvent` stream in-process. Stage 6.2 transports that stream over
**SignalR** (the pinned v1 transport; WebSocket/SSE fallback is noted in §1 of the master plan
but NOT implemented in v1). The stage delivers three things: a server-side `TelemetryHub` with
per-run groups, a TypeScript client wrapper with auto-reconnect and sequence-based resume, and a
ring buffer for late-joiners. Together these close the loop between engine events and a browser,
but they deliberately do not render anything — the live-monitor UI is Stage 6.3.

Ground truth: the hub transmits only the DTOs from Stage 6.1 (`RunEvent` / `RunEventKind`). It
does NOT re-project engine types, does NOT invent new kinds, and does NOT attempt to reverse the
synthetic flag. Sequence numbers allocated by `SequenceAllocator` (6.1.1) are the only resume
key; timestamps are informational. Per §0.1 the file budget is ≤ 20 per step; per §0.1 constraint
2, new code must hit 100% line coverage under coverlet.

Two non-obvious engine-parity choices drive this stage:
- Run identity is `Guid` (`WorkflowInstanceId`, from `WorkflowEvent.cs:18`), not a string — the
  hub must accept/return `Guid` on wire.
- `WorkflowExecutionStatus.Paused` (`src/ExecutionEngine/Enums/WorkflowExecutionStatus.cs:42`)
  is a *workflow-level* status only; it is NEVER an event, so the hub never emits a
  `WorkflowPaused` kind. Status derivation for late-joiners uses a snapshot query (Step 7.2.3),
  NOT a synthetic event here.

## Prerequisites
- Stage 6.1 complete (all three steps).
- Host project has ASP.NET Core 8 minimal API bootstrapped (Step 1.1.3).
- External deps: `Microsoft.AspNetCore.SignalR 8.0.x`, `@microsoft/signalr 8.0.x`,
  Vitest 1.x (UI unit), Playwright 1.45 (UI integration), `MessagePack` (optional binary protocol
  — v1 uses JSON).
- Serilog with request-scoped enricher to carry `ConnectionId` and `RunId` across hub invocations.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 6.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/host/WorkflowUi.Host/Hubs/TelemetryHub.cs` (new)
- `src/host/WorkflowUi.Host/Hubs/TelemetryHubSink.cs` (new — `IRunEventSink` adapter → hub)
- `src/host/WorkflowUi.Host/Hubs/RunGroupTracker.cs` (new — connection→runIds map)
- `src/host/WorkflowUi.Host/Hubs/RunHistoryBuffer.cs` (new, Step 6.2.3)
- `src/host/WorkflowUi.Host/Hubs/ITelemetryClient.cs` (new — strongly-typed client contract)
- `src/host/WorkflowUi.Host/DependencyInjection/HubServiceCollectionExtensions.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Hubs/TelemetryHubTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Hubs/RunGroupTrackerTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Hubs/RunHistoryBufferTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Hubs/TwoClientAttachTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Hubs/ReconnectResumeTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Hubs/LateJoinReplayTests.cs` (new)
- `src/ui/src/telemetry/TelemetryClient.ts` (new)
- `src/ui/src/telemetry/types.ts` (new — mirror of `RunEvent` DTO)
- `src/ui/src/telemetry/sequenceGapDetector.ts` (new)
- `src/ui/src/telemetry/__tests__/TelemetryClient.test.ts` (new)
- `src/ui/src/telemetry/__tests__/sequenceGapDetector.test.ts` (new)
- `src/ui/e2e/telemetry-reconnect.spec.ts` (new)

## Shared fixtures / fakes
- **`FakeRunEventSource`**: publishes scripted `RunEvent[]` to the sink at controlled cadence.
- **`TestHubClient`**: C# `HubConnectionBuilder` harness used in WebApplicationFactory tests.
- **Seed ring-buffer data**: JSONL corpus `Fixtures/run-1000-events.jsonl` (exactly 1000 events,
  mixed kinds including 2 synthetic skips and 3 synthetic edges) used for late-join tests.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="6.2"`, `phaseId="6"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-6.2.plan-read`, `workflow-ui.stage-6.2.apply`, `workflow-ui.stage-6.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_6_2_started_total`, counter `workflow_ui_stage_6_2_failed_total`, histogram `workflow_ui_stage_6_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 6.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage62` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Attach the live monitor to a busy run, disconnect the network, reconnect, and confirm event ordering by `(runId, seq)`.
- Run with a screen reader/reduced-motion setting and verify updates remain understandable.
- Leave the monitor open for 10 minutes and confirm no duplicate subscriptions or memory blow-up.

---

## Step 6.2.1 — SignalR hub with per-run groups (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Create `TelemetryHub` at route `/hubs/telemetry` with group semantics: clients invoke
`AttachRun(runId)` to join group `run:{runId}` and `DetachRun(runId)` to leave. The hub pushes
every `RunEvent` to all members of the matching group via strongly-typed client method
`RunEvent(RunEvent e)` defined in `ITelemetryClient`. The hub also tracks which connections
belong to which runs (`RunGroupTracker`) so disconnects clean up correctly.

### 4. Depends on
- Step 6.1.3

### 5. Inputs
- `IRunEventSink` hook from 6.1; the hub's `TelemetryHubSink` implements this interface and calls
  `IHubContext<TelemetryHub, ITelemetryClient>.Clients.Group(...).RunEvent(e)`.

### 6. Deliverables
- `TelemetryHub.cs` — extends `Hub<ITelemetryClient>`, methods:
  - `Task AttachRun(Guid runId)` → `Groups.AddToGroupAsync(ConnectionId, $"run:{runId}")`
  - `Task DetachRun(Guid runId)` → `Groups.RemoveFromGroupAsync`
  - `override OnDisconnectedAsync(Exception?)` → `RunGroupTracker.ClearForConnection(ConnectionId)`.
- `RunGroupTracker.cs` — `ConcurrentDictionary<string, HashSet<Guid>>` keyed by connection id;
  methods `Attach`, `Detach`, `ClearForConnection`, `RunsFor`.
- `ITelemetryClient` — `Task RunEvent(RunEvent e); Task RunSnapshot(RunSnapshotDto s);`.
- `TelemetryHubSink.PublishAsync(RunEvent e)` → `clients.Group($"run:{e.RunId}").RunEvent(e)`.
- `HubServiceCollectionExtensions.AddTelemetryHub()` — registers hub, sink, tracker; `MapHub`
  called in Program.cs.
- Unit tests + 2-client integration test.

### 7. Acceptance criteria
- [ ] Two concurrent clients attached to same runId receive identical streams in order.
- [ ] A client attached to runId A does NOT receive events for runId B.
- [ ] `DetachRun` removes the connection from the group within one event boundary.
- [ ] `OnDisconnectedAsync` clears all runId memberships for the connection.
- [ ] `AttachRun` with `Guid.Empty` returns HTTP 400 / hub exception (defensive).
- [ ] Hub enforces auth policy `RequireAuthenticatedUser` (placeholder until 7.1 lands; tests use
      `AllowAnonymous` under `WebApplicationFactory.WithWebHostBuilder`).
- [ ] Hub method names are PascalCase on wire (`AttachRun`, `DetachRun`) per SignalR convention.

### 8. Unit tests (xUnit + FluentAssertions)
1. `AttachRun_AddsConnectionToGroup`.
2. `DetachRun_RemovesConnectionFromGroup`.
3. `AttachRun_EmptyGuid_Throws`.
4. `OnDisconnected_ClearsAllGroupsForConnection`.
5. `RunGroupTracker_Attach_Detach_RunsForReturnsCorrectSet`.
6. `RunGroupTracker_ConcurrentAttach_IsThreadSafe`.
7. `TelemetryHubSink_PublishAsync_CallsGroupClients`.
8. `TelemetryHubSink_NullRunEvent_Throws`.

Three AAA sketches:

```csharp
// #1 AttachRun_AddsConnectionToGroup
var groups  = Substitute.For<IGroupManager>();
var context = new FakeHubCallerContext(connectionId: "c1", user: Fake.User("u"));
var tracker = new RunGroupTracker();
var hub = new TelemetryHub(tracker) {
    Context = context,
    Groups  = groups
};
var runId = Guid.NewGuid();
// Act
await hub.AttachRun(runId);
// Assert
await groups.Received(1).AddToGroupAsync("c1", $"run:{runId}", Arg.Any<CancellationToken>());
tracker.RunsFor("c1").Should().ContainSingle().Which.Should().Be(runId);

// #4 OnDisconnected_ClearsAllGroupsForConnection
tracker.Attach("c1", r1); tracker.Attach("c1", r2);
var hub = new TelemetryHub(tracker) { Context = Fake.Ctx("c1"), Groups = groups };
// Act
await hub.OnDisconnectedAsync(null);
// Assert
tracker.RunsFor("c1").Should().BeEmpty();
await groups.Received(1).RemoveFromGroupAsync("c1", $"run:{r1}", Arg.Any<CancellationToken>());
await groups.Received(1).RemoveFromGroupAsync("c1", $"run:{r2}", Arg.Any<CancellationToken>());

// #7 TelemetryHubSink_PublishAsync_CallsGroupClients
var client = Substitute.For<ITelemetryClient>();
var all    = Substitute.For<IHubClients<ITelemetryClient>>();
var ctx    = Substitute.For<IHubContext<TelemetryHub, ITelemetryClient>>();
ctx.Clients.Returns(all);
all.Group($"run:{runId}").Returns(client);
var sink = new TelemetryHubSink(ctx);
var ev = RunEventFactory.RunStarted(runId, seq:1);
// Act
await sink.PublishAsync(ev, default);
// Assert
await client.Received(1).RunEvent(ev);
```

### 9. Integration tests
- `TwoClientAttachTests.TwoClients_SameRun_ReceiveSameStream`:
  ```csharp
  using var factory = new WorkflowUiHostFactory();
  var c1 = await factory.ConnectHubAsync("/hubs/telemetry");
  var c2 = await factory.ConnectHubAsync("/hubs/telemetry");
  var evs1 = new List<RunEvent>(); var evs2 = new List<RunEvent>();
  c1.On<RunEvent>("RunEvent", evs1.Add);
  c2.On<RunEvent>("RunEvent", evs2.Add);
  await c1.InvokeAsync("AttachRun", runId);
  await c2.InvokeAsync("AttachRun", runId);
  factory.Publish(scripted9Events);
  await factory.DrainAsync();
  evs1.Should().BeEquivalentTo(evs2, opt => opt.WithStrictOrdering());
  ```
- `TwoClientAttachTests.DetachStopsDelivery`.

### 10. Golden-file / parity tests
- `HubWireFormatTests.ServerSerializer_MatchesClientTypes` — JSON-serialise each `RunEventKind`
  and assert the camelCase key set equals the UI `types.ts` discriminated union keys.

### 11. Edge cases & negative tests
- Duplicate `AttachRun` for same (connection, runId): idempotent — tracker contains single entry.
- `DetachRun` for runId not currently attached: no-op, no exception.
- Connection drops mid-publish: `client.RunEvent(ev)` throws `HubException`; sink logs and
  continues — never propagates into `EngineEventBridge`.
- Group name collision attempt (`AttachRun` with id whose `ToString()` contains newline) — `Guid`
  serialisation guarantees no injection; test passes `Guid.NewGuid().ToString()` literal.
- Reconnect storm: 100 clients reconnect within 1 s — tracker throughput ≥ 10k ops/s in unit stress.

### 12. Observability hooks
- Metric `signalr_connected_clients{hub="telemetry"}` gauge.
- Metric `signalr_group_membership_count{runId}` gauge (approximate, updated on add/remove).
- Serilog enricher adds `ConnectionId`, `UserId`, `RunId` on every hub invocation.
- OTel span `telemetry.hub.attachRun` / `detachRun`.

### 13. Risks & mitigations
- **R6.2.1-A — group leak on ungraceful disconnect**: SignalR handles group removal on transport
  close; `RunGroupTracker` is defence-in-depth.
- **R6.2.1-B — out-of-order delivery under server restart**: v1 accepts — Step 6.2.2 client
  triggers `Replay` to recover.

### 14. Evaluator gap-analysis prompts
1. Is the hub strongly-typed via `ITelemetryClient`?
2. Are groups named deterministically (`run:{guid}`)?
3. Does `OnDisconnectedAsync` actually use the tracker data?
4. Are attach/detach idempotent?
5. Is the sink registered as a singleton `IRunEventSink` alongside the bridge?
6. Does the hub avoid bleed between `runId` groups?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, two-client integration test green, parity test green.

---

## Step 6.2.2 — TS client with auto-reconnect & sequence resume (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Implement a TypeScript wrapper `TelemetryClient` around `@microsoft/signalr`'s `HubConnection`
that (a) exposes an idiomatic `onRunEvent(handler)` API, (b) auto-reconnects with exponential
back-off (0, 2, 10, 30 s then capped), (c) detects sequence gaps via `SequenceGapDetector`, and
(d) invokes hub method `Replay(runId, fromSeq)` to fill gaps. The client is framework-agnostic
(no React import) so it can be reused in live-monitor and test harnesses.

### 4. Depends on
- Step 6.2.1

### 5. Inputs
- TypeScript DTOs in `types.ts` (mirror of `RunEvent`).
- Hub route `/hubs/telemetry` and method `Replay`.

### 6. Deliverables
- `types.ts` — discriminated union mirroring `RunEventKind` exactly (11 members).
- `TelemetryClient.ts` — class with:
  - `connect(): Promise<void>`
  - `attach(runId: string): Promise<void>` (last-known-seq reset)
  - `detach(runId: string): Promise<void>`
  - `onRunEvent(h: (ev: RunEvent) => void): () => void` (returns unsubscribe)
  - Private reconnect logic with back-off policy array.
- `sequenceGapDetector.ts` — pure function
  `detect(events: RunEvent[], lastSeenSeq: number | null): { missing: [from, to] | null; nextLastSeen: number }`.
- Vitest suites for both files.
- Playwright spec `telemetry-reconnect.spec.ts` that forcibly closes the WebSocket (via
  `page.context().setOffline(true)`) and asserts UI resume.

### 7. Acceptance criteria
- [ ] Reconnect back-off matches policy `[0, 2000, 10000, 30000, 30000, ...]` ms.
- [ ] After reconnect, client calls `Replay(runId, lastSeenSeq + 1)` exactly once per runId it
      had attached to.
- [ ] Gap detector correctly identifies `[lastSeen+1, incoming-1]` missing window.
- [ ] Duplicate events (same `(runId, seq)`) are dropped silently.
- [ ] `onRunEvent` handlers receive events in monotonically increasing `seq` order post-dedup.
- [ ] `detach` invokes hub `DetachRun` AND stops firing handlers for that runId.
- [ ] Client survives 3 forced disconnects within 10 s without losing any of the 100 test events.

### 8. Unit tests (Vitest)
1. `connect_transitions_Disconnected→Connecting→Connected`.
2. `reconnect_backoff_matchesPolicy`.
3. `attach_after_reconnect_triggersReplay_fromLastSeq`.
4. `sequenceGapDetector_returnsNull_whenContiguous`.
5. `sequenceGapDetector_returnsGap_whenSkipDetected`.
6. `sequenceGapDetector_returnsNull_whenInitial` (lastSeen = null).
7. `duplicate_seq_isDeduplicated`.
8. `multiple_subscribers_allReceiveEvents`.
9. `unsubscribe_stopsDelivery`.
10. `detach_unsubscribesRunId_butOtherRunsUnaffected`.
11. `onclose_withNonTransientError_stopsReconnecting`.

Three AAA sketches:

```typescript
// #3 attach_after_reconnect_triggersReplay_fromLastSeq
const hub = mockHubConnection();
const client = new TelemetryClient(() => hub);
await client.connect();
await client.attach('run-1');
hub.emit({ runId: 'run-1', seq: 5, kind: 'NodeStarted', ... });
// Act — simulate drop/reconnect
await hub.forceReconnect();
// Assert
expect(hub.invokeCalls).toContainEqual(['Replay', 'run-1', 6]);

// #5 sequenceGapDetector_returnsGap_whenSkipDetected
const incoming = [{ seq: 10 }, { seq: 11 }];
const result = detect(incoming, 6);
expect(result.missing).toEqual([7, 9]);
expect(result.nextLastSeen).toBe(11);

// #7 duplicate_seq_isDeduplicated
const received: RunEvent[] = [];
client.onRunEvent(e => received.push(e));
hub.emit(E(5)); hub.emit(E(5)); hub.emit(E(6));
expect(received.map(e => e.seq)).toEqual([5, 6]);
```

### 9. Integration tests
- Playwright `telemetry-reconnect.spec.ts`:
  1. Launch dev server, connect live monitor.
  2. Start fixture run (emit 100 events).
  3. After 50 events, `page.context().setOffline(true)`; wait 3 s; set offline false.
  4. Assert final received count == 100 and all `seq` are contiguous.
- Server-side `ReconnectResumeTests.ClientReconnect_ReceivesMissingEvents` using C#
  `HubConnection` test harness mimicking the TS client.

### 10. Golden-file / parity tests
- `types.ts` union matches `RunEventKind` — cross-language contract test compiles a TS checker
  from the C# enum via `dotnet build` MSBuild task; mismatch fails CI.

### 11. Edge cases & negative tests
- Reconnect storm (server restart during burst) — back-off cap prevents tight loop.
- Token expired mid-stream — `onclose` with `HttpConnectionOptions.accessTokenFactory` returning
  new token on next attempt; if factory throws, reconnect stops and surfaces error via
  `onError` hook.
- `Replay(runId, seq)` server-side returns 404 (unknown run) — client logs and drops attach.
- Massive gap (> 1000 events): server ring buffer returns only latest 1000 with
  `{ truncated: true, fromSeq: N }`; client surfaces warning.
- Multiple concurrent `attach` calls same runId — deduped to single hub call.

### 12. Observability hooks
- Client emits `console.debug` with structured `{runId, seq, kind}` — wired through `logger` arg.
- Metric `ui_telemetry_reconnects_total` incremented on each reconnect (Prometheus client if
  host is instrumented; otherwise exposed via `window.__telemetryStats`).
- Playwright trace captured on failure.

### 13. Risks & mitigations
- **R6.2.2-A — duplicate delivery after replay**: dedup set keyed by `(runId, seq)` capped at
  10k entries; older entries LRU-evicted (events older than `lastSeenSeq - 10000` cannot be duped).
- **R6.2.2-B — replay loop if server lies about seq**: client caps replay attempts at 3 per
  reconnect.

### 14. Evaluator gap-analysis prompts
1. Does back-off array match the spec literal `[0, 2000, 10000, 30000, 30000]`?
2. Are gaps detected BEFORE handler dispatch (never fires gap events upstream)?
3. Does `detach` truly stop handler delivery for that runId?
4. Does the client survive a token-expiry scenario?
5. Is the dedup window bounded?
6. Are all 11 `RunEventKind` values present in `types.ts`?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, Playwright reconnect spec green, contract type-check green.

---

## Step 6.2.3 — Run-history ring buffer (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
Add server-side `RunHistoryBuffer` holding the last **1000 events per run** (`ConcurrentDictionary
<Guid, CircularBuffer<RunEvent>>`). New or reconnecting clients call hub method `Replay(runId,
fromSeq)` which returns the subset of buffered events with `seq >= fromSeq`. If the requested
`fromSeq` is older than the oldest buffered event, the response includes
`{ truncated: true, oldestAvailable: seq }` so the client can surface a warning. Buffer is memory
only in v1; Phase 7.2.3 adds on-disk JSONL sidecars for survival across restart.

### 4. Depends on
- Step 6.2.1

### 5. Inputs
- `TelemetryHubSink` publish pipeline (write path).
- Hub method `Replay` (read path).
- Eviction: buffer evicts a run entirely on terminal event (`RunCompleted`, `RunFailed`,
  `RunCancelled`) **10 minutes after** terminal timestamp (hold window so late-joiners can still
  see the final state).

### 6. Deliverables
- `RunHistoryBuffer.cs` — thread-safe, with methods:
  - `Append(RunEvent e)`
  - `ReplayAsync(Guid runId, long fromSeq, CancellationToken) -> ReplayResponse`
  - `ScheduleEviction(Guid runId, DateTime terminalUtc)`
  - `long OldestSeq(Guid runId)`
- `CircularBuffer<T>.cs` — fixed-capacity FIFO with `IReadOnlyList<T> Snapshot()`.
- Hub method `public Task<ReplayResponse> Replay(Guid runId, long fromSeq)`.
- Evictor hosted service using `IHostedService` + `PeriodicTimer` (1 min cadence).
- Unit tests + `LateJoinReplayTests`.

### 7. Acceptance criteria
- [ ] Buffer capacity is exactly 1000 per run; the 1001st append drops the oldest.
- [ ] `Replay` returns events in ascending `seq` order.
- [ ] `Replay` with `fromSeq < oldestSeq` returns `truncated=true` and the available window.
- [ ] `Replay` for unknown runId returns `404`-equivalent (`ReplayResponse.NotFound = true`).
- [ ] Terminal events schedule eviction 10 min in the future; non-terminal do not.
- [ ] After eviction, `Replay` returns `NotFound=true`.
- [ ] 10-thread append + 10-thread replay stress test completes without loss or duplication.
- [ ] Memory footprint bound: 1000 events × 1 KB avg = ≤ 1 MB/run; per-host soft cap of
      `MaxActiveRuns=10000` enforced (overflow evicts LRU run — warning logged).

### 8. Unit tests
1. `Append_UnderCapacity_PreservesAll`.
2. `Append_OverCapacity_DropsOldest`.
3. `Replay_FromSeqContained_ReturnsSubset`.
4. `Replay_FromSeqBelowOldest_Truncated`.
5. `Replay_UnknownRun_NotFound`.
6. `TerminalEvent_SchedulesEviction_10MinsOut`.
7. `Evictor_RemovesExpiredRuns`.
8. `ConcurrentAppendAndReplay_NoCorruption`.
9. `OldestSeq_UpdatesAsBufferRotates`.
10. `MaxActiveRuns_Overflow_EvictsLru`.

Three AAA sketches:

```csharp
// #2 Append_OverCapacity_DropsOldest
var buf = new RunHistoryBuffer(capacity: 3);
var r = Guid.NewGuid();
for (var i = 1; i <= 5; i++) buf.Append(new RunEvent { RunId = r, Seq = i, ... });
// Act
var resp = await buf.ReplayAsync(r, 0, default);
// Assert
resp.Events.Select(e => e.Seq).Should().Equal(3, 4, 5);
resp.Truncated.Should().BeTrue();
resp.OldestAvailable.Should().Be(3);

// #4 Replay_FromSeqBelowOldest_Truncated
buf.Append(new RunEvent { RunId=r, Seq=10 });
// ...fills to push oldest up to seq 50
var resp = await buf.ReplayAsync(r, fromSeq: 5, default);
resp.Truncated.Should().BeTrue();
resp.OldestAvailable.Should().Be(50);
resp.Events.First().Seq.Should().Be(50);

// #6 TerminalEvent_SchedulesEviction_10MinsOut
var clock = new FakeClock(DateTime.UtcNow);
var buf = new RunHistoryBuffer(capacity: 1000, clock: clock);
buf.Append(RunEventFactory.RunStarted(r));
buf.Append(RunEventFactory.RunCompleted(r));       // terminal
// Act
clock.Advance(TimeSpan.FromMinutes(9));
buf.EvictExpired();                                 // not yet
(await buf.ReplayAsync(r, 0, default)).NotFound.Should().BeFalse();
clock.Advance(TimeSpan.FromMinutes(2));
buf.EvictExpired();
// Assert
(await buf.ReplayAsync(r, 0, default)).NotFound.Should().BeTrue();
```

### 9. Integration tests
- `LateJoinReplayTests.NewClientAttaching_MidRun_ReceivesAllEvents`:
  1. Host up, emit 200 events via `TelemetryHubSink` BEFORE any client attaches.
  2. Client connects, calls `AttachRun(runId)` then `Replay(runId, 0)`.
  3. Assert 200 events received with contiguous `seq`.
- `LateJoinReplayTests.ReplayBeyondBuffer_TruncatedResponse`.
- `LateJoinReplayTests.AfterEviction_ReplayReturns404` (use fake clock test host).

### 10. Golden-file / parity tests
- `ReplayResponse.json` golden for shape:
  ```json
  { "runId":"...", "events":[...], "truncated":false, "oldestAvailable":1, "notFound":false }
  ```

### 11. Edge cases & negative tests
- Buffer capacity 0 (misconfiguration): constructor throws `ArgumentOutOfRangeException`.
- `fromSeq < 0`: treated as 0 (inclusive start).
- `fromSeq > latest`: returns empty `Events` with `truncated=false`, `notFound=false`.
- Eviction during `Replay` in progress: `Replay` operates on a snapshot copy, never throws.
- Memory pressure: 10k active runs × 1000 events — smoke test allocates ~10 GB-free bound checked.
- Ring buffer corruption under concurrent append: property test with 100k random appends vs
  expected array — zero divergence.

### 12. Observability hooks
- Metric `workflow_ui_run_history_events_total{runId}` gauge of current buffered count.
- Metric `workflow_ui_run_history_active_runs` gauge.
- Log line on eviction `"Evicted run {RunId}, events={Count}, age={Minutes}"`.
- OTel span `telemetry.replay` with attributes `run.id`, `from.seq`, `events.returned`,
  `truncated`.

### 13. Risks & mitigations
- **R6.2.3-A — memory pressure under thousands of simultaneous runs**: LRU soft cap +
  Phase 7.2.3 disk spill.
- **R6.2.3-B — replay storm on mass reconnect**: server-side throttle `MaxReplayConcurrency = 32`
  via `SemaphoreSlim`.
- **R6.2.3-C — eviction clock skew**: inject `IClock` (also used by cleanup job 7.2.4).

### 14. Evaluator gap-analysis prompts
1. Is 1000-per-run capacity enforced on append (not on replay filter)?
2. Does `truncated` flag accurately reflect dropped history?
3. Is eviction scheduled only on terminal events?
4. Is the buffer genuinely lock-free for readers via snapshot copy?
5. Is the `MaxActiveRuns` cap tested?
6. Does `Replay` correctly return empty-but-not-truncated when `fromSeq > latest`?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, late-join integration test green, eviction property test green, memory soft cap
enforced.
