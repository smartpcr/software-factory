# Forge — Release gate: deploy-to-lab pipeline gate for coding workstreams

> **Source of truth.** This document defines the **release gate** — the third gate in a
> coding workstream's delivery chain, after the build gate (P26) and the test gate
> (P56/P59). Where it touches the story-spawn lifecycle it refines
> `docs/forge/e2e.md`; where it touches repo configuration it extends the Phase 26/29
> build-gate fields. The rest of `forge_implementation.md` is unchanged.

---

## 1. What this is — at a glance

**Today.** A coding workstream is gated twice, both **inside the worktree on the Forge
host**: the build gate (`Init → Restore → Build`) and the test gate (impacted-test
selection, P59). Both run before push/PR. Nothing ever validates the change **as a
deployed artifact on real infrastructure** — the qa-e2e workstream authors and runs
e2e tests, but on the same host, against an in-process or compose stack
(`docs/forge/e2e.md` §8.3: for `lab-*` setups the gate host *skips*).

**After.** Repos can opt in to a **release gate**: a CI pipeline definition committed
to the repo (**GitHub Actions workflow** or **ADO pipeline**, matching the repo's
provider) that Forge triggers per-workstream after review passes and before merge.
The pipeline:

1. **Packages** the workstream branch into release artifacts (repo-configured
   `PackageCommand`, auto-detected default per ecosystem).
2. **Copies** the artifacts to a **target lab machine** (self-hosted runner pool +
   secrets conventions from `docs/forge/e2e.md` §8.2/§8.3 — never literal hosts or
   credentials in the repo).
3. **Deploys** them on the target via the repo's `tests/release/deploy.ps1` contract
   script.
4. **Runs the e2e suite filtered to the workstream's stage scenarios** against the
   deployed instance and publishes results.

The gate **only fires for workstreams with concrete, testable deliverables** — a
skeleton/scaffolding-only workstream (interfaces, stubs, DI registration, docs) is
skipped with an explicit, audited reason (§4).

```
per-iter:    BUILD ─→ QUALITY ─→ TEST                (worktree, Forge host — unchanged)
post-pass:   BUILD ─→ QUALITY ─→ TEST                (worktree, Forge host — unchanged)
             └─→ push ─→ PR ─→ REVIEW passes
                                  │
                                  ▼
                        ┌───────────────────┐
                        │   RELEASE GATE    │   (CI pipeline, lab runner)
                        │ package → copy →  │
                        │ deploy → e2e      │
                        └────────┬──────────┘
                            pass │ fail/skip-reason
                                  ▼
                              MERGE ─→ complete
```

**Scope.** The gate applies to **impl** workstreams (`Category='impl'`). qa-e2e
workstreams author the tests the gate runs; they are not themselves release-gated.
Plan-doc workstreams never are.

---

## 2. Placement in the lifecycle

The gate sits on the **`review → merging`** transition in
`StorySpawnOrchestrator.DriveReviewAsync` — the point where today the orchestrator
calls `ApproveAndMergeAsync` once review findings are resolved
(`StorySpawnOrchestrator.cs:4687/4735/5454`).

New workstream status: **`releasing`** (`WorkstreamStatuses.Releasing`), between
`review` and `merging`:

```
active → review → releasing → merging → complete
                     │
                     └─ (skip: applicability says no) ──→ merging   [audited]
                     └─ (fail) ──→ stalled  [StallReason + 📎 log chip + Retry]
```

Rationale for pre-merge (not post-merge): the artifact validated on the lab is built
from **exactly the PR branch under review**; a red release run blocks the merge
instead of poisoning the base for every dependent stage. The existing
`Forge:Stories:GateImplOnPriorStageE2e` semantics are preserved — a downstream stage
that deps on `S — E2E` now transitively waits for S's release run too, because S's
qa-e2e companion can only complete after S merges.

`WorkstreamMergeAdvancer` (external merges) is **not** release-gated: a human merging
the PR upstream is an explicit operator override, same as today for reviews.

---

## 3. Repo configuration (mapped to repo)

Follows the Phase 26/29 pattern exactly: columns on `RepoRow` (`Entities.cs`, under a
new `// -- Phase 98 — release gate --` header), a `Phase98Migration` (next free slot
after `Phase97Migration`), DTO fields in `ForgeContracts.cs` (attach + patch), write
path in `ProductsEndpoints.PatchRepo`, edit surface in `EditRepoModal.tsx`.

| Field | Type / default | Meaning |
|---|---|---|
| `ReleaseGateEnabled` | `bool`, default `false` | Master switch. Off = whole feature invisible for this repo. |
| `ReleasePipelinePath` | `string?` | Repo-relative path of the committed pipeline definition. Defaults on scaffold: `.github/workflows/forge-release-gate.yml` (GitHub) / `pipelines/forge-release-gate.yml` (ADO). |
| `ReleasePipelineId` | `string?` | **ADO only.** Pipeline definition id (needed to queue runs). Captured automatically by the scaffold endpoint (§5.4) or entered manually. |
| `PackageCommand` | `string?` | Build/package step producing release artifacts. `null` → `BuildSystemDetector` default (§5.2). |
| `ReleaseArtifactPath` | `string`, default `.artifacts/release` | Repo-relative staging dir the `PackageCommand` must populate; the pipeline publishes/copies this dir. |
| `ReleaseRunnerPool` | `string`, default `forge-lab-wsfc` | ADO agent pool name. |
| `ReleaseRunnerLabels` | `string`, default `self-hosted, wsfc-lab` | GitHub runner labels (comma-separated). |
| `ReleaseSecretsRef` | `string`, default `forge-lab-vg` (ADO) / `lab-wsfc` (GitHub) | ADO variable group (backed by `forge-lab-kv` Key Vault) or GitHub environment name. Surfaces `LAB_TARGET_HOST`, `LAB_ADMIN_USER`, `LAB_ADMIN_PASSWORD` per the e2e.md §8.3 convention (env-var *names* in the repo, values only in the secret store). |
| `ReleaseE2eTestCommand` | `string?` | Template for running e2e on/against the lab target, with a `{testFilter}` placeholder (§6). `null` → ecosystem default, e.g. `dotnet test tests/E2e --filter "{testFilter}"`. |
| `ReleaseTimeoutSec` | `int`, default `3600` | Wall-clock cap per run; poller cancels + marks `timed-out` beyond it. |

The three shapes on `RepoRow` (git / sync / build-gate, P23.3) become four; any set
may still be empty. `IsReleaseGateConfigured` (helper, not a column) =
`ReleaseGateEnabled && ReleasePipelinePath != null && (kind == github || ReleasePipelineId != null)`.

---

## 4. Applicability — concrete deliverables, not skeleton

The gate must not burn a lab slot on a workstream that ships nothing observable.
Applicability is decided **at gate time** (review has passed, the diff is final) by
`ReleaseGateApplicability.Decide(repo, spawn, workstream, changedFiles, diffText)`
— a pure, unit-testable class in `Services/Stories/`. Decision + reasons are
persisted on the run row and emitted as `task.release-gate.skipped` when negative.

Ordered rules; first hit wins:

1. **`disabled`** — `IsReleaseGateConfigured` is false.
2. **`not-impl`** — `workstream.Category != 'impl'`.
3. **`declared-skip`** — the stage's plan section carries `### Release Gate` with
   `- **Applies**: no` (operator/architect intent wins, both directions — see below).
4. **`no-e2e-companion`** — the stage minted no trailing qa-e2e workstream (no
   `### Test Scenarios`), **and** no earlier merged stage of the same spawn already
   shipped e2e scenarios that cover it via `### Cross-Stage Dependencies`. No
   scenarios ⇒ nothing to run on the lab ⇒ nothing the gate can verify.
5. **`docs-only`** — every changed file matches the existing docs-only classifier
   used by the test gate short-circuit (docs/markdown/images).
6. **`skeleton-only`** — the **concrete-deliverable heuristic** (below) finds no
   substantive executable change.
7. **`applicable`** — otherwise.

### 4.1 Declared intent (plan layout v2 extension)

A stage in `implementation-plan.md` may carry an optional H3:

```markdown
### Release Gate
- **Applies**: yes | no
- **Reason**: <one line — required when overriding the heuristic>
```

`PlanParser` stores it as `Stage.ReleaseGate : bool?` (null = not declared);
decomposition copies it to `DecompositionWorkstream.ReleaseGate` →
`WorkstreamRow.ReleaseGateDeclared` (`TEXT NULL`, `'yes'|'no'|NULL`). Declared
`yes` skips rules 5–6 (forces the gate even for a small diff); declared `no` is
rule 3. Both `ProduceDecompositionAsync` and `PlanReconciler` must map it (same
dual-write rule as `DecompositionDepsResolver` — the reconciler re-derives rows
every tick and would otherwise fight the orchestrator).

### 4.2 Concrete-deliverable heuristic (`skeleton-only`)

Input: the workstream's final changed-file set + unified diff
(`IStoryGitOps.GetChangedFilesAsync` + `git diff base...branch`). A workstream is
**skeleton-only** when **no** changed source file contains a *substantive hunk*.
A hunk is substantive unless every added line is one of:

- blank / comment / using-import / namespace / attribute;
- a type or member **declaration line** (signature without body, interface member,
  abstract member, record positional decl);
- a stub body: `throw new NotImplementedException`, `todo!()`, `unimplemented!()`,
  bare `pass`, `return default`/`return null` as the *only* statement, `// TODO`;
- DI/registration boilerplate (`services.Add…`, exports/barrel `index.ts` lines);
- pure config/manifest edits (`.csproj`, `package.json`, lockfiles).

Tuning constants live in `ReleaseGateConstants` (locked by a spec-consistency test,
same pattern as `ScoringConstants`): `MinSubstantiveHunks = 1`,
`SkeletonStubPatterns[]`, `DocsOnlyExtensions[]`. The heuristic is deliberately
**permissive** (one real hunk ⇒ applicable): false-positive lab runs cost minutes;
false-negative skips cost a missed regression on hardware. Anything borderline the
architect can pin with `### Release Gate`.

---

## 5. The pipeline contract

### 5.1 What the committed pipeline must do

The pipeline definition is a **normal, reviewable file in the repo** — Forge
generates it once (scaffold, §5.4), the repo owner may edit it, and the gate simply
triggers it per run. Contract (both providers):

- **Inputs / parameters** (all strings): `forgeRunKey` (correlation id, required),
  `branch` (workstream branch), `testFilter` (§6), `artifactPath`
  (`ReleaseArtifactPath`), `storyId`, `workstreamId` (for traceability only).
- **Stage 1 — package** (hosted or lab runner): checkout `branch`, run
  `PackageCommand`, fail on non-zero, publish `artifactPath` as pipeline artifact
  `forge-release`.
- **Stage 2 — deploy + e2e** (lab runner: `pool: {ReleaseRunnerPool}` / GH
  `runs-on: [{ReleaseRunnerLabels}]`, secrets from `ReleaseSecretsRef`): download
  artifact → copy to the target machine (`LAB_TARGET_HOST` via the runner's
  transport — `Copy-Item -ToSession` on Windows labs) → run
  `tests/release/deploy.ps1 -ArtifactPath <staged> -TargetHost $env:LAB_TARGET_HOST
  -AdminUser $env:LAB_ADMIN_USER -AdminPassword $env:LAB_ADMIN_PASSWORD` → run
  `ReleaseE2eTestCommand` with `{testFilter}` substituted, `LAB_*` env-vars wired
  (the e2e step-def base class already reads them, e2e.md §8.3) → publish TRX/logs
  as artifact `forge-release-results`.
- **Exit** — pipeline result is the gate verdict. No skip path on the lab runner:
  a missing `deploy.ps1` or absent secret is a **hard fail** with a clear message.

### 5.2 Repo-side contract files

| Path | Authored by | Purpose |
|---|---|---|
| `{ReleasePipelinePath}` | scaffolded, owner-editable | The pipeline definition above. |
| `tests/release/deploy.ps1` | scaffolded **skeleton**, owner/impl-workstream completes | Installs/starts the artifact on the target. Contract params: `-ArtifactPath -TargetHost -AdminUser -AdminPassword`. Must be idempotent (redeploy over a previous run). |
| `tests/release/healthcheck.ps1` | optional | Post-deploy readiness probe; pipeline calls it when present. |

`BuildSystemDetector.Detect` gains a `PackageCommand` on `DetectedCommands`:
`.sln`/`.csproj` → `dotnet publish -c Release -o .artifacts/release`; `package.json`
→ `npm run build` (+ `npm pack` when `bin`/`files` present); `Cargo.toml` →
`cargo build --release` + copy; `go.mod` → `go build -o .artifacts/release/ ./...`.
Same precedence as today: repo override wins over detection.

### 5.3 Renderer

`ReleaseGateRenderer` (static, `Services/Stories/Templates/`, sibling of
`E2eLabRenderer` and reusing its pool/secret conventions):

- `RenderGitHubWorkflow(repo)` → `on: workflow_dispatch` with the §5.1 inputs,
  `run-name: "forge-release-gate ${{ inputs.forgeRunKey }}"` (**the GitHub
  correlation mechanism** — see §7.3), two jobs (`package` hosted,
  `deploy-e2e` on `[{labels}]` with `environment: {ReleaseSecretsRef}`).
- `RenderAzurePipeline(repo)` → parameterized YAML, `trigger: none`, stage 1 on
  hosted pool, stage 2 `pool: {ReleaseRunnerPool}` with
  `variables: - group: {ReleaseSecretsRef}` + `AzureKeyVault@2`.
- `RenderDeployScriptSkeleton(repo)` → `deploy.ps1` with the contract params, a
  fail-fast body (`throw "deploy.ps1 not implemented for this repo"`), and comments
  pointing at this doc.

Templates embed **no hostnames and no credentials** — only env-var/secret *names*
(evaluator rule inherited from e2e.md §8.4).

### 5.4 Scaffolding (one-time per repo)

`POST /api/repos/{id}/scaffold-release-gate` (PO/tech-lead role):

1. Render the three files; **commit them to the default branch** via server-owned
   git (audited in `forge_actions`; direct push, no PR — these are inert until
   enabled and both providers require the definition to exist on the default branch
   before it can be dispatched).
2. **ADO:** create the pipeline definition
   (`POST {project}/_apis/pipelines` referencing the YAML path), store the returned
   id in `ReleasePipelineId`.
3. Set `ReleasePipelinePath`; leave `ReleaseGateEnabled` for the operator to flip
   after filling in `deploy.ps1` and provisioning secrets.

The endpoint is idempotent (existing files are left untouched and reported).

---

## 6. Which e2e tests run

The gate runs **the workstream's own stage scenarios plus any cross-stage scenarios
that name it** — exactly the set its qa-e2e companion owns
(`tests/Features/{storyId}/{phaseSlug}/{stageSlug}.feature`, e2e.md §5.2).

Selection is by **test-category filter**, not file path (works for Reqnroll/xUnit,
and path-independent if the repo reorganizes): the qa-e2e producer already tags each
feature `@{stageSlug}`; `ReleaseGateTestFilterBuilder.Build(spawn, workstream)`
returns `Category={stageSlug}` (plus `|Category={otherStageSlug}` for cross-stage
edges pointing at this stage). The filter is passed as the `testFilter` pipeline
parameter and substituted into `ReleaseE2eTestCommand`.

Edge cases:

- qa-e2e companion **not yet merged** (it deps on this impl workstream, so normally
  it isn't): the impl branch **contains no feature files yet**. The gate therefore
  checks out the qa-e2e branch's `tests/` subtree when the companion workstream has
  an open PR (`git fetch origin {e2eBranch}` + sparse checkout of `tests/` inside the
  pipeline's package stage — rendered into the template). If the companion hasn't
  produced tests yet at gate time, applicability rule 4 already skipped the gate.
- Filter resolves to zero tests at runtime: the e2e step fails with
  `release-gate.no-tests-matched` — that's a red gate, not a silent pass (a filter
  that matches nothing means the tagging contract broke).

---

## 7. Runtime: trigger, track, verdict

### 7.1 Run tracking — `release_gate_runs`

New per-product table (Phase98Migration):

```
release_gate_runs(
  Id TEXT PK, RepoId TEXT NOT NULL, StorySpawnId TEXT, WorkstreamId TEXT NOT NULL,
  Branch TEXT NOT NULL, ForgeRunKey TEXT NOT NULL UNIQUE,
  Provider TEXT NOT NULL,             -- 'ado' | 'github'
  PipelineRef TEXT NOT NULL,          -- ADO pipeline id | GH workflow filename
  ProviderRunId TEXT NULL,            -- filled on/after dispatch
  State TEXT NOT NULL,                -- queued|dispatched|running|passed|failed|error|timed-out|cancelled|skipped
  SkipReason TEXT NULL,               -- §4 reason codes when State='skipped'
  TestFilter TEXT NULL, Attempt INTEGER NOT NULL DEFAULT 1,
  RequestedAt TEXT NOT NULL, DispatchedAt TEXT NULL, CompletedAt TEXT NULL,
  LogUrl TEXT NULL, FailureSummary TEXT NULL)
UNIQUE partial: one row per WorkstreamId in open states (queued|dispatched|running)
```

**Per-repo serialization:** at most one run per repo in `dispatched|running` — the
lab is a shared machine; concurrent deploys would clobber each other. Excess rows
wait in `queued` (FIFO by `RequestedAt`); the sync job dispatches the head when the
lane frees. Same CAS-update idiom as `task_queue`.

### 7.2 Provider surface

`IForgeProvider` gains a real pipeline trigger (the existing `TriggerBuildAsync` is
branch-only and stays for the PR-board button):

```csharp
Task<PipelineRunRef?> TriggerPipelineAsync(
    string pipelineRef, string branch,
    IReadOnlyDictionary<string, string> parameters,
    ProviderCallContext ctx, CancellationToken ct = default);
Task<PipelineRunStatus?> GetPipelineRunAsync(
    string pipelineRef, string providerRunId, ProviderCallContext ctx,
    CancellationToken ct = default);
```

- **ADO:** `POST {project}/_apis/pipelines/{id}/runs` with
  `templateParameters` + `resources.repositories.self.refName` — response **carries
  the run id** directly. `GetPipelineRunAsync` = `GET …/runs/{id}` (`state`/`result`).
- **GitHub:** `POST /repos/{o}/{r}/actions/workflows/{file}/dispatches` with
  `ref` + `inputs` — returns **204, no run id**. Correlation: the workflow sets
  `run-name` to include `forgeRunKey` (§5.3); the sync job lists
  `GET …/actions/workflows/{file}/runs?branch={branch}&created>={RequestedAt}` and
  matches `display_title` containing the key. Until matched, the row stays
  `dispatched`.
- `NullForgeProvider` / `LocalStoryProviderOps` return deterministic fakes so
  orchestrator tests drive the full state machine without CI.

### 7.3 `ReleaseGateSyncJob`

Dedicated hosted service (the 2 h `BuildSyncJob` cadence is far too slow for a
merge-blocking gate): **20 s tick**, does nothing when no open rows exist (cheap
indexed probe — idle cost ~zero). Per tick:

1. Dispatch `queued` heads whose repo lane is free (`TriggerPipelineAsync`,
   CAS `queued→dispatched`, stamp `ProviderRunId` when returned).
2. Resolve GitHub `dispatched` rows to `ProviderRunId` (run-name match); >5 min
   unresolved → `error` (`dispatch-not-observed`).
3. Poll `running|dispatched` rows via `GetPipelineRunAsync`; terminal → CAS to
   `passed|failed|cancelled`, stamp `CompletedAt`/`LogUrl` (provider run web URL),
   pull the failing step's log tail (16 KB cap, reuse `BuildFailureParser` regexes)
   into `FailureSummary`.
4. Enforce `ReleaseTimeoutSec` → provider cancel + `timed-out`.
5. On any terminal transition: emit the event (§8) and **nudge the orchestrator**
   (`StorySpawnOrchestrator.DriveAsync(storyId)`), same pattern as
   `SpawnDependencyScheduler`.

### 7.4 Orchestrator hook (`DriveReviewAsync`)

At the current approve-and-merge point, when `w.Status == "review"` and review is
clean:

```
decision = ReleaseGateApplicability.Decide(...)
if not applicable:
    insert release_gate_runs row State='skipped' + SkipReason   (audit trail)
    → ApproveAndMergeAsync as today
else:
    ensure open run row exists (idempotent — re-tick = no-op, Q28 invariant)
    w.Status = 'releasing'
next tick(s):
    row passed    → ApproveAndMergeAsync → 'merging'            (unchanged path)
    row failed / error / timed-out →
        w.Status = 'stalled'
        StallReason = stage-specific text + FailureSummary head + 📎 LogUrl
        (release failures are usually env/deploy issues — mirror the
         inherited-gate messaging: "inspect the lab run before Restart")
```

**No automatic regenerate loop in v1.** A lab failure feeds a human first; the
operator either fixes the environment and hits
`POST /api/workstreams/{id}/release-gate/retry` (new row, `Attempt+1`, back to
`releasing`) or Restarts the workstream if the change itself is bad. A later phase
can wire `GateFailure(Stage="release")` into the P56 recovery-attempt budget — the
`BuildFailedException.Stage` plumbing already supports a fourth stage token.

Spawn-level: no change — `done` still requires every workstream `complete`; a
skipped gate reaches `complete` exactly as today.

---

## 8. Events, telemetry, audit

- `task.release-gate.skipped` `{workstreamId, reason}` — from the hook, rule §4.
- `task.release-gate.triggered` `{runId, forgeRunKey, pipelineRef, branch, testFilter, attempt}`
- `task.release-gate.running` `{runId, providerRunId, logUrl}`
- `task.release-gate.passed` / `task.release-gate.failed` / `task.release-gate.timed-out`
  `{runId, logUrl, failureSummary?, durationSec}`
- Meter (`Forge.Api.Phase16`): `forge.release.gate.{triggered,passed,failed,skipped}`
  counters + `forge.release.gate.duration` histogram; skip counter tagged by reason.
- `forge_actions` rows for scaffold, retry, and every provider dispatch (write-path
  audit rule, P6).
- Gate logs: `FailureSummary` persists on the row; full logs stay provider-side
  behind `LogUrl` (no `.forge/logs/` copy — the run doesn't execute in the worktree).

---

## 9. UI

- **EditRepoModal** — new "Release gate" section: enable toggle, pipeline
  path/id (read-only once scaffolded), package command, artifact path, runner
  pool/labels, secrets ref, e2e command, timeout; `Scaffold…` button →
  `POST …/scaffold-release-gate`, disabled-state hint listing what's missing.
- **Swarm DAG / stage card** — `releasing` renders an amber `⇪ releasing` chip on
  the impl row; failed shows the stall banner with the `📎 view lab run` link
  (`LogUrl`) and a `Retry release` action (calls the retry endpoint).
- **Workstream detail panel** — release-run history list (attempt, state, duration,
  link), skip rows shown greyed with the reason code.
- **Inbox** — `release-gate failed` notification to the story's lead (reuses the
  `user.{userId}` channel), actionable Retry/Open-run buttons.

---

## 10. Data-model summary (Phase98Migration — next free slot)

- `repos` + 9 columns (§3) — idempotent `pragma_table_info`-guarded `ADD COLUMN`s.
- `workstreams` + `ReleaseGateDeclared TEXT NULL` (§4.1).
- New table `release_gate_runs` (§7.1) + indexes
  (`IX_release_gate_runs_WorkstreamId`, partial-unique open-state,
  `IX_release_gate_runs_RepoId_State`).
- `WorkstreamStatuses.Releasing = "releasing"` constant; DAG/status renderers
  updated (unknown-status fallbacks already tolerate new literals).
- Contracts: repo attach/patch DTOs, `ReleaseGateRunDto`, retry + scaffold
  endpoints in OpenAPI → `npm run gen:api`.

---

## 11. Implementation plan

> Follows the `implementation-plan.md` contract (Phase = independent feature,
> Stage = PR unit ≤20 files, Step = one commit). Test conventions as in
> `docs/forge/e2e.md` §10: `[unit]` xUnit w/ in-memory story-ops,
> `[integration]` full orchestrator drive on temp SQLite (`[Collection("Phase28")]`),
> `[ui-unit]` vitest, `[ui-e2e]` Playwright.

### Phase 1: Schema & repo config

#### Dependencies
- _none — start phase_

#### Stage 1.1: Phase98 migration + entities + contracts

**Deliverables:**
- `Phase98Migration.cs` (repos columns, `workstreams.ReleaseGateDeclared`,
  `release_gate_runs` + indexes), wired at the end of the migration chain.
- `RepoRow`/`WorkstreamRow`/`ReleaseGateRunRow` entity updates; `ProductDbContext`
  registration; `WorkstreamStatuses.Releasing`; `ReleaseGateConstants`.
- Attach/patch DTO fields + `ProductsEndpoints.PatchRepo` write path.

**Test Scenarios:**
- [ ] [unit] Migration is idempotent — runs twice on a fresh and an already-migrated DB without error; columns/table present.
- [ ] [unit] `PatchRepo` round-trips all nine release fields; `IsReleaseGateConfigured` truth table (github vs ado, missing pipeline id).
- [ ] [unit] Spec-consistency test locks `ReleaseGateConstants` to this doc's values.

#### Stage 1.2: EditRepoModal release-gate section

**Deliverables:**
- `EditRepoModal.tsx` section + generated-client fields (`npm run gen:api`).

**Test Scenarios:**
- [ ] [ui-unit] Section renders, disabled-state hint lists missing config, patch payload includes edited fields.

### Phase 2: Scaffolding & renderer

#### Dependencies
- Phase 1

#### Stage 2.1: `ReleaseGateRenderer` + `BuildSystemDetector.PackageCommand`

**Deliverables:**
- `Templates/ReleaseGateRenderer.cs` (GH workflow, ADO pipeline, deploy.ps1
  skeleton) per §5; `DetectedCommands.PackageCommand` per ecosystem.

**Test Scenarios:**
- [ ] [unit] GH template contains `workflow_dispatch` inputs, `run-name` with `forgeRunKey`, `environment:` = secrets ref, no literal hostnames/credentials.
- [ ] [unit] ADO template contains `trigger: none`, parameters, `pool:` + variable group; snapshot tests for both.
- [ ] [unit] Detector returns the documented `PackageCommand` per ecosystem fixture (sln/npm/cargo/go); repo override wins.

#### Stage 2.2: Scaffold endpoint

**Deliverables:**
- `POST /api/repos/{id}/scaffold-release-gate` (role-gated, idempotent, audited):
  default-branch commit via server git ops; ADO pipeline-definition create +
  `ReleasePipelineId` capture.

**Test Scenarios:**
- [ ] [unit] Idempotency — second call leaves existing files untouched and reports them.
- [ ] [unit] ADO path stores the returned pipeline id (stub HTTP handler); GH path leaves it null.
- [ ] [unit] Non-PO/tech-lead → 403; audit row written.

### Phase 3: Applicability

#### Dependencies
- Phase 1

#### Stage 3.1: Plan-parser `### Release Gate` + decomposition plumbing

**Deliverables:**
- `PlanParser` optional H3 → `Stage.ReleaseGate`; `DecompositionWorkstream.ReleaseGate`;
  persisted to `WorkstreamRow.ReleaseGateDeclared` by **both** `ProduceDecompositionAsync`
  and `PlanReconciler` (dual-write rule).

**Test Scenarios:**
- [ ] [unit] Parser reads yes/no/absent; malformed block → plan lint error.
- [ ] [unit] Reconciler re-derivation preserves the declared value (no fight with orchestrator).

#### Stage 3.2: `ReleaseGateApplicability` + skeleton heuristic

**Deliverables:**
- `ReleaseGateApplicability.Decide` with the §4 rule ladder; substantive-hunk
  classifier with `ReleaseGateConstants` patterns.

**Test Scenarios:**
- [ ] [unit] Rule-ladder table test: each reason code produced by a minimal fixture (disabled, not-impl, declared-skip, no-e2e-companion, docs-only, skeleton-only, applicable).
- [ ] [unit] Skeleton corpus: interface-only diff, NotImplementedException bodies, DI-registration-only, lockfile-only → skeleton; one real method body → applicable.
- [ ] [unit] Declared `yes` overrides docs-only/skeleton; declared `no` overrides a substantive diff.

### Phase 4: Runtime — provider, sync job, orchestrator

#### Dependencies
- Phase 2
- Phase 3

#### Stage 4.1: `TriggerPipelineAsync`/`GetPipelineRunAsync` on providers

**Deliverables:**
- `IForgeProvider` additions + ADO/GitHub/Null implementations + `PipelineRunRef`/
  `PipelineRunStatus` records; `LocalStoryProviderOps` deterministic fake.

**Test Scenarios:**
- [ ] [unit] ADO: request body carries templateParameters + refName; run id parsed from response (stub handler).
- [ ] [unit] GH: dispatch body carries inputs; 204 handled; run-listing match by `display_title` containing the key.

#### Stage 4.2: `release_gate_runs` state machine + `ReleaseGateSyncJob`

**Deliverables:**
- `IReleaseGateRunService` (enqueue/dispatch/poll/complete/retry with CAS
  transitions, per-repo lane serialization); `ReleaseGateSyncJob` hosted service
  with `RunOnceAsync` for tests; timeout + cancel; events + meters (§8).

**Test Scenarios:**
- [ ] [unit] Partial-unique open-state constraint — second enqueue for the same workstream is a no-op.
- [ ] [unit] Repo lane: two queued runs on one repo dispatch strictly serially; different repos in parallel.
- [ ] [unit] Timeout tick cancels + marks `timed-out`; GH unresolved-dispatch → `error` after 5 min.
- [ ] [integration] Full pass/fail cycle against the Null provider fake drives `queued→dispatched→running→passed|failed` and emits the event sequence.

#### Stage 4.3: Orchestrator hook + retry endpoint

**Deliverables:**
- `DriveReviewAsync` release branch (§7.4): applicability call, `releasing`
  status, skip audit row, pass→merge, fail→stall with StallReason + LogUrl;
  `POST /api/workstreams/{id}/release-gate/retry`; sync-job → `DriveAsync` nudge.

**Test Scenarios:**
- [ ] [integration] Applicable workstream: review pass → `releasing`; fake run passes → `merging → complete`.
- [ ] [integration] Skip path: skeleton workstream merges directly; `skipped` row with reason persisted.
- [ ] [integration] Fail path: workstream stalls with LogUrl in StallReason; retry endpoint re-enters `releasing` with `Attempt=2`.
- [ ] [integration] Idempotent re-tick: driving twice in `releasing` creates no duplicate run.
- [ ] [unit] External merge via `WorkstreamMergeAdvancer` bypasses the gate (documented operator override).

### Phase 5: UI & notifications

#### Dependencies
- Phase 4

#### Stage 5.1: Swarm + workstream detail + inbox surfaces

**Deliverables:**
- `releasing` chip + stall banner retry/log actions (SwarmMasterPanel /
  SwarmDetailPanel); release-run history in the detail panel; inbox
  `release-gate failed` notification wiring; store subscriptions to the §8 events.

**Test Scenarios:**
- [ ] [ui-unit] Status→chip mapping includes `releasing`; run-history component renders states + skip reasons.
- [ ] [ui-e2e] (SKIP until live lab) Stall banner exposes working Retry + log link against a seeded failed run.

---

## 12. Open questions (resolved defaults)

| # | Question | Default taken |
|---|---|---|
| R1 | Gate pre-merge or post-merge? | **Pre-merge** (`review → releasing → merging`) — validates the exact PR artifact, blocks base poisoning. |
| R2 | Auto-regenerate on gate failure? | **No (v1)** — stall + operator retry; lab failures are usually environmental. P56 recovery budget is the designed extension point (`Stage="release"`). |
| R3 | Reuse `builds`/`BuildSyncJob`? | **No** — different cadence (20 s vs 2 h), different lifecycle (parameterized dispatch + correlation), dedicated `release_gate_runs` keeps P27 untouched. |
| R4 | Where do lab machine coordinates live? | **Secret stores only** (KeyVault variable group / GH environment), per e2e.md §8.3. Repo config stores the *reference name*. |
| R5 | Skeleton detection — diff heuristic or plan-declared? | **Both**: declared intent wins, permissive heuristic as fallback (§4). |
