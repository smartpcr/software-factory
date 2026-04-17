# Stage 8.2 — Docs

**Phase:** §14 Phase 8 — Packaging, Docs, Release
**Stage status:** [ ]
**Total effort:** 7 d
**Step count:** 3

## Steps

- [ ] **Step 1**: User guide (3 d)
- [ ] **Step 2**: Developer guide (add-a-kind) (2 d)
- [ ] **Step 3**: API reference from OpenAPI (2 d)

---
## Context

Stage 8.2 produces the three documentation artefacts required for GA: a user guide with
walk-throughs and screenshots for each node kind; a developer guide centred on Appendix B of the
master plan (the "add-a-kind" checklist); and an API reference generated from the OpenAPI
specification served by the host. The docs site is built with **Docusaurus 3** under `docs/site/`
(MDX) and deployed to GitHub Pages via the release workflow (Stage 8.3.1 hooks into the same
pipeline).

The user guide is anchored to parity rules: every node kind page is generated from the exact
per-kind DTO shape in §2.3 of the master plan, so docs cannot claim fields the engine doesn't
have. The developer guide's "add-a-kind" checklist is mirrored verbatim from Appendix B — any
edit to one flows to the other via a CI link-check step. The API reference auto-refreshes from
`/api/openapi.json` on every build, so route drift shows up as doc diff.

Screenshot drift is mitigated by Playwright-driven automation: the `tools/capture-screenshots.ts`
script drives the dev server through canonical flows and writes PNGs to `docs/site/static/img/`
with deterministic names. The markdown link checker runs on every PR and fails on broken
internal or external links.

## Prerequisites
- Stage 7.3 complete (feature set stable).
- Stage 8.1 complete (packaging available for `docker pull` examples).
- External deps: Docusaurus 3.x, Redoc 2.x, `lychee` link-checker 0.15.x, Playwright 1.45,
  `@asyncapi/generator` optional for SignalR docs (stretch).

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `docs/site/docusaurus.config.ts` (new)
- `docs/site/sidebars.ts` (new)
- `docs/site/docs/intro.mdx` (new)
- `docs/site/docs/user-guide/getting-started.mdx` (new)
- `docs/site/docs/user-guide/designer-tour.mdx` (new)
- `docs/site/docs/user-guide/kinds/_meta.json` (new)
- `docs/site/docs/user-guide/kinds/noop.mdx` (new)
- `docs/site/docs/user-guide/kinds/csharp.mdx` (new)
- `docs/site/docs/user-guide/kinds/csharp-script.mdx` (new)
- `docs/site/docs/user-guide/kinds/csharp-task.mdx` (new)
- `docs/site/docs/user-guide/kinds/powershell.mdx` (new)
- `docs/site/docs/user-guide/kinds/powershell-task.mdx` (new)
- `docs/site/docs/user-guide/kinds/ifelse.mdx` (new)
- `docs/site/docs/user-guide/kinds/switch.mdx` (new)
- `docs/site/docs/user-guide/kinds/foreach.mdx` (new)
- `docs/site/docs/user-guide/kinds/while.mdx` (new)
- `docs/site/docs/user-guide/kinds/subflow.mdx` (new)
- `docs/site/docs/user-guide/kinds/timer.mdx` (new)
- `docs/site/docs/user-guide/kinds/container.mdx` (new)
- `docs/site/docs/dev-guide/add-a-kind.mdx` (new — mirrors Appendix B)
- `docs/site/docs/api/index.mdx` (new — Redoc embed)
- `docs/site/src/components/OpenApiEmbed.tsx` (new)
- `docs/workflow-ui-user-guide.md` (new — single-file summary for docs/)
- `tools/capture-screenshots.ts` (new)
- `tools/check-appendix-b-parity.ts` (new)
- `.github/workflows/docs.yml` (new)

## Shared fixtures / fakes
- **`seed-docs-workspace/`** — pre-populated workflow set used by screenshot automation so
  captures are deterministic.
- **`appendix-b.fragment.md`** — the canonical checklist, included by both master plan and
  dev-guide page via file-include plugin; parity test compares byte-identity.
- **`lychee.toml`** — allow-list for external links (GitHub repos, docs.microsoft.com).

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="8.2"`, `phaseId="8"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-8.2.plan-read`, `workflow-ui.stage-8.2.apply`, `workflow-ui.stage-8.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_8_2_started_total`, counter `workflow_ui_stage_8_2_failed_total`, histogram `workflow_ui_stage_8_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 8.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage82` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Build the docs/package from a clean checkout and follow the published quickstart exactly once.
- Run the release dry-run, inspect generated notes/artifacts, then verify rollback steps are documented.
- Open the docs site in light/dark mode and keyboard through the main navigation + search.

---

### Step 1 — User guide (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
Author the end-user walk-through: installation, launching the designer, creating a workflow, and
a dedicated page per node kind (13 kinds) showing the property grid, YAML produced, and a
screenshot of the canvas. Screenshots are generated by Playwright against seeded workflows for
determinism. A `lychee`-based link checker runs in CI.

### 4. Depends on
- Step 7.3.3

### 5. Inputs
- Per-kind DTO shapes from §2.3 of master plan.
- Property-grid schema files (Phase 4).

### 6. Deliverables
- Docusaurus site scaffold.
- 13 node-kind pages + 2 tour pages + intro.
- `tools/capture-screenshots.ts` Playwright script.
- `docs/workflow-ui-user-guide.md` single-file summary (plan requirement; links to site).
- CI workflow `docs.yml` running build, screenshot capture, and `lychee`.

### 7. Acceptance criteria
- [ ] Every `RuntimeType` enum value (13 in §0.2.1) has a dedicated page.
- [ ] Each page shows: description, configurable fields (matching DTO), example YAML, canvas
      screenshot, "edges out" table keyed by `MessageType`.
- [ ] `lychee` passes with zero broken internal/external links.
- [ ] Screenshots regenerate deterministically (same seed → same bytes within 1 KB delta for
      anti-aliasing tolerance).
- [ ] Build output under `docs/site/build/` deploys via GitHub Pages.
- [ ] Search (Docusaurus local search plugin) indexes all pages.
- [ ] Dark mode + light mode both render correctly (visual regression snapshot).

### 8. Unit tests (doc-build harness)
1. `SidebarGenerator_IncludesAllKinds` — tool scans `kinds/` dir, compares count to 13.
2. `KindPage_ContainsDtoFields` — scans each MDX, asserts every property name from DTO appears.
3. `KindPage_ContainsExampleYaml`.
4. `KindPage_ContainsMessageTypeTable`.
5. `LinkChecker_PassesOnFullSite`.
6. `ScreenshotCapture_Idempotent`.
7. `BuildOutput_ContainsAllExpectedPaths`.
8. `Search_IndexIncludesKindPages`.

AAA sketches (node script flavoured):

```ts
// #2 KindPage_ContainsDtoFields
import { dtoShapes } from '../src/contract/dto-shapes';
test.each(Object.entries(dtoShapes))('%s page lists all DTO fields', async (kind, fields) => {
  const md = await fs.readFile(`docs/site/docs/user-guide/kinds/${slug(kind)}.mdx`, 'utf8');
  for (const f of fields) {
    expect(md, `${kind} missing ${f}`).toContain(f);
  }
});

// #5 LinkChecker_PassesOnFullSite
const { stdout, status } = await execa('lychee', ['--config','lychee.toml','docs/site/build']);
expect(status, stdout).toBe(0);

// #6 ScreenshotCapture_Idempotent
const first  = await captureAll('seed-docs-workspace');
const second = await captureAll('seed-docs-workspace');
for (const key of Object.keys(first)) {
  const diff = pixelDiff(first[key], second[key]);
  expect(diff).toBeLessThan(1024);
}
```

### 9. Integration tests
- Playwright spec running on dev server for capture flow.
- `docs.yml` workflow executes build + link check + capture on every PR touching `docs/site/`.

### 10. Golden-file / parity tests
- For each node kind, a fragment listing expected DTO fields is generated at test time from
  `engine-contract.ts` and diffed against the MDX.
- `message-type-table.golden.md` fragment reused across kind pages.

### 11. Edge cases & negative tests
- Adding a new enum value to `RuntimeType` without a kind page: `SidebarGenerator_IncludesAllKinds`
  fails.
- Non-deterministic screenshot (animation mid-render): Playwright uses `animations: 'disabled'`.
- External link rotations (e.g. Microsoft docs URL changes): `lychee` cache + allow-list.
- MDX with unclosed JSX: Docusaurus build fails — caught by CI build step.
- Typos in DTO field names: DTO-shape parity test catches.

### 12. Observability hooks
- Docs build time captured as CI metric; regression > 20% fails.
- `lychee --format json` output uploaded as CI artifact.

### 13. Risks & mitigations
- **R8.2.1-A — screenshot drift on theme change**: snapshots regenerated per release; visual
  diff tool surfaces unintended changes.
- **R8.2.1-B — MDX <-> DTO drift**: parity test blocks merge.

### 14. Evaluator gap-analysis prompts
1. Are all 13 kinds covered?
2. Does each page list the exact DTO fields?
3. Is the link checker wired to CI?
4. Are screenshots captured via automation?
5. Is the MessageType edge table present per kind?
6. Is `RetryPolicy` / `CircuitBreakerPolicy` covered on the base node page?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, link checker green, DTO-field parity green.

---

### Step 2 — Developer guide (add-a-kind) (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Author `docs/site/docs/dev-guide/add-a-kind.mdx` mirroring Appendix B of the master plan. Provide
a worked dog-food example ("add a `WebhookNode`") showing each of the 9 checklist items applied
in sequence: `*NodeDefinition.cs`, `engine-contract.ts` regeneration, per-kind DTO, port matrix
entry, palette + icon, mapper partial, golden fixture pair, property-grid schema, user-guide
section. A CI "parity" check compares the checklist bullets in the dev-guide page against the
master plan Appendix B and fails on drift.

### 4. Depends on
- Step 8.2.1

### 5. Inputs
- Appendix B (§ master plan).
- Phase 5 mapper surface.

### 6. Deliverables
- `add-a-kind.mdx` with 9-step walk-through + dog-food example.
- `tools/check-appendix-b-parity.ts` — parses both files, normalises whitespace, diffs.
- Dog-food kind fixture under `src/__fixtures__/dev-guide/WebhookNode/` (not shipped to engine;
  used only by the example compile-test).

### 7. Acceptance criteria
- [ ] Dev-guide checklist has exactly 9 numbered items.
- [ ] Each item matches Appendix B verbatim after whitespace normalisation.
- [ ] Dog-food example compiles in a dedicated CI job `docs-dogfood.yml`.
- [ ] Every cross-reference to a phase/step is a clickable link.
- [ ] Screenshots of palette + property grid for WebhookNode generated by automation.
- [ ] Parity tool output included as a CI artifact on failure.

### 8. Unit tests
1. `AppendixBParity_ExactMatch`.
2. `Checklist_HasNineItems`.
3. `Dogfood_FixtureCompiles`.
4. `Dogfood_MapperPartial_Present`.
5. `Dogfood_PortMatrixRow_Present`.
6. `Dogfood_PaletteIcon_Present`.
7. `CrossReferences_Resolve`.
8. `Screenshot_WebhookNode_Generated`.

AAA sketches:

```ts
// #1 AppendixBParity_ExactMatch
const planSection = extractAppendixB(await read('docs/workflow-ui-plan.md'));
const guideSection = extractChecklist(await read('docs/site/docs/dev-guide/add-a-kind.mdx'));
expect(normalise(guideSection)).toEqual(normalise(planSection));

// #3 Dogfood_FixtureCompiles
const { status } = await execa('dotnet', ['build', 'src/__fixtures__/dev-guide/WebhookNode/']);
expect(status).toBe(0);
```

### 9. Integration tests
- `docs-dogfood.yml` workflow builds the fixture kind and runs the test matrix.

### 10. Golden-file / parity tests
- `appendix-b.fragment.md` is the canonical source; the parity tool reads both sides.

### 11. Edge cases & negative tests
- Appendix B item renumbered in master plan: parity tool detects and fails.
- MDX uses a callout that changes line count: parity normalises before diff.
- Dog-food fixture shadows a real kind: test runs in isolation project, never loaded by host.
- Mapper partial missing for dog-food: compile fails, CI red.
- New item added to Appendix B (10 items): parity fails; doc author updates.

### 12. Observability hooks
- CI produces `parity-report.txt` artifact on failure.

### 13. Risks & mitigations
- **R8.2.2-A — docs drift**: parity tool is the sole gate.
- **R8.2.2-B — dog-food kind becomes real**: namespace `__DevGuideSample__` prefix prevents
  accidental uptake.

### 14. Evaluator gap-analysis prompts
1. Is Appendix B parity enforced by a CI check?
2. Is the dog-food example compiled, not just referenced?
3. Does the guide cite real file paths?
4. Are cross-references valid?
5. Does the example include a mapper partial + port matrix row?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, parity check green, dog-food compile green.

---

### Step 3 — API reference from OpenAPI (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Expose `/api/openapi.json` via Swashbuckle (already a Step 1.1.3 transitive) and embed Redoc in
the docs site for browsing. Add a CI step validating the OpenAPI schema with `openapi-cli
validate` and asserting every controller method has `[ProducesResponseType]` annotations. The
Redoc page auto-refreshes on build.

### 4. Depends on
- Step 1.1.3

### 5. Inputs
- Controllers + minimal API routes.
- Swashbuckle configuration.

### 6. Deliverables
- `Program.cs` additions: `AddSwaggerGen`, `UseSwagger`.
- `OpenApiEmbed.tsx` React component embedding Redoc.
- `docs/site/docs/api/index.mdx` using `<OpenApiEmbed />`.
- CI step running `openapi-cli validate`.

### 7. Acceptance criteria
- [ ] `GET /api/openapi.json` returns 200 with `openapi: 3.0.x` document.
- [ ] Schema validates against OAS 3.0.
- [ ] Every controller action produces at least one `ProducesResponseType` annotation — else CI
      fails.
- [ ] Redoc page renders in docs site.
- [ ] Operation IDs are stable (used by generated clients in stretch).
- [ ] Security schemes include `bearer` (JWT) as declared.
- [ ] Version in OpenAPI info block equals `VersionInfo.Version`.

### 8. Unit tests
1. `OpenApi_Endpoint_Returns200`.
2. `OpenApi_Document_ValidatesAgainstOAS3`.
3. `Controllers_AllActions_HaveProducesResponseType`.
4. `OpenApi_SecurityScheme_Bearer_Declared`.
5. `OpenApi_Version_MatchesVersionInfo`.
6. `OperationIds_AreStable` (golden list).
7. `RedocEmbed_RendersInTestDom`.
8. `OpenApi_NoHardcodedLocalhost`.

AAA sketches:

```csharp
// #2 OpenApi_Document_ValidatesAgainstOAS3
var client = factory.CreateClient();
var json = await client.GetStringAsync("/api/openapi.json");
var result = new OpenApiStringReader().Read(json, out var diag);
diag.Errors.Should().BeEmpty();
result.Info.Version.Should().NotBeNullOrEmpty();

// #3 Controllers_AllActions_HaveProducesResponseType
var bad = typeof(Program).Assembly.GetTypes()
    .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
    .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
    .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
    .Where(m => !m.GetCustomAttributes<ProducesResponseTypeAttribute>().Any())
    .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
    .ToList();
bad.Should().BeEmpty($"Missing ProducesResponseType on: {string.Join(", ", bad)}");
```

### 9. Integration tests
- Docs build: Redoc renders on dev server; Playwright asserts `.redoc-wrap` present.

### 10. Golden-file / parity tests
- `openapi-operationids.golden.json` — stable ids; changing one requires deliberate update.

### 11. Edge cases & negative tests
- Controller added without `[ProducesResponseType]`: CI fails.
- Breaking change to response shape: `operationId` unchanged but schema diff surfaces in PR.
- Swagger UI disabled in prod (only Redoc in docs site): test environment check.
- Very large schema: Redoc lazy-loads (documented).
- Circular schema references: Swashbuckle handles via `$ref`.

### 12. Observability hooks
- Metric `openapi_document_bytes` (sanity check for bloat).

### 13. Risks & mitigations
- **R8.2.3-A — hand-edited docs drift from code**: Redoc is generated, not written.
- **R8.2.3-B — operationId instability breaks codegen clients**: golden list locks ids.

### 14. Evaluator gap-analysis prompts
1. Is every action annotated with `ProducesResponseType`?
2. Is the schema validated in CI?
3. Is Redoc embedded (not just linked)?
4. Are operationIds stable?
5. Does the OpenAPI version match assembly version?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, schema validation green, `ProducesResponseType` scan green.
