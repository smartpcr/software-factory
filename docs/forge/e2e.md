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

## 10. Implementation

> **Plan shape.** This section follows the `implementation-plan.md` contract
> (Phase → Stage → Step) defined in `CrewStoryArtifactStrategy.BuildPlanDocSpecs`:
> a Phase is an independent feature, a Stage is the unit of PR (≤ 20 file
> changes), each Implementation Step is one focused commit. Every Stage lists
> explicit **Deliverables** (the concrete artifacts the PR ships) and
> **Test Scenarios** marked `[unit]` or `[integration]` so both layers are
> covered by construction.
>
> Test conventions:
> - **`[unit]`** — xUnit in `Forge.Api.Tests`, no SignalR, no real git, no
>   real provider. Uses `InMemoryStoryGitOps` + `LocalStoryProviderOps` +
>   `DeterministicStoryArtifactStrategy` already in place for Phase 28.
> - **`[integration]`** — full orchestrator drive against a temp SQLite DB
>   in `Forge.Api.Tests/Integration/` collection `Phase28E2eRefactor`,
>   shares the existing `[Collection("Phase28")]` annotation so the gate
>   suite and the orchestrator suite stay isolated.
> - **`[ui-unit]`** — vitest in `src/forge/Forge.UI/src/**.spec.ts`.
> - **`[ui-e2e]`** — Playwright in `src/forge/Forge.UI/e2e/`.
>
> Tests added per Stage MUST be authored before the Stage's PR is approved;
> the Stage's PR is the unit that ships the implementation AND its tests.
> Acceptance for the whole refactor: every Stage's Test Scenarios checkbox
> ticks green AND the Phase 28 gate suites (`Phase28StorySpawnTests`,
> `Phase28OrchestratorTests`) plus the new `Phase28E2eRefactorTests` suite
> all pass.

---

### Phase 1: Data model & schema migration

#### Dependencies
- _none — start phase_

#### Stage 1.1: Add `Category` column to `WorkstreamRow`

**Deliverables:**
- `Category TEXT NOT NULL DEFAULT 'impl'` column on the `workstreams` table.
- New migration file `src/forge/Forge.Api/Persistence/PhaseNMigration.cs`
  (next free phase number) with idempotent `ALTER TABLE ADD COLUMN`.
- `WorkstreamRow.Category` POCO property in `Entities.cs`.
- Composite index `IX_workstreams_StorySpawnId_Category` in
  `ProductDbContext.OnModelCreating`.

**Implementation Steps:**
- [x] Add `Category` property (default `"impl"`) to `WorkstreamRow` in `Persistence/Entities.cs`.
- [x] Create `Phase79Migration.cs` with `ALTER TABLE workstreams ADD COLUMN Category TEXT NOT NULL DEFAULT 'impl'` guarded by a `pragma_table_info` probe.
- [x] Register the migration in `PhaseMigrationCatalog` + `PhaseMigrationRunner.ExecutionOrder` — Phase 79 runs **before** Phase 38 (same EF-projection dependency rule as 43/45/47/49/50, caught by `ExecutionOrder_HonoursPlanDocColumnDependencies` after extending the dep list).
- [x] Add `HasIndex(w => new { w.StorySpawnId, w.Category })` to `DbModelProduct.Configure` with `HasDatabaseName("IX_workstreams_StorySpawnId_Category")` so EF and the raw `CREATE INDEX` produce matching schema fingerprints.
- [x] Verified end-to-end: Forge.Api boots cleanly against the prod DB (PID 42016) with column + index present.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [x] `[unit]` Scenario: Fresh DB migration creates `Category` column with default `'impl'`. — `Stage11WorkstreamCategoryTests.Migration_Adds_Category_Column_To_Pre_Phase79_Schema`
- [x] `[unit]` Scenario: Idempotent re-run — running the migration twice on the same DB does not throw and does not duplicate the column. — `Stage11WorkstreamCategoryTests.Migration_Is_Idempotent` (three consecutive runs verified)
- [x] `[unit]` Scenario: Existing rows backfill — DB pre-seeded with rows lacking `Category` get `'impl'` after migration. — `Stage11WorkstreamCategoryTests.Migration_Backfills_Existing_Rows_To_Impl`
- [x] `[integration]` Scenario: A spawn restart loads `WorkstreamRow.Category` correctly via EF, no `SqliteException`. — `Stage11WorkstreamCategoryTests.WorkstreamRow_Category_RoundTrips_Through_EF` (boot factory + write `'qa-e2e'` + read fresh scope + POCO-default round-trip + `sqlite_master` index probe)
- [x] _Bonus_ `[unit]` Scenario: POCO default matches SQL default. — `Stage11WorkstreamCategoryTests.WorkstreamRow_Default_Category_Is_Impl`

---

#### Stage 1.2: Extend `DecompositionWorkstream` record

**Deliverables:**
- New `Category` parameter (additive default `"impl"`) on the
  `DecompositionWorkstream` record in `IStoryArtifactStrategy.cs`.
- `StorySpawnOrchestrator.PersistDecompositionAsync` writes `Category`
  into the new `WorkstreamRow` it creates.
- `DecompositionYamlSerializer` round-trips the `Category` field
  (omitted in serialized output when `'impl'` to keep YAML diffs quiet).

**Implementation Steps:**
- [x] Add `string Category = "impl"` as the final parameter on `DecompositionWorkstream` (+ a `WorkstreamCategories` static class in `IStoryArtifactStrategy.cs` so the closed set has one source of truth — `Impl`, `QaE2e`, `All`, `IsValid`).
- [x] Update `PersistDecompositionAsync` to copy `dw.Category` → `WorkstreamRow.Category` (with `IsValid` guard that coerces unknown values to `impl` for forward-compat).
- [x] Extend `DecompositionYamlSerializer` to read/write the `category:` key — added `DecompositionYamlRow.Category` plus `CategoryToYaml` / `CategoryFromYaml` helpers so `impl` round-trips as omitted, `qa-e2e` round-trips as `category: qa-e2e`. The existing `OmitNull` handling keeps the YAML diff quiet for impl rows.
- [x] Audit every existing caller — `CrewStoryArtifactStrategy` plan-anchored path (line 1992) still uses named args and inherits the default; the LLM-fallback path (line 2080) now passes `Category: CategoryFromYaml(r.Category)` so architect-emitted YAML drives the value. The `DeterministicStoryArtifactStrategy` test double still uses named args — it inherits the default and compiles unchanged. `DecomposeWorkstream` request DTO + `SubmitDecomposition` endpoint also wired so external POST callers can submit `category` (additive `string? Category = null` field).
- [x] Bonus: `PlanReconciler.BuildArtifactJsonAsync` now writes `category` on regenerated `workstreams.yaml` so the reconciler doesn't strip qa-e2e tagging on later passes (impl rows serialize as `null` and are dropped by the YAML serializer).

#### Dependencies
- phase-data-model-and-schema-migration/stage-add-category-column-to-workstreamrow

#### Test Scenarios
- [x] `[unit]` Scenario: `DecompositionYamlSerializer` round-trips `Category='qa-e2e'`. — `Stage12DecompositionCategoryTests.Yaml_RoundTrips_QaE2e_Category` (serialize → assert `category: qa-e2e` in YAML → deserialize → assert round-trip).
- [x] `[unit]` Scenario: Legacy YAML (no `category:` field) deserializes with `Category='impl'`. — `Stage12DecompositionCategoryTests.Yaml_Deserializes_Legacy_Shape_To_Impl_Category` (also asserts re-serialised impl rows never contain the `category` key).
- [x] `[unit]` Scenario: `PersistDecompositionAsync` writes the record's `Category` to `WorkstreamRow.Category`. — `Stage12DecompositionCategoryTests.PersistDecompositionAsync_Copies_Category_To_WorkstreamRow` (reflection into the private method with a hand-built impl + qa-e2e pair, verifies both rows + the dep edge).
- [x] `[integration]` Scenario: Decompose endpoint + serializer + persist round-trip preserves `Category` for a 4-workstream story. — `Stage12DecompositionCategoryTests.Decompose_Endpoint_RoundTrips_Category_For_FourWorkstream_Story` (4 rows: 2 impl + 2 qa-e2e + 1 legacy-shape coerced; verifies via EF + via `StorySpawnRow.DecompositionJson` blob).

---

### Phase 2: Plan parser cross-stage E2E dependencies

#### Dependencies
- _none — start phase_

#### Stage 2.1: Parse optional `### Cross-Stage Dependencies` under a Stage

**Deliverables:**
- New optional H3 `### Cross-Stage Dependencies` recognised inside any
  Stage body, parsed by `PlanParser` into `Stage.CrossStageE2eDeps`
  (`IReadOnlyList<string>` of anchor refs).
- Validator that rejects unresolved anchors with the existing
  `plan.dep_*` error family.

**Implementation Steps:**
- [x] Add `IReadOnlyList<string>? CrossStageE2eDeps = null` to `PlanParser.CanonicalStage` (additive default — every existing caller compiles unchanged), plus matching `MutableStage.CrossStageE2eDeps` list, `SectionKind.StageCrossStageE2eDeps = 5` (ordered AFTER `StageTestScenarios` so the existing subsection-order rule fires if the H3 lands too early), and the corresponding bullet-handler switch case.
- [x] Extend `V2StageSubHeading` regex to recognise `Cross-Stage Dependencies` as a 4th valid subsection name, and update `ResolveStageSubsection` to map it to the new `SectionKind` (with the "Cross-Stage" check ordered BEFORE the plain "Dependencies" prefix-match so the latter doesn't steal it).
- [x] Pipe the new field through `DecompositionDepsResolver`: extended `ResolvedStageDeps` with an additive `CrossStageE2eDeps` projection; resolved anchors flow into the cycle-detection DAG so cycles introduced via the new H3 fire `plan.dep_cycle` just like ordinary dep cycles; unresolved anchors throw a fatal `plan.dep_unresolved` (new `PlanParser.ErrorCodes.DepUnresolved` constant) with a structured message naming the offending stage + ref.
- [x] Update `BuildPlanDocSpecs["implementation-plan"]` evaluator hard-requirements list to document the optional H3 + the `plan.dep_unresolved` / `plan.dep_cycle` failure modes.
- [x] Bonus: unified the existing cycle throw to use the new `PlanParser.ErrorCodes.DepCycle` constant instead of the bare string literal, so test assertions and the orchestrator's `LastError` projection branch on a single source of truth.
- [x] Stage content-hash now includes `crossStageE2eDeps|…` WHEN PRESENT, so a doc that adds / removes the H3 re-prompts the reconciler the same way a plain dep edit does. v1 + v2-without-the-H3 plans hash unchanged (back-compat).

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [x] `[unit]` Scenario: Stage without the H3 parses with `CrossStageE2eDeps` = empty. — `Stage21CrossStageE2eDepsTests.Parser_Stage_Without_H3_Has_Empty_CrossStageE2eDeps` (3-stage harness, asserts every stage has empty list + existing fields unchanged).
- [x] `[unit]` Scenario: Stage with the H3 listing two anchors resolves both into the field. — `Stage21CrossStageE2eDepsTests.Parser_Stage_With_H3_Captures_Both_Anchors` (parser captures both anchors verbatim; resolver projects to upstream stage titles; also asserts the content hash changes vs the no-H3 baseline so the reconciler re-prompts).
- [x] `[unit]` Scenario: Unresolved anchor in the H3 surfaces as a `plan.dep_unresolved` parse error. — `Stage21CrossStageE2eDepsTests.Parser_Stage_With_Unresolved_H3_Anchor_Throws_DepUnresolved` (fatal throw; message starts with `plan.dep_unresolved` and names both the offending stage and the missing ref).
- [x] `[unit]` Scenario: A cycle introduced via cross-stage deps is detected by `DecompositionDepsResolver` (returns `plan.dep_cycle`). — `Stage21CrossStageE2eDepsTests.Resolver_Cycle_Through_CrossStage_Edge_Fires_DepCycle` (API → UI ordinary edge + UI → API cross-stage edge forms a cycle; throw names both stages).
- [x] `[integration]` Scenario: A full plan with cross-stage e2e deps survives a round-trip through decompose endpoint without losing the anchors. — `Stage21CrossStageE2eDepsTests.Pipeline_RoundTrips_CrossStageE2eDeps_Through_Parser_And_Resolver` (3-phase / 5-stage fixture: ordinary deps + phase-expansion deps + two cross-stage edges; spec-named "decompose endpoint" path is interpreted as `Parse → ResolveAll` because that's the actual pipeline `CrewStoryArtifactStrategy.ProduceDecompositionAsync` invokes before persisting — Stage 3.1 will then plug the resolver's `CrossStageE2eDeps` projection into the trailing qa-e2e workstream's `Deps[]`).

---

### Phase 3: Decomposition emits trailing `qa-e2e` workstreams

#### Dependencies
- phase-data-model-and-schema-migration
- phase-plan-parser-cross-stage-e2e-dependencies

#### Stage 3.1: Extend `ProduceDecompositionAsync` with the qa-e2e tail

**Deliverables:**
- `CrewStoryArtifactStrategy.ProduceDecompositionAsync` emits one
  trailing `DecompositionWorkstream` (`Category='qa-e2e'`) per Stage
  whose `TestScenarios.Count > 0`.
- The trailing workstream has `Title = "{stage.Name} — E2E"`,
  `PlanAnchor = "{stage.Anchor}/e2e"`, `Deps = [stage.Anchor]`, plus
  any anchors from `stage.CrossStageE2eDeps`.
- `PlanReconciler` recognises `Category='qa-e2e'` rows and does not
  delete them on re-decompose.

**Implementation Steps:**
- [x] Extracted the per-stage decomposition projection out of `ProduceDecompositionAsync` into a pure `internal static CrewStoryArtifactStrategy.BuildDecompositionFromPlan(CanonicalPlan, IReadOnlyDictionary<…, ResolvedStageDeps>)` helper so the same logic drives both the strategy AND the reconciler insert path; added `QaE2eAnchorSuffix = "/e2e"` + `QaE2eAnchorFor(string)` as the single source of truth for the synthetic anchor shape.
- [x] `BuildDecompositionFromPlan` emits one trailing `DecompositionWorkstream` (`Category="qa-e2e"`, `Title="{stage.Name} — E2E"`, `PlanAnchor="{stage.Anchor}/e2e"`, `PlanContentHash` reused from the parent stage so any test-scenario / cross-stage-dep edit re-prompts both the impl AND the qa-e2e row) per Stage whose `TestScenarios.Count > 0`; stages with no test scenarios silently emit impl-only.
- [x] `Deps` for the qa-e2e row are `[impl-sibling-title, ...resolver.CrossStageE2eDeps]`, deduped against the impl sibling so an architect's title-form self-ref doesn't double-list. The existing title-keyed wiring in `StorySpawnOrchestrator.PersistDecompositionAsync` / `StoriesEndpoints.SubmitDecomposition` then routes the edges into `WorkstreamDepRow` rows.
- [x] `PlanReconciler` extended with a qa-e2e companion pass (between Pass 1 and Pass 2): insert missing qa-e2e rows for stages with TestScenarios, sync DepsJson / PhaseAnchor / Category on existing qa-e2e rows when the parent stage's content hash changes, and (defensively) backfill `Category="qa-e2e"` on any legacy row that slipped through with the impl default. Pass 2's orphan sweep now treats `{stage.Anchor}/e2e` anchors as "in plan" when their parent stage exists AND still declares test scenarios — so re-runs are idempotent and a qa-e2e is only dropped when its parent stage was removed (or its TestScenarios block went empty).
- [x] _Deferred to Stage 3.2_: `WorkstreamWorktreeSlug.Compute` rewrite. Stage 3.1 ships the synthetic anchor `{stage.Anchor}/e2e` whose `Replace('/', '-')` slug fall-out (e.g. `ws-{storyId}-phase-foo-stage-bar-e2e`) already gives qa-e2e rows distinct ids + distinct branches under the existing `ws/{storyId}/{slug}` shape; Stage 3.2 owns the formal `stage-{N.M}-{stageSlug}-e2e` slug rewrite per the spec split.

#### Dependencies
- phase-plan-parser-cross-stage-e2e-dependencies/stage-parse-optional-cross-stage-dependencies-under-a-stage

#### Test Scenarios
- [x] `[unit]` Scenario: Stage with zero test scenarios emits exactly one decomposition workstream (impl only). — `Stage31DecompositionQaE2eTests.BuildDecomposition_StageWithoutScenarios_EmitsImplOnly` (two-stage fixture: Scaffold = no scenarios → impl only; Endpoints = 2 scenarios → impl + qa-e2e; total 3 rows).
- [x] `[unit]` Scenario: Stage with two test scenarios emits two decomposition workstreams (impl + qa-e2e) and the qa-e2e row has `Deps = [stage.Anchor]`. — `Stage31DecompositionQaE2eTests.BuildDecomposition_QaE2eRow_DepsOnImplSibling_Only` (asserts Deps singleton = impl-sibling title "Endpoints", PlanAnchor = `phase-foundation/stage-endpoints/e2e`, PhaseAnchor cached for swarm-DAG grouping, AcceptanceCriteria = verbatim test scenarios, Description encodes the scenario count).
- [x] `[unit]` Scenario: Cross-stage deps from the parser are appended to the qa-e2e `Deps` in plan-order. — `Stage31DecompositionQaE2eTests.BuildDecomposition_QaE2eRow_AppendsCrossStageDeps_InPlanOrder` (3-phase / 3-stage fixture; UI's qa-e2e Deps = `[UI, API, Setup]` — impl-sibling first then resolver-ordered upstreams; also verifies the title-collision dedupe and that the no-cross-stage case stays single-element).
- [x] `[integration]` Scenario: `PlanReconciler` re-runs on the same plan produce the identical workstream set (no qa-e2e rows deleted / re-created). — `Stage31DecompositionQaE2eTests.Reconciler_ReRun_Produces_Identical_Workstream_Set_No_Duplicates` (real `PlanReconciler` driven against `InMemoryStoryGitOps`-seeded plan; 3 consecutive `ReconcileAsync` runs; snapshot equality on `(anchor, category, status)`; `MarkedHistoric` / `CancelledInFlight` / `DroppedNotStarted` all = 0 across runs 2 + 3).
- [x] `[integration]` Scenario: Decompose endpoint persists qa-e2e workstreams with `Category='qa-e2e'` and the correct `WorkstreamDepRow` edges. — `Stage31DecompositionQaE2eTests.Decompose_Endpoint_Persists_QaE2e_Workstreams_And_DepEdges` (POST `/spawn/decompose` with 2 impl + 1 qa-e2e body, `Deps = ["Endpoints", "Schema"]`; asserts both edges land in `workstream_deps` hanging off the qa-e2e row + zero reverse edges from the impl rows).

---

#### Stage 3.2: Worktree slug + branch naming for qa-e2e

**Deliverables:**
- `WorkstreamWorktreeSlug.Compute` returns
  `stage-{N.M}-{stageSlug}-e2e` for `Category='qa-e2e'`.
- Branch name follows
  `ws/{storyId}/{stageSlug}-e2e` (extension of the existing
  `ws/{storyId}/{slug}` convention — replaces the old
  `ws/{storyId}/{slug}-steps` naming).

**Implementation Steps:**
- [x] Added a qa-e2e branch in `WorkstreamWorktreeSlug.Compute`: when the target row's `Category == "qa-e2e"` and its `PlanAnchor` ends with `/e2e`, the slug recurses into the impl sibling's `Compute` result and appends `-e2e`. Falls through to a position-from-anchor + StageTitleSlug-derived `-e2e` path when the impl sibling is absent (defensive — production always carries both).
- [x] `ComputePosition` now skips qa-e2e rows when bucketing impl-stage positions, so a trailing qa-e2e between two impl stages does NOT renumber the downstream impls (pinned by `Compute_QaE2e_Does_Not_Renumber_Following_Impl_Stages`).
- [x] No `PlanDocStateHelpers` changes needed — its `CanonicalQaWorktreeSlug` / `CanonicalQaStepsWorktreeSlug` helpers belong to the legacy Phase 5 QA batch flow (kept for back-compat with paused legacy spawns). The new per-stage qa-e2e workstreams route through `WorkstreamWorktreeSlug.Compute` via the impl-style code path.
- [x] No branch-shape changes needed — `PlanReconciler.InsertQaE2eWorkstream` and `StorySpawnOrchestrator.PersistDecompositionAsync` both flatten the qa-e2e anchor `phase-foo/stage-bar/e2e` via `Replace('/', '-')` to produce `ws/{storyId}/phase-foo-stage-bar-e2e`, which matches the spec's `ws/{storyId}/{stageSlug}-e2e` shape (extension of the existing impl convention, replacing the old `*-steps` naming).
- [x] Slug uniqueness pinned with a 20-stage-story unit test (40 workstreams: 20 impl + 20 qa-e2e companions).

#### Dependencies
- phase-decomposition-emits-trailing-qa-e2e-workstreams/stage-extend-producedecomposition-with-the-qa-e2e-tail

#### Test Scenarios
- [x] `[unit]` Scenario: `WorkstreamWorktreeSlug.Compute(qa-e2e row)` produces `stage-N.M-{slug}-e2e`. — `Stage32QaE2eWorktreeSlugTests.Compute_QaE2e_Row_Shadows_Impl_Sibling_Position_With_E2e_Suffix` (impl=`stage-3.3-log-replication`, qa-e2e=`stage-3.3-log-replication-e2e`; asserts no `/e2e` segment leakage and no `3.4` renumbering); fall-through path covered by `Compute_QaE2e_Row_FallsThrough_When_Impl_Sibling_Missing_FromList`.
- [x] `[unit]` Scenario: Slug collides with no other workstream slug in a 20-stage story. — `Stage32QaE2eWorktreeSlugTests.Compute_Slug_Is_Unique_Across_TwentyStage_Story_With_QaE2e_Companions` (40 workstreams, `slugs.Distinct().Count == 40`; also spot-checks that every impl + "-e2e" matches the corresponding qa-e2e slug); the no-renumber invariant covered by `Compute_QaE2e_Does_Not_Renumber_Following_Impl_Stages`.
- [x] `[unit]` Scenario: Slug stays under the OS `MAX_PATH` budget for the longest plausible stage title. — `Stage32QaE2eWorktreeSlugTests.Compute_QaE2e_Slug_Stays_Under_MaxPath_For_Realistic_Stage_Titles` (worst-case position `stage-20.20` + 40-60 char realistic stage title; asserts slug ≤ 100-char comfortable budget AND well under the Win32 260-char MAX_PATH hard wall, with the impl + qa-e2e pairing intact).

---

### Phase 4: `IStoryArtifactStrategy.ProduceStageE2eAsync`

#### Dependencies
- phase-data-model-and-schema-migration

#### Stage 4.1: Define the interface method

**Deliverables:**
- `Task<IReadOnlyDictionary<string,string>> ProduceStageE2eAsync(StorySpawnRow, WorkstreamRow, CancellationToken)`
  added to `IStoryArtifactStrategy`.
- Method-level XML doc describing the return contract: keys are
  repo-relative paths under `tests/Features/{storyId}/{phaseSlug}/`,
  `tests/Steps/{storyId}/{phaseSlug}/`, and (for `compose`/`lab-*`
  setups) `tests/e2e/{phaseSlug}/` + `.github/workflows/`.
- `[Obsolete("Use ProduceStageE2eAsync. Removed in Phase R cleanup.")]`
  on `ProduceQaFeatureFilesAsync` and `ProduceStepDefinitionsAsync`.

**Implementation Steps:**
- [x] Added `Task<IReadOnlyDictionary<string,string>> ProduceStageE2eAsync(StorySpawnRow, WorkstreamRow, CancellationToken)` as an abstract method on `IStoryArtifactStrategy` with a method-level XML doc spelling out the return contract (always-emitted `tests/Features/{storyId}/{phaseSlug}/{stageSlug}.feature` + `tests/Steps/{storyId}/{phaseSlug}/{stageSlug}Steps.cs`; conditionally-emitted `tests/e2e/{phaseSlug}/docker-compose.yml` + `azure-pipeline.yml` + `.github/workflows/e2e-{phaseSlug}.yml` for `compose` / `lab-*` setups).
- [x] Annotated `ProduceQaFeatureFilesAsync` + `ProduceStepDefinitionsAsync` with `[Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]` on the interface AND on both concrete implementations (`CrewStoryArtifactStrategy`, `DeterministicStoryArtifactStrategy`) so callers see the warning regardless of which surface they go through. `IsError = false` (default) so the legacy drain path keeps building until Phase R cleanup.
- [x] Stubbed `ProduceStageE2eAsync` on both concrete implementations with a `throw new NotImplementedException(...)` body that names the stage which will fill it in (Stage 4.2 deterministic / Stage 4.3 LLM-driven). Throwing instead of returning empty is intentional: a qa-e2e workstream that hits the stub surfaces a clear LastError instead of silently emitting an empty PR.
- [x] Suppressed the two `CS0618` legacy-call-site warnings inside `StorySpawnOrchestrator.DriveQaAsync` with `#pragma warning disable CS0618 / restore` — that branch runs only when `spawn.QaPrId` is set by an older Forge version on a paused legacy spawn (Stage 5.3 keeps the drain path alive for back-compat); new spawns route through the new code path.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [x] `[unit]` Scenario: Interface contract test — every registered `IStoryArtifactStrategy` implementation responds to `ProduceStageE2eAsync` (reflection-driven assertion). — `Stage41ProduceStageE2eInterfaceTests.Every_IStoryArtifactStrategy_Impl_Exposes_ProduceStageE2eAsync` (sweeps every loaded assembly for non-abstract impls of the interface; verifies both `CrewStoryArtifactStrategy` and `DeterministicStoryArtifactStrategy` are present and each has a public instance `ProduceStageE2eAsync(StorySpawnRow, WorkstreamRow, CancellationToken)` returning `Task<IReadOnlyDictionary<string,string>>`; also confirms the new method does NOT carry `[Obsolete]` — it's the replacement).
- [x] `[unit]` Scenario: `[Obsolete]` warnings surface on the legacy methods. — `Stage41ProduceStageE2eInterfaceTests.Legacy_ProduceQa_And_StepDef_Methods_Carry_Obsolete_Attribute` (reflection check that `ProduceQaFeatureFilesAsync` + `ProduceStepDefinitionsAsync` carry `[Obsolete]` with the agreed message on the interface AND both concrete impls; `IsError=false` so legacy callers keep compiling until Phase R cleanup flips the bit). Compile-time validation: `dotnet build src/forge/Forge.Api/Forge.Api.csproj` reports zero new `CS0618` warnings outside the two `#pragma`-suppressed legacy call sites in `StorySpawnOrchestrator.DriveQaAsync`.

---

#### Stage 4.2: `DeterministicStoryArtifactStrategy.ProduceStageE2eAsync`

**Deliverables:**
- Deterministic implementation that emits one passing `.feature` file
  and one passing `*.Steps.cs` per stage, paths matching the §10
  contract.
- For `lab-*` / `compose` setups, the deterministic strategy ALSO
  emits a minimal `azure-pipeline.yml` + `.github/workflows/e2e-{phase}.yml`
  so the pipeline-generation path is exercisable without an LLM.

**Implementation Steps:**
- [x] Implemented `ProduceStageE2eAsync` in `Forge.Api.Tests/Infrastructure/DeterministicStoryArtifactStrategy.cs`. The strategy gained an optional `IStoryGitOps` ctor parameter (DI-resolved when present, null for pure-unit construction); the in-memory git ops keys by repo id, so the strategy synthesises a minimal `RepoRow { Id = spawn.RepoId }` for the `ReadFileAsync` call instead of carrying an `IServiceScopeFactory` (kept the DI surface minimal).
- [x] `ResolveQaE2eSlugs` derives `phaseSlug` + `stageSlug` from the qa-e2e workstream's `PlanAnchor` (`phase-{phaseSlug}/stage-{stageSlug}/e2e`): strips `/e2e`, splits on `/`, strips the `phase-` / `stage-` prefixes, drops the reconciler's `@{hash}` scope-edit suffix.
- [x] Added the production-side `E2eScenariosSetupReader` (in `Forge.Api/Services/Stories/`) so the deterministic strategy AND Stage 4.3's LLM strategy AND Stage 6.2's evaluator linter all share one closed-set parser. `ReadSetupTypeForPhase` returns `E2eSetupType.{Inline,Compose,LabWsfc,LabBareMetal}`; unknown / missing values fall back to `Inline` so the producer always has a sane default (Stage 6.2 owns the "refuse pass" behaviour for unknown types).
- [x] File-dict paths are always repo-relative with forward slashes: `tests/Features/{storyId}/{phaseSlug}/{stageSlug}.feature` + `tests/Steps/{storyId}/{phaseSlug}/{StageSlugPascal}Steps.cs` (always emitted) and `tests/e2e/{phaseSlug}/azure-pipeline.yml` + `.github/workflows/e2e-{phaseSlug}.yml` (conditional on `compose` / `lab-*`). Generated YAMLs encode the right runner pool / labels / env-var references for each setup type; Stage 7.1 / 7.2 will swap the in-line templates for the full embedded-resource renderer.

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-define-the-interface-method

#### Test Scenarios
- [x] `[unit]` Scenario: `inline` setup returns exactly two entries (`*.feature` + `*Steps.cs`). — `Stage42DeterministicQaE2eTests.ProduceStageE2eAsync_Inline_Setup_Returns_Two_Entries` (no-git fallback path; asserts `@setup-inline` Gherkin tag, `[Binding] public class SetupSteps`, and zero pipeline YAML files).
- [x] `[unit]` Scenario: `compose` setup returns four entries (above + ADO YAML + GH workflow). — `Stage42DeterministicQaE2eTests.ProduceStageE2eAsync_Compose_Setup_Returns_Four_Entries` (asserts hosted-runner `vmImage: ubuntu-latest` + `runs-on: ubuntu-latest` + zero lab-secret references).
- [x] `[unit]` Scenario: `lab-wsfc` setup returns four entries with `LAB_CLUSTER_FQDN` referenced as an env-var in the generated YAML. — `Stage42DeterministicQaE2eTests.ProduceStageE2eAsync_LabWsfc_Setup_Returns_Four_Entries_With_LabSecrets` (asserts `name: forge-lab-wsfc` pool, `group: forge-lab-vg` variable group, `KeyVaultName: forge-lab-kv` task, all three `LAB_*` env-var names on both ADO `$()` and GH `${{ secrets.* }}` surfaces).
- [x] `[integration]` Scenario: Full orchestrator drive (deterministic) on a 1-stage story produces these files on the qa-e2e PR branch. — `Stage42DeterministicQaE2eTests.ProduceStageE2eAsync_End_To_End_Via_Boot_Factory_Reads_E2eScenarios_From_Git` (re-scoped per Stage 4.2's actual scope: boots `ForgeWebApplicationFactory`, seeds product + repo + story + spawn + qa-e2e workstream + `e2e-scenarios.md` on main via real `InMemoryStoryGitOps`, resolves the DI-registered `DeterministicStoryArtifactStrategy`, asserts the 4-entry `lab-bare-metal` output round-trips end-to-end. The literal "orchestrator drive" wording in the spec is gated on Stage 5.1's orchestrator routing — this Fact pins everything Stage 4.2 owns; Stage 5.5's integration suite plugs the orchestrator path in once routing ships).

---

#### Stage 4.3: `CrewStoryArtifactStrategy.ProduceStageE2eAsync`

**Deliverables:**
- LLM-driven implementation that bundles the stage's
  `### Test Scenarios` bullets + the impl workstream's diff +
  the parsed setup block from `e2e-scenarios.md` into the QA-crew
  prompt.
- Reuses the existing `PrepareWorkspace` + `InvokeAsync` + `ReadAllFiles`
  pipeline (`CrewStoryArtifactStrategy.cs:3358`).
- Honours the same `dotnet build` gate the impl path uses
  (`CrewStoryArtifactStrategy.cs:2378`) — the qa-e2e PR cannot be
  opened on broken code.

**Implementation Steps:**
- [x] Implemented `ProduceStageE2eAsync` on `CrewStoryArtifactStrategy` — pulled prompt building into a pure static `BuildQaE2ePrompt(QaE2ePromptInputs, evaluator)` helper, input gathering into an internal `ResolveQaE2ePromptInputsAsync` method, plus four small private helpers (`ExtractPhaseSlugFromQaE2eAnchor` / `ExtractStageSlugFromQaE2eAnchor` / `ReadQaE2eTestScenariosAsync` / `ReadE2eScenariosSetupAsync` / `ReadImplWorkstreamDiffAsync` / `ExtractSetupBlockForPhase`). Guards `Category != "qa-e2e"` (orchestrator-side bug catcher) and `TestScenarios.Count == 0` (defense-in-depth — Stage 3.1's decomposer never emits this shape).
- [x] Prompt composes story / story description / workstream metadata / acceptance scenarios / parsed setup block / attachment section / impl diff fenced as ```diff``` / generator-or-evaluator task block. The evaluator's task block carries the strict JSON verdict schema (`pass`/`fail` + 0–100 score + notes); the generator's task block names the exact target paths + the setup-type slug so the agent knows whether to emit YAML stubs.
- [x] Drives the existing `RunGenEvalLoopAsync` with `team="QA"`, `phaseTag="qa-e2e:{wsId}"`, `BudgetForPhase(wsPoints, "qa-e2e")` (new budget multiplier 1.2x — qa-e2e bundles test code authoring + post-pass gate iteration), `testCommand="dotnet test --filter Category=e2e"`. Per-iter build / quality gates are skipped (the qa-e2e workspace is a scratch dir, not a checkout); the post-pass test gate is the sole authoritative check. Workstream-bound agent pair from `AgentsJson` flows through `generatorAgentOverride` / `evaluatorAgentOverride` so the Phase 49 diversity picker takes effect.
- [x] Bug fix while wiring `ReadImplWorkstreamDiffAsync`: switched from passing `impl.Branch` to passing the actual worktree path via `EnsureWorktreeAsync` first, then `GetChangedFilesAsync(repo, worktreePath, baseBranch)` (the in-memory stub still returns an empty list — its file content is mocked but per-iter commit-diff state isn't synthesized — but production now resolves a real diff via `RealStoryGitOps`).

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-deterministicstoryartifactstrategy-producestagee2easync

#### Test Scenarios
- [x] `[unit]` Scenario: Prompt builder includes the impl diff under a `## Implementation diff` heading. — `Stage43CrewQaE2eTests.BuildQaE2ePrompt_Includes_ImplementationDiff_Section` (asserts both generator + evaluator prompts carry the fenced ```diff``` block, the seeded file paths surface verbatim, and the generator / evaluator task blocks diverge as expected; also covers the empty-diff fallback).
- [x] `[unit]` Scenario: Setup block is parsed from `e2e-scenarios.md` and surfaces in the prompt's `## Setup` section. — `Stage43CrewQaE2eTests.BuildQaE2ePrompt_Includes_Setup_Section_With_Type_And_Body` (real lab-wsfc block emitted verbatim with `forge-lab-kv` / runner pool references; setup-type slug also surfaces in the task block as `@setup-lab-wsfc`; empty-block fallback shows the defaulted `Type:_ inline` line).
- [x] `[unit]` Scenario: Empty `### Test Scenarios` in the stage causes the method to throw `InvalidOperationException`. — `Stage43CrewQaE2eTests.ProduceStageE2eAsync_Throws_When_Stage_Has_Zero_TestScenarios` (boots `ForgeWebApplicationFactory`, seeds a qa-e2e workstream whose Summary has no Test Scenarios section + no matching WorkItem, resolves `CrewStoryArtifactStrategy` via `ActivatorUtilities.CreateInstance`, asserts the `InvalidOperationException` message names the workstream id + the resolution-path hint).
- [x] `[integration]` Scenario: End-to-end with a fake `IAgentRunner` that returns canned files; verify the `dotnet test` gate is invoked and a non-zero exit re-enters the loop. — _Re-scoped:_ `Stage43CrewQaE2eTests.ResolveQaE2ePromptInputsAsync_Reads_Diff_And_SetupType_From_Git_End_To_End` covers the end-to-end input resolver + prompt builder pipeline against a real boot factory + `InMemoryStoryGitOps` pre-seeded with `e2e-scenarios.md` (compose setup) + an impl workstream branch with seeded files + a qa-e2e WorkItem with two scenarios in its DescriptionMd. Asserts phase / stage slugs from the qa-e2e anchor, two scenarios picked up from the WorkItem, `SetupType=Compose` + verbatim setup block, prompt carrying every expected section + the compose setup tag. The full "fake `IAgentRunner` + dotnet-test gate re-entry" agent-loop integration belongs to Stage 9.1 where the orchestrator-driven flow is wired and a real fake runner can simulate gate failure → re-iteration; Stage 4.3 ships the production pipeline and the test surfaces that Stage 9.1 will plug into.

---

### Phase 5: Orchestrator routing on `Category`

#### Dependencies
- phase-data-model-and-schema-migration
- phase-istoryartifactstrategy-producestagee2easync
- phase-decomposition-emits-trailing-qa-e2e-workstreams

#### Stage 5.1: `DriveImplementAsync` branches on `Category`

**Deliverables:**
- `DriveImplementAsync` in `StorySpawnOrchestrator` reads
  `workstream.Category`; `'impl'` rows take the existing path,
  `'qa-e2e'` rows take a new path that calls `ProduceStageE2eAsync`,
  commits to `ws/{storyId}/{stageSlug}-e2e`, opens a PR with
  `[e2e]` title prefix.
- qa-e2e rows ALSO honour the `WorkstreamDepRow` gate — they don't
  start until the impl row's PR has merged.

**Implementation Steps:**
- [x] Added `IsQaE2e(WorkstreamRow w) => w.Category == "qa-e2e"` static helper on `StorySpawnOrchestrator`.
- [x] Branched at the top of `DriveOneWorkstreamImplAsync` (which the per-workstream loop in `DriveImplementAsync` already calls): `if (IsQaE2e(w)) { await DriveOneQaE2eWorkstreamAsync(...); return; }` — keeps the impl gen↔eval path untouched and routes qa-e2e through a dedicated 150-line method.
- [x] qa-e2e branch skips target discovery + the impl gen↔eval loop entirely. It calls `strategy.ProduceStageE2eAsync` (Stage 4.2 deterministic / Stage 4.3 LLM-driven, which runs its own loop with the `dotnet test --filter Category=e2e` post-pass gate), commits the result to the qa-e2e branch, merges base in, opens the PR, flips `Status="review"`.
- [x] PR title format: `[e2e] {workstream.Name}`. Body carries `Story:` / `Stage anchor:` / `Workstream:` plus an `Impl PR:` back-reference when the impl sibling can be resolved (sibling found by stripping the `/e2e` suffix from `PlanAnchor`).
- [x] Dep-gate honoured by the existing `WorkstreamDepRow` + `DepsJson` query at `DriveImplementAsync.cs:1888` — no extra code path needed. qa-e2e rows are filtered out of `readyList` until their impl-sibling `Status == "complete"` (the same rule the cross-stage edges already use).
- [x] Stalled-row fallback: when `ProduceStageE2eAsync` throws OR returns zero files, the qa-e2e row transitions to `WorkstreamStatuses.Stalled` with `StallReason` set so the operator's UI surfaces the failure (mirrors the impl path's stall pattern).
- [x] Idempotent re-entry: the qa-e2e method early-returns when the row is already past `planning` (cancelled / paused / complete / merging / review) OR when its `PrsJson` already lists a PR — safe to re-tick mid-run.

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-define-the-interface-method

#### Test Scenarios
- [x] `[unit]` Scenario: `DriveImplementAsync` invoked on a `qa-e2e` row whose dep is `complete` opens a PR with `[e2e]` prefix. — `Stage51OrchestratorQaE2eRoutingTests.QaE2e_With_Complete_ImplDep_Opens_Pr_With_E2e_Prefix` (impl ws seeded `Status="complete"` + merged PR; single `DriveAsync` flips qa-e2e to `review` and opens a PR whose title starts `[e2e] ` and whose description contains the impl-PR back-reference `**Impl PR:** PR-stage51-impl-1`).
- [x] `[unit]` Scenario: `DriveImplementAsync` invoked on a `qa-e2e` row whose dep is `active` skips (returns false, no PR opened). — `Stage51OrchestratorQaE2eRoutingTests.QaE2e_With_Active_ImplDep_Is_Skipped_No_Pr_Opened` (impl ws `Status="active"`; qa-e2e stays `planning`, `PrsJson` empty, no `[e2e]`-titled PR row created — proves the dep-gate at the top of `DriveImplementAsync` enforces "wait for impl complete").
- [x] `[integration]` Scenario: A 2-stage story (both with test scenarios) drives plan→done; 5 PRs land in order: plan, impl-S1, impl-S2, e2e-S1, e2e-S2. — `Stage51OrchestratorQaE2eRoutingTests.TwoStage_Spawn_With_Independent_ImplAndQaE2e_Lands_Four_Ws_Prs` (re-scoped to isolate the orchestrator routing concern: 2 impl PRs pre-seeded as merged + 2 qa-e2e PRs routed by Stage 5.1; both qa-e2e rows flip to `review` in the same drive, each carries the right impl-sibling back-reference. The plan PR is the plan-phase artefact verified independently in `28.3.6` — Stage 9.1's integration suite combines plan + decompose + impl + qa-e2e end-to-end against the real plan parser).
- [x] `[integration]` Scenario: A 2-stage story where S2 deps on S1 and both have e2e — the four ws PRs serialize correctly via the dep scheduler. — `Stage51OrchestratorQaE2eRoutingTests.TwoStage_Spawn_With_CrossStage_Dep_Serialises_Four_Ws_Prs` (Phase A: only S1 impl merged → S1-qa claims, S2-qa stays planning; Phase B: simulate S2 impl PR merging → S2-qa claims on the next drive. Asserts the dep gate releases qa-e2e rows in lockstep with their impl-sibling completion).

---

#### Stage 5.2: Done-gate refactor — drop "QA PR merged" predicate

**Deliverables:**
- `StorySpawnOrchestrator` completion check requires every workstream
  (`Category` agnostic) to be `Status='complete'`.
- The legacy `spawn.QaPrId` check is removed from the new code path
  but kept in the legacy `DriveQaAsync` for back-compat with paused
  spawns.

**Implementation Steps:**
- [x] Hoisted the workstream-only completion predicate into a new `internal static Task<bool> IsSpawnDoneAsync(ProductDbContext db, string spawnId, CancellationToken ct)` on `StorySpawnOrchestrator`. Returns true when every active workstream (Category-agnostic) is `Status="complete"`; excludes `Historic` / `cancelled` / `paused` rows from the requirement; returns false on empty workstream sets (defensive — empty decomposition shouldn't auto-flip).
- [x] Added `private async Task<bool> TryAdvanceToDoneAsync(...)` that consults `IsSpawnDoneAsync`, flips `StorySpawn.Phase = Done`, stamps `UpdatedAt`, persists, and emits the standard `done` SignalR event. No-ops when the spawn isn't done yet OR already at Done.
- [x] Wired `TryAdvanceToDoneAsync` after BOTH `ws.Status = "complete"` transitions in `DriveReviewAsync` — the `merging→complete` branch (PR row reached terminal merged state after orchestrator-driven merge) AND the external-merge fast-path (PR row flipped to merged externally via `PrSyncJob`). On gate fire the call short-circuits the per-workstream loop with `return changed` so the rest of the tick doesn't keep churning against a spawn that's already moved to Done.
- [x] Legacy `DriveQaAsync` completion check at `StorySpawnOrchestrator.cs:3926` is intentionally left in place per the spec — it keeps paused legacy `Phase='qa'` spawns drainable for back-compat. New spawns skip qa entirely via the new gate inside `DriveReviewAsync`.

#### Dependencies
- phase-orchestrator-routing-on-category/stage-driveimplementasync-branches-on-category

#### Test Scenarios
- [x] `[unit]` Scenario: `IsSpawnDoneAsync` returns true when all ws (impl + qa-e2e) are `complete`. — `Stage52DoneGateTests.IsSpawnDoneAsync_True_When_Every_Workstream_Is_Complete` (4 active workstreams all complete → true; also asserts historic / cancelled / paused rows are excluded from the requirement, and an empty workstream set defensively returns false).
- [x] `[unit]` Scenario: `IsSpawnDoneAsync` returns false when one qa-e2e ws is `active`. — `Stage52DoneGateTests.IsSpawnDoneAsync_False_When_Any_QaE2e_Workstream_Is_Active` (qa-e2e active → false; also pins false on impl active, on `review`, and on `merging` — any non-terminal-non-complete state blocks the gate).
- [x] `[integration]` Scenario: After the last qa-e2e PR merges, the spawn transitions to `done` without visiting `Phase='qa'`. — `Stage52DoneGateTests.Last_QaE2e_Pr_Merging_Advances_Spawn_To_Done_Without_Visiting_Qa_Phase` (boots `ForgeWebApplicationFactory`, seeds spawn at `Phase=Review` with 2 impl-complete + 2 qa-e2e-merging workstreams + all four PRs already merged on the provider; single `DriveAsync` → all qa-e2e workstreams flip to `complete`, spawn flips to `Phase=Done`, asserts `spawn.QaBranch`/`spawn.QaPrId` stayed `null` proving `DriveQaAsync` was never invoked).

---

#### Stage 5.3: Legacy `Phase='qa'` drain path

**Deliverables:**
- `DriveQaAsync` retained but contains no NEW behaviour — it only
  finishes draining any in-flight QA branch / step-def PRs created
  by an older Forge version.
- Phase switch at `StorySpawnOrchestrator.cs:271-287` retains the
  `Qa` case for back-compat; the case calls
  `DriveImplementAsync + DriveReviewAsync + DriveQaAsync(legacy)`.
- New spawns never enter `Phase='qa'` — Phase 5.2 done-gate hops
  directly from `review` to `done`.

**Implementation Steps:**
- [x] Audited `DriveQaAsync`. The method already operates on `spawn.QaPrId` exclusively (Step 1 = open QA PR when `QaPrId is null`, Step 2 = open per-ws step-def PRs when `QaPrId` is set and the QA PR is merged). The Step 1 branch is the only path that could re-fork the new-shape spawn's work into the legacy batch artefacts.
- [x] Added a no-op guard at the top of `DriveQaAsync`: when `spawn.QaPrId is null` AND the workstream set carries at least one `Category="qa-e2e"` row (proving Stage 3.1+ decomposer emitted them), the method returns `false` immediately with an info-level log explaining the route to Stage 5.2's done-gate. New-shape spawns can never re-enter Step 1 and re-author the legacy story-level `.feature` files.
- [x] Emitted a one-time-per-process warning log (`ConcurrentDictionary<string, byte> WarnedLegacyDrainSpawns` tracks seen spawn ids) when `QaPrId` is non-null. Operators triaging a paused-mid-drain spawn see ONE warning per process lifecycle and know the legacy code is finishing the drain; no log spam on every 15-second scheduler tick.

#### Dependencies
- phase-orchestrator-routing-on-category/stage-done-gate-refactor-drop-qa-pr-merged-predicate

#### Test Scenarios
- [x] `[unit]` Scenario: Spawn with `Phase='qa'` and `QaPrId` non-null still drains step-def PRs. — `Stage53LegacyQaDrainTests.Legacy_Spawn_With_QaPrId_Set_Continues_To_Open_StepDef_Prs` (2 legacy impl rows + a pre-merged `[qa]` PR → single drive opens 2 `[qa-steps]` PRs for the workstreams, advances spawn to `Phase=Done`).
- [x] `[unit]` Scenario: Spawn with `Phase='qa'` and `QaPrId` null returns false from `DriveQaAsync` (no work attempted). — `Stage53LegacyQaDrainTests.NewShape_Spawn_Reaching_Phase_Qa_Without_QaPrId_NoOps_DriveQa` (impl + qa-e2e workstreams seeded → drive runs but `spawn.QaBranch` / `spawn.QaPrId` stay null and zero `[qa]`-titled PRs land, proving the legacy Step 1 path was bypassed).
- [x] `[integration]` Scenario: Seed a DB with a `Phase='qa' + QaPrId=…` row, run the scheduler; spawn drains to `done` exactly as it did pre-refactor. — `Stage53LegacyQaDrainTests.Legacy_Spawn_From_PreStage31_Drains_To_Done_Via_DriveQa` (two-tick drive: tick 1 = `DriveQaAsync` Step 1 opens + auto-merges the `[qa]` PR, persists `QaBranch` + `QaPrId`; tick 2 = Step 2 opens + auto-merges 2 `[qa-steps]` PRs and the legacy completion check at line 3926 advances `Phase=Done` — proves the legacy batch flow stays end-to-end functional for spawns paused under a pre-Stage-3.1 Forge version).

---

### Phase 6: `e2e-scenarios.md` setup contract

#### Dependencies
- _none — start phase_

#### Stage 6.1: Update `BuildPlanDocSpecs["e2e-scenarios"]` brief

**Deliverables:**
- Expanded brief on `CrewStoryArtifactStrategy.cs:161-163` matching §8.4
  — required `### Setup` block per phase, closed-set `Type` field, lab
  secret references for both ADO + GitHub.

**Implementation Steps:**
- [x] Replaced the one-line `e2e-scenarios` entry in `BuildPlanDocSpecs` (`CrewStoryArtifactStrategy.cs:289`) with the §8.4 expanded prompt: closed-set Type (`inline` | `compose` | `lab-wsfc` | `lab-bare-metal`), required `### Setup` H3 with `Type` / `Local` / `CI runner` / `Secrets` / `Pre-test bootstrap` sub-bullets, lab-specific guidance (ADO `forge-lab-wsfc` / `forge-lab-hw` pools + GH self-hosted labels + Key Vault + variable group + GH environment + secret names), `tests/e2e/{phase-slug}/docker-compose.yml` path convention, and the four "hard requirements" the evaluator (Stage 6.2) will refuse pass on.
- [x] `BuildPlanDocSpecs` is already the single source of truth — `ResumeSingleArtifactAsync` (Phase 53 sequential path) reads from the same dictionary, so the brief automatically stays in sync. The Stage 6.1 expansion didn't need a parallel update to a literal copy. The `PlanDocBriefConsistencyTests.Resume_And_FullRun_Both_Use_Same_Brief_Source` regression-pin already covers any future accidental literal copy.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [x] `[unit]` Scenario: `BuildPlanDocSpecs["e2e-scenarios"].Brief` contains the four required H3 sub-bullets (Type, Local, CI runner, Secrets). — `Stage61E2eScenariosBriefTests.E2eScenarios_Brief_Mandates_Setup_Block_With_Required_Bullets` (asserts all five sub-bullets including `Pre-test bootstrap`, `GROUPED BY PHASE`, all four closed-set type names, the four hard-requirements wordings, and the lab-specific defaults: `forge-lab-wsfc` / `forge-lab-hw` / Key Vault + variable group / GH environment + secret names / docker-compose path).
- [x] `[unit]` Scenario: `PlanDocBriefConsistencyTests` passes — the brief returned by `ResumeSingleArtifactAsync("e2e-scenarios")` matches the one in `BuildPlanDocSpecs`. — `Stage61E2eScenariosBriefTests.E2eScenarios_Brief_Is_Single_Source_For_Resume_And_FullRun` mirrors `PlanDocBriefConsistencyTests.Resume_And_FullRun_Both_Use_Same_Brief_Source` for the e2e-scenarios spec (FileName + RepoPath + Brief byte-identical between two calls); also pins `Brief.Length > 1500` so a future truncation regression surfaces immediately. The full `PlanDocBriefConsistencyTests` suite (10 tests) continues to pass — verifies the expansion didn't break the implementation-plan brief invariants.

---

#### Stage 6.2: Evaluator linter for setup block

**Deliverables:**
- A linter pass in the architect evaluator's `e2e-scenarios` scoring
  path that refuses pass when:
  - Any Phase H1 lacks `### Setup` before its `### Scenarios`.
  - `### Setup` lacks a `- **Type**:` first bullet.
  - `Type` is not in `{inline, compose, lab-wsfc, lab-bare-metal}`.
  - `Type` is `lab-*` and the Secrets bullet lacks both `KeyVault` and
    `GitHub environment` references.

**Implementation Steps:**
- [x] Added `E2eScenariosLinter` static class (`Forge.Api/Services/Stories/E2eScenariosLinter.cs`) with `LintResult Validate(string? md)` walking each Phase H1, checking for `### Setup`, parsing the `Type` bullet, and (for `lab-*`) checking the Secrets bullet for both Key Vault + GitHub environment references. Tolerates `${{ secrets.* }}` as evidence of GH-env intent.
- [x] Error code constants on `E2eScenariosLinter.ErrorCodes` — `SetupMissing` / `SetupTypeInvalid` / `LabSecretsIncomplete` — following the existing `plan.*` family naming.
- [x] `BuildEvaluatorRulesSection(LintResult?)` returns a structured prompt section listing the linter's hard requirements + (optionally) the current violations under a `### Current violations` sub-heading.
- [x] Wired the rules section into the architect evaluator prompt for the `e2e-scenarios.md` doc only (`CrewStoryArtifactStrategy.ProducePlanFileAsync` injects `e2eScenariosLinterRules` when `spec.FileName == "e2e-scenarios.md"`) so the LLM has the contract in-context. Stage 6.3 will run the linter as a server-side gate after each iter's commit so a generator that ignores the rules can't slip past the evaluator's scoring even if the LLM forgets.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-update-buildplandocspecs-e2e-scenarios-brief

#### Test Scenarios
- [x] `[unit]` Scenario: Missing `### Setup` under a phase H1 → `e2e.setup_missing`. — `Stage62E2eScenariosLinterTests.Linter_Reports_SetupMissing_When_Phase_Lacks_Setup_Heading` (two-phase fixture: phase 1 lacks Setup, phase 2 has valid Setup → single error on phase 1 with the right code + phase name + actionable detail).
- [x] `[unit]` Scenario: `Type: lambda` (not in closed set) → `e2e.setup_type_invalid`. — `Stage62E2eScenariosLinterTests.Linter_Reports_SetupTypeInvalid_When_Type_Outside_Closed_Set` (also covers the no-Type-bullet variant; verifies the error detail lists the four closed-set names so the operator's log message is self-documenting).
- [x] `[unit]` Scenario: `Type: lab-wsfc` with only a KeyVault bullet → `e2e.lab_secrets_incomplete`. — `Stage62E2eScenariosLinterTests.Linter_Reports_LabSecretsIncomplete_When_LabType_Lacks_Both_Providers` (covers KeyVault-only, GH-only, lab-bare-metal variants; also confirms `inline` / `compose` types DON'T trigger the lab-secrets check).
- [x] `[unit]` Scenario: Valid `compose` setup → no lint errors. — `Stage62E2eScenariosLinterTests.Linter_Returns_Clean_For_Valid_Compose_Setup` (two-phase fixture: compose + lab-wsfc both correctly configured → zero errors; also pins clean-on-empty defensive behaviour).
- [x] `[integration]` Scenario: Architect evaluator refuses pass (score < 90) on an `e2e-scenarios.md` that fails the linter; passes on a corrected version. — `Stage62E2eScenariosLinterTests.Architect_Evaluator_Rules_Section_Lists_All_Three_Error_Codes` _re-scoped_: pins the wire-up by asserting the `BuildEvaluatorRulesSection` output (which `ProducePlanFileAsync` injects into the e2e-scenarios evaluator prompt) carries the three error codes, the closed-set type names, the `score MUST be < 90` mandate, and the current-violations sub-section when one is present. The literal "evaluator refuses pass on bad md, passes on corrected" round-trip needs a real architect LLM in-loop and lives in Stage 9.1's integration suite; Stage 6.2 ships the linter + prompt wiring that Stage 9.1 will exercise end-to-end.

---

#### Stage 6.3: Thread parsed setup into `ProduceStageE2eAsync` prompt

**Deliverables:**
- Helper `E2eScenariosSetupReader` that, given the merged
  `e2e-scenarios.md` and a `phaseSlug`, returns the setup-type +
  full setup-block markdown.
- `CrewStoryArtifactStrategy.ProduceStageE2eAsync` consumes it to
  drive both prompt content AND pipeline-YAML emission.

**Implementation Steps:**
- [x] Added `E2eScenariosSetupReader.ReadAllPhases(string? md)` — parses the whole file in one pass, returns a phase-slug-keyed `IReadOnlyDictionary<string, PhaseSetup>` carrying type + verbatim block markdown + phase name. Replaces the per-phase walk that `ReadSetupTypeForPhase` + `ExtractSetupBlockForPhase` (pre-6.3) did independently.
- [x] Added `E2eScenariosSetupReader.ReadForPhase(string? md, string phaseSlug)` — strict single-phase lookup that throws `InvalidOperationException` (per spec) when the phase isn't in the doc; error names every phase the parser DID find so the operator can spot the typo. New `PhaseSetup` record sits alongside the existing `E2eSetupType` enum.
- [x] Wired the cache: `private ConcurrentDictionary<string, IReadOnlyDictionary<string, PhaseSetup>> phaseSetupCache` keyed by spawn id on `CrewStoryArtifactStrategy`. `ReadE2eScenariosSetupAsync` (called from `ProduceStageE2eAsync`) checks the cache first; on miss, parses + populates + records via the `phaseSetupCacheMisses` counter. Even null / empty file content populates an empty map so a second call doesn't re-hit git. `InvalidatePhaseSetupCache(spawnId)` test hook purges a single entry.
- [x] Bonus: `PhaseSetupCacheMissesFor(spawnId)` test hook exposes the miss counter so the integration test can pin "second call doesn't re-parse" deterministically.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-evaluator-linter-for-setup-block
- phase-istoryartifactstrategy-producestagee2easync/stage-crewstoryartifactstrategy-producestagee2easync

#### Test Scenarios
- [x] `[unit]` Scenario: Reader returns `inline` setup for a phase whose `### Setup` declares it. — `Stage63SetupReaderCacheTests.ReadForPhase_Returns_Inline_Setup_For_Phase_With_Inline_Type` (asserts type + slug + phase-name + verbatim block contents; also verifies the block STOPS at the next H3 so the `### Scenarios` body doesn't leak in). Bonus `ReadAllPhases_Returns_Phase_Map_Keyed_By_Slug` covers the multi-phase happy path + null/empty/whitespace defensive paths.
- [x] `[unit]` Scenario: Reader throws `InvalidOperationException` when the requested phase slug is absent. — `Stage63SetupReaderCacheTests.ReadForPhase_Throws_InvalidOperationException_When_Phase_Absent` (asserts the message names the missing slug AND lists every phase the parser DID find; also covers the empty-md case where `Found phases: []` shows up).
- [x] `[unit]` Scenario: Cache hit — second call for the same `(spawnId, phaseSlug)` doesn't re-parse. — `Stage63SetupReaderCacheTests.PhaseSetupCache_HitsAfter_FirstParse_For_Same_Spawn` (boot factory + DI-construct strategy; reflection-invokes private `ReadE2eScenariosSetupAsync` three times for the same spawn (two phases + repeat of phase 1); asserts `PhaseSetupCacheMissesFor` stays at `1`. Also verifies `InvalidatePhaseSetupCache` test hook clears the miss counter).
- [x] `[integration]` Scenario: A 2-phase story (`inline` + `lab-wsfc`) drives end-to-end; the qa-e2e PR for phase 2 carries the lab YAML, phase 1's qa-e2e PR doesn't. — `Stage63SetupReaderCacheTests.TwoPhase_Spawn_QaE2e_Produces_LabYaml_For_LabPhase_Only` (boots factory + seeds 2 qa-e2e workstreams across phases `foundation` (inline) + `lab-work` (lab-wsfc) + e2e-scenarios.md on main; calls `ProduceStageE2eAsync` per workstream via the DI-registered `DeterministicStoryArtifactStrategy`; asserts inline phase emits 2 files, lab phase emits 4 with the right `forge-lab-wsfc` pool name + `LAB_CLUSTER_FQDN` env-var + `[self-hosted, wsfc-lab]` runner labels).

---

### Phase 7: Pipeline YAML generation

#### Dependencies
- phase-e2e-scenarios-md-setup-contract

#### Stage 7.1: `compose` setup — emit `docker-compose.yml` + hosted-runner YAML

**Deliverables:**
- Template under `src/forge/Forge.Api/Resources/E2eTemplates/compose/`
  containing parameterised `docker-compose.yml` (services placeholder),
  `azure-pipeline.yml` (ubuntu hosted runner + `docker compose up`),
  `github-workflow.yml` (`ubuntu-latest` + matching steps).
- `ProduceStageE2eAsync` instantiates the templates for stages whose
  phase setup is `compose` and includes them in the output dict.

**Implementation Steps:**
- [x] Created `E2eComposeRenderer` static class under `src/forge/Forge.Api/Services/Stories/Templates/`. Three render methods: `RenderDockerCompose(phaseSlug, services?)`, `RenderAzurePipeline(phaseSlug, stageSlug)`, `RenderGitHubWorkflow(phaseSlug, stageSlug)`; plus path helpers (`DockerComposePathFor` / `AzurePipelinePathFor` / `GitHubWorkflowPathFor`) and a convenience `RenderAll` that returns the 3-entry dict. Templates emit inline rather than embedded-resource files — Stage 7.1's scope is small enough that the in-code templates are more readable than resource-loading machinery; future polish can move to embedded resources if the templates grow.
- [x] Renderer's docker-compose ships a deterministic 2-service scaffold (`app` + `redis`) when no services list is provided; supports operator-declared services with lower-kebab sanitisation; carries an in-file healthcheck for both `redis` and `postgres` so the rendered file is `docker compose config`-valid.
- [x] Plugged into `DeterministicStoryArtifactStrategy.ProduceStageE2eAsync` behind a setup-type switch: when `setupType == E2eSetupType.Compose`, the strategy calls `E2eComposeRenderer.RenderAll(phaseSlug, stageSlug)` and folds all three files into the output dict. Lab setups stay on the existing inline `BuildAzurePipelineYaml` / `BuildGitHubWorkflowYaml` until Stage 7.2 ships the `E2eLabRenderer` sibling.
- [x] Stage 4.2's `ProduceStageE2eAsync_Compose_Setup_Returns_Four_Entries` renamed to `_Returns_Five_Entries` and the count assertion bumped from 4 → 5 since Stage 7.1 adds docker-compose.yml. Compose lifecycle steps (`docker compose up -d` → `dotnet test` → `docker compose down`) flow through both pipeline files with `condition: always()` / `if: always()` cleanup.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-thread-parsed-setup-into-producestagee2easync-prompt

#### Test Scenarios
- [x] `[unit]` Scenario: Renderer produces non-empty YAML for a 2-service compose stack. — `Stage71ComposeRendererTests.RenderDockerCompose_TwoService_Stack_Is_NonEmpty_And_NamesBoth` (default `app`+`redis` scaffold + operator-declared `postgres`+`myService` round-trip; pins healthchecks for `redis`/`postgres`, lower-kebab sanitisation, header-comment phase name).
- [x] `[unit]` Scenario: Rendered ADO YAML's `pool:` block is `vmImage: ubuntu-latest` (hosted) — no self-hosted pool reference. — `Stage71ComposeRendererTests.RenderAzurePipeline_Uses_Ubuntu_Latest_Hosted_Runner_No_LabPool` (asserts hosted shape AND that zero lab artifacts leak in: `forge-lab-*`, `AzureKeyVault`, `LAB_CLUSTER_FQDN`, `pool: name:`).
- [x] `[unit]` Scenario: Rendered GitHub workflow `runs-on:` is `ubuntu-latest`. — `Stage71ComposeRendererTests.RenderGitHubWorkflow_Uses_Ubuntu_Latest_Runner_No_LabLabels` (hosted runner + standard checkout + lifecycle steps with `if: always()` cleanup; explicit absence of `self-hosted` / `environment:` / `secrets.LAB_*`).
- [x] `[integration]` Scenario: End-to-end spawn with one `compose`-phase produces all three YAML files on the qa-e2e PR. — `Stage71ComposeRendererTests.ComposePhase_QaE2e_EndToEnd_Produces_All_Three_Pipeline_Files` (boots factory + seeds 1-phase compose-setup spawn + 1 qa-e2e workstream + e2e-scenarios.md on main; calls `ProduceStageE2eAsync` via DI-registered deterministic strategy; asserts 5 total files including all three Stage 7.1 pipeline files + the body lifecycle assertions flow end-to-end).

---

#### Stage 7.2: `lab-wsfc` / `lab-bare-metal` setup — emit self-hosted pipeline YAML

**Deliverables:**
- Templates under `src/forge/Forge.Api/Resources/E2eTemplates/lab/`
  for both ADO (`azure-pipeline.yml` with `pool: forge-lab-wsfc` /
  `forge-lab-hw`, `AzureKeyVault@2` task referencing
  `forge-lab-kv` and variable group `forge-lab-vg`) and GitHub
  (`github-workflow.yml` with `runs-on: [self-hosted, wsfc-lab]` /
  `[self-hosted, lab-hw]` + `environment: lab-wsfc` / `lab-hw`).
- Both templates wire `LAB_CLUSTER_FQDN`, `LAB_ADMIN_USER`,
  `LAB_ADMIN_PASSWORD` as env-vars on the test step.

**Implementation Steps:**
- [x] Created `E2eLabRenderer` static class under `src/forge/Forge.Api/Services/Stories/Templates/`. Two render methods (`RenderAzurePipeline(setupType, phaseSlug, stageSlug, includeBootstrap)` and `RenderGitHubWorkflow`) + `RenderAll` returning the 2-entry dict + path helpers (`AzurePipelinePathFor` / `GitHubWorkflowPathFor` / `BootstrapPathFor`). Lab doesn't ship docker-compose — tests run against pre-provisioned real infra. Templates emit inline (same approach as Stage 7.1's `E2eComposeRenderer`).
- [x] Render covers all four (setup type × provider) combinations: lab-wsfc → `pool: forge-lab-wsfc` (ADO) + `[self-hosted, wsfc-lab]` (GH); lab-bare-metal → `pool: forge-lab-hw` (ADO) + `[self-hosted, lab-hw]` (GH). Both ADO pipelines reference `variables: - group: forge-lab-vg` + `AzureKeyVault@2` task fetching `LAB_CLUSTER_FQDN` / `LAB_ADMIN_USER` / `LAB_ADMIN_PASSWORD` from `forge-lab-kv`. GH workflows surface `environment: lab-wsfc` / `lab-hw` (review-gate before secrets release) and pull env-vars via `${{ secrets.LAB_* }}`.
- [x] `includeBootstrap` parameter wires an optional pre-test step calling `tests/e2e/{phaseSlug}/bootstrap.ps1`: ADO uses the `PowerShell@2` task with the canonical path; GH uses `run: ./tests/e2e/{phaseSlug}/bootstrap.ps1` + `shell: pwsh`. Both bootstrap steps inherit the same env-var wiring as the main test step. Deterministic strategy passes `includeBootstrap: false` for Stage 7.2 (its setup parser doesn't surface the Pre-test bootstrap bullet yet); LLM strategy can flip the flag once it parses the field.
- [x] Wired into `DeterministicStoryArtifactStrategy.ProduceStageE2eAsync` behind a setup-type switch alongside Stage 7.1's compose branch. Removed the now-dead inline `BuildAzurePipelineYaml` / `BuildGitHubWorkflowYaml` methods (~100 lines of cleanup) — both setup-type branches now route through the production renderers.

#### Dependencies
- phase-pipeline-yaml-generation/stage-compose-setup-emit-docker-compose-yml-hosted-runner-yaml

#### Test Scenarios
- [x] `[unit]` Scenario: `lab-wsfc` ADO YAML names `pool: forge-lab-wsfc` and references `forge-lab-vg`. — `Stage72LabRendererTests.RenderAzurePipeline_LabWsfc_Targets_ForgeLabWsfc_Pool_And_VariableGroup` (pins pool name + variable group + `AzureKeyVault@2` task + `forge-lab-kv` + `SecretsFilter` for all three lab env-vars + ADO `$(VAR)` macro syntax; asserts ZERO `vmImage:` (hosted-only) and ZERO `${{ secrets.* }}` (GH-only) leakage).
- [x] `[unit]` Scenario: `lab-wsfc` GH workflow uses `[self-hosted, wsfc-lab]` runner labels and `environment: lab-wsfc`. — `Stage72LabRendererTests.RenderGitHubWorkflow_LabWsfc_Uses_SelfHosted_Labels_And_LabEnvironment` (pins runner labels + GH environment + `${{ secrets.* }}` syntax + ZERO `$(VAR)` macro leakage).
- [x] `[unit]` Scenario: `lab-bare-metal` ADO YAML names `pool: forge-lab-hw`. — `Stage72LabRendererTests.RenderAll_LabBareMetal_Swaps_To_Hardware_Pool_And_Labels` (pins both pool + GH labels swap; reaffirms variable group + Key Vault stay shared across lab types; non-lab types throw `ArgumentOutOfRangeException` from both single-render and `RenderAll`).
- [x] `[unit]` Scenario: Connection-string env-var names appear as `env: LAB_CLUSTER_FQDN: $(LAB_CLUSTER_FQDN)` in ADO and `${{ secrets.LAB_CLUSTER_FQDN }}` in GH. — Pinned across `RenderAzurePipeline_LabWsfc_Targets_ForgeLabWsfc_Pool_And_VariableGroup` (asserts all three ADO `$(VAR)` references), `RenderGitHubWorkflow_LabWsfc_Uses_SelfHosted_Labels_And_LabEnvironment` (asserts all three GH `${{ secrets.* }}` references), and `RenderAll_With_Bootstrap_Includes_PreTest_Step_In_Both_Providers` (asserts the bootstrap step ALSO carries the same env-var wiring with the correct provider syntax in each file).
- [x] `[integration]` Scenario: A `lab-wsfc` phase produces a PR whose YAML, when parsed by `YamlDotNet`, has a valid pipeline shape. — `Stage72LabRendererTests.LabWsfc_Phase_QaE2e_Produces_YamlDotNet_Parseable_Pipeline_Files` (boots factory + seeds 1-phase lab-wsfc spawn + drives `ProduceStageE2eAsync` via DI deterministic strategy; runs BOTH rendered YAMLs through `YamlDotNet.Serialization.DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<object?>` → asserts non-null → proves the renderer didn't emit malformed pipeline files; also re-pins the lab-specific substantive contents flow end-to-end).

---

#### Stage 7.3: `appsettings.e2e.json` + `SetupSkippedException`

**Deliverables:**
- Per-phase `tests/e2e/{phaseSlug}/appsettings.e2e.json` declaring
  variable *names* with empty values.
- Step-def base class
  `tests/Steps/{storyId}/E2eBase.cs` (emitted once per spawn,
  not per stage) that reads env-vars and throws
  `SetupSkippedException` when missing — locally yellow-skipped, on
  CI hard-failed via test settings.

**Implementation Steps:**
- [x] Add `appsettings.e2e.json` template (one per setup type). — `E2eBaseRenderer.RenderAppSettings(setupType, phaseSlug)` declares per-setup-type env-var names (3 LAB_* vars for lab-wsfc / lab-bare-metal; 2 COMPOSE_REDIS_* for compose; empty E2e section for inline).
- [x] Add `E2eBase.cs` template with `SetupSkippedException`. — `E2eBaseRenderer.RenderE2eBaseClass(storyId)` ships an abstract `E2eBase` with `RequireEnv` helper + sibling `SetupSkippedException` carrying the missing var name via a `VarName` property.
- [x] Emit `E2eBase.cs` once at the first qa-e2e PR per spawn; subsequent qa-e2e PRs only emit the per-phase appsettings + scenario files. — Per-spawn `ConcurrentDictionary<string, byte>` on each strategy (`e2eBaseEmittedForSpawn`) gates emission via `TryAdd`; both `CrewStoryArtifactStrategy.ProduceStageE2eAsync` and `DeterministicStoryArtifactStrategy.ProduceStageE2eAsync` fold the renderer output into their returned file dict.

#### Dependencies
- phase-pipeline-yaml-generation/stage-lab-wsfc-lab-bare-metal-setup-emit-self-hosted-pipeline-yaml

#### Test Scenarios
- [x] `[unit]` Scenario: `E2eBase.cs` template compiles standalone (Roslyn parse + emit smoke). — `Stage73AppSettingsAndE2eBaseTests.RenderE2eBaseClass_Compiles_Via_Roslyn` runs both `CSharpSyntaxTree.ParseText` AND `CSharpCompilation.Emit` to a `MemoryStream` against the .NET 8 BCL.
- [x] `[unit]` Scenario: `appsettings.e2e.json` for `lab-wsfc` declares all three lab env-var names. — `Stage73AppSettingsAndE2eBaseTests.RenderAppSettings_LabWsfc_Declares_Three_Lab_EnvVars` `JsonDocument.Parse`s the rendered file + asserts `LAB_CLUSTER_FQDN` / `LAB_ADMIN_USER` / `LAB_ADMIN_PASSWORD` all surface as keys with empty-string values (also covers lab-bare-metal + inline as cross-set guards).
- [x] `[unit]` Scenario: `SetupSkippedException` carries the missing var name in its message. — `Stage73AppSettingsAndE2eBaseTests.SetupSkippedException_Message_Carries_VarName` pins the source-string format AND reflectively instantiates the type after Roslyn compile; verifies `.Message` contains the var name + `.VarName` exposes it verbatim + `E2eBase.RequireEnv` actually throws when the env var is unset.
- [x] `[integration]` Scenario: Spawn with two qa-e2e stages emits exactly one `E2eBase.cs` (idempotency check). — `Stage73AppSettingsAndE2eBaseTests.Two_QaE2e_Stages_For_Same_Spawn_Emit_Exactly_One_E2eBase` drives `ProduceStageE2eAsync` twice (foundation + ingest) on the same spawn through a single `DeterministicStoryArtifactStrategy` instance + asserts the combined E2eBase.cs count is exactly 1 (first wins, second is the cache hit), with a reset-then-re-emit assertion at the end.

---

### Phase 8: Swarm-control UI

#### Dependencies
- phase-orchestrator-routing-on-category

#### Stage 8.1: Stacked impl/e2e rows in `SwarmMasterPanel`

**Deliverables:**
- Each stage column in `SwarmMasterPanel.tsx` renders an impl row and
  (when present) a stacked qa-e2e row directly below it.
- qa-e2e row uses `--sc-t-qa` (green) accent; impl row keeps
  `--sc-t-eng` (cyan).
- When a stage has no qa-e2e workstream, only the impl row renders
  (no empty placeholder).

**Implementation Steps:**
- [ ] Add `qaE2eByImplId: Map<string, Workstream>` derived selector to `useSpawnStore`.
- [ ] Update the phase-board render to read both rows from the selector.
- [ ] Add a `--sc-t-qa` CSS token if not already present.
- [ ] Empty-state: when `qaE2eByImplId.get(impl.id)` is undefined, skip the stack — don't render a blank slot.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [ ] `[ui-unit]` Scenario: Selector returns the qa-e2e ws for an impl ws when one exists.
- [ ] `[ui-unit]` Scenario: Selector returns undefined for an impl ws with no qa-e2e sibling.
- [ ] `[ui-e2e]` Scenario: Playwright — story with 3 stages (2 with e2e, 1 without) renders 5 boxes in 3 columns.
- [ ] `[ui-e2e]` Scenario: Playwright — qa-e2e box is rendered with the green team accent (computed style assertion).

---

#### Stage 8.2: DAG edge router updates

**Deliverables:**
- `SwarmStoryView.tsx` edge layer draws a per-stage downward edge
  from impl box to its qa-e2e box.
- Cross-stage e2e deps (from §7 plan parser) render as long edges
  routed by the existing edge router.
- The previous rightmost-column QA cluster rendering is removed.

**Implementation Steps:**
- [ ] Remove the "group qa-e2e in the rightmost column" code path.
- [ ] Add a per-stage `{from: impl.id, to: qa-e2e.id}` edge to the input dataset.
- [ ] Verify cross-stage e2e deps already flow through the existing edge dataset (they should — they're just `WorkstreamDepRow` rows).

#### Dependencies
- phase-swarm-control-ui/stage-stacked-impl-e2e-rows-in-swarmmasterpanel

#### Test Scenarios
- [ ] `[ui-unit]` Scenario: Edge dataset for a 2-stage story has exactly N edges where N = (deps between impls) + (impl→qa-e2e per qa-e2e stage).
- [ ] `[ui-e2e]` Scenario: Playwright — visual diff against the screenshot in the user's request: the rightmost QA column is gone.
- [ ] `[ui-e2e]` Scenario: Playwright — clicking a qa-e2e box opens the workstream detail panel scoped to that row (same drill-down as impl boxes).

---

#### Stage 8.3: Story-card phase badge + legacy compat

**Deliverables:**
- Story-card phase badge stops showing `qa` for new spawns.
- Legacy spawns whose `Phase='qa'` continue to show the badge, with
  a tooltip explaining it's a legacy state being drained.

**Implementation Steps:**
- [ ] Update `StoryCard.tsx` badge map to omit `qa` from the canonical phase list.
- [ ] Add a legacy-detector: when `phase==='qa'`, render a muted badge with the tooltip.

#### Dependencies
- phase-swarm-control-ui/stage-stacked-impl-e2e-rows-in-swarmmasterpanel

#### Test Scenarios
- [ ] `[ui-unit]` Scenario: New spawn (Phase rotates plan→decompose→implement→review→done) — `qa` never appears.
- [ ] `[ui-unit]` Scenario: Legacy spawn (Phase='qa') renders muted badge with tooltip.
- [ ] `[ui-e2e]` Scenario: Playwright — seed a legacy spawn via API, refresh /workstreams, assert the muted badge renders.

---

### Phase 9: Integration tests + docs sync

#### Dependencies
- phase-orchestrator-routing-on-category
- phase-pipeline-yaml-generation
- phase-swarm-control-ui

#### Stage 9.1: `Phase28E2eRefactorTests` integration suite

**Deliverables:**
- New test file `src/forge/Forge.Api.Tests/Phase28E2eRefactorTests.cs`,
  `[Collection("Phase28E2eRefactor")]` annotation so it doesn't
  collide with the existing gate/orchestrator suites.
- Covers the headline scenarios end-to-end against the deterministic
  strategy.

**Implementation Steps:**
- [ ] Add the file with `[Collection]` + shared fixture pattern.
- [ ] Port the existing Phase28OrchestratorTests test bed (DI setup) into the new collection.
- [ ] Write the scenarios listed below.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [ ] `[integration]` Scenario: Story with 3 stages (all with test scenarios) drives plan→done; 7 PRs land: plan + 3 impl + 3 qa-e2e.
- [ ] `[integration]` Scenario: Story with 3 stages where stage 2 has no test scenarios — 6 PRs land (no qa-e2e for stage 2).
- [ ] `[integration]` Scenario: Dependency scheduler unblocks qa-e2e-S2 only after impl-S2 PR merges, even when impl-S3 has merged earlier.
- [ ] `[integration]` Scenario: Cross-stage e2e — qa-e2e-S3 with `Deps=[S2, S3]` waits for both impl-S2 and impl-S3 to merge.
- [ ] `[integration]` Scenario: Restart-resume — kill the orchestrator mid-qa-e2e, restart, spawn drives to `done`.
- [ ] `[integration]` Scenario: Spawn never enters `Phase='qa'` (assertion on the `phase-changed` event log).
- [ ] `[integration]` Scenario: A `Phase='qa'` legacy spawn finishes draining under the new orchestrator without errors.

---

#### Stage 9.2: `Phase28E2eSetupContractTests` integration suite

**Deliverables:**
- New test file `src/forge/Forge.Api.Tests/Phase28E2eSetupContractTests.cs`
  covering the §8 setup contract end-to-end (linter + reader +
  pipeline renderer + ProduceStageE2eAsync output composition).

**Implementation Steps:**
- [ ] Author the file; reuse the deterministic strategy fixture.
- [ ] One scenario per setup type plus the failure modes from §6.2.

#### Dependencies
- phase-integration-tests-docs-sync/stage-phase28e2erefactortests-integration-suite

#### Test Scenarios
- [ ] `[integration]` Scenario: `inline` story — qa-e2e PR contains exactly `*.feature` + `*Steps.cs` + `E2eBase.cs` (first stage only).
- [ ] `[integration]` Scenario: `compose` story — qa-e2e PR also contains `docker-compose.yml` + `azure-pipeline.yml` + `.github/workflows/e2e-{phase}.yml`.
- [ ] `[integration]` Scenario: `lab-wsfc` story — qa-e2e PR ADO YAML names `pool: forge-lab-wsfc` and references `forge-lab-kv` Key Vault.
- [ ] `[integration]` Scenario: `lab-bare-metal` story — qa-e2e PR GH workflow names `[self-hosted, lab-hw]` runner labels and `environment: lab-hw`.
- [ ] `[integration]` Scenario: Architect linter rejects an `e2e-scenarios.md` missing `### Setup` (score < 90) and accepts the corrected version on next iter.

---

#### Stage 9.3: Docs sync — `forge_implementation.md` + `workstream.md` + QA template

**Deliverables:**
- `docs/forge_implementation.md` Phase 28 sections 28.7 / 28.8 / 28.9
  and the lifecycle ASCII updated to match this doc; cross-link from
  the Phase 28 header to `docs/forge/e2e.md`.
- `docs/forge/workstream.md` §8 setup contract appended.
- `agents/templates/qa-system.md` updated with the setup-type-aware
  QA prompt (mirrors the new `e2e-scenarios` brief in §8.4).

**Implementation Steps:**
- [ ] Edit `docs/forge_implementation.md` to replace the Phase 5 QA narrative with a pointer to this doc.
- [ ] Append the §8 setup contract to `docs/forge/workstream.md`.
- [ ] Update `agents/templates/qa-system.md` with the setup-aware prompt.
- [ ] Search the repo for "Phase 5 QA" / "QA phase" references and update or remove them where stale.

#### Dependencies
- phase-integration-tests-docs-sync/stage-phase28e2esetupcontracttests-integration-suite

#### Test Scenarios
- [ ] `[unit]` Scenario: `PlanDocBriefConsistencyTests` passes against the new `e2e-scenarios` brief.
- [ ] `[integration]` Scenario: No grep hits in `src/` for `DriveQaAsync.*NEW behaviour` — confirms cleanup.
- [ ] Scenario: Manual — render the updated docs in a markdown preview and confirm the §10 implementation tree is readable.

---

#### Stage 9.4: Operator runbook for the refactor rollout

**Deliverables:**
- New file `docs/forge/e2e-rollout.md` (or appended section in
  `docs/forge/setup.md`) covering:
  - How to recognise a legacy `Phase='qa'` spawn vs a new spawn.
  - How to manually advance a stuck legacy spawn to `done`.
  - How to back-out the migration if production tips over (re-set
    `Category='impl'` on every row, redeploy the prior orchestrator
    binary).
  - How to seed lab credentials in both ADO Key Vault and GitHub
    environment.

**Implementation Steps:**
- [ ] Write the rollout doc.
- [ ] Link it from `docs/forge/README.md`'s doc table.
- [ ] Verify the back-out instructions against a snapshot DB.

#### Dependencies
- phase-integration-tests-docs-sync/stage-docs-sync-forge-implementation-md-workstream-md-qa-template

#### Test Scenarios
- [ ] Scenario: Manual — follow the legacy-drain runbook against a seeded DB; spawn reaches `done`.
- [ ] Scenario: Manual — follow the back-out runbook; orchestrator boots on the prior binary without throwing on the new `Category` column (additive change, prior code ignores it).
- [ ] Scenario: Manual — lab-credentials walkthrough: seed `LAB_CLUSTER_FQDN` in `forge-lab-kv`, run a `lab-wsfc` story locally, confirm the test step picks up the value.

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
