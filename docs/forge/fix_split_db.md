# Phase 73 DB-split half-applied — recovery plan

## Symptom

Operator reports: workstream `Reranker Trainer` (story
`code-intelligence:AGENT-MEMORY`) reached `verdict=pass, score=96` at
iter 31 but the UI shows `status=blocked, iter=0, latestScore=null`.
Downstream workstreams stay blocked. Cost meter keeps climbing on
spawn-level `lastError`: *"Agent XY (team Review) failed during phase
'review:ws-…-reranker-trainer:r0': Max turns (60) reached"*.

## Root cause

`SplitApplyService` (Phase 73 / Phase F) was started but never
completed. Evidence on the host:

1. `forge.db.pre-phase73.20260517011334.bak` exists → step 3 backup ran.
2. `code-intelligence.db`, `failover-cluster.db`, `raft.db`, `local.db`
   exist with partial product-side tables → steps 4-5 (schema replay +
   partition copy) ran.
3. `forge_seed_state` has only `phase22-backfill-done`; **no
   `phase73-split-applied` row** → step 8 (stamp marker) **never ran**.
4. `forge.db` still holds `workstreams`, `deliverables`, `task_queue`,
   `prs`, etc. → step 7 (drop source tables) **never ran**.
5. `forge.db-wal` last modified seconds ago (writers still active);
   `code-intelligence.db-wal` last modified at boot only (snapshot
   frozen).

Resulting divergence:

| Path | Slug source | Routes to | Effect |
|------|-------------|-----------|--------|
| Background writers (`StorySpawnOrchestrator.DriveAsync`, …) | No `AsyncLocal`, no `HttpContext` → `CreateUnscoped()` | `forge.db` | All real progress accumulates here |
| HTTP reads from UI (`X-Forge-Product-Id: code-intelligence`) | `ProductScopeMiddleware` → header → factory | `code-intelligence.db` | Frozen snapshot, no recent deliverables |

Concrete numbers: `forge.db` has 268 AGENT-MEMORY deliverables incl.
iter 31 pass+96; `code-intelligence.db` has 219 (max iter ≈ 17).

## Stages

Every checkbox represents a unit of work. Stages with `🔒` require the
operator's restart-window approval and the marker guard from Stage 1.
No gaps — every step is tracked through to verification.

### Stage 0 — Sideline per-product DBs (operational, no code) 🔒
Renames the per-product DB triplets (`*.db`, `*.db-wal`, `*.db-shm`)
out of the way so `ProductDbContextFactory.CreateFor` falls back to
the control DB on the very next `File.Exists` evaluation. Reverses
the half-applied split with zero data loss (the parked files remain on
disk for inspection / future re-split). Cannot be applied while
Forge.Api is running because SQLite's `Cache=Shared` connection pool
holds OS handles open for the lifetime of the process.

- [x] **0.1** Identify the four divergent DBs (`code-intelligence.db`,
      `failover-cluster.db`, `raft.db`, `local.db`) plus `-wal` /
      `-shm` siblings. Skip `forge.db*` and `*.bak` files.
- [x] **0.2** At restart window: stop Forge.Api → rename each file
      to `{name}.parked-phase73-halfapplied-{yyyyMMddHHmmss}` → start
      Forge.Api. **Done at 2026-05-17T14:22:31 UTC.**
- [x] **0.3** Verify the UI now shows live data:
      `curl /api/stories/code-intelligence:AGENT-MEMORY/spawn -H "X-Forge-Product-Id: code-intelligence"`
      returns Reranker Trainer `status=complete, iter=33,
      latestScore=96, latestVerdict=pass`. **Verified.**

### Stage 1 — `phase73-split-applied` marker guard
Hardens `ProductDbContextFactory.CreateFor` so a per-product file is
never opened unless `forge_seed_state.phase73-split-applied` is
stamped. Prevents this exact failure mode from recurring if someone
re-runs the dry-run or partial splitter.

- [x] **1.1** Add `Persistence/SplitMarkerCache.cs` — process-wide
      lazy probe of the marker keyed by control-DB path. Public
      `IsPhase73Applied(IForgeDbPaths)` + `ResetForTesting()`.
- [x] **1.2** Edit `Persistence/ProductDbContextFactory.cs` —
      `CreateFor(slug)` now treats `(perProductFileExists &&
      splitApplied)` as the only condition that opens the per-product
      file; one-time `WARN` per slug when half-applied state is
      detected. New optional `ILogger<ProductDbContextFactory>` ctor
      overload (preserves all existing call sites).
- [x] **1.3** Add `Forge.Api.Tests/Phase73HalfAppliedGuardTests.cs` —
      7 facts:
      - `IsPhase73Applied_ReturnsFalse_OnFreshControlDb`
      - `IsPhase73Applied_ReturnsFalse_WhenSeedTableExistsButRowMissing`
      - `IsPhase73Applied_ReturnsTrue_AfterMarkerStamped`
      - `IsPhase73Applied_CachesAfterFirstProbe`
      - `Factory_RoutesToControlDb_WhenPerProductFileExistsButMarkerMissing`
      - `Factory_RoutesToPerProductDb_WhenMarkerStamped`
      - `Factory_RoutesToControlDb_WhenMarkerStampedButFileMissing`
      - `Factory_DoesNotPickUpFileDroppedAfterFirstResolve`
- [x] **1.4** Edit `Forge.Api.Tests/SplitContextsTests.cs` — update
      `Factory_FallsBackTo_ControlDbPath_WhenPerProductFileMissing` to
      stamp the marker before asserting per-product pickup; keep the
      missing-file fallback assertion intact.
- [x] **1.5** `dotnet build src/forge/Forge.Api/Forge.Api.csproj`
      succeeds (only `MSB3027/MSB3021` apphost-copy errors from the
      running process — expected and harmless per `CLAUDE.md`).
- [x] **1.6** `dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj
      --filter "FullyQualifiedName~Phase73|FullyQualifiedName~SplitContexts"`
      → **passed** (part of the 72-passing full run, see Stage 3.6).

### Stage 2A — `ProductSlugExtractor` helper
Derives the product slug from common Forge entity IDs by convention,
falling back to a `ControlDbContext.Repos → Products` lookup when the
convention misses. Used by Stage 2B/2D.

- [x] **2A.1** Add `Persistence/ProductSlugExtractor.cs`:
      - `FromStoryId(string storyId)` → slug before first `:` in
        `{slug}:{STORY_KEY}` (e.g. `code-intelligence:AGENT-MEMORY` →
        `code-intelligence`).
      - `FromSpawnId(string spawnId)` → strip `spawn-` prefix +
        delegate to story-id heuristic.
      - `FromWorkstreamId(string wsId)` → strip `ws-` prefix and walk
        prefix candidates against the known product slug set from
        `ControlDbContext.Products`.
      - `ResolveAsync(string anyId, ControlDbContext control,
        CancellationToken)` → tries each heuristic in order, then a DB
        lookup via `WorkItem.Id → RepoId → Products.Slug`.
- [x] **2A.2** Add `Forge.Api.Tests/ProductSlugExtractorTests.cs` —
      facts cover happy-path conventions, ambiguous prefixes, fallback
      DB lookup, and missing input.

### Stage 2B — Push `AsyncLocal` slug in remaining writer entry points
Most background workers already push the slug via
`ProductFanout.ForEachProductAsync` (`AutoMergeWatcher`,
`AgentMemoryReaper`, `BuildSyncJob`, `BudgetCooldownReconciler`,
`CrewQueueWorker`, `LeadAutoDispatcher`, `PrSyncJob`,
`SloppyReassignmentReconciler`, `StalledMergedRecoveryReconciler`,
`TranscriptRetentionJanitor`, `WorkItemSyncJob`). The gaps are the
orchestrator (called by scheduler + HTTP endpoints) and the resume
sweeper.

- [x] **2B.1** Edit `Services/Stories/StorySpawnOrchestrator.cs` —
      in `DriveAsync(string storyId, bool forceResume, CancellationToken)`
      insert `using var _ = ProductDbContextFactory.PushAsyncLocalSlug(
      ProductSlugExtractor.FromStoryId(storyId))` immediately before
      `using var scope = this.services.CreateScope()` (line 184).
      Single push covers every nested `CreateScope()` because
      `AsyncLocal<T>` flows through `IServiceScopeFactory.CreateScope`.
- [x] **2B.2** Edit `Services/Stories/StoryResumeRecoveryService.cs` —
      in `SweepAsync`, wrap each per-spawn `Task.Run` body with
      `PushAsyncLocalSlug(ProductSlugExtractor.FromStoryId(storyId))`.
- [x] **2B.3** Audit `Services/BuildValidationWorker.cs`,
      `Services/WorktreeChangeFeed.cs`,
      `Services/Stories/SpawnDependencyScheduler.cs` for
      `ProductDbContext` use. `BuildValidationWorker` consumes the
      build runs which are routed via `BuildSyncJob` (already scoped).
      `WorktreeChangeFeed` doesn't open `ProductDbContext`.
      `SpawnDependencyScheduler` only routes through
      `orchestrator.DriveAsync` (covered by 2B.1) plus a
      `ControlDbContext`-only `story_spawns` projection that doesn't
      need a slug.
- [x] **2B.4** Add `Forge.Api.Tests/Phase73SlugPropagationTests.cs` —
      facts:
      - `DriveAsync_PushesSlugBeforeOpeningProductContext`
      - `SweepAsync_PushesSlugPerSpawn`
      - `SlugPush_PropagatesThroughNestedCreateScope`

### Stage 2C — `SplitApplyService.ResumeAsync` for idempotent retry
Today's `ApplyAsync` is all-or-nothing: it refuses to run if the
marker is present and rolls back if any verification fails. After
half-application it leaves the operator with no automated path back
to "consistent". Add an explicit `ResumeAsync` that handles the exact
recovery case we observed: marker absent + per-product files exist +
source tables still present in control DB.

- [x] **2C.1** Edit `Persistence/Split/SplitApplyService.cs` — add
      `ResumeAsync(CancellationToken)`:
      1. Bail if marker already present.
      2. Refuse unless per-product files exist for every active
         product (loud, operator-actionable).
      3. For each product table: `DELETE FROM {table}` on the
         per-product DB → re-execute the boundary's partition-copy
         SQL from the control DB → verify counts → on mismatch abort.
      4. Drop the product-side tables from the control DB.
      5. Stamp `phase73-split-applied`.
- [x] **2C.2** Edit `Endpoints/AdminDbSplitEndpoints.cs` — add
      `POST /api/admin/db/split-resume` (gated by `RequireRole.ProductOwner`
      + `Forge:Db:SplitEnabled=true`, same pattern as `/split-apply`).
- [x] **2C.3** Add `Forge.Api.Tests/SplitResumeServiceTests.cs` —
      facts: idempotent on already-applied; refuses with missing
      per-product file; truncate-then-copy correctness; counts
      verification; marker stamped on success.

### Stage 2D — Fail-loud `CreateUnscoped` once marker is stamped
After Stage 2C succeeds the operator stamps the marker, and from that
point on any writer that resolves a `ProductDbContext` without a slug
is a bug we want to surface immediately rather than silently routing
to `forge.db`.

- [x] **2D.1** Edit `Persistence/ProductDbContextFactory.cs`
      `CreateForCurrentRequest()` — when
      `SplitMarkerCache.IsPhase73Applied` is true AND
      `Forge:Db:FailLoudOnUnscoped` config is true (default `false`
      until the operator validates 2C), throw
      `InvalidOperationException` with the resolved control path +
      stack-trace hint. Default `false` keeps the legacy
      `CreateUnscoped` fallback intact so this stage can ship without
      a same-tick regression.
- [x] **2D.2** Add `Forge.Api.Tests/Phase73FailLoudTests.cs` — facts:
      marker absent → no throw (legacy); marker present + flag false
      → no throw (opt-in); marker present + flag true → throws with
      diagnostic message including `ControlDbPath`.

### Stage 2E — Cross-product write integration test
- [x] **2E.1** Add `Forge.Api.Tests/Phase73CrossProductWriteTests.cs`
      using `MultiProductForgeWebApplicationFactory`: seed two
      products, push slug for product A, write a `DeliverableRow`,
      assert it lands in `productA.db` not `forge.db`. Repeat for B
      under its own slug push.

### Stage 3 — Apply + verify (restart window)
The remaining tasks are operator-driven and require the restart that
unlocks the file handles. They MUST be done in order.

- [x] **3.1** 🔒 Operator approves restart.
- [x] **3.2** Stop Forge.Api (PID 7356 stopped via `scripts\run-forge-ui.ps1 -ServerOnly -Stop`).
- [x] **3.3** Rename per-product DB triplets (12 files moved to `.parked-phase73-halfapplied-20260517142231`).
- [x] **3.4** Start Forge.Api — Stage 1 + Stage 2 patches live (new PID 28976).
- [x] **3.5** Verify UI: with and without `X-Forge-Product-Id: code-intelligence` header the API returns Reranker Trainer `status=complete, iter=33, latestScore=96, latestVerdict=pass`.
- [x] **3.6** `dotnet test --filter "FullyQualifiedName~Phase73|FullyQualifiedName~SplitContexts|FullyQualifiedName~SplitResume|FullyQualifiedName~ProductSlugExtractor|FullyQualifiedName~SplitApplyService"` → **72 passed / 0 failed / 0 skipped** in 18 s.
- [x] **3.7** (Optional, deferred) operator chooses whether to retry
      Phase 73 cleanly via `POST /api/admin/db/split-resume`. Until
      then the system runs single-DB on `forge.db` (parked
      per-product files are out of band). **Deferred — current state
      is healthy single-DB; operator triggers when a maintenance
      window is available.**
- [x] **3.8** Commit the Stage 1 + Stage 2 patch set + this document.
      Local commit `156feb85` on `ws/test`; **NOT pushed** (push
      requires separate explicit approval per `CLAUDE.md`).

### Operator script (restart window — Stage 3.2-3.5 combined)

```powershell
# 0. Approval gate — operator types 'yes' to proceed.

# 1. Stop Forge.Api gracefully. Adjust if you run it under a launcher.
Get-Process -Name Forge.Api -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2

# 2. Sideline the half-applied per-product DBs (Stage 0).
$ts = Get-Date -Format yyyyMMddHHmmss
Get-ChildItem E:\forge\*.db, E:\forge\*.db-wal, E:\forge\*.db-shm |
    Where-Object { $_.Name -notlike 'forge.db*' -and $_.Name -notlike '*.bak' } |
    Rename-Item -NewName { "$($_.Name).parked-phase73-halfapplied-$ts" }

# 3. Re-build to refresh bin/ now that file locks are released.
cd E:\work\github\crp\workflow
dotnet build src\forge\Forge.Api\Forge.Api.csproj -nologo

# 4. Run the new tests.
dotnet test src\forge\Forge.Api.Tests\Forge.Api.Tests.csproj `
    --filter "FullyQualifiedName~Phase73|FullyQualifiedName~SplitContexts|FullyQualifiedName~SplitResume|FullyQualifiedName~ProductSlugExtractor" `
    --nologo

# 5. Start Forge.Api back up via the usual launcher.
#    (left to the operator — `scripts\run-forge-ui.ps1` etc.)

# 6. Verify the live UI now sees post-Phase-73 data.
$h = @{ 'X-Forge-User' = 'xiaodoli'; 'X-Forge-Product-Id' = 'code-intelligence' }
$j = Invoke-RestMethod -Headers $h `
    "http://localhost:5180/api/stories/code-intelligence:AGENT-MEMORY/spawn"
$j.workstreams | Where-Object { $_.name -eq 'Reranker Trainer' } |
    Format-List name, status, iter, latestScore, latestVerdict
# Expected: status=review, iter=33, latestScore=96, latestVerdict=pass
```

## Files touched

```
src/forge/Forge.Api/Persistence/SplitMarkerCache.cs                          (new)
src/forge/Forge.Api/Persistence/ProductSlugExtractor.cs                      (new)
src/forge/Forge.Api/Persistence/ProductDbContextFactory.cs                   (edit)
src/forge/Forge.Api/Persistence/Split/SplitApplyService.cs                   (edit — add ResumeAsync)
src/forge/Forge.Api/Endpoints/AdminDbSplitEndpoints.cs                       (edit — add /split-resume)
src/forge/Forge.Api/Services/Stories/StorySpawnOrchestrator.cs               (edit — push slug)
src/forge/Forge.Api/Services/Stories/StoryResumeRecoveryService.cs           (edit — push slug per spawn)
src/forge/Forge.Api.Tests/Phase73HalfAppliedGuardTests.cs                    (new)
src/forge/Forge.Api.Tests/Phase73SlugPropagationTests.cs                     (new)
src/forge/Forge.Api.Tests/Phase73FailLoudTests.cs                            (new)
src/forge/Forge.Api.Tests/Phase73CrossProductWriteTests.cs                   (new)
src/forge/Forge.Api.Tests/SplitResumeServiceTests.cs                         (new)
src/forge/Forge.Api.Tests/ProductSlugExtractorTests.cs                       (new)
src/forge/Forge.Api.Tests/SplitContextsTests.cs                              (edit)
docs/forge/fix_split_db.md                                                   (this file, new)
```

## Out of scope (separate tickets)

- Spawn-level Reranker Trainer review hitting `Max turns (60)` on
  PR #33 — `CrewStoryArtifactStrategy.InvokeAsync` for the `Review`
  team needs `MaxTurns: Math.Max(60, points × 30)` on `AgentContext`
  (mirrors `CrewQueueWorker.cs:1341` for the Engineer/DeliverableLoop
  path). Tracked separately. The spawn `lastError` should be
  dismissed before the restart so it doesn't keep re-burning budget.
- Old `forge.db` bloat after the split actually completes — Phase 73
  drops the product tables but the SQLite page file isn't compacted.
  A `VACUUM` follow-up is its own ticket.
