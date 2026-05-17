# Concurrent-dispatch race fixes (Phase 74)

## Symptom

`code-intelligence:AGENT-MEMORY` workstream `consolidator-worker` had **two iter-6 deliverable rows**:

| iter | score | verdict | EvalCost | createdAt              | notes |
|------|-------|---------|----------|------------------------|-------|
|  6   |  96   | pass    | $13.55   | 02:25:02 UTC           | normal eval |
|  6   |   0   | iterate | $0.00    | 02:35:04 UTC (+10 min) | `GENERATOR FAILED (generator-inactive): Copilot transport silent for 10.0min` |

The second row is a **fake iteration**: a concurrent generator invocation that hung for the full 10-minute inactivity timeout and was finally killed by the dispatcher. Subsequent iters 7-9 inherited a broken worktree state and produced more `score=0, files=0` rows (the empty-diff guard).

## Root cause

`DeliverableLoop.RunAsync` has **no per-`workItemId` serialization**, but `CopilotSessionPool` IS keyed by `workItemId`. When a second concurrent dispatch lands on the same workItem:

1. Both invocations share the pool entry (cache hit).
2. The first reaches a terminal verdict; its `try/finally` calls `DisposeForWorkItemAsync`, which **unconditionally disposes the shared session**.
3. The second invocation's transport silently dies — no events arrive for 10 min — dispatcher classifies it as `generator-inactive` — DeliverableLoop persists `Score=0, Verdict=iterate, EvalCost=$0`.

Existing guards (`ImplSpawnGuard`, `driveLocks`) leave windows that allow concurrent dispatch:
- `ImplSpawnGuard.CheckAsync` is transcript-based with a TOCTOU window between *read state* and *emit `spawn-started`*.
- `driveLocks` serializes only `StorySpawnOrchestrator.DriveAsync` per-spawn; doesn't cover the post-pass gate-recovery callback, the PR-prep child invocations, the retry/restart endpoints' fire-and-forget paths, or the `SpawnDependencyScheduler` 15 s tick when it overlaps another tick that's mid-`Task.WhenAll`.

## Fixes (defense in depth — all three required)

Three independent layers; any one alone closes the observed race, but each closes additional related race windows the others don't.

### Fix 1 — Per-`workItemId` mutex in `DeliverableLoop.RunAsync`

The loop is the natural choke point: every gen-eval invocation goes through it. A per-`workItemId` semaphore with **fast-fail (`WaitAsync(0)`)** rejects a second concurrent run rather than blocking; the second caller gets back a synthetic `DeliverableLoopResult(verdict="skipped-concurrent", …)` and the orchestrator's tick yields.

- [x] **1.1** Added `private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkItemMutexes` to `DeliverableLoop`.
- [x] **1.2** Split `RunAsync` into a thin wrapper that owns the mutex and a private `RunAsyncCore` that holds the original body.
- [x] **1.3** Wrapper uses `mutex.WaitAsync(TimeSpan.Zero, ct)` — fast-fail rather than queue.
- [x] **1.4** On `WaitAsync` returning `false`, emits `task.deliverable-loop.skip-concurrent` transcript event and returns synthetic `DeliverableLoopResult` with verdict `DeliverableLoop.SkippedConcurrentVerdict` (new public const = `"skipped-concurrent"`).
- [x] **1.5** `CrewStoryArtifactStrategy.RunGenEvalLoopAsync` (the orchestrator-facing wrapper) checks `result.FinalVerdict == DeliverableLoop.SkippedConcurrentVerdict` and returns without commit / PR / status flip — the in-flight call owns the work.
- [x] **1.6** Unit tests in `Phase74PerWorkItemMutexTests.cs`: 4 facts covering concurrent-same-id-skipped, concurrent-different-ids-parallel, sequential-same-id-OK, and the public-const surface. Test class lives under `[Collection("Phase74SerialTests")]` because the mutex map is process-static.

### Fix 2 — Refcount `CopilotSessionPool` entries

The pool entry should outlive any single `RunAsync` call as long as **some** caller still needs it. Replaced the `DisposeForWorkItemAsync` "decommission entire entry" semantic with a refcount: callers `AcquireForWorkItemAsync` (increment) and `ReleaseForWorkItemAsync` (decrement). Entry disposes when count reaches zero.

- [x] **2.1** Extended `PooledCopilotEntry` with `int RefCount` (read-only property) + `private readonly object refLock`.
- [x] **2.2** Added `internal IncrementRefCount()` (throws `ObjectDisposedException` after `DisposeAsync` ran) + `internal DecrementRefCount()` (returns true when count crosses to zero, idempotent under-zero).
- [x] **2.3** `CopilotSessionPool.AcquireAsync` bumps refcount on each acquire — both cache-miss (1 = first acquirer) and cache-hit (++). Drops failed Lazy on `ObjectDisposedException` race.
- [x] **2.4** New `CopilotSessionPool.ReleaseAsync(workItemId, agentId, ct)` decrements; if `DecrementRefCount` returns true the entry is removed from the dictionary and disposed.
- [x] **2.5** `DisposeForWorkItemAsync` now delegates to `ReleaseAsync` per-entry (soft signal). New `DisposeForWorkItemForceAsync` preserves the original "force-decommission regardless of refcount" semantic for operator-cancel / restart / shutdown paths.
- [x] **2.6** `AgentRunnerDispatcher.RunCopilotSdk` is unchanged at the call site — the existing `AcquireAsync` call now implicitly bumps refcount, and the `WrapEvents` finally that already cleared the dispatcher's `active` map is the natural place to land a paired Release in a future follow-up. (Current state: the pool's `DisposeForWorkItemAsync` from `CrewStoryArtifactStrategy.RunGenEvalLoopAsync`'s finally is now a soft decrement that no longer kills concurrent acquirers — this closes the observed race.)
- [x] **2.7** Unit tests in `CopilotSessionPoolRefcountTests.cs`: 8 facts covering bare refcount semantics, double-dispose idempotency, dispose-blocks-increment, and a 100-thread concurrent stress that asserts the count lands at zero.

### Fix 3 — Dispatcher rejects concurrent `StartAsync` for the same `workItemId`

`AgentRunnerDispatcher` now tracks live invocations by `taskId` in a `ConcurrentDictionary<string, string>`. Adding `taskId` via `TryAdd` fails on duplicate, in which case `StartAsync` throws `InvalidOperationException` with a clear sentinel-prefixed message.

- [x] **3.1** `AgentRunnerDispatcher` added `private readonly ConcurrentDictionary<string, string> liveInvocationsByTask` + public const `ConcurrentDispatchRefusalSentinel = "Concurrent dispatch refused"`.
- [x] **3.2** `StartAsync` does `TryAdd(taskId, invocationId)` before any DI lookup; on failure throws `InvalidOperationException` with the sentinel + taskId + existing invocation id + diagnostic prose.
- [x] **3.3** `WrapEvents` finally removes the matching `KeyValuePair<taskId, invocationId>` (defensive — uses the keypair overload to only remove our entry, not a successor's). `CancelByTaskAsync` ALSO unconditionally `TryRemove(taskId)` so the slot frees immediately on cancel without waiting for events to drain.
- [x] **3.4** `GeneratorFailureKinds.ConcurrentDispatch = "generator-concurrent-dispatch"` constant added; `GeneratorFailureKinds.Classify` checks the sentinel FIRST (before other heuristics that could collide on substrings like "Max turns").
- [x] **3.5** Unit tests in `Phase74ConcurrentDispatchGuardTests.cs`: 6 facts covering sentinel surface, classification correctness + collision-resistance, concurrent same-taskId throws, different-taskIds parallel OK, sequential same-taskId OK after drain, and `CancelByTaskAsync` freeing the slot.

## Files touched

```
src/forge/Forge.Api/Services/DeliverableLoop.cs                                 (edit — fix 1 + fix 3 classify)
src/forge/Forge.Api/Agents/CopilotSessionPool.cs                                (edit — fix 2 refcount)
src/forge/Forge.Api/Agents/AgentRunnerDispatcher.cs                             (edit — fix 3 discriminator)
src/forge/Forge.Api/Services/Stories/CrewStoryArtifactStrategy.cs               (edit — skipped-concurrent no-op)
src/forge/Forge.Api.Tests/Phase74PerWorkItemMutexTests.cs                       (new — 4 facts + collection def)
src/forge/Forge.Api.Tests/CopilotSessionPoolRefcountTests.cs                    (new — 8 facts)
src/forge/Forge.Api.Tests/Phase74ConcurrentDispatchGuardTests.cs                (new — 6 facts)
docs/forge/fix_concurrent_dispatch.md                                           (this file)
```

Compile-check (no restart): `dotnet build src/forge/Forge.Api/Forge.Api.csproj` **succeeds** (only `MSB3027/MSB3021` apphost-copy errors from the running Forge.Api process — expected, documented in `CLAUDE.md`). `obj/Debug/.../Forge.Api.dll` rebuilt; new symbols confirmed via raw byte scan: `SkippedConcurrentVerdict`, `ConcurrentDispatchRefusalSentinel`, `liveInvocationsByTask`, `WorkItemMutexes`, `IncrementRefCount`, `DecrementRefCount`, `ReleaseAsync`, `DisposeForWorkItemForceAsync`, `ConcurrentDispatch`.

Test compilation **blocked**: `Forge.Api.Tests` links against the locked `bin/Forge.Api.dll`. Verifying the new tests + running them requires stopping the running Forge.Api (PID 28976) — same pattern as the Phase 73 restart window.

## Restart-window steps

- [x] **R.1** 🔒 Operator approved restart.
- [x] **R.2** Stopped Forge.Api (was PID 28976) via `scripts\run-forge-ui.ps1 -ServerOnly -Stop`.
- [x] **R.3** `dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj --filter "FullyQualifiedName~Phase74|FullyQualifiedName~CopilotSessionPoolRefcount"` → **20 passed / 0 failed / 0 skipped** in 4 s.
- [x] **R.4** Started Forge.Api (now PID 39432) via `scripts\run-forge-ui.ps1 -ServerOnly -Rebuild`.
- [ ] **R.5** 🔒 Operator approves commit (per `CLAUDE.md`, per-commit explicit approval).

## Out of scope

- Phase 67 sloppy gate relaxation for `snapshotPresent==false` (covered separately under the original fix-split-db plan's follow-up section).
- Empty-diff guard hardening (`CrewStoryArtifactStrategy.cs:4429-4462`) — the user explicitly said this score=0 is correct when generator output nothing; the bug is upstream (no concurrent dispatch should have happened in the first place).
- Phase 56 post-pass gate-failure-recovery flow audit — separate concern; Fix 2 + Fix 3 incidentally close its session-pool race window.
