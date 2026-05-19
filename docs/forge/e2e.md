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
- [ ] Add the new abstract method to `IStoryArtifactStrategy`.
- [ ] Annotate the two legacy methods with `[Obsolete]`.
- [ ] Provide a default (throw-NotImplemented) implementation in any internal test double that doesn't need it yet.

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [ ] `[unit]` Scenario: Interface contract test — every registered `IStoryArtifactStrategy` implementation responds to `ProduceStageE2eAsync` (reflection-driven assertion).
- [ ] `[unit]` Scenario: `[Obsolete]` warnings surface on the legacy methods (compile-time check via roslyn analyser test).

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
- [ ] Implement `ProduceStageE2eAsync` in `Forge.Api.Tests/Infrastructure/DeterministicStoryArtifactStrategy.cs`.
- [ ] Read the workstream's resolved `PlanAnchor` to derive `phaseSlug`/`stageSlug`.
- [ ] Read the merged `e2e-scenarios.md` from in-memory git to detect setup type (default `inline` when absent).
- [ ] Return the file dict; ensure paths are repo-relative with forward slashes.

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-define-the-interface-method

#### Test Scenarios
- [ ] `[unit]` Scenario: `inline` setup returns exactly two entries (`*.feature` + `*Steps.cs`).
- [ ] `[unit]` Scenario: `compose` setup returns four entries (above + ADO YAML + GH workflow).
- [ ] `[unit]` Scenario: `lab-wsfc` setup returns four entries with `LAB_CLUSTER_FQDN` referenced as an env-var in the generated YAML.
- [ ] `[integration]` Scenario: Full orchestrator drive (deterministic) on a 1-stage story produces these files on the qa-e2e PR branch.

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
- [ ] Implement `ProduceStageE2eAsync` on `CrewStoryArtifactStrategy`.
- [ ] Compose the QA-crew prompt from: workstream metadata + impl-PR diff (via `git diff base..ws-branch`) + setup block from merged `e2e-scenarios.md`.
- [ ] Run the gen↔eval loop with `BudgetForPhase(spawn.StoryPoints, "qa-e2e")` — add the budget key.
- [ ] After loop passes, run `dotnet test --filter Category=e2e` against the produced step-defs; fail the loop on non-zero exit.

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-deterministicstoryartifactstrategy-producestagee2easync

#### Test Scenarios
- [ ] `[unit]` Scenario: Prompt builder includes the impl diff under a `## Implementation diff` heading.
- [ ] `[unit]` Scenario: Setup block is parsed from `e2e-scenarios.md` and surfaces in the prompt's `## Setup` section.
- [ ] `[unit]` Scenario: Empty `### Test Scenarios` in the stage causes the method to throw `InvalidOperationException` (Phase 3.1 guards against this calling at all, but defense-in-depth).
- [ ] `[integration]` Scenario: End-to-end with a fake `IAgentRunner` that returns canned files; verify the `dotnet test` gate is invoked and a non-zero exit re-enters the loop.

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
- [ ] Add a `IsQaE2e(WorkstreamRow w) => w.Category == "qa-e2e"` helper.
- [ ] In `DriveImplementAsync`'s per-workstream loop, branch on `IsQaE2e`.
- [ ] qa-e2e branch: skip target-discovery (no impl files to edit); use the new `ProduceStageE2eAsync`.
- [ ] PR title format: `"[e2e] {stage.Name}"`; body links back to the impl PR by id.
- [ ] Dep-gate honoured by the existing `WorkstreamDepRow` query — no extra code path needed.

#### Dependencies
- phase-istoryartifactstrategy-producestagee2easync/stage-define-the-interface-method

#### Test Scenarios
- [ ] `[unit]` Scenario: `DriveImplementAsync` invoked on a `qa-e2e` row whose dep is `complete` opens a PR with `[e2e]` prefix.
- [ ] `[unit]` Scenario: `DriveImplementAsync` invoked on a `qa-e2e` row whose dep is `active` skips (returns false, no PR opened).
- [ ] `[integration]` Scenario: A 2-stage story (both with test scenarios) drives plan→done; 5 PRs land in order: plan, impl-S1, impl-S2, e2e-S1, e2e-S2.
- [ ] `[integration]` Scenario: A 2-stage story where S2 deps on S1 and both have e2e — the four ws PRs serialize correctly via the dep scheduler.

---

#### Stage 5.2: Done-gate refactor — drop "QA PR merged" predicate

**Deliverables:**
- `StorySpawnOrchestrator` completion check requires every workstream
  (`Category` agnostic) to be `Status='complete'`.
- The legacy `spawn.QaPrId` check is removed from the new code path
  but kept in the legacy `DriveQaAsync` for back-compat with paused
  spawns.

**Implementation Steps:**
- [ ] Locate the completion check (currently invoked from `DriveQaAsync` at `StorySpawnOrchestrator.cs:3717`).
- [ ] Hoist it into a `IsSpawnDoneAsync` helper that consults workstreams only.
- [ ] Call `IsSpawnDoneAsync` from `DriveReviewAsync` after each ws PR merge.
- [ ] When `IsSpawnDoneAsync` returns true, advance `Phase = 'done'` and emit `done` SignalR event (existing helper).

#### Dependencies
- phase-orchestrator-routing-on-category/stage-driveimplementasync-branches-on-category

#### Test Scenarios
- [ ] `[unit]` Scenario: `IsSpawnDoneAsync` returns true when all ws (impl + qa-e2e) are `complete`.
- [ ] `[unit]` Scenario: `IsSpawnDoneAsync` returns false when one qa-e2e ws is `active`.
- [ ] `[integration]` Scenario: After the last qa-e2e PR merges, the spawn transitions to `done` without visiting `Phase='qa'`.

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
- [ ] Audit `DriveQaAsync` (`StorySpawnOrchestrator.cs:3615`) and confirm it operates on `spawn.QaPrId` exclusively — if any branch leaks into the new path, gate it on `spawn.QaPrId is not null`.
- [ ] Add a no-op guard at the top of `DriveQaAsync`: `if (spawn.QaPrId is null && all-workstreams-via-new-shape) return false`.
- [ ] Emit a one-time warning log when an existing `QaPrId` is detected so operators see the legacy drain in action.

#### Dependencies
- phase-orchestrator-routing-on-category/stage-done-gate-refactor-drop-qa-pr-merged-predicate

#### Test Scenarios
- [ ] `[unit]` Scenario: Spawn with `Phase='qa'` and `QaPrId` non-null still drains step-def PRs.
- [ ] `[unit]` Scenario: Spawn with `Phase='qa'` and `QaPrId` null returns false from `DriveQaAsync` (no work attempted).
- [ ] `[integration]` Scenario: Seed a DB with a `Phase='qa' + QaPrId=…` row, run the scheduler; spawn drains to `done` exactly as it did pre-refactor.

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
- [ ] Replace the one-line `e2e-scenarios` entry in `BuildPlanDocSpecs` with the §8.4 prompt.
- [ ] Add the same content to `PlanDocBriefConsistencyTests` so the resume-brief stays in sync (the test catches drift introduced in 2026-05-04).

#### Dependencies
- _none — start stage_

#### Test Scenarios
- [ ] `[unit]` Scenario: `BuildPlanDocSpecs["e2e-scenarios"].Brief` contains the four required H3 sub-bullets (Type, Local, CI runner, Secrets).
- [ ] `[unit]` Scenario: `PlanDocBriefConsistencyTests` passes — the brief returned by `ResumeSingleArtifactAsync("e2e-scenarios")` matches the one in `BuildPlanDocSpecs`.

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
- [ ] Add `E2eScenariosLinter` static class with `LintResult Validate(string md)`.
- [ ] Call the linter from the evaluator prompt builder; surface lint errors as a structured evaluator feedback section.
- [ ] Linter error codes follow the existing `plan.*` family: `e2e.setup_missing`, `e2e.setup_type_invalid`, `e2e.lab_secrets_incomplete`.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-update-buildplandocspecs-e2e-scenarios-brief

#### Test Scenarios
- [ ] `[unit]` Scenario: Missing `### Setup` under a phase H1 → `e2e.setup_missing`.
- [ ] `[unit]` Scenario: `Type: lambda` (not in closed set) → `e2e.setup_type_invalid`.
- [ ] `[unit]` Scenario: `Type: lab-wsfc` with only a KeyVault bullet → `e2e.lab_secrets_incomplete`.
- [ ] `[unit]` Scenario: Valid `compose` setup → no lint errors.
- [ ] `[integration]` Scenario: Architect evaluator refuses pass (score < 90) on an `e2e-scenarios.md` that fails the linter; passes on a corrected version.

---

#### Stage 6.3: Thread parsed setup into `ProduceStageE2eAsync` prompt

**Deliverables:**
- Helper `E2eScenariosSetupReader` that, given the merged
  `e2e-scenarios.md` and a `phaseSlug`, returns the setup-type +
  full setup-block markdown.
- `CrewStoryArtifactStrategy.ProduceStageE2eAsync` consumes it to
  drive both prompt content AND pipeline-YAML emission.

**Implementation Steps:**
- [ ] Implement `E2eScenariosSetupReader.ReadForPhase(string md, string phaseSlug)`.
- [ ] Wire the reader into `ProduceStageE2eAsync` (Phase 4.3) before building the QA-crew prompt.
- [ ] Cache the parsed setup map on the strategy instance per spawn to avoid re-reading the file once per stage.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-evaluator-linter-for-setup-block
- phase-istoryartifactstrategy-producestagee2easync/stage-crewstoryartifactstrategy-producestagee2easync

#### Test Scenarios
- [ ] `[unit]` Scenario: Reader returns `inline` setup for a phase whose `### Setup` declares it.
- [ ] `[unit]` Scenario: Reader throws `InvalidOperationException` when the requested phase slug is absent.
- [ ] `[unit]` Scenario: Cache hit — second call for the same `(spawnId, phaseSlug)` doesn't re-parse.
- [ ] `[integration]` Scenario: A 2-phase story (`inline` + `lab-wsfc`) drives end-to-end; the qa-e2e PR for phase 2 carries the lab YAML, phase 1's qa-e2e PR doesn't.

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
- [ ] Add the three template files as embedded resources.
- [ ] Write `ComposePipelineRenderer` that substitutes `{phaseSlug}` / `{services}` placeholders.
- [ ] Plug the renderer into `ProduceStageE2eAsync` behind a setup-type switch.

#### Dependencies
- phase-e2e-scenarios-md-setup-contract/stage-thread-parsed-setup-into-producestagee2easync-prompt

#### Test Scenarios
- [ ] `[unit]` Scenario: Renderer produces non-empty YAML for a 2-service compose stack.
- [ ] `[unit]` Scenario: Rendered ADO YAML's `pool:` block is `vmImage: ubuntu-latest` (hosted) — no self-hosted pool reference.
- [ ] `[unit]` Scenario: Rendered GitHub workflow `runs-on:` is `ubuntu-latest`.
- [ ] `[integration]` Scenario: End-to-end spawn with one `compose`-phase produces all three YAML files on the qa-e2e PR.

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
- [ ] Add the four template files (2 setup types × 2 providers).
- [ ] Write `LabPipelineRenderer` with `(SetupType, Provider)` parameters.
- [ ] Wire into `ProduceStageE2eAsync` after the compose switch.
- [ ] Include a pre-test step calling
  `tests/e2e/{phase-slug}/bootstrap.ps1` when the setup block names one.

#### Dependencies
- phase-pipeline-yaml-generation/stage-compose-setup-emit-docker-compose-yml-hosted-runner-yaml

#### Test Scenarios
- [ ] `[unit]` Scenario: `lab-wsfc` ADO YAML names `pool: forge-lab-wsfc` and references `forge-lab-vg`.
- [ ] `[unit]` Scenario: `lab-wsfc` GH workflow uses `[self-hosted, wsfc-lab]` runner labels and `environment: lab-wsfc`.
- [ ] `[unit]` Scenario: `lab-bare-metal` ADO YAML names `pool: forge-lab-hw`.
- [ ] `[unit]` Scenario: Connection-string env-var names appear as `env: LAB_CLUSTER_FQDN: $(LAB_CLUSTER_FQDN)` in ADO and `${{ secrets.LAB_CLUSTER_FQDN }}` in GH.
- [ ] `[integration]` Scenario: A `lab-wsfc` phase produces a PR whose YAML, when parsed by `YamlDotNet`, has a valid pipeline shape (smoke-validates we didn't ship malformed YAML).

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
- [ ] Add `appsettings.e2e.json` template (one per setup type).
- [ ] Add `E2eBase.cs` template with `SetupSkippedException`.
- [ ] Emit `E2eBase.cs` once at the first qa-e2e PR per spawn; subsequent qa-e2e PRs only emit the per-phase appsettings + scenario files.

#### Dependencies
- phase-pipeline-yaml-generation/stage-lab-wsfc-lab-bare-metal-setup-emit-self-hosted-pipeline-yaml

#### Test Scenarios
- [ ] `[unit]` Scenario: `E2eBase.cs` template compiles standalone (Roslyn parse + emit smoke).
- [ ] `[unit]` Scenario: `appsettings.e2e.json` for `lab-wsfc` declares all three lab env-var names.
- [ ] `[unit]` Scenario: `SetupSkippedException` carries the missing var name in its message.
- [ ] `[integration]` Scenario: Spawn with two qa-e2e stages emits exactly one `E2eBase.cs` (idempotency check).

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
