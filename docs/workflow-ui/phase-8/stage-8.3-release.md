# Stage 8.3 — Release

**Phase:** §14 Phase 8 — Packaging, Docs, Release
**Stage status:** [ ]
**Total effort:** 6 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 8.3 closes out v1 with the release pipeline itself: tag-triggered builds that produce
signed artifacts, automated changelog generation from PR labels, and a post-release parity
checklist that re-runs engine parity tests (§0.2.1/§0.2.2/§0.2.3) against the released engine
version. The pipeline reuses the container (Stage 8.1.1) and self-contained builds (Stage 8.1.3)
and publishes them alongside the docs site (Stage 8.2). Code signing uses GitHub OIDC federation
to a KMS-backed signing identity (Azure Key Vault or AWS KMS) so no long-lived secrets are
stored in CI.

Two v1 release criteria surface here:
1. **Engine parity on release tag** — the post-release checklist (Step 8.3.3) re-runs the
   parity suite against the engine version published by the tag, not HEAD. This catches the case
   where the engine advances on `main` between freeze and ship.
2. **Reproducible artifacts** — same commit SHA + same runner image must produce byte-identical
   container digests and exe hashes; asserted via a dry-run build that compares against the
   real release (Step 8.3.1).

The changelog (Step 8.3.2) is populated from PR labels matching the conventional-commit style
(`feat:`, `fix:`, `chore:`, `docs:`), grouped into sections in release notes. Labels are
enforced at merge time by `actions/labeler` + a required status check.

## Prerequisites
- Stage 8.1 complete (publishable artifacts).
- Stage 8.2 complete (docs site built).
- External deps: GitHub Actions, `sigstore-cosign` 2.x for container signing, Azure Key Vault
  or AWS KMS for exe signing, `release-drafter` or `git-cliff` for changelog, `release-please`
  optional for automated version bumps.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 8.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `.github/workflows/release.yml` (new)
- `.github/workflows/release-drafter.yml` (new)
- `.github/workflows/post-release-parity.yml` (new)
- `.github/release-drafter.yml` (new — labels → categories config)
- `.github/labeler.yml` (new — file-path → label rules)
- `deploy/sign/sign-exe.ps1` (new)
- `deploy/sign/sign-container.sh` (new)
- `deploy/sign/verify-signatures.sh` (new)
- `docs/release-checklist.md` (new — human runbook)
- `src/host/WorkflowUi.Host.ContractTests/PostReleaseParityTests.cs` (new)
- `tools/release-dryrun.ps1` (new)

## Shared fixtures / fakes
- **Mock signing identity** for CI tests: ephemeral cert in GitHub secrets scoped only to
  `release-dryrun` environment; never used for real releases.
- **`sample-release-notes.md`** — snapshot of notes produced by release-drafter used for
  format tests.
- **Frozen engine reference** — git submodule pointing at the engine release tag matching the
  UI tag; parity tests run against this tag.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="8.3"`, `phaseId="8"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-8.3.plan-read`, `workflow-ui.stage-8.3.apply`, `workflow-ui.stage-8.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_8_3_started_total`, counter `workflow_ui_stage_8_3_failed_total`, histogram `workflow_ui_stage_8_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| packaging | bundle misses runtime dependency | smoke boot or installer test fails | restore manifest/asset entry and rebuild package |
| docs site | screenshots or API docs drift | docs parity / link checks fail | recapture deterministic assets and republish |
| release automation | tag/changelog/publish order breaks | release dry-run fails | roll back tag or package and rerun validated workflow |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| docs / package smoke build | 60 s | 120 s |
| container/app cold boot | 5 s | 15 s |
| release validation bundle | 120 s | 300 s |

## Accessibility checklist
- [ ] Docs pages use semantic headings, landmarks, and accessible code-block labels.
- [ ] Screenshots/media include alt text describing workflow state changes relevant to the page.
- [ ] Release notes tables and badge colors have non-color cues.
- [ ] Installer/package docs list keyboard steps for key flows.
- [ ] Docs-site search results and navigation are reachable without hover-only interactions.

## Security callouts
- Release jobs must use least-privilege tokens and redact package registry credentials from logs.
- Docs/screenshots must not contain secrets, real tenant ids, or local filesystem paths.
- Signed artifacts and checksums should be verified before publish/promote steps continue.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 8.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage83` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Build the docs/package from a clean checkout and follow the published quickstart exactly once.
- Run the release dry-run, inspect generated notes/artifacts, then verify rollback steps are documented.
- Open the docs site in light/dark mode and keyboard through the main navigation + search.

---

## Step 8.3.1 — Release pipeline (tagged container + signed exe) (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
A `release.yml` workflow triggered by `v*` tags that: (a) runs full CI, (b) builds container
(linux/amd64, linux/arm64) and pushes to `ghcr.io/{owner}/workflow-ui:{tag}` + `:latest`, (c)
signs container with `cosign` via GH OIDC, (d) builds self-contained exes for Windows + Linux,
(e) signs Windows exe via Azure Key Vault codesigning, (f) uploads artifacts to the GitHub
Release, (g) deploys docs site to GitHub Pages. A dry-run mode (`workflow_dispatch`) exercises
every step except the final push so rehearsals are safe.

### 4. Depends on
- Step 8.1.3

### 5. Inputs
- Tag `v{major.minor.patch}` pushed to `main`.
- `version.json` aligned with tag.

### 6. Deliverables
- `release.yml` workflow.
- Signing scripts.
- `release-dryrun.ps1` local rehearsal.
- `verify-signatures.sh` run post-release.

### 7. Acceptance criteria
- [ ] Tag push triggers the workflow (verified with `workflow_dispatch` dry-run first).
- [ ] Container image pushed to ghcr with tag + `latest`.
- [ ] Container signature verifiable via `cosign verify --certificate-identity-regexp
      "https://github.com/..."`.
- [ ] Windows exe signed by Authenticode; `signtool verify /pa` passes.
- [ ] Linux exe SHA-256 published alongside GitHub Release as `SHA256SUMS`.
- [ ] Docs site deployed to `{owner}.github.io/{repo}` on every tag.
- [ ] Dry-run mode produces artifacts but does NOT push or publish.
- [ ] Release fails fast if `version.json` does not match tag.
- [ ] Reproducibility check: two consecutive dry-runs produce identical container digests.

### 8. Unit tests
These are pipeline meta-tests (`act` local runner + GH Actions validator):
1. `Workflow_ValidatesWithActionlint`.
2. `Workflow_DryRun_NoPushCommands`.
3. `Workflow_OnTagMatch_TriggersBuild`.
4. `Workflow_FailsWhenVersionJsonMismatchTag`.
5. `CosignVerify_SucceedsAgainstTestSignature`.
6. `Signtool_Verify_SucceedsOnSignedExe`.
7. `ShaSums_File_ContainsAllArtifacts`.
8. `Reproducibility_TwoDryRuns_DigestsMatch`.
9. `Docs_Deploy_Step_RunsOnlyOnTag`.

Three AAA sketches:

```yaml
# #4 Workflow_FailsWhenVersionJsonMismatchTag (step in release.yml)
- name: Assert version.json matches tag
  run: |
    TAG="${GITHUB_REF#refs/tags/v}"
    JSON_VER=$(jq -r '.version' version.json)
    if [ "$TAG" != "$JSON_VER" ]; then
      echo "::error::version.json ($JSON_VER) != tag ($TAG)"; exit 1
    fi
```

```csharp
// Meta-test (C#): #8 Reproducibility_TwoDryRuns_DigestsMatch
[Fact, Trait("Category","Release")]
public async Task Reproducibility_TwoDryRuns_DigestsMatch()
{
    var d1 = await ReleaseDryRun.RunAsync();
    var d2 = await ReleaseDryRun.RunAsync();
    d1.ContainerDigest.Should().Be(d2.ContainerDigest);
    d1.ExeSha256["win-x64"].Should().Be(d2.ExeSha256["win-x64"]);
}
```

```bash
# #5 CosignVerify_SucceedsAgainstTestSignature
cosign sign --identity-token=$ACTIONS_ID_TOKEN --yes $IMAGE
cosign verify --certificate-identity-regexp "https://github.com/.*" \
              --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
              $IMAGE
```

### 9. Integration tests
- `post-release-parity.yml` runs after the release workflow and hits the newly published
  endpoints via `docker pull`; asserts `/api/version` matches the tag.

### 10. Golden-file / parity tests
- `release-dryrun.golden.json` pinning expected artifact names + digests for a known tag.

### 11. Edge cases & negative tests
- Tag format `v1.2` (no patch) — workflow validates semver and fails with clear message.
- KMS signing service unavailable: workflow retries 3× then fails; human unblocks.
- GHCR rate limit hit: workflow uses exponential back-off up to 10 min.
- Tag pushed twice: second run detects existing release and no-ops with warning.
- Docs deploy fails after container push: Pages step retries; container release already atomic.

### 12. Observability hooks
- Slack/Teams webhook on success + failure.
- OTel span exported from workflow for build duration tracking.

### 13. Risks & mitigations
- **R8.3.1-A — signing key exposure**: all signing via OIDC-to-KMS; no key on runner.
- **R8.3.1-B — non-reproducible builds**: `SOURCE_DATE_EPOCH` set from commit, Dockerfile uses
  `--timestamp` for deterministic layers.
- **R8.3.1-C — half-published release**: artifacts uploaded last; rollback script reverts tag.

### 14. Evaluator gap-analysis prompts
1. Is every signing operation via OIDC (no stored keys)?
2. Does dry-run mode fully exercise build without pushing?
3. Is version.json vs tag parity enforced before artifacts?
4. Are container digests reproducible?
5. Is docs deploy gated on successful artifact publish?
6. Is there a rollback script?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, dry-run green, reproducibility check green, post-release parity green.

---

## Step 8.3.2 — Changelog automation from PR labels (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Configure `release-drafter` to auto-draft release notes from PR labels. Labels map to sections:
`feat` → Features, `fix` → Bug Fixes, `perf` → Performance, `docs` → Documentation, `chore` →
Housekeeping, `breaking` → Breaking Changes (promoted to top). `actions/labeler` assigns labels
based on file-path rules (`src/host/** → area:host`, `src/ui/** → area:ui`, `docs/** → docs`).
A required status check enforces at least one category label per PR.

### 4. Depends on
- Step 8.3.1

### 5. Inputs
- PR metadata: labels, title, author.
- Merged PRs since last tag.

### 6. Deliverables
- `release-drafter.yml` config.
- `labeler.yml` config.
- `release-drafter.yml` GH Actions workflow.
- Required status check `label-present`.
- Snapshot test of generated notes.

### 7. Acceptance criteria
- [ ] Release notes include one section per non-empty category.
- [ ] Breaking changes appear first.
- [ ] Each entry links to PR + author.
- [ ] PR without any category label fails the `label-present` check.
- [ ] Draft release updated within 60 s of merge to main.
- [ ] Template uses `$CHANGES`, `$CONTRIBUTORS`, `$RESOLVED_ISSUES` placeholders.
- [ ] Notes in Markdown valid for GitHub Releases.

### 8. Unit tests
1. `ReleaseDrafterConfig_Parses`.
2. `LabelerRules_PathMatches` (per rule).
3. `GeneratedNotes_Contain_BreakingSection_First`.
4. `GeneratedNotes_GroupByLabel`.
5. `GeneratedNotes_EscapeSpecialChars`.
6. `LabelPresentCheck_FailsOnUnlabeledPR`.
7. `Template_RendersWithoutErrors`.
8. `Snapshot_NotesMatchExpected` (sample.md).

AAA sketches:

```ts
// #3 GeneratedNotes_Contain_BreakingSection_First
const prs = [
  pr({ title: 'remove X', labels: ['breaking','feat'] }),
  pr({ title: 'add Y',    labels: ['feat'] }),
  pr({ title: 'fix Z',    labels: ['fix'] }),
];
const notes = render(config, prs);
const breakingIdx = notes.indexOf('## Breaking Changes');
const featIdx = notes.indexOf('## Features');
expect(breakingIdx).toBeGreaterThan(-1);
expect(breakingIdx).toBeLessThan(featIdx);

// #6 LabelPresentCheck_FailsOnUnlabeledPR
const result = await labelCheck({ labels: [] });
expect(result.status).toBe('failure');
expect(result.message).toMatch(/at least one of/i);

// #8 Snapshot_NotesMatchExpected
const notes = await generateNotes({ sinceTag: 'v0.9.0' });
expect(notes).toMatchFileSnapshot('fixtures/sample-release-notes.md');
```

### 9. Integration tests
- A "harness PR" with known labels is opened in a test repo and the draft inspected.

### 10. Golden-file / parity tests
- `sample-release-notes.md` snapshot.

### 11. Edge cases & negative tests
- PR with multiple category labels: entry appears under highest-priority category only.
- Very long PR title: truncated at 100 chars with ellipsis + full title as title-attribute
  (not useful on GitHub — accepted).
- Dependabot PRs: auto-labeled `chore` via labeler rule.
- Merge without squash (merge commit): drafter attributes to PR author, not committer.
- No PRs since last tag: notes contain placeholder "No user-visible changes".

### 12. Observability hooks
- Metric `release_drafter_runs_total{status}`.

### 13. Risks & mitigations
- **R8.3.2-A — unlabeled PRs slip past**: required status check on branch protection.
- **R8.3.2-B — category explosion**: closed taxonomy (7 categories) in config.

### 14. Evaluator gap-analysis prompts
1. Is `label-present` a required check?
2. Do breaking changes appear first?
3. Are labels assigned automatically by path?
4. Is the snapshot test stable?
5. Is the config valid YAML (parser test)?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, snapshot test green, required check enforced on `main`.

---

## Step 8.3.3 — Post-release checklist & parity re-run (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Define a human + automated checklist that runs after every tagged release:
1. Pull published container and smoke-test `/api/health` + `/api/version`.
2. Run parity test suite (§0.2.1/§0.2.2/§0.2.3) against the engine version pinned by the
   tagged commit.
3. Verify code signatures (cosign for container, signtool for exe).
4. Confirm docs site updated (`/api/version` on site matches tag).
5. Open a tracking issue if parity fails, labelled `release-regression`.
The automated portion lives in `post-release-parity.yml`, triggered by `release.published`.

### 4. Depends on
- Step 8.3.1

### 5. Inputs
- Published release artifacts.
- Engine submodule pinned to release commit.

### 6. Deliverables
- `post-release-parity.yml` workflow.
- `PostReleaseParityTests.cs` running engine-parity tests against submodule.
- `docs/release-checklist.md` human runbook.
- Issue template `release-regression.yml`.

### 7. Acceptance criteria
- [ ] Workflow triggers on `release.published` (not on tag push — post-signing).
- [ ] Container smoke asserts health endpoint and version match.
- [ ] Parity tests run against the engine commit that built the release (not HEAD).
- [ ] Signature verification step fails loudly on mismatch.
- [ ] On failure, issue auto-opened with logs attached.
- [ ] Checklist.md enumerates manual steps not automated.
- [ ] Green post-release parity run is a required input to marking release GA.

### 8. Unit tests
1. `Workflow_Triggers_OnReleasePublished`.
2. `ContainerSmoke_Health_Ok`.
3. `ContainerSmoke_Version_MatchesTag`.
4. `EngineParity_EnumsMatch`.
5. `EngineParity_NodeDtosMatch`.
6. `EngineParity_YamlRoundTrip_AllFixtures`.
7. `Cosign_Verify_PublishedImage_Succeeds`.
8. `Signtool_Verify_PublishedExe_Succeeds`.
9. `Failure_OpensTrackingIssue`.

Three AAA sketches:

```csharp
// #4 EngineParity_EnumsMatch — re-runs §0.2.1 assertions against published engine DLL
[Fact]
public void EngineParity_EnumsMatch()
{
    var engineAsm = LoadFromSubmodule("ExecutionEngine.dll");
    var runtimeTypeVals = Enum.GetNames(engineAsm.GetType("ExecutionEngine.Enums.RuntimeType")!);
    runtimeTypeVals.Should().BeEquivalentTo(new[] {
        "Noop","CSharp","CSharpScript","CSharpTask","PowerShell","PowerShellTask",
        "IfElse","ForEach","While","Switch","Subflow","Timer","Container"
    });
}

// #6 EngineParity_YamlRoundTrip_AllFixtures
var loader = new WorkflowLoader();
var ser    = new WorkflowSerializer();
foreach (var path in Directory.EnumerateFiles("Fixtures", "*.yaml"))
{
    var doc  = loader.Load(new StreamReader(path));
    var back = ser.Serialize(doc);
    CanonicalDiff(File.ReadAllText(path), back).Should().BeEmpty(path);
}

// #9 Failure_OpensTrackingIssue
// act — force parity to fail via mock engine
// assert — GH CLI call captured with issue body containing logs excerpt
mockGh.Calls.Should().ContainSingle(c =>
    c.Command == "issue create" && c.Args.Any(a => a.Contains("release-regression")));
```

### 9. Integration tests
- Full end-to-end: stage a dummy release, run workflow against it, observe green output.

### 10. Golden-file / parity tests
- Reuses §0.2 golden fixtures against the *published* engine — the whole point of this step.

### 11. Edge cases & negative tests
- Engine submodule pointer out of sync with release: workflow fails with explicit error.
- Published container digest not yet propagated to registry: retry with back-off for 5 min.
- Codesign verification fails due to cert rotation: opens `release-regression` issue.
- No existing `release-regression` label: workflow creates it.
- Manual override: maintainer can dismiss regression issue with comment (CLI helper provided).

### 12. Observability hooks
- Workflow run duration metric.
- Artifacts: parity report, signature verification logs.

### 13. Risks & mitigations
- **R8.3.3-A — engine drifts between release and post-release**: submodule pin prevents.
- **R8.3.3-B — flaky signature verify due to registry delay**: back-off + retry.
- **R8.3.3-C — human skips manual steps**: docs/release-checklist.md has explicit checkboxes
  and release cannot be marked GA without them.

### 14. Evaluator gap-analysis prompts
1. Is engine-parity actually re-run against the published engine commit?
2. Does container smoke test the PUBLISHED image (not local)?
3. Are signatures verified?
4. Is an issue auto-opened on failure?
5. Is the human checklist enumerated?
6. Does GA gating require post-release parity green?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, parity tests green on release tag, signature verifications green, checklist
documented.
