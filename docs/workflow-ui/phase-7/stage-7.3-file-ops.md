# Stage 7.3 — File Ops

**Phase:** §13 Phase 7 — Auth, Persistence, File Ops
**Stage status:** [ ]
**Total effort:** 6 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 7.3 adds user-facing file operations on top of the `IWorkflowStore` (Stage 7.2.1): a
workspace browser (list/create/rename/delete), zip-based import/export, and duplicate-as-template.
Every operation is gated by the Stage 7.1 auth policies (`Editor` required for mutations) and
every mutation lands in the audit log (Stage 7.1.4). Nothing here forks YAML parsing — imports go
through `WorkflowLoader.Validate` so bad payloads are rejected before touching disk.

Ground truth:
- `IWorkflowStore` is the only write path — tree operations go via the store, not raw `File.*`
  calls.
- Engine `WorkflowSerializer` is invoked on the YAML inside the zip on import, guaranteeing the
  reimport is byte-identical to what a fresh save would produce (parity invariant §0.2.3).
- `NodeDefinition.NodeId` ids remain stable on duplicate; the WORKFLOW id (`WorkflowDefinition.Id`)
  is regenerated so the copy can live alongside the original.

Two security invariants:
- **Path traversal**: every id/path passes through `PathSafety.CanonicalizeAndCheck`.
- **Zip-slip**: every zip entry name is sanitised (reject `..`, absolute paths, drive letters)
  before extraction.

## Prerequisites
- Stage 7.2 complete.
- Stage 7.1.3 policies registered.
- External deps: `System.IO.Compression` (built-in), `SharpZipLib 1.4.x` (only if
  System.IO.Compression proves insufficient for entry-name escaping — v1 uses stdlib).

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 7.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/host/WorkflowUi.Host/Controllers/FilesController.cs` (new)
- `src/host/WorkflowUi.Host/Files/TreeProjector.cs` (new)
- `src/host/WorkflowUi.Host/Files/ImportExportService.cs` (new)
- `src/host/WorkflowUi.Host/Files/ZipEntryNameSanitiser.cs` (new)
- `src/host/WorkflowUi.Host/Files/DuplicateService.cs` (new)
- `src/host/WorkflowUi.Host/Files/IdGenerator.cs` (new)
- `src/host/WorkflowUi.Host/DependencyInjection/FilesServiceCollectionExtensions.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Files/TreeProjectorTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Files/ImportExportServiceTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Files/ZipEntryNameSanitiserTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Files/DuplicateServiceTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Files/IdGeneratorTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Files/WorkspaceApiTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Files/ImportExportRoundTripTests.cs` (new)
- `src/ui/src/workspace/WorkspaceTree.tsx` (new)
- `src/ui/src/workspace/ImportExportDialog.tsx` (new)
- `src/ui/src/workspace/DuplicateDialog.tsx` (new)
- `src/ui/e2e/workspace-tree.spec.ts` (new)
- `src/ui/e2e/workspace-import-export.spec.ts` (new)
- `src/ui/e2e/workspace-duplicate.spec.ts` (new)

## Shared fixtures / fakes
- **Sample workspace seed** `seed/workspace/` with nested folders (`marketing/`, `ops/`) to test
  tree projection.
- **Zip fixtures**: `good.zip` (valid workflow pair), `zipslip.zip` (contains `../bad.yaml`),
  `toolarge.zip` (100 MB, tests size cap), `missingsidecar.zip` (yaml only).
- **Id collision seed**: store pre-loaded with `copy-of-wf1`, `copy-of-wf1-2` to exercise
  `IdGenerator` retry.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="7.3"`, `phaseId="7"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-7.3.plan-read`, `workflow-ui.stage-7.3.apply`, `workflow-ui.stage-7.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_7_3_started_total`, counter `workflow_ui_stage_7_3_failed_total`, histogram `workflow_ui_stage_7_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| auth surface | token / cookie leakage in logs | security tests or redaction audit fails | redact headers/claims and narrow structured fields |
| persistence layer | concurrent saves or stale writes win | etag/version tests fail | require optimistic concurrency token or last-write warning |
| file sandbox | path traversal or root escape | negative path tests fail | canonicalize full path and reject outside-root access |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| token refresh or auth handshake | 150 ms | 500 ms |
| YAML save / sidecar save | 200 ms | 750 ms |
| sandboxed file open / list | 150 ms | 600 ms |

## Accessibility checklist
- [ ] Auth dialogs/forms support full keyboard navigation and announce failures inline.
- [ ] File pickers and tree views expose ARIA roles, selection state, and current path context.
- [ ] Permission-denied states use clear text, not icon-only indicators.
- [ ] Autosave/toast messages are announced via a polite live region.
- [ ] Sensitive values (tokens/paths) are masked but still copyable through explicit affordances.

## Security callouts
- Protect every persistence/file endpoint against path traversal, CSRF, and confused-deputy writes.
- Redact tokens, refresh secrets, and claims from logs, ProblemDetails, and test artifacts.
- Validate filenames, extensions, and sandbox roots before any disk operation; never echo raw absolute paths back to the browser.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 7.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage73` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Log in, expire the token/session intentionally, and confirm recovery or redirect behavior.
- Save/open a workflow from nested folders and verify traversal attempts are blocked with the documented error.
- Simulate two tabs editing the same workflow and confirm the stale-write guidance is visible.

---

## Step 7.3.1 — Workspace browser API + UI (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Expose REST endpoints for a workspace tree rooted at `WorkflowsRoot` with virtual folder support
based on `/`-delimited workflow ids (e.g. id `marketing/newsletter` groups under `marketing/`
folder in the tree). Operations: `GET /api/files` → tree JSON, `POST /api/files` (create empty
workflow), `PATCH /api/files/{id}` (rename), `DELETE /api/files/{id}`. The React
`WorkspaceTree` uses `react-arborist` for virtualised tree rendering and supports inline rename.

### 4. Depends on
- Step 7.2.1

### 5. Inputs
- `IWorkflowStore.ListAsync` result.
- `TreeProjector.Project(IReadOnlyList<WorkflowSummary>) → TreeNode` converting id paths to nodes.

### 6. Deliverables
- `FilesController.cs` endpoints listed above.
- `TreeProjector.cs` pure function projecting flat ids into a tree.
- `WorkspaceTree.tsx` — React tree with inline rename, context menu (new/rename/delete).
- Unit tests + Playwright spec.

### 7. Acceptance criteria
- [ ] Tree correctly groups ids with `/` separators as folders.
- [ ] Empty folder (no workflows under prefix) does NOT appear (only real workflows).
- [ ] Create endpoint produces a valid empty workflow YAML (passes `WorkflowLoader`).
- [ ] Rename preserves sidecar.
- [ ] Rename to colliding id returns 409 Conflict.
- [ ] Delete is idempotent (delete of non-existent id → 404, not 500).
- [ ] All mutations require Editor role.

### 8. Unit tests
1. `TreeProjector_FlatIds_ProducesFlatTree`.
2. `TreeProjector_SlashSeparated_ProducesNestedTree`.
3. `TreeProjector_DeepNesting_PreservesOrder`.
4. `TreeProjector_EmptyList_ReturnsEmptyRoot`.
5. `TreeProjector_DuplicateIds_Rejected`.
6. `Rename_Collision_Returns409`.
7. `Rename_PreservesSidecar`.
8. `Delete_NonExistent_Returns404`.
9. `Create_GeneratesValidEmptyWorkflow` (engine validates).

Three AAA sketches:

```csharp
// #2 TreeProjector_SlashSeparated_ProducesNestedTree
var summaries = new[] {
    new WorkflowSummary("marketing/newsletter", ...),
    new WorkflowSummary("marketing/campaign",   ...),
    new WorkflowSummary("ops/deploy",            ...),
};
// Act
var tree = TreeProjector.Project(summaries);
// Assert
tree.Children.Should().HaveCount(2);     // marketing, ops
tree.Children.Single(c => c.Name=="marketing").Children.Should().HaveCount(2);

// #6 Rename_Collision_Returns409
await store.SaveAsync("a", yaml, "{}", null, Editor(), default);
await store.SaveAsync("b", yaml, "{}", null, Editor(), default);
// Act — PATCH /api/files/a  body { newId: "b" }
var resp = await client.PatchAsJsonAsync("/api/files/a", new { newId = "b" });
// Assert
resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

// #9 Create_GeneratesValidEmptyWorkflow
var resp = await client.PostAsJsonAsync("/api/files", new { id = "fresh" });
resp.StatusCode.Should().Be(HttpStatusCode.Created);
var rec = await store.GetAsync("fresh", Editor(), default);
// engine loader validates; must not throw
Action act = () => loader.Load(new StringReader(rec!.Yaml));
act.Should().NotThrow();
```

### 9. Integration tests
- `WorkspaceApiTests.Crud_EndToEnd` (create, list, rename, delete).
- Playwright `workspace-tree.spec.ts`: render tree, rename via inline edit, delete via context
  menu.

### 10. Golden-file / parity tests
- `TreeNode.json` shape golden.

### 11. Edge cases & negative tests
- Id containing `/` at start or end (leading/trailing) → rejected.
- Id with consecutive slashes (`a//b`) → rejected.
- Unicode folder names → allowed, rendered via Tailwind truncation.
- Very deep nesting (20 levels): allowed; tree component handles.
- Rename across folders (`a/b` → `c/b`): allowed; store sees as delete-then-create under the
  hood but controller emits a single `Moved` audit entry.

### 12. Observability hooks
- Metric `workspace_ops_total{op}`.
- Log `"Workspace.{Op} {Id} by {UserId}"`.

### 13. Risks & mitigations
- **R7.3.1-A — id collision on rename race**: relies on store-side etag + file existence check.
- **R7.3.1-B — tree explosion on misordered ids**: TreeProjector sorts alphabetically.

### 14. Evaluator gap-analysis prompts
1. Does TreeProjector correctly nest on `/`?
2. Is collision returned as 409, not 500?
3. Does the create endpoint emit an engine-valid YAML?
4. Are all mutations Editor-gated?
5. Is rename atomic from API perspective (single audit row)?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, CRUD + rename Playwright green.

---

## Step 7.3.2 — Import / export zip (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Support round-trip export and import of a workflow as a zip containing `workflow.yaml` and
`workflow.ui.json`. Export: `GET /api/files/{id}/export` streams `application/zip`. Import: `POST
/api/files/import` (multipart form) extracts, validates YAML via `WorkflowLoader`, sanitises
entry names against zip-slip, and creates/updates the workflow via the store. Import imposes a
**100 MB total zip size cap** and **10 MB single entry cap**.

### 4. Depends on
- Step 7.3.1

### 5. Inputs
- Client-uploaded zip.
- Workflow id from URL or derived from zip metadata.

### 6. Deliverables
- `ImportExportService.cs` with `ExportAsync(id) → Stream` and `ImportAsync(Stream zip,
  string? targetId, principal) → ImportResult`.
- `ZipEntryNameSanitiser.cs` — `bool IsSafe(string entryName)`.
- Unit tests (zip-slip, oversize, round-trip).
- Playwright `workspace-import-export.spec.ts`.

### 7. Acceptance criteria
- [ ] Export produces a zip whose re-import yields byte-identical YAML and sidecar.
- [ ] Import rejects zip-slip entries (`..`, absolute paths, drive letters, backslash traversal).
- [ ] Import rejects zips over 100 MB with 413 Payload Too Large.
- [ ] Import rejects single entries over 10 MB with 413.
- [ ] Import with missing sidecar creates sidecar `{}`.
- [ ] Import with missing yaml returns 400.
- [ ] Import with YAML that fails `WorkflowLoader` returns 422 and does NOT write any file.
- [ ] Export streams without buffering whole zip in memory (chunked response).

### 8. Unit tests
1. `RoundTrip_ExportThenImport_Identical`.
2. `Sanitiser_Rejects_DoubleDot`.
3. `Sanitiser_Rejects_AbsolutePath`.
4. `Sanitiser_Rejects_DriveLetter`.
5. `Sanitiser_Rejects_BackslashTraversal`.
6. `Import_OversizeZip_Rejected`.
7. `Import_OversizeEntry_Rejected`.
8. `Import_InvalidYaml_Rejected_NoFilesWritten`.
9. `Import_MissingSidecar_DefaultsEmptyObject`.
10. `Import_MissingYaml_Rejected`.

Three AAA sketches:

```csharp
// #1 RoundTrip_ExportThenImport_Identical
await store.SaveAsync("wf1", yaml, sidecar, null, Editor(), default);
// Act
await using var exported = await svc.ExportAsync("wf1", Editor(), default);
var imported = await svc.ImportAsync(exported, targetId:"wf2", Editor(), default);
// Assert
var src = (await store.GetAsync("wf1", Editor(), default))!;
var dst = (await store.GetAsync("wf2", Editor(), default))!;
dst.Yaml.Should().Be(src.Yaml);
dst.SidecarJson.Should().Be(src.SidecarJson);

// #2 Sanitiser_Rejects_DoubleDot
ZipEntryNameSanitiser.IsSafe("../../../etc/passwd").Should().BeFalse();
ZipEntryNameSanitiser.IsSafe("a/../b").Should().BeFalse();
ZipEntryNameSanitiser.IsSafe("normal/path.yaml").Should().BeTrue();

// #8 Import_InvalidYaml_Rejected_NoFilesWritten
var zip = BuildZip(("workflow.yaml", "this: is: invalid: yaml: :"));
// Act
Func<Task> act = () => svc.ImportAsync(zip, "wf-bad", Editor(), default);
// Assert
await act.Should().ThrowAsync<ImportValidationException>();
(await store.GetAsync("wf-bad", Editor(), default)).Should().BeNull();
```

### 9. Integration tests
- `ImportExportRoundTripTests.Export_Import_Yields_Identical` via API.
- Playwright `workspace-import-export.spec.ts` — user picks file, uploads, sees new entry in
  tree.

### 10. Golden-file / parity tests
- `exported-zip.golden.json` — for each fixture workflow, pinned zip entry manifest
  (entry names + uncompressed sizes).

### 11. Edge cases & negative tests
- Zip bomb (small file, huge uncompressed): caught by per-entry size cap during streaming
  extraction.
- Zip with BOM in yaml: preserved on import, engine loader unaffected.
- Zip with extra files beyond workflow.yaml + workflow.ui.json: extra entries ignored, warning
  logged.
- Zip with alternate encoding (not UTF-8 name): entries still sanitised; controller may reject.
- Duplicate targetId on import with no overwrite flag: 409 Conflict.

### 12. Observability hooks
- Metric `files_import_total{result}`, `files_export_total`.
- Log `"Import {FileName} by {UserId} → {Result}"`.

### 13. Risks & mitigations
- **R7.3.2-A — zip-slip**: explicit sanitiser with 4 negative cases and 1 positive.
- **R7.3.2-B — zip bomb**: streaming extraction with per-entry and total caps.
- **R7.3.2-C — YAML option drift**: import goes through engine `WorkflowLoader` — same options
  as save path.

### 14. Evaluator gap-analysis prompts
1. Does the sanitiser cover all four attack vectors listed?
2. Is the 100 MB / 10 MB cap enforced during streaming (not after buffering)?
3. Does invalid YAML leave zero partial state?
4. Is the round-trip proven byte-identical?
5. Does import use `WorkflowLoader` (never hand-rolled YAML)?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, zip-slip + oversize tests green, Playwright round-trip green.

---

## Step 7.3.3 — Duplicate + template workflows (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Add `POST /api/files/{id}/duplicate` that copies `{id}.yaml` + `{id}.ui.json` to a new
workflow under a generated id. The workflow-level `WorkflowDefinition.Id` is regenerated (new
GUID-derived slug) but `NodeDefinition.NodeId` values remain stable so sidecar-preserved layout
still matches. If the target id collides, `IdGenerator.NextAvailable` suffixes `-2`, `-3`, … up
to 100 before returning error. Template mode: when source id is under `/templates/` prefix, the
duplicate is placed at root.

### 4. Depends on
- Step 7.3.1

### 5. Inputs
- Source workflow via store.
- `IdGenerator.GenerateBase(sourceId) → string` and `NextAvailable(base, existsFn) → string`.

### 6. Deliverables
- `DuplicateService.cs`.
- `IdGenerator.cs`.
- `DuplicateDialog.tsx` in UI.
- Unit + Playwright tests.

### 7. Acceptance criteria
- [ ] Duplicate yields a new workflow whose YAML load produces a `WorkflowDefinition` with a
      different `Id` than the source.
- [ ] All `NodeDefinition.NodeId` values equal the source.
- [ ] Sidecar (layout) is copied verbatim.
- [ ] Collision: 3 attempts, suffix `-2`, `-3`, `-4` tried — succeeds.
- [ ] 100 consecutive collisions → 507 Insufficient Storage with clear message.
- [ ] Template duplication (source under `/templates/`): target placed at root by default,
      configurable via body `{ targetId }`.
- [ ] Editor role required.

### 8. Unit tests
1. `Duplicate_ProducesNewWorkflowId_SameNodeIds`.
2. `Duplicate_CopiesSidecarVerbatim`.
3. `IdGenerator_NextAvailable_Suffixes2`.
4. `IdGenerator_NextAvailable_AfterTwoCollisions_ReturnsSuffix3`.
5. `IdGenerator_100Collisions_Throws`.
6. `Duplicate_TemplatePrefix_PlacesAtRoot`.
7. `Duplicate_ExplicitTargetId_Used`.
8. `Duplicate_MissingSource_Returns404`.

Three AAA sketches:

```csharp
// #1 Duplicate_ProducesNewWorkflowId_SameNodeIds
await store.SaveAsync("src", yamlWithNodes, "{}", null, Editor(), default);
// Act
var newId = await svc.DuplicateAsync("src", targetId:null, Editor(), default);
// Assert
var srcDef = loader.Load(new StringReader((await store.GetAsync("src", Editor(), default))!.Yaml));
var newDef = loader.Load(new StringReader((await store.GetAsync(newId, Editor(), default))!.Yaml));
newDef.Id.Should().NotBe(srcDef.Id);
newDef.Nodes.Select(n => n.NodeId).Should().Equal(srcDef.Nodes.Select(n => n.NodeId));

// #4 IdGenerator_NextAvailable_AfterTwoCollisions_ReturnsSuffix3
var taken = new HashSet<string> { "copy-of-src", "copy-of-src-2" };
var id = IdGenerator.NextAvailable("copy-of-src", id => taken.Contains(id));
id.Should().Be("copy-of-src-3");

// #5 IdGenerator_100Collisions_Throws
var all = Enumerable.Range(1, 100).Select(i => i == 1 ? "copy" : $"copy-{i}").ToHashSet();
Action act = () => IdGenerator.NextAvailable("copy", id => all.Contains(id));
act.Should().Throw<IdGenerationException>()
    .WithMessage("*after 100*");
```

### 9. Integration tests
- Playwright `workspace-duplicate.spec.ts`: right-click → duplicate, assert new tree entry
  appears with `-2` suffix when colliding.

### 10. Golden-file / parity tests
- None required (parity preserved via existing store golden tests).

### 11. Edge cases & negative tests
- Source under `/templates/` with targetId explicit containing `/` — respect the explicit path.
- Source contains absolute file refs (e.g. `CSharpScriptNode.scriptPath`): copied verbatim —
  caller's responsibility to adjust.
- Duplicate of a workflow whose sidecar is malformed JSON: copied verbatim (no parsing required).
- Concurrent duplicates of the same source: each gets a unique id via `IdGenerator` atomic check.
- Source with `Id: null` (legacy): duplicate assigns a fresh `Id`.

### 12. Observability hooks
- Metric `files_duplicate_total`.
- Log `"Duplicate {SourceId} → {TargetId} by {UserId}"`.

### 13. Risks & mitigations
- **R7.3.3-A — id collisions under race**: `IdGenerator` queries store existsFn within a
  retry loop; worst case returns 507 after 100 attempts.
- **R7.3.3-B — user confusion with template placement**: document in tooltip + user guide
  (Step 8.2.1).

### 14. Evaluator gap-analysis prompts
1. Is the workflow `Id` actually regenerated (not copied)?
2. Are all `NodeId` values preserved so sidecar layout still matches?
3. Does collision retry reach suffix `-3` when two prior collisions?
4. Is the 100-attempt cap actually tested?
5. Does template prefix trigger root placement?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, duplicate + collision Playwright green, id-rewrite unit tests green.
