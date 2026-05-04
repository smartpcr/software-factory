# Plan — Fix Remaining Forge.Api.Tests Failures

> Snapshot taken after the test-cleanup pass landed
> (`Forge:Background:Disabled` gating + empty test DB + `EnsureDefaultProductAndRepoAsync`
> seed + EventLog provider strip + `CrewQueueWorker` fire-and-forget dispose-guard).
> 905 → 744 runnable, **44 still failing**, plus **161 tests excluded** because
> their helpers crash the host on dispose.

## Where we are

| | Failed | Passed | Skipped | Total runnable |
|---|---|---|---|---|
| Before cleanup pass | 308 | 597 | — | 905 |
| **After cleanup pass** | **44** | **696** | 4 | **744** (161 excluded) |

The 161 excluded tests live in `Phase16ExecutionTests`, `Phase28StorySpawnTests`,
`Phase28OrchestratorTests`, `RestartBehaviorTests`, `ProviderE2ETests`. They all
share one root cause: their helpers spawn agent work via fire-and-forget
`Task.Run`, then `Dispose()` the test factory while those Tasks are still alive.
The Tasks reach for the disposed `IServiceProvider`/`ILogger` and either crash
the test host or leak into the next test's run.

## Fix-budget categories

The 44 measurable failures group into 9 distinct root causes. Effort estimates
are wall-clock for one engineer who already has the repo loaded; the
`fix-time` field assumes my Path-A scaffolding (gating + test seed) stays in
place.

| Category | Count | Type | Fix time | Order |
|---|---|---|---|---|
| **A** | 4 | DI registration miss | 5 min | 1 |
| **B** | 15 | Test seed gap | 1–2 h | 3 |
| **C** | 3 | API contract drift | 30 min | 4 |
| **D** | 1 | Validator regression | 30 min | 5 |
| **F** | 10 | Stale prototype-data assertions | 1 h (delete or rewrite) | 2 |
| **G** | 1 | Idempotency cache logic | 30 min | 6 |
| **H** | 1 | Sandbox setup | 15 min | 7 |
| **I** | 2 | Jitter envelope drift | 15 min | 8 |
| **Z** | 7 | Mixed | 2 h | 9 |
| **Excluded (161)** | — | Fire-and-forget host-crash race | 2–4 h | 10 |

Total budget for the 44 measurable: **~6–8 hours**.
Excluded suites: another **2–4 hours**.

---

## Category A — `WorkItemSyncJob` not in DI (4 tests)

```
Forge.Api.Tests.WorkItemSyncJobExtraTests.Each_Invocation_Appends_SyncRun_Row
Forge.Api.Tests.WorkItemSyncJobExtraTests.On_Failure_Sets_LastSyncStatus_Failed_Without_Touching_LastSyncAt
Forge.Api.Tests.WorkItemSyncJobExtraTests.Tick_Picks_Up_Stale_Running_Repo_Over_19_Minutes
Forge.Api.Tests.WorkItemSyncJobExtraTests.Tick_Skips_Running_Repo_Under_19_Minutes
```

**Root cause**: `Program.cs` registers `WorkItemSyncJob` with the generic
`AddHostedService<T>()` overload. Path A's `if (enableBackground)` gating
turned that into "not registered at all" in test mode — the singleton went
with it.

**Fix**: Already in working tree. Two-line change:

```csharp
// Before:
if (enableBackground) builder.Services.AddHostedService<Forge.Api.Services.WorkItemSyncJob>();

// After:
builder.Services.AddSingleton<Forge.Api.Services.WorkItemSyncJob>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorkItemSyncJob>());
```

**Validation**: re-run `WorkItemSyncJobExtraTests` — all 4 should pass.

---

## Category F — Stale prototype-data assertions (10 tests)

```
CardsEndpointsTests.GetCards_Returns_All_Seeded_Cards               Expected: 23 Actual: 0
PrsEndpointsTests.GetPrs_Returns_All_Seeded                         Expected: 9  Actual: 0
StateEndpointsSmokeTests.GetState_HasExpectedCounts                 Expected: 5  Actual: 0
TimelineEndpointsTests.GetTimeline_Default_Window_Returns_All_Bars  Expected: 8  Actual: 0
TimelineEndpointsTests.GetTimeline_Narrow_Window_Excludes_…         Expected: 4  Actual: 0
WorkItemsLifecycleTests.Hierarchy_Filter_Returns_Children_Of_Parent ["STAGE-X1","STAGE-X2"] vs []
WorkstreamsEndpointsTests.DeleteWorkstream_With_Force_Removes_Row   NoContent vs NotFound
Phase14EvaluatorScoreParserTests.Notes_Are_Truncated_To_800_Chars   Expected: 800 Actual: 1000
Phase14DeliverableLoopTests.Pass_On_First_Iter_Closes_Work_Item…    Expected: "closed" Actual: "active"
Phase23IdempotencyTests.Upsert_Updates_All_ProviderOwned_Fields     Expected: "design" Actual: "dev"
```

**Root cause**: These tests were authored when `Program.cs` ran
`ForgeSeed.EnsureSeededAsync` on every boot, populating 5 epics, 23 cards,
9 PRs, 8 timeline bars, etc. That seed call is now commented out (operators
keep their real data; prototype data was pollution). The assertions still
expect those exact counts.

**Two valid responses**:

1. **Delete the obsolete tests** — `GetCards_Returns_All_Seeded_Cards`,
   `GetPrs_Returns_All_Seeded`, `GetState_HasExpectedCounts`,
   `GetTimeline_Default_Window_Returns_All_Bars`,
   `GetTimeline_Narrow_Window_Excludes_…` are tautological smoke tests that
   only assert the prototype seed is intact. They have no value once the
   seed is gone.

2. **Rewrite each to seed its own data** — for the tests that exercise real
   logic (`Hierarchy_Filter_Returns_Children_Of_Parent`,
   `Notes_Are_Truncated_To_800_Chars`, `Pass_On_First_Iter_Closes_…`,
   `Upsert_Updates_All_ProviderOwned_Fields`,
   `DeleteWorkstream_With_Force_Removes_Row`), insert the rows the test needs
   directly via `ForgeDbContext` in test setup. Don't depend on a global seed.

**Recommendation**: do both. Delete the 5 tautologies; rewrite the 5 with
real assertions. Keeps the suite honest and decouples test data from any
future global-seed change.

**Files**:
- `src/forge/Forge.Api.Tests/CardsEndpointsTests.cs:GetCards_Returns_All_Seeded_Cards`
- `src/forge/Forge.Api.Tests/PrsEndpointsTests.cs:GetPrs_Returns_All_Seeded`
- `src/forge/Forge.Api.Tests/StateEndpointsSmokeTests.cs:GetState_HasExpectedCounts` (and `GetState_FirstEpic_IsMultiTenantAuth`)
- `src/forge/Forge.Api.Tests/TimelineEndpointsTests.cs:*`
- `src/forge/Forge.Api.Tests/WorkItemsLifecycleTests.cs:Hierarchy_Filter_Returns_Children_Of_Parent`
- `src/forge/Forge.Api.Tests/WorkstreamsEndpointsTests.cs:DeleteWorkstream_With_Force_Removes_Row`
- `src/forge/Forge.Api.Tests/Phase14EvaluatorScoreParserTests.cs:Notes_Are_Truncated_To_800_Chars`
- `src/forge/Forge.Api.Tests/Phase14DeliverableLoopTests.cs:Pass_On_First_Iter_Closes_Work_Item_And_Stops`
- `src/forge/Forge.Api.Tests/Phase23IdempotencyTests.cs:Upsert_Updates_All_ProviderOwned_Fields`

---

## Category B — Tests reach for missing reference rows (15 tests)

```
AgentSoulAndBudgetTests.ListAgents_Returns_Seeded_Agents
CardsEndpointsTests.GetCard_ById_Returns_Card                       (and 4 sibling tests)
PrsEndpointsTests.CancelBuild_Returns_202_And_Sets_Build_Queued     (and 3 sibling tests)
StateEndpointsSmokeTests.GetState_FirstEpic_IsMultiTenantAuth
TimelineEndpointsTests.GetTimeline_Bar_Has_Expected_Shape
WorkItemsEndpointsTests.ListWorkItems_Filters_By_Search_Q
WorkstreamsEndpointsTests.DeleteWorkstream_With_Open_Prs_Without_Force_Returns_422
WorkstreamsEndpointsTests.GetWorkstreams_Returns_All_Seeded
```

**Root cause**: Same shape as Category F — these tests assume specific
*entities* (a card with id `#234`, a PR with id `PR #240`, a workstream
named `ws-oidc`) exist in the seed. Empty DB → 404 / `IndexOutOfRange` /
empty collection. Different from F because the test logic itself is
meaningful — only the data setup is wrong.

**Fix pattern**: Each test class adds an `IAsyncLifetime` (xUnit fixture
init) that inserts the rows that test class depends on. Example:

```csharp
public class CardsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>, IAsyncLifetime
{
    private readonly ForgeWebApplicationFactory factory;
    public CardsEndpointsTests(ForgeWebApplicationFactory f) => this.factory = f;

    public async Task InitializeAsync()
    {
        using var scope = this.factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (!await db.Cards.AnyAsync(c => c.Id == "#234"))
        {
            db.Cards.Add(new CardRow { Id = "#234", /* … fields the tests touch */ });
            await db.SaveChangesAsync();
        }
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
```

**Effort**: ~5-10 min per class, 6 classes affected → 30-60 min total.

---

## Category C — Plan endpoint API contract changed (3 tests)

```
Phase13CrewsTests.Plan_On_Sdk_Crew_Returns_501_Stub                  Expected: NotImplemented Actual: OK
Phase14ScheduleGateTests.Plan_Bypasses_Gate_When_ScheduleEnabled_False  Expected: NotImplemented Actual: BadRequest
Phase14ScheduleGateTests.Plan_Override_True_Writes_DispatchAfterHours_History  Expected: NotImplemented Actual: BadRequest
```

**Root cause**: The `POST /api/dispatch/plan` endpoint used to return 501 for
SDK-backed crews ("plan path not yet implemented"). It now returns 200 / 400.
Either the implementation shipped (good — update the assertions) or it
*regressed* and silently accepts requests it shouldn't (bad — fix the
endpoint).

**Triage step**: read `DispatchEndpoints.cs:Plan` (or wherever the route
lives). If the body is empty / TODO, the tests are right and the impl is
the bug. If the body is real, the tests are stale.

**Fix time**: 30 min total — one investigation, one decision, three
assertion updates or one endpoint fix.

---

## Category D — Sync endpoint accepts what it shouldn't (1 test)

```
WorkItemsSyncEndpointTests.Sync_Pulls_Stub_And_Reports_Counts        Expected: BadRequest Actual: OK
```

**Root cause**: the test calls `POST /api/work-items/sync/{repoId}` with
something the test thinks should be rejected (probably a missing required
field or a validation failure). The endpoint now accepts it and returns 200.

**Triage**: open the test, read the request body it posts, then read the
endpoint to see why validation no longer trips. Likely either a recent
permissive change to `SyncWorkItemsRequest` validation or the test's
assumption of what's "bad" is now wrong.

**Fix time**: 30 min.

---

## Category G — Idempotency replay broken (1 test)

```
IdempotencyAndAuditTests.Idempotency_Replays_Cached_Response_For_Duplicate_Key
  Assert.True() Failure (Expected: True Actual: False)
```

**Root cause**: the test posts a request twice with the same
`Idempotency-Key` header and expects the second response to be a cached
replay of the first (same body). It isn't.

**Triage**: read the idempotency middleware (search `IdempotencyHeader`).
Likely either (a) cache TTL regressed to 0 / immediate expiry, or (b) the
cache key no longer includes route+method, so the second hit misses.

**Fix time**: 30 min.

---

## Category H — Sandbox refuses missing worktree (1 test)

```
AgentRuntimeTests.Sandbox_Refuses_Over_Token_Budget
  Forge.Api.Agents.AgentSandboxViolationException : WorktreePath is required for tool sessions.
```

**Root cause**: A previous code change made `WorktreePath` mandatory for
agent tool sessions (per memory rule
`feedback_worktree_path_meaning.md`). The test wasn't updated and passes
empty / null.

**Fix**: Update the test to pass a real worktree path (e.g.
`Path.Combine(Path.GetTempPath(), $"sandbox-{Guid.NewGuid():N}")`) and
clean it up in finally.

**Fix time**: 15 min.

---

## Category I — Jitter envelope drift (2 tests, possibly flaky)

```
ScheduleJitterTests.Hundred_Ticks_Fall_Within_Plus_Minus_60s_Envelope
  Range: (1140 - 1260) Actual: 7217.6
WorkItemSyncJobTests.NextJitteredInterval_Falls_Within_Envelope
  Range: (1140 - 1260) Actual: 7245.6
```

**Root cause**: Both tests assert that 100 jittered ticks fall within a
±60s envelope around a 1200s base interval (`1140-1260` ms). The actual
average is ~7200 — exactly **6×** the expected. Either:
1. The base interval changed from 1200s to 7200s (config / constant
   drift), and the envelope assertion wasn't updated.
2. The test calculates the wrong unit (ms vs s mismatch).

**Triage**: read both tests + the production constant. If the constant
moved, update both assertions. If the test math is wrong, fix the test.

**Fix time**: 15 min total (same root cause for both).

---

## Category Z — Mixed (7 tests)

```
AgentReadyAuditExtraTests.SyncJob_Writes_ForgeActions_Audit_Row_For_AgentReady_Match
  System.NotSupportedException : SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY
```

This one matches the existing memory rule
`feedback_sqlite_no_order_by_datetimeoffset.md` — there's a server-side
`OrderBy(x => x.At)` somewhere in `WorkItemSyncJob` that needs to either
sort by `Id` or materialize before sorting.

```
Phase26BuildTestGateTests.E2E_ValidateBuild_Catches_Failing_Test
Phase26BuildTestGateTests.ValidateBuild_Runs_Restore_Build_Test_And_Returns_Steps
Phase26BuildTestGateTests.ValidateBuild_Stops_On_First_Failure
  System.TimeoutException : Validation run … did not complete within 120s
```

These shell out real `dotnet restore` + `dotnet build` + `dotnet test` and
hit a 120s wall clock. On the dev box, restore alone can take ≥120s on a
cold NuGet cache. Either bump the test timeout to 300s, or pre-warm
caches in the test class fixture, or skip them when `--filter` is used in
CI without the warm cache.

```
ProductsEndpointsTests.ProductScope_Header_Filters_Workstreams
WorkItemsEndpointsTests.ListWorkItems_Filters_By_Track_And_Type
WorkItemsEndpointsTests.ListWorkItems_Includes_Cards_Backfill
```

Three more "missing seed data" tests that sneak through Category B's
filter — they don't assert against specific named rows, they assert
against shape (`expected ≥20 backfilled, got 0`, `Collection was empty`).
Same fix pattern as Category B.

**Fix time**: 2 hours total — most time goes into the SQLite ordering
fix (need to find the offending query) and decision on the build-timeout
tests.

---

## Excluded suites (161 tests) — host-crash race

```
Phase16ExecutionTests              33 tests
Phase28StorySpawnTests             54 tests (approx; pre-fix count was 42, but suite has grown)
Phase28OrchestratorTests           12 tests
RestartBehaviorTests               42 tests
ProviderE2ETests                   ~20 tests
```

**Root cause** (one cause, five symptoms): each test in these classes
boots a `ForgeWebApplicationFactory`, calls `worker.RunOnceAsync(default)`
(which fire-and-forgets a `Task.Run` running `RunAgentAsync`), waits some
fixed window (5–15s), then disposes the factory. If the agent is still
running when dispose happens — the local-stub backend can take 20+ s on a
loaded box — the agent reaches into a disposed `IServiceProvider` and
throws `ObjectDisposedException` from a context that crashes the test
host process. (`UnobservedTaskException`'s `SetObserved()` doesn't catch
synchronous logger throws inside the Task.)

**Fix pattern**: Each test class with this pattern has a helper context
record (`PipelineTestContext` in Phase26, similar in Phase28). The
helper's `Dispose()` should:

1. Set the worker's `shuttingDown` flag (already public on
   `CrewQueueWorker`).
2. Wait briefly (`await Task.Delay(100)`) for in-flight Task.Runs to drain.
3. Then dispose the factory.

OR — extract a `TestRunContext.WaitForAgentDrainAsync(timeout)` helper
that polls `task_queue` until the row is `completed` / `cancelled` before
disposing.

OR — the fix I already made in `CrewQueueWorker.cs` (wrap the fire-and-forget
in `try { … } catch (ObjectDisposedException) { }`) needs to also catch
`InvalidOperationException` thrown by EF when DbContext is disposed. The
guard catches some of it but not all.

**Effort**: 30 min per class × 5 classes = 2.5 hours, OR a shared
`AwaitAgentDrainAsync` helper added to the test factory (1.5 hours, fixes
all five). Recommend the helper.

---

## Recommended order

1. **A** (5 min) — already fixed, just validate.
2. **F** (1 h) — biggest single bucket, mostly delete-or-rewrite. Decisions
   are obvious.
3. **B** (1–2 h) — pattern repeats, builds muscle memory.
4. **C / D / G / H / I** (~2 h together) — small fixes, small files.
5. **Z** (2 h) — heterogenous, save for last.
6. **Excluded suites** (2–4 h) — drain-helper refactor + per-class
   adoption.

After step 5: full suite should report **~5 failed / ~739 passed of
744 runnable** (99.3%).
After step 6: **~5 failed / ~900 passed of 905** (99.4%).

## What this plan does **not** cover

- **Test parallelism**. With factory-per-test and the agent-drain race,
  parallelism above 1 is risky. xUnit defaults to one-thread-per-class;
  if the suite parallelism is later raised, expect new races. Out of
  scope here.
- **Coverage rewrite**. Several Category F tests would be more valuable
  rewritten as smaller unit tests of the underlying methods rather than
  full-host integration tests. That's a separate refactor.
- **Test-DB strategy**. The current "empty per-test temp file" pattern
  is fast (1 min for 744 tests) but means every test class re-runs all
  migrations + seed-crew. A single shared DB with explicit transactions
  per test would be faster and reduce the 100k+ leaked
  `forge-test-*.db` files in `%TEMP%`. Out of scope here; flag for a
  follow-up.
