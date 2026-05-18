# Forge — E2E refactor: trailing per-stage QA workstreams

> **Source of truth.** This document refines `docs/forge_implementation.md` Phase 28
> (story-driven spawn). Where they disagree, this doc wins for the parts it
> covers — the orchestrator's QA/E2E flow, the decomposition shape, the swarm DAG
> rendering, and the per-spawn `e2e-scenarios.md` contract. The rest of
> `forge_implementation.md` is unchanged.

---

## 1. What changes — at a glance

**Today.** `StorySpawnOrchestrator.DriveQaAsync` runs as a single "Phase 5"
*after* every implementation workstream merges. It emits one `.feature` file per
workstream into `tests/Features/{storyId}/`, opens one QA PR, then opens one
`ws/{storyId}/{slug}-steps` PR per workstream. In the swarm DAG this surfaces as
the rightmost column of QA stages disconnected from their implementation
siblings (the column circled in the operator screenshot).

**After.** QA folds into Phase 3 (Implement) as **trailing workstreams on each
stage**:

- For every implementation workstream `S` whose source Stage in
  `implementation-plan.md` carries `### Test Scenarios`, decomposition emits
  one extra `qa-e2e` workstream `S-e2e` with `deps = [S]` (i.e. depends on the
  implementation workstream).
- `S-e2e` owns the Gherkin `.feature` file **and** the step-definition file
  for that stage, **and** runs the e2e suite for that stage's scenarios.
- The standalone "Phase 5: QA" goes away. `StorySpawn.Phase` collapses from
  `plan → decompose → implement → review → qa → done` to
  `plan → decompose → implement → review → done`. `qa` remains a recognised
  phase value for back-compat with persisted rows but the orchestrator no
  longer enters it.
- The "QA branch" (`story/{storyId}/qa`) and `StorySpawnRow.QaBranch` /
  `QaPrId` columns are retained on the row for historical spawns but the
  orchestrator stops writing to them; new spawns leave them null.

**Implementation-plan.md is unchanged** — each Stage still owns its
`### Test Scenarios` checklist. Decomposition reads those scenarios as the
acceptance criteria for the trailing `qa-e2e` workstream, the same way it
already populates `DecompositionWorkstream.AcceptanceCriteria` for the impl
workstream today (`CrewStoryArtifactStrategy.cs:1997`).

---

## 2. The DAG after the refactor

For a story with three stages, two of which have test scenarios:

```
        plan/decompose
              │
              ▼
   ┌──────────┴──────────┬──────────────────────────┐
   ▼                     ▼                          ▼
┌──────┐            ┌──────┐                   ┌──────┐
│ S1   │            │ S2   │                   │ S3   │   (impl workstream
│ impl │            │ impl │                   │ impl │    per stage)
└──┬───┘            └──┬───┘                   └──┬───┘
   ▼                   ▼                          │
┌──────┐            ┌──────┐                      │
│ S1-  │            │ S2-  │     (S3 has no       │
│ e2e  │            │ e2e  │      test            │
└──────┘            └──────┘      scenarios)      │
                                                  ▼
                                            (no trailing
                                             qa workstream)
```

Stage-level dependencies that already exist on the **impl** side (e.g.
`S3 deps on S2`) automatically apply to the qa-e2e workstream because qa-e2e
deps on its impl sibling. The orchestrator's existing dep scheduler
(`SpawnDependencyScheduler` + `StorySpawnOrchestrator.DriveImplementAsync`)
handles unblocking unchanged — the qa-e2e workstream is just another
workstream row with a `dependsOn` edge to its impl sibling.

**Cross-stage e2e** (a scenario that needs Stage A *and* Stage B implemented
to run) is supported by extending `Deps` on the `S-e2e` workstream to include
both impl workstreams. The decomposition path (§4) describes how this maps
back to the plan.

---

## 3. Updated lifecycle

```
┌────────────────────────────────────────────────────────────────────┐
│ STORY                                                              │
│                                                                    │
│ Phase 1 — PLAN         (Architect, ≤4 docs incl. e2e-scenarios.md) │
│ Phase 2 — DECOMPOSE    (Architect, emits IMPL + trailing E2E rows) │
│ Phase 3 — IMPLEMENT    (Engineer per IMPL, Engineer-QA per E2E)    │
│ Phase 4 — REVIEW       (Review)                                    │
│ Phase 5 — DONE                                                     │
└────────────────────────────────────────────────────────────────────┘
```

- Phase 3 ends when every workstream (impl + e2e) is `complete`.
- Phase 4 (REVIEW) and Phase 3 already interleave in the current
  orchestrator switch (`StorySpawnOrchestrator.cs:271-287`); that loop is
  unchanged. The Qa case is removed from the switch.
- A spawn whose plan has zero test scenarios across all stages produces zero
  qa-e2e workstreams and goes straight from impl/review to done — matches
  today's behaviour for stories without `### Test Scenarios`.

---

## 4. Decomposition: one impl + (optional) one qa-e2e per stage

`CrewStoryArtifactStrategy.ProduceDecompositionAsync` today returns one
`DecompositionWorkstream` per `Stage` parsed from `implementation-plan.md`
(line 1982). The refactor extends that loop:

```csharp
foreach (var s in stages)
{
    var implWs = new DecompositionWorkstream(
        Title: s.Name,
        ...
        AcceptanceCriteria: s.TestScenarios.ToList(),
        PlanAnchor: s.Anchor,
        Category: "impl");                            // NEW field
    yield return implWs;

    if (s.TestScenarios.Count == 0) continue;

    yield return new DecompositionWorkstream(
        Title: $"{s.Name} — E2E",
        Description: $"Gherkin + step-defs + run e2e for {s.Anchor}",
        AcceptanceCriteria: s.TestScenarios.ToList(),
        Deps: new[] { implWs.Title },                 // trailing dep
        Deliverables: Array.Empty<string>(),
        Timeline: "",
        EstimatedEffort: "",
        PlanAnchor: $"{s.Anchor}/e2e",                // child anchor
        PhaseAnchor: implWs.PhaseAnchor,
        Category: "qa-e2e");                          // NEW field
}
```

**Cross-stage e2e.** When a stage's `### Test Scenarios` block declares
`### Cross-Stage Dependencies` (new optional H3 under a stage's E2E section)
listing additional stage anchors, those anchors are added to the qa-e2e
workstream's `Deps`. The plan parser change is small (§7) and the linter
already enforces the "anchor form, not titles" rule, so cross-stage e2e
inherits the existing cycle detector.

---

## 5. Orchestrator changes

### 5.1 `StorySpawnOrchestrator`

- **Remove** `DriveQaAsync` from the phase switch
  (`StorySpawnOrchestrator.cs:279-283`). The `Qa` case still calls
  `DriveImplementAsync` + `DriveReviewAsync` for back-compat with mid-flight
  spawns (so a spawn that paused in the legacy `qa` phase resumes; it just
  stops opening NEW qa-batch PRs).
- **Add** a `Category` discriminator on `WorkstreamRow` so
  `DriveImplementAsync` knows whether to invoke
  `IStoryArtifactStrategy.ProduceWorkstreamChangesAsync` (impl) or the new
  `ProduceStageE2eAsync` (qa-e2e). Same paired-agent loop, different
  prompt + different writable subtree.
- **Done-gate** simplifies: a spawn reaches `done` when every workstream
  (both categories) is `Status='complete'`. The "QA PR merged" predicate
  in `28.8.1` is dropped — there is no story-level QA PR anymore.

### 5.2 `IStoryArtifactStrategy`

- **Add** `Task<IReadOnlyDictionary<string,string>> ProduceStageE2eAsync(
    StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct)`
  returning the dict of `{relPath → content}` for that stage's e2e
  contribution: one `.feature` file under
  `tests/Features/{storyId}/{phaseSlug}/{stageSlug}.feature` and one
  step-definition file under
  `tests/Steps/{storyId}/{phaseSlug}/{stageSlug}Steps.cs`. The strategy is
  also responsible for running the e2e suite for that stage and refusing
  to return until it passes — same gate semantics as the impl
  `dotnet build` gate today (`CrewStoryArtifactStrategy.cs:2378`).
- **Deprecate** `ProduceQaFeatureFilesAsync` + `ProduceStepDefinitionsAsync`.
  Keep the methods on the interface for one release so resumed legacy
  spawns can still drive their existing `qa` phase, but mark them
  `[Obsolete]` and route new spawns through `ProduceStageE2eAsync`.

### 5.3 `SpawnDependencyScheduler`

No change. The scheduler already treats every `WorkstreamRow` uniformly and
unblocks on `WorkstreamDepRow` merge state.

---

## 6. UI changes (swarm DAG view)

- **Stage card** (`SwarmMasterPanel.tsx` phase board) renders the impl
  workstream and its trailing qa-e2e workstream as **two stacked rows** in
  the same column instead of two separate columns. The e2e row uses
  `--sc-t-qa` (green) accent; the impl row keeps `--sc-t-eng` (cyan).
- **DAG link rendering** (`SwarmStoryView.tsx`) collapses the all-to-all
  rightmost-column edges into per-stage downward edges. Cross-stage e2e
  deps still render as long edges across columns; the existing edge
  router handles that case (it already draws `S3 → S5` impl deps today).
- **Phase badge** on the story card stops showing `qa` for new spawns.
  Legacy spawns that paused in `qa` continue to display the old badge.

---

## 7. Plan-parser changes (`PlanParser.cs`)

- **Optional**: parse an `### Cross-Stage Dependencies` H3 *inside* a
  stage's body when the operator wants the e2e to wait on additional
  upstream stages. Stored as a new field
  `Stage.CrossStageE2eDeps : IReadOnlyList<string>` (anchor refs).
- **Validator**: every Stage with `### Test Scenarios` must declare at
  least one scenario as a checkbox — the existing "at least 2 test
  scenarios per stage" rule (`CrewStoryArtifactStrategy.cs:156`) is
  unchanged; it now also gates whether a qa-e2e workstream is emitted.

---

## 8. `e2e-scenarios.md` amendments — setup-aware authoring

`e2e-scenarios.md` is one of the four plan-phase docs the Architect crew
produces. Today its `BuildPlanDocSpecs` brief is one line
(`CrewStoryArtifactStrategy.cs:161-163`). The refactor expands it to require
a `## Setup` section per phase whose scenarios need infrastructure that the
default CI runner cannot satisfy.

### 8.1 Required structure (new)

```markdown
# {Story title} — E2E scenarios

## Phase 1: {phase name}

### Setup
- **Local**: docker-compose file at `tests/e2e/{phase-slug}/docker-compose.yml`
  spinning up Redis + Postgres + the service under test. `docker compose up -d`
  before `dotnet test`.
- **CI runner**: `ubuntu-latest` (containers supported on GH-hosted runners).
- **Secrets**: none required (everything runs against the compose stack).

### Scenarios
- **Scenario:** ... Given ... When ... Then ...

## Phase 2: {phase name}

### Setup
- **Local**: requires a Windows Server Failover Cluster (WSFC) with CAU
  (Cluster-Aware Updating) enabled. Not reproducible on a single workstation.
- **CI runner**: ADO self-hosted agent in the **`forge-lab-wsfc`** pool
  (3-node cluster pre-provisioned in the lab). Mark the YAML job with
  `pool: forge-lab-wsfc`. GitHub Actions equivalent uses the
  `self-hosted, wsfc-lab` labels.
- **Lab connection**:
  - **ADO**: stored in `forge-lab-kv` Azure Key Vault, surfaced via the
    `AzureKeyVault@2` task as variables `LAB_CLUSTER_FQDN`,
    `LAB_ADMIN_USER`, `LAB_ADMIN_PASSWORD`. Pipeline must reference the
    variable group `forge-lab-vg`.
  - **GitHub**: stored as repository secrets `LAB_CLUSTER_FQDN`,
    `LAB_ADMIN_USER`, `LAB_ADMIN_PASSWORD` (environment-scoped to
    `lab-wsfc`, requiring environment review for non-default branches).
- **Pre-test bootstrap**: `tests/e2e/{phase-slug}/bootstrap.ps1` runs on
  the lab cluster head node to reset CAU state before each test run; the
  pipeline must call it as a pre-step.

### Scenarios
- **Scenario:** Cluster node drains during CAU run — Given ... When ... Then ...
```

### 8.2 Setup-type taxonomy (closed set)

The Architect evaluator refuses pass when a phase's `### Setup` block omits
all four sub-bullets or when its `CI runner` choice is inconsistent with
its `Secrets` block. The four recognised setup types:

| Type            | Local prereq                         | CI runner                          | Secrets store             |
|-----------------|--------------------------------------|------------------------------------|---------------------------|
| `inline`        | none — runs against process under test | any hosted                         | none                      |
| `compose`       | `docker compose up`                  | hosted (linux or windows w/ Docker)| none                      |
| `lab-wsfc`      | physical/virtual cluster             | self-hosted `forge-lab-wsfc` pool   | KeyVault `forge-lab-kv` / GH env `lab-wsfc` |
| `lab-bare-metal`| hardware-specific lab (e.g. drivers, NICs) | self-hosted `forge-lab-hw` pool | KeyVault `forge-lab-kv` / GH env `lab-hw` |

`lab-*` types are also surfaced to the qa-e2e workstream prompt so the
Engineer-QA crew knows to emit a pipeline YAML stub alongside the
`.feature` and step-def files (§5.2 — `ProduceStageE2eAsync`'s output dict
includes `tests/e2e/{phase-slug}/azure-pipeline.yml` and
`.github/workflows/e2e-{phase-slug}.yml` when the phase's setup is
`lab-*` or `compose`).

### 8.3 Connection-string convention (CI)

Connection strings to the lab environment are **never** hard-coded in
`.feature` files or step definitions. Instead:

- `tests/e2e/{phase-slug}/appsettings.e2e.json` declares the variable
  *names* (`LAB_CLUSTER_FQDN`, etc.) with empty values.
- The step-def base class reads them via
  `Environment.GetEnvironmentVariable("LAB_CLUSTER_FQDN")` and throws a
  clear `SetupSkippedException` when missing — locally this surfaces as
  a yellow "skipped (no lab)" badge; on CI it must be a hard failure.
- The CI YAML (generated by the qa-e2e workstream) wires the secrets into
  the env block of the test step.

### 8.4 Architect prompt update

The `e2e-scenarios` entry in `BuildPlanDocSpecs` becomes:

```csharp
["e2e-scenarios"] = ("e2e-scenarios.md",
    """
    Gherkin-style feature scenarios (Feature / Scenario / Given-When-Then)
    covering happy paths and edge cases QA will run, GROUPED BY PHASE.

    Each Phase H1 (`# Phase N: ...`) MUST be followed by:

      ### Setup
      - **Local**: <how a developer runs this on their workstation>
      - **CI runner**: <runner pool / labels / hosted image>
      - **Secrets**: <none | KeyVault path | GH environment + secret names>
      - **Pre-test bootstrap**: <command(s), or "none">

      ### Scenarios
      - **Scenario:** ...

    Setup type MUST be one of: `inline`, `compose`, `lab-wsfc`,
    `lab-bare-metal`. State the type explicitly as the first sub-bullet of
    `### Setup` (e.g. `- **Type**: lab-wsfc`).

    When a phase needs `docker-compose`, name the compose file path at
    `tests/e2e/{phase-slug}/docker-compose.yml` and list the services it
    starts.

    When a phase needs a lab environment (Windows Server Failover Cluster,
    CAU, hardware-specific NIC, etc.), state:
      - the ADO agent pool name (default `forge-lab-wsfc` for clusters,
        `forge-lab-hw` for hardware),
      - the GitHub self-hosted runner labels,
      - the Azure Key Vault name and variable group (ADO),
      - the GitHub environment name and secret names.

    Hard requirements (evaluator refuses pass otherwise):
    - Every Phase H1 has a `### Setup` H3 directly before `### Scenarios`.
    - Setup `Type` is one of the closed set above.
    - When Type is `lab-*`, the Secrets bullet names BOTH a KeyVault entry
      AND a GitHub environment (Forge supports both providers).
    - Connection strings are referenced as env-var names, never literal
      hostnames or credentials.
    """,
    $"{docDir}/e2e-scenarios.md"),
```

---

## 9. Data-model changes

### 9.1 `WorkstreamRow` (add)

```
Category  TEXT NOT NULL DEFAULT 'impl'   -- 'impl' | 'qa-e2e'
```

Backfill: every existing row gets `'impl'`. A small migration
(`PhaseN+1Migration.cs` — pick the next free phase number) sets the column
and writes the index.

### 9.2 `DecompositionWorkstream` (add)

```csharp
public sealed record DecompositionWorkstream(
    ...,
    string Category = "impl");   // NEW, last param (additive default)
```

Persisted into `WorkstreamRow.Category` by
`StorySpawnOrchestrator.PersistDecompositionAsync`.

### 9.3 `StorySpawnRow` (deprecate, do not drop)

`QaBranch` and `QaPrId` stay on the row for back-compat with already-merged
spawns. New spawns leave them null. A future cleanup phase
(`forge_v2.md`) can drop the columns once no row in production still
references them.

---

## 10. Phased implementation plan

Each phase ends with all unit + integration + e2e tests passing for the
phase's scope. The work follows the existing Forge convention: one PR per
stage, each PR ≤ 20 file changes.

### Phase A — Data model + decomposition (≤ 5 stages)

**A.1** Add `WorkstreamRow.Category` column + migration. Backfill `'impl'`
for every existing row. Index on `(StorySpawnId, Category)` to keep the
DAG-render fast on stories with many stages.

**A.2** Add `DecompositionWorkstream.Category` field (additive,
default `'impl'`). Persist into `WorkstreamRow.Category` in
`StorySpawnOrchestrator.PersistDecompositionAsync`.

**A.3** Extend `CrewStoryArtifactStrategy.ProduceDecompositionAsync` to
emit the trailing `qa-e2e` workstream after each stage with
`### Test Scenarios`. Set `PlanAnchor = $"{stage.Anchor}/e2e"` and
`Deps = [implWorkstream.Anchor]`. Cross-stage deps from
`### Cross-Stage Dependencies` (parser change in A.4) are appended.

**A.4** `PlanParser` — parse optional `### Cross-Stage Dependencies` H3
inside each stage; surface as `Stage.CrossStageE2eDeps`. Linter rejects
unresolved anchors (re-uses `DecompositionDepsResolver`).

**A.5** `Phase28StorySpawnTests` + `Phase28OrchestratorTests` — add
fixtures for: (a) stage with no test scenarios emits no qa-e2e workstream,
(b) stage with test scenarios emits exactly one qa-e2e workstream with
the right deps, (c) cross-stage deps round-trip through decomposition.

**Phase A gate**: existing 51 tests still pass + new tests pass; running
the orchestrator end-to-end on the deterministic strategy produces the
same number of PRs per story as before (impl × N) **plus** one e2e PR per
stage that has test scenarios.

### Phase B — Orchestrator: route qa-e2e workstreams through impl driver (≤ 5 stages)

**B.1** Add `IStoryArtifactStrategy.ProduceStageE2eAsync` — returns the
`{relPath → content}` dict for the stage's `.feature` + step-def files
(under `tests/Features/{storyId}/{phaseSlug}/` and
`tests/Steps/{storyId}/{phaseSlug}/`). Two concrete bindings:

- `DeterministicStoryArtifactStrategy` — emits a passing scenario by
  construction (same shape as today's deterministic QA path).
- `CrewStoryArtifactStrategy` — invokes the QA crew with a prompt that
  bundles the stage's `### Test Scenarios` bullets + the impl
  workstream's diff so the e2e test exercises what was actually built.

**B.2** `DriveImplementAsync` branches on `workstream.Category`:
`'impl'` → existing path; `'qa-e2e'` → `ProduceStageE2eAsync`, commit
to the same `ws/{storyId}/{slug}` branch convention (e.g.
`ws/{storyId}/{stageSlug}-e2e`), open PR with `[e2e]` prefix in the
title.

**B.3** Remove the `Qa` case from `StorySpawnOrchestrator`'s phase switch
for spawns whose `Phase` is `'review'` and whose all-workstreams-merged
check passes — promote to `Done` directly. Keep the `Qa` case alive but
no-op for legacy spawns paused in the old phase (it just calls
`DriveImplementAsync` + `DriveReviewAsync` to drain any in-flight rows).

**B.4** Done-gate (`StorySpawnOrchestrator` completion check) drops the
"QA PR merged" predicate. Replaces it with: every workstream of every
category is `Status='complete'`.

**B.5** Tests — add an end-to-end orchestrator test that drives a spawn
with two stages (one with test scenarios, one without) from `plan` to
`done`, asserting the final PR count is 4 (plan + impl-A + impl-B +
e2e-A) and `Phase` never enters `qa`. Existing back-compat test: a spawn
seeded with `Phase='qa'` still drains correctly.

**Phase B gate**: 60+ Phase 28 tests pass; deterministic strategy produces
exactly one e2e PR per stage with `### Test Scenarios`; ADO work-item
closure happens after the last e2e PR merges.

### Phase C — `e2e-scenarios.md` setup contract (≤ 4 stages)

**C.1** Update `BuildPlanDocSpecs["e2e-scenarios"]` Brief (`CrewStoryArtifactStrategy.cs:161`)
with the §8.4 expanded prompt.

**C.2** Add an evaluator-side linter for `e2e-scenarios.md` that refuses
pass when any phase H1 is missing `### Setup`, when the setup `Type` is
not in the closed set, or when a `lab-*` setup lacks both KeyVault and
GitHub-environment secret references.

**C.3** Wire the parsed setup info into the qa-e2e workstream input —
`ProduceStageE2eAsync` reads its phase's setup block from the merged
`e2e-scenarios.md` and includes it in the QA-crew prompt, so the
generated `.feature` / step-defs / pipeline YAML stubs match the
declared setup type.

**C.4** Tests — fixture stories with each of the four setup types:
assert that the qa-e2e workstream output contains the right pipeline
file (`azure-pipeline.yml` + `.github/workflows/*.yml`) when setup is
`compose` or `lab-*`, and contains *only* the `.feature` + step-defs
when setup is `inline`.

**Phase C gate**: a smoke story with two phases (`inline` and `lab-wsfc`)
spawns end-to-end and produces:

- `tests/Features/{id}/phase-1/{stage}.feature`
- `tests/Steps/{id}/phase-1/{stage}Steps.cs`
- `tests/Features/{id}/phase-2/{stage}.feature`
- `tests/Steps/{id}/phase-2/{stage}Steps.cs`
- `tests/e2e/phase-2/azure-pipeline.yml`
- `.github/workflows/e2e-phase-2.yml`

and the generated YAMLs reference `LAB_CLUSTER_FQDN` /
`LAB_ADMIN_USER` / `LAB_ADMIN_PASSWORD` from the right secret store.

### Phase D — Swarm-control UI (≤ 4 stages)

**D.1** `SwarmMasterPanel.tsx` — render each stage as a stacked
impl-over-e2e pair in the phase board (mode `impl`). When the stage has
no e2e workstream, render impl only.

**D.2** `SwarmStoryView.tsx` — DAG edges: per-stage downward edge from
impl to e2e; cross-stage e2e deps render as long edges. The rightmost
"all e2e in one column" rendering is removed.

**D.3** Story-card phase badge — drop `qa` from the badge for new
spawns; keep it visible for legacy spawns whose `Phase` is still `qa`.

**D.4** Vitest + Playwright UI tests — covered stages: stacked render,
edge router for cross-stage e2e deps, badge for legacy vs new spawn.

**Phase D gate**: visual diff against the operator screenshot shows the
right-column QA cluster gone, e2e rows stacked under each impl row,
DAG edges still routing correctly for a real story (5+ stages, mixed
inline/lab setups).

### Phase E — Cleanup + docs (≤ 3 stages)

**E.1** Mark `ProduceQaFeatureFilesAsync` + `ProduceStepDefinitionsAsync`
`[Obsolete]` with a pointer to `ProduceStageE2eAsync`. Keep them
implemented for one release.

**E.2** Update `docs/forge_implementation.md` Phase 28 sections 28.7,
28.8, 28.9, and the lifecycle ASCII to match this doc. Cross-link
back here from the `## Phase 28` header.

**E.3** Add the §8 setup contract to `docs/forge/workstream.md` and
update the `agents/templates/qa-system.md` template to include the
setup-type-aware QA prompt.

**Phase E gate**: docs render correctly; the only references to "Phase 5
QA" in the repo are inside this doc (explaining the migration) and inside
legacy migration code paths.

---

## 11. Migration / rollout notes

- **In-flight spawns.** Any spawn currently in `Phase='qa'` continues to
  drain via the legacy `DriveQaAsync` path (kept alive but not extended).
  Operators can let them complete naturally or manually advance to `done`
  via the existing `PATCH /spawn/phase` endpoint once their last
  step-def PR merges.
- **Brand-new spawns** never enter the `qa` phase — they advance
  `review → done` directly once every workstream (impl + qa-e2e) is
  `complete`.
- **Persisted decompositions.** Existing `DecompositionJson` rows don't
  contain `Category`; they read as `'impl'` (the default). The
  reconciler (`PlanReconciler`) re-runs decomposition on the next
  orchestrator tick after the plan PR merges and emits the new shape.
  Already-merged plan PRs are not retroactively re-decomposed — the
  spawn keeps its original impl-only workstream set.

---

## 12. Out-of-scope (defer to forge_v2 or a follow-up)

- **Cross-story e2e** — e.g. a story whose scenarios depend on infra
  another story is building. Stories remain independent.
- **Dynamic lab provisioning** — Forge does not create lab clusters on
  demand. The `lab-*` setup types assume the cluster already exists in
  the pool.
- **Test-result ingestion back into the story card** — pass/fail/warn
  badges on the qa-e2e workstream are surfaced via the existing build
  sync (`Phase 27`), not via a new e2e-specific store.
- **Dropping `QaBranch` / `QaPrId` from `StorySpawnRow`** — these
  columns stay until no production row references them; the column drop
  is a `forge_v2.md` candidate.
