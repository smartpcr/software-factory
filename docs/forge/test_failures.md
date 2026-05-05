# Forge.Api.Tests — Resolved (was: 8 failures)

> Resolved 2026-05-04 in commit `b64e29a` ("fix broken tests, enable resume,
> open question, wrong workstree failed PR"). All eight test failures listed
> below now pass. Doc kept for context — see "Fixes" below for what landed.

## Current status

| | Count |
|---|---|
| Total tests | 905+ |
| Failing (in this list) | **0** |
| Skipped (intentional) | 4 |

Repro of the eight originally-failing tests + their immediate siblings:

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --no-build `
  --filter "FullyQualifiedName~Phase28OrchestratorTests|FullyQualifiedName~Phase16ExecutionTests.Server_Side_Push|FullyQualifiedName~Phase16ExecutionTests.PairedCrew"
```

Last verified run: 13 passed, 1 skipped (the `[Skip]`-marked
`Spawn_From_ProductStory_With_AutoProgress_Drives_End_To_End_Inside_The_POST`).

## Fixes

### Phase 28 orchestrator wiring (was 6)

| # | Test | Fix |
|---|---|---|
| 1 | `Spawn_From_ProductStory_Opens_Plan_Pr_And_Carries_Description_And_Points` | Orchestrator now persists a synthesized `WorkItemRow` when a spawn arrives without one (covers `ProductStoryRow` source). Test asserts on `StoryPaths.Safe(storyId)` so the colon in the id is sanitised the same way the orchestrator sanitises it. |
| 2 | `Plan_Pr_Merge_Triggers_Decomposition_And_Workstream_Creation` | `StorySpawnOrchestrator.DriveAsync` is now single-step per call (each call advances by one sub-step inside the current phase). The previous tight loop auto-merged the plan PR and ran decomposition before the test could observe the intermediate state, then `PATCH /spawn/phase` to "decompose" 422'd. |
| 3 | `Decompose_Gate_Blocks_Until_Plan_Artifacts_Land_On_Main` | `EnsurePlanDocWorkItemsAsync` switched from `string[]` to `List<string>` for the `docIds` collection. C# 12's implicit `T[] → ReadOnlySpan<T>` made EF route `.Contains(...)` through `MemoryExtensions.Contains` which the parameter extractor can't compile (`ReadOnlySpan` is a ref struct → `TRet` constraint violation), surfacing as a `DbUpdateException` wrapper. |
| 4 | `Implement_Phase_Auto_Assigns_Engineer_And_Opens_Workstream_Prs` | Same root as #2 — single-step drive lets `PATCH … "implement"` succeed with the spawn still at `decompose`. |
| 5 | `Review_Phase_Posts_Comments_And_Engineer_Resolves_Then_Merges` | Same root as #2 — workstreams now exist by the time the test queries them. |
| 6 | `Dependency_Scheduler_Unblocks_Downstream_When_Upstream_Merges` | `SpawnDependencyScheduler.TickOnceAsync` no longer filters out `Done`-phase spawns, so the final WI-closure step (`DriveDoneAsync`) actually runs. |

Supporting changes that all six depended on:

- `LocalStoryProviderOps` now takes `IServiceScopeFactory` (singleton-safe
  scope creation) instead of `IServiceProvider`. The previous binding
  captured a request-scope provider that disposed mid-flight when the
  orchestrator's inline drive ran past the response boundary, surfacing as
  `ObjectDisposedException` on the first `OpenPrAsync`.
- `ForgeWebApplicationFactory` now `RemoveAll<T>() + AddSingleton<T,…>()`
  on the three story bindings (clean replace, not append) and sets
  `Forge:Stories:SpawnDriveInline=true` (production `appsettings.json` has
  it `false` for fire-and-forget; tests need inline so `PlanPrId` is
  populated before they read it).

### Phase 16 real-git pipeline (was 2)

| # | Test | Fix |
|---|---|---|
| 7 | `Server_Side_Push_Runs_Before_PR_Creation` | Test pre-seeds a real commit on `ws/<workItemId>` via a side worktree before kicking the worker. The local-stub agent doesn't write files, so the worker's "skip push when 0 commits ahead of base" guard correctly suppressed the push and the remote stayed empty. The seed commit models what a real Claude/Copilot agent would commit to the worktree. Test also polls `ls-remote` for the branch instead of a fixed `Task.Delay`. |
| 8 | `PairedCrew_EndToEnd_Pickup_Through_PlanAgreement` | Two changes: `CrewQueueWorker` now writes `evaluatorCrewId` into `WorkItemRow.AssignedAgentJson` so the paired-crew assignment record exposes both sides (test asserts on `e2e-eval`); the test polls for `task.finished/failed` instead of a fixed 3 s `Task.Delay`, and filters the "all task.* events carry the generator crew id" assertion to events that actually came from the generator (evaluator events legitimately carry the evaluator crew id). |

## Out of scope

The following remain in the test suite and are **independent of this list**:

- `Phase16ExecutionTests.FullPipeline_RealAI_Worktree_Iterate_PR` — marked
  `[Trait("Category", "Integration")]`. CI excludes integration tests; this
  one needs real Claude API access to pass.
- `Phase39PairDiversityTests.*` — both tests resolve `IStoryArtifactStrategy`
  from DI and expect the production `CrewStoryArtifactStrategy` to enforce
  the diversity rule, but the test factory replaces the binding with
  `DeterministicStoryArtifactStrategy` for every test. These tests need
  either a per-test override (don't apply the factory replacement here) or
  an in-test stub runner that surfaces the diversity violation.
- `AuthAndMeTests`'s three `[Skip]` results — guarded by `FORGE_AUTH_ENABLED=1`.
- `AgentSoulAndBudgetTests.Reassign_Moves_Agent_And_Writes_History` —
  `[Skip]`-marked in source.
