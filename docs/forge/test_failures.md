# Forge.Api.Tests — Remaining Failures

> Snapshot after the test-cleanup pass (`docs/forge/fix-tests.md`) landed.
> 308 → 8 failing. The 8 below are real product / wiring issues — not
> stale assertions. Each needs targeted investigation.

## Summary

| | Count |
|---|---|
| Total tests | 905 |
| Passing | ~893 |
| Failing | **8** |
| Skipped (intentional) | 4 |

All 8 failures concentrate in two test classes that exercise real git
worktrees and the Phase 28 story-spawn orchestrator. Other classes
(`Phase14*`, `Phase15*`, `Phase16-Phase23*`, `Phase26*`, `Phase27*`,
`Phase28StorySpawnTests`, `RestartBehaviorTests`, `ProviderE2ETests`,
`AuthAndMeTests`, all endpoint tests) are green.

## The 8 failures

### Phase 28 orchestrator wiring (6)

All six share the same root cause: **the test factory replaces the
orchestrator's three real DI bindings with deterministic stubs in
`ConfigureTestServices`, but the stubs don't satisfy the orchestrator's
contract end-to-end.** The orchestrator then either bails early
("story isn't a WorkItem", "no plan artifacts on main") or completes
without populating the fields the test asserts on.

The factory overrides at `ForgeWebApplicationFactory.cs:154-163`:

```csharp
services.Add(ServiceDescriptor.Singleton<IStoryArtifactStrategy, DeterministicStoryArtifactStrategy>());
services.Add(ServiceDescriptor.Singleton<IStoryGitOps,           InMemoryStoryGitOps>());
services.Add(ServiceDescriptor.Singleton<IStoryProviderOps,      LocalStoryProviderOps>());
```

Production wires `CrewStoryArtifactStrategy` (real LLM via `IAgentRunner`),
`RealStoryGitOps` (real git CLI), `ProviderStoryProviderOps` (real ADO /
GitHub PR creation). The deterministic doubles must produce the same
observable side effects (PlanPrId populated, plan artifacts visible on
main, decomposition → workstreams) that the orchestrator's pipeline
expects in the next phase.

#### 1. `Phase28OrchestratorTests.Spawn_From_ProductStory_Opens_Plan_Pr_And_Carries_Description_And_Points`

> Plan PR id was not populated — orchestrator likely bailed because the
> story isn't a WorkItem.

**Hypothesis**: the orchestrator looks up `WorkItemRow` by story id, the
test's `SeedStoryAsync` only inserts a story row (`product_stories`?), no
matching `work_items` row. Either:
- (a) Add a corresponding `WorkItemRow` in `SeedStoryAsync`, or
- (b) Update the orchestrator to seed the WI when one is missing (matches
  prod behavior since real spawns always start from a WI).

**Where to look**: `StorySpawnOrchestrator.SpawnAsync` — find the
`return early` path that triggers the bail message. Then either fix the
seed or align the orchestrator with the test's narrative.

#### 2. `Phase28OrchestratorTests.Plan_Pr_Merge_Triggers_Decomposition_And_Workstream_Creation`

> Assert.Equal() Failure: Values differ

**Hypothesis**: the test merges the plan PR via the API, expects the
decompose phase to fire and produce N workstreams. The deterministic
`InMemoryStoryGitOps` doesn't actually merge anything to "main" — its
`FileExistsAsync(branch="main", path)` may always return false, so the
decompose gate (which checks `plan artifacts on main`) never opens.

**Where to look**: `InMemoryStoryGitOps.MergeAsync` and
`FileExistsAsync` — confirm the merge updates the in-memory "main" tree
and that the gate's check sees it.

#### 3. `Phase28OrchestratorTests.Decompose_Gate_Blocks_Until_Plan_Artifacts_Land_On_Main`

> Microsoft.EntityFrameworkCore.DbUpdateException : An error occurred
> while saving the entity changes. See the inner exception for details.

**Hypothesis**: the test creates rows with conflicting IDs / FK
violations during seed. Probably tries to insert a workstream / spawn
that overlaps another test's seed within the same fixture-scoped DB.
Inner exception will tell us — needs `--verbosity normal` rerun to
capture it.

#### 4. `Phase28OrchestratorTests.Implement_Phase_Auto_Assigns_Engineer_And_Opens_Workstream_Prs`

> Assert.Equal() Failure: Values differ

Same family as #2. Implement-phase advances after decompose; if
decompose didn't produce real workstreams, implement has nothing to
auto-assign.

#### 5. `Phase28OrchestratorTests.Review_Phase_Posts_Comments_And_Engineer_Resolves_Then_Merges`

> System.InvalidOperationException : Sequence contains no elements

LINQ `.First()` / `.Single()` on an empty collection. Same family —
prerequisite phases didn't produce the rows the review phase queries.

#### 6. `Phase28OrchestratorTests.Dependency_Scheduler_Unblocks_Downstream_When_Upstream_Merges`

> Assert.Equal() Failure: Values differ

`SpawnDependencyScheduler.TickOnceAsync` doesn't unblock the downstream
because the upstream's "merged" state isn't reflected in the in-memory
git ops.

**Recommended order to investigate**: fix #1 (`Spawn_From_ProductStory`)
first — it's the simplest path through the orchestrator. Once that
test's bail-early condition is resolved, #2-#6 likely move with it
since they exercise progressively-deeper points in the same pipeline.

---

### Phase 16 real-git pipeline (2)

#### 7. `Phase16ExecutionTests.Server_Side_Push_Runs_Before_PR_Creation`

> Branch 'ws/WI-PUSH-…' should exist on remote after server-side push.
> ls-remote output: ''

**Hypothesis**: the local-stub agent doesn't make a commit on the
worktree branch — it just produces evaluator output. The worker's push
guard (memory rule: "skip push when 0 commits ahead of base") then
rightly skips. The remote stays empty.

**Where to look**: `CrewQueueWorker` post-loop delivery — find the
`commits ahead == 0 → skip push` branch. Either:
- (a) Update the test to seed a real commit on the worktree branch
  before kicking the worker (most realistic — mirrors what a real
  agent would do), OR
- (b) Update local-stub to make a token commit when the deliverable
  passes (changes prod behavior — riskier).

#### 8. `Phase16ExecutionTests.PairedCrew_EndToEnd_Pickup_Through_PlanAgreement`

> Assert.Contains() Failure: Sub-string not found

**Hypothesis**: the paired-crew flow emits an event whose name or
payload string changed since the test was written. Need the actual
`Filter` predicate from the test to know which substring is being
checked.

**Where to look**: `Phase16ExecutionTests.cs` around the
`PairedCrew_EndToEnd_Pickup_Through_PlanAgreement` body — find the
`Assert.Contains(events, e => …)` and trace what shape it expects vs.
what the worker emits today.

---

## What's *not* on this list (intentionally)

- **`AuthAndMeTests`'s 3 `[Skip]` results** — guarded by the
  `FORGE_AUTH_ENABLED=1` env var; they're skip-by-design when auth
  isn't configured. Not failures.
- **`AgentSoulAndBudgetTests.Reassign_Moves_Agent_And_Writes_History`**
  is `[Skip]`-marked in source. Not a failure.
- **The 4 `[Skip]` tests** total: those 3 + the Reassign one.

## How to reproduce

```powershell
# Build (must be a clone with no Forge.Api.exe locking the bin output —
# either stop the running server first, or run from a separate worktree
# that doesn't share the bin dir):
git worktree add --detach C:\forge\workflow\.worktree\test-validate main
cd C:\forge\workflow\.worktree\test-validate
dotnet build src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj

# Run only the 8 expected failures + a few siblings for context:
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --no-build `
  --filter "FullyQualifiedName~Phase28OrchestratorTests|FullyQualifiedName~Phase16ExecutionTests.Server_Side_Push|FullyQualifiedName~Phase16ExecutionTests.PairedCrew" `
  --logger "trx;LogFileName=remaining.trx"
```

## Owner / status

Status as of 2026-05-04: cleanup pass closed; the eight remaining
failures need targeted product fixes (not test-side cleanup) and are
better tracked individually as work items than as a single sweep.
