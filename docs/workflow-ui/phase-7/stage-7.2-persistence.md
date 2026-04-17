# Stage 7.2 — Persistence

**Phase:** §13 Phase 7 — Auth, Persistence, File Ops
**Stage status:** [ ]
**Total effort:** 7 d
**Step count:** 4

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Phase 7.2 promotes in-memory state to durable storage. The master plan pins a **file-based**
persistence model in v1: workflows live as `{id}.yaml` + `{id}.ui.json` sidecar pairs in a
configurable `WorkflowsRoot` directory, and run history lives as JSONL sidecars under
`RunsRoot/{runId}/events.jsonl`. The host exposes an `IWorkflowStore` abstraction that the
existing controllers already call (Phase 2.4.4); Stage 7.2.1 provides the filesystem adapter and
wires it through DI. A **SQLite adapter is STRETCH** — scaffold only, not production-required in
v1 (clearly labelled in Step 7.2.1 deliverables).

Engine ground truth in play here:
- `src/ExecutionEngine/Workflow/WorkflowLoader.cs` — YAML load path; the store delegates to it
  for validation on read.
- `src/ExecutionEngine/Workflow/WorkflowSerializer.cs` — YAML save path; the store delegates for
  writes to guarantee byte-identical canonicalisation with engine.
- `src/ExecutionEngine/Persistence/ICheckpointStorage.cs` & `FileCheckpointStorage.cs` — an
  existing engine-owned checkpoint surface. The UI run-history is distinct (event log, not state
  checkpoint) and does NOT re-use this; reference only for naming conventions (per-instance
  files, JSON, directory-configurable).

ETag semantics: `ETag = SHA-256(yamlBytes || sidecarBytes)` hex-encoded. Optimistic concurrency
uses `If-Match` on PUT; mismatch returns `412 Precondition Failed`. File mtime resolution is
NOT relied upon (Windows NTFS 100 ns vs POSIX 1 s mismatches).

Cleanup: run JSONL files older than 30 days are deleted by a background hosted service (Step
7.2.4) using an injected `IClock` so tests can advance time synthetically.

## Prerequisites
- Stage 7.1 complete (auth guards store endpoints).
- Stage 6.2.3 run-history buffer (source of events for JSONL writer).
- External deps: `System.IO.Abstractions 21.x` (for fake filesystem in tests),
  `YamlDotNet 15.x` (same version engine uses per `global.json` / Directory.Packages.props),
  `Microsoft.Data.Sqlite 8.0.x` (stretch-only scaffold).

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 7.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/host/WorkflowUi.Host/Persistence/IWorkflowStore.cs` (new)
- `src/host/WorkflowUi.Host/Persistence/FileWorkflowStore.cs` (new, Step 7.2.1)
- `src/host/WorkflowUi.Host/Persistence/ETagComputer.cs` (new, Step 7.2.2)
- `src/host/WorkflowUi.Host/Persistence/PathSafety.cs` (new — canonicalise + prefix check)
- `src/host/WorkflowUi.Host/Persistence/SqliteWorkflowStore.cs` (new, STRETCH label)
- `src/host/WorkflowUi.Host/Persistence/RunHistoryWriter.cs` (new, Step 7.2.3)
- `src/host/WorkflowUi.Host/Persistence/RunHistoryReader.cs` (new)
- `src/host/WorkflowUi.Host/Persistence/RunCleanupHostedService.cs` (new, Step 7.2.4)
- `src/host/WorkflowUi.Host/Persistence/IClock.cs` + `SystemClock.cs` (new)
- `src/host/WorkflowUi.Host/DependencyInjection/PersistenceServiceCollectionExtensions.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Persistence/FileWorkflowStoreTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Persistence/ETagComputerTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Persistence/PathSafetyTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Persistence/RunHistoryWriterTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Persistence/RunCleanupTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Persistence/WorkflowCrudTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Persistence/ConcurrencyTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Persistence/RestartDurabilityTests.cs` (new)
- `src/host/WorkflowUi.Host.ContractTests/PersistenceShapeTests.cs` (new — round-trip parity)

## Shared fixtures / fakes
- **`MockFileSystem`** (`System.IO.Abstractions.TestingHelpers`) — isolates tests from real disk.
- **`FakeClock`** implementing `IClock` with `Advance(TimeSpan)`.
- **`seed/workflows/*.yaml`** — 5 golden workflows spanning all `RuntimeType` values for
  round-trip parity.
- **Seed SQLite DB**: `seed/sqlite/workflows.seed.db` (stretch-only, migrations-free schema).

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="7.2"`, `phaseId="7"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-7.2.plan-read`, `workflow-ui.stage-7.2.apply`, `workflow-ui.stage-7.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_7_2_started_total`, counter `workflow_ui_stage_7_2_failed_total`, histogram `workflow_ui_stage_7_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 7.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage72` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Log in, expire the token/session intentionally, and confirm recovery or redirect behavior.
- Save/open a workflow from nested folders and verify traversal attempts are blocked with the documented error.
- Simulate two tabs editing the same workflow and confirm the stale-write guidance is visible.

---

## Step 7.2.1 — Workflow repository abstraction (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Define `IWorkflowStore` and implement `FileWorkflowStore` as the v1 default. Operations: `ListAsync`,
`GetAsync(id)`, `SaveAsync(id, yaml, sidecar, etag?)`, `DeleteAsync(id, etag)`, `ExistsAsync(id)`.
Every operation takes a `ClaimsPrincipal` for role enforcement and produces structured audit
logs via Stage 7.1.4. Paths are canonicalised against `WorkflowsRoot`; any traversal attempt
(`..`) is rejected. The engine's `WorkflowLoader`/`WorkflowSerializer` are the ONLY code paths
used for YAML parsing/emission — the store never hand-rolls YAML.

**Stretch:** scaffold `SqliteWorkflowStore` behind the same interface with a `--persistence=sqlite`
startup flag; ship as non-default and explicitly flagged experimental.

### 4. Depends on
- Step 2.4.4 (YAML load endpoint)
- Step 2.4.5 (YAML save endpoint)

### 5. Inputs
- `WorkflowsRoot` path from `PersistenceOptions`.
- Engine `WorkflowLoader` + `WorkflowSerializer` instances.

### 6. Deliverables
- `IWorkflowStore.cs`:
  ```csharp
  public interface IWorkflowStore {
      Task<IReadOnlyList<WorkflowSummary>> ListAsync(ClaimsPrincipal u, CancellationToken ct);
      Task<WorkflowRecord?> GetAsync(string id, ClaimsPrincipal u, CancellationToken ct);
      Task<SaveResult> SaveAsync(string id, string yaml, string sidecarJson, string? etag,
                                 ClaimsPrincipal u, CancellationToken ct);
      Task<bool> DeleteAsync(string id, string etag, ClaimsPrincipal u, CancellationToken ct);
  }
  public record WorkflowRecord(string Id, string Yaml, string SidecarJson, string ETag,
                               DateTime LastModifiedUtc);
  public enum SaveResult { Created, Updated, PreconditionFailed }
  ```
- `FileWorkflowStore.cs` — uses `IFileSystem` abstraction.
- `PathSafety.CanonicalizeAndCheck(root, id) → string?` returns null on traversal.
- Registration in DI with default `FileWorkflowStore`; conditional `SqliteWorkflowStore` for
  `persistence=sqlite`.
- Unit + integration tests.

### 7. Acceptance criteria
- [ ] CRUD round-trips preserve YAML byte-for-byte (engine-canonicalised).
- [ ] Path traversal `../etc/passwd` returns 400 and logs warning.
- [ ] Concurrent `SaveAsync` for same id: one succeeds, other returns `PreconditionFailed` when
      `etag` provided.
- [ ] `ListAsync` returns summaries with id + last modified + size; does NOT load full YAML.
- [ ] Viewer role can call List/Get; Editor required for Save/Delete.
- [ ] Missing id on Get returns `null` (→ controller 404).
- [ ] Sidecar missing is allowed (treated as empty JSON `{}`); YAML missing is NOT (returns null).

### 8. Unit tests
1. `Save_NewId_Creates_BothFiles`.
2. `Save_ExistingId_WithMatchingEtag_Updates`.
3. `Save_ExistingId_WithStaleEtag_ReturnsPreconditionFailed`.
4. `Save_InvalidYaml_ReturnsError_NoFilesWritten`.
5. `Get_MissingId_ReturnsNull`.
6. `Get_YamlPresent_SidecarMissing_ReturnsRecordWithEmptySidecar`.
7. `Delete_WithMatchingEtag_RemovesBothFiles`.
8. `Delete_WithStaleEtag_ReturnsFalse`.
9. `List_ReturnsOnlyYamlFiles_IgnoresOther`.
10. `PathTraversal_Rejected_AndLogged`.
11. `Save_FileSystemIoError_PropagatesAsFaultedTask`.
12. `EngineSerializer_Invoked_ForEveryWrite` (spy assertion).

Three AAA sketches:

```csharp
// #2 Save_ExistingId_WithMatchingEtag_Updates
var fs = new MockFileSystem();
var store = new FileWorkflowStore(fs, new PersistenceOptions { WorkflowsRoot = "C:/wf" },
    engineSerializer, engineLoader, NullLogger<FileWorkflowStore>.Instance);
var initial = await store.SaveAsync("wf1", yaml1, "{}", etag:null, Editor(), default);
var record  = (await store.GetAsync("wf1", Editor(), default))!;
// Act
var res = await store.SaveAsync("wf1", yaml2, "{}", etag:record.ETag, Editor(), default);
// Assert
res.Should().Be(SaveResult.Updated);
fs.GetFile("C:/wf/wf1.yaml").TextContents.Should().Be(yaml2);

// #3 Save_ExistingId_WithStaleEtag_ReturnsPreconditionFailed
await store.SaveAsync("wf1", yaml1, "{}", null, Editor(), default);
// Act
var res = await store.SaveAsync("wf1", yaml2, "{}", etag:"stale-etag", Editor(), default);
// Assert
res.Should().Be(SaveResult.PreconditionFailed);
fs.GetFile("C:/wf/wf1.yaml").TextContents.Should().Be(yaml1);  // unchanged

// #10 PathTraversal_Rejected_AndLogged
var logger = new ListLogger<FileWorkflowStore>();
var store = new FileWorkflowStore(fs, opts, ser, loader, logger);
// Act
var result = await store.SaveAsync("../etc/passwd", "yaml", "{}", null, Editor(), default);
// Assert
result.Should().Be(SaveResult.PreconditionFailed);
fs.AllFiles.Should().BeEmpty();
logger.Entries.Should().ContainSingle(e =>
    e.Level == LogLevel.Warning && e.Message.Contains("traversal"));
```

### 9. Integration tests
- `WorkflowCrudTests.CreateReadUpdateDelete_EndToEnd` via API with Editor token.
- `WorkflowCrudTests.ViewerToken_CanRead_CannotWrite`.

### 10. Golden-file / parity tests
- `PersistenceShapeTests.SaveThenGet_RoundTripsAllFixtures` — seed 5 workflows, save via store,
  read back, assert byte-identical YAML + sidecar.
- Parity assertion: `yaml` produced by store MUST equal `WorkflowSerializer.Serialize(WorkflowLoader
  .Load(yaml))`.

### 11. Edge cases & negative tests
- Id with non-ASCII characters: accepted if not traversal, filesystem-safe encoded.
- Id `"CON"` on Windows (reserved name): rejected with 400.
- Extremely long id (> 200 chars): rejected.
- YAML with BOM: engine handles; store preserves.
- Symlink inside `WorkflowsRoot` pointing outside: rejected by canonicalisation (`GetFullPath`
  resolves, then `StartsWith(root)` check).
- Filesystem disk full on Save: faulted task, no partial file left (write to `.tmp` + atomic
  rename).

### 12. Observability hooks
- Metric `workflow_store_operations_total{op, result}`.
- Log `"Store.{Op} id={Id} user={UserId} result={Result}"` at Info.
- OTel span per op.

### 13. Risks & mitigations
- **R7.2.1-A — path traversal**: canonicalise + StartsWith; test covers escaping attempts.
- **R7.2.1-B — partial write on crash**: write to sibling `.tmp` then rename (Windows
  `File.Replace`).
- **R7.2.1-C — SQLite stretch never ships**: clearly label experimental; no docs reference v1.

### 14. Evaluator gap-analysis prompts
1. Is the store the ONLY code path that touches `WorkflowsRoot`?
2. Does Save use atomic rename to prevent partial writes?
3. Is `WorkflowSerializer` invoked for every write (no hand-rolled YAML)?
4. Is SQLite labelled STRETCH and disabled by default?
5. Are reserved Windows names rejected?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, CRUD integration green, parity round-trip across 5 fixtures green.

---

## Step 7.2.2 — Optimistic concurrency via ETag (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Compute a stable `ETag = sha256(yamlBytes || sidecarBytes)` on every Get; controllers surface it
as the `ETag` HTTP header on reads and require `If-Match` on writes. Two concurrent PUTs
targeting the same workflow: the later writer receives **412 Precondition Failed**. UI surfaces
the 412 as a "changed on disk" toast with diff/merge options (UI copy only, no merge logic in v1).

### 4. Depends on
- Step 7.2.1

### 5. Inputs
- `yamlBytes`, `sidecarBytes` from store.
- Request `If-Match` header.

### 6. Deliverables
- `ETagComputer.Compute(yaml, sidecar) → string` (hex sha256, 64 chars, quoted on wire).
- Controllers add `ETag` response header on Get, `If-Match` required on Put/Delete.
- 412 response body includes `{ currentEtag: "..." }` so client can prompt refresh.
- Unit tests + Playwright two-tab edit test.

### 7. Acceptance criteria
- [ ] ETag is deterministic (same bytes → same etag).
- [ ] ETag changes when yaml OR sidecar bytes change.
- [ ] Order of concatenation (yaml first, sidecar second) is fixed — documented.
- [ ] 412 response includes current etag in body.
- [ ] `If-Match: *` forces write regardless (admin escape hatch, allowed only for Editor).
- [ ] ETag header is weak-etag quoted: `W/"<hex>"`.

### 8. Unit tests
1. `Compute_SameInputs_ProducesSameEtag`.
2. `Compute_DifferentYaml_DifferentEtag`.
3. `Compute_DifferentSidecar_DifferentEtag`.
4. `Compute_OrderMatters_YamlBeforeSidecar`.
5. `IfMatch_Wildcard_ForcesWrite`.
6. `IfMatch_Missing_On_PUT_Returns_428_PreconditionRequired`.
7. `IfMatch_Stale_On_PUT_Returns_412_WithCurrentEtag`.
8. `GetResponse_IncludesWeakEtagHeader`.

AAA sketches:

```csharp
// #3 Compute_DifferentSidecar_DifferentEtag
var e1 = ETagComputer.Compute("yaml"u8, """{"a":1}"""u8);
var e2 = ETagComputer.Compute("yaml"u8, """{"a":2}"""u8);
e1.Should().NotBe(e2);

// #7 IfMatch_Stale_On_PUT_Returns_412_WithCurrentEtag
await client.PutAsync("/api/workflows/wf1", PutBody("yaml1"), ifMatch: null);
var resp1 = await client.GetAsync("/api/workflows/wf1");
var etag = resp1.Headers.ETag!.Tag;
await client.PutAsync("/api/workflows/wf1", PutBody("yaml2"), ifMatch: etag);     // ok
// Act — write with stale etag
var stale = await client.PutAsync("/api/workflows/wf1", PutBody("yaml3"), ifMatch: etag);
// Assert
stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
var body = await stale.Content.ReadFromJsonAsync<Precondition412Body>();
body!.CurrentEtag.Should().NotBe(etag);

// #4 Compute_OrderMatters_YamlBeforeSidecar
var a = ETagComputer.Compute("A"u8, "B"u8);
var b = ETagComputer.Compute("B"u8, "A"u8);
a.Should().NotBe(b);
```

### 9. Integration tests
- `ConcurrencyTests.TwoTab_Edit_SecondReturns412` via WebApplicationFactory + Playwright.

### 10. Golden-file / parity tests
- `ETagSample.json` containing `(yaml, sidecar, expectedEtag)` triples — pinned sha256 outputs.

### 11. Edge cases & negative tests
- Missing `If-Match` on PUT → 428 Precondition Required (not 412).
- Very large YAML (> 1 MB) — streaming hash to avoid buffering.
- Encoding ambiguity: hash is over UTF-8 bytes; BOM preserved in input.
- Concurrent GET + PUT: reader returns consistent snapshot (file read → hash → respond).
- Client sends malformed `If-Match` header (unquoted) — treated as stale, 412.

### 12. Observability hooks
- Metric `workflow_store_etag_mismatch_total`.
- Log `"Etag mismatch on {Id}: expected={Expected} got={Given}"`.

### 13. Risks & mitigations
- **R7.2.2-A — mtime resolution fooling etag**: we never use mtime; content hash only.
- **R7.2.2-B — weak vs strong etag semantics**: we emit weak (`W/`) since canonicalisation may
  reflow whitespace on re-save.

### 14. Evaluator gap-analysis prompts
1. Is etag truly content-based (not mtime)?
2. Does 412 body include the current etag?
3. Is the concatenation order fixed + documented?
4. Is `*` wildcard supported and role-gated?
5. Is 428 used for missing If-Match (vs 412)?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, two-tab concurrency test green, pinned etag fixtures green.

---

## Step 7.2.3 — Run-history persistence (JSONL) (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Persist every `RunEvent` passing through `TelemetryHubSink` (Stage 6.2.1) to an append-only JSONL
file at `{RunsRoot}/{runId}/events.jsonl`. File rotates at 10 MB into `events.1.jsonl`,
`events.2.jsonl`, … with an index file `meta.json` listing segments + seq ranges. After host
restart, `RunHistoryReader` can replay any run from disk; `GET /api/runs` (Phase 6.3.3) includes
both active (buffer) and persisted (disk) runs.

### 4. Depends on
- Step 6.2.3 (ring buffer)

### 5. Inputs
- `RunEvent` stream from sink.
- `RunsRoot` configurable path.

### 6. Deliverables
- `RunHistoryWriter.cs` — async queue; one writer task per runId; flush every 100 events or 1 s.
- `RunHistoryReader.cs` — returns `IAsyncEnumerable<RunEvent>` for replay.
- `meta.json` schema: `{ "runId":"...", "segments":[{ "file":"events.1.jsonl", "fromSeq":1,
  "toSeq":1000 }], "terminalStatus":"Completed"|"Failed"|"Cancelled"|null,
  "terminalUtc":"..."|null }`.
- `RunHistoryWriterTests.cs` + `RestartDurabilityTests.cs`.

### 7. Acceptance criteria
- [ ] Every `RunEvent` appended within 1 s of receipt (flush cadence).
- [ ] File rotates at exactly 10 MB (± 1 event to preserve atomicity of newline).
- [ ] JSONL lines parse back to identical `RunEvent` objects.
- [ ] Meta file is rewritten atomically (`.tmp` + rename) on every rotation.
- [ ] On host restart, `RunHistoryReader.Replay(runId, fromSeq)` returns events in contiguous seq
      order across segments.
- [ ] Terminal events trigger final meta update with `terminalStatus` and `terminalUtc`.

### 8. Unit tests
1. `Append_WritesJsonlLine`.
2. `Append_BatchFlush_HappensWithin1s`.
3. `Rotation_At10MB_CreatesNewSegment`.
4. `Rotation_UpdatesMetaAtomically`.
5. `Reader_Replay_CrossesSegments`.
6. `TerminalEvent_UpdatesMetaStatus`.
7. `Writer_ConcurrentRuns_DoesNotInterleaveFiles`.
8. `Writer_DiskFull_LogsAndDropsGracefully`.

Three AAA sketches:

```csharp
// #3 Rotation_At10MB_CreatesNewSegment
var w = new RunHistoryWriter(fs, new RunsOptions { RunsRoot="C:/runs", SegmentBytes=1_000 },
    clock, NullLogger<RunHistoryWriter>.Instance);
for (var i=1; i<=200; i++)
    await w.AppendAsync(RunEventFactory.NodeCompleted(runId, seq:i, payloadSize:10));
await w.FlushAsync();
// Assert
fs.Directory.GetFiles($"C:/runs/{runId}").Should().Contain("events.1.jsonl")
    .And.Contain("events.2.jsonl");

// #5 Reader_Replay_CrossesSegments
// (prior arrange: 2 segments, seqs 1-100 and 101-150)
var r = new RunHistoryReader(fs, opts);
var all = new List<RunEvent>();
await foreach (var e in r.Replay(runId, fromSeq: 50, default)) all.Add(e);
// Assert
all.Select(e => e.Seq).Should().Equal(Enumerable.Range(50, 101));

// #6 TerminalEvent_UpdatesMetaStatus
await w.AppendAsync(RunEventFactory.RunCompleted(runId, seq:100));
await w.FlushAsync();
var meta = JsonSerializer.Deserialize<MetaJson>(fs.File.ReadAllText($"C:/runs/{runId}/meta.json"))!;
meta.TerminalStatus.Should().Be("Completed");
meta.TerminalUtc.Should().NotBeNull();
```

### 9. Integration tests
- `RestartDurabilityTests.Run_CompletedBeforeRestart_Replayable` — bring up host, run fixture,
  stop, bring up again, query `/api/runs/{id}/events`, assert 100 events returned.

### 10. Golden-file / parity tests
- `meta.golden.json` schema pinned.

### 11. Edge cases & negative tests
- JSONL file corruption mid-line: reader skips malformed line with warning, continues.
- Very slow disk: backpressure via bounded channel (cap 10k in-flight); drops oldest on overflow
  with metric increment.
- Two writers for same runId (misconfiguration): second fails to acquire file lock and logs
  error.
- Very short run (1 event, then crash): meta may be missing `terminalStatus` — reader defaults to
  `"Unknown"`.
- Seq gap detected on read: reader logs and yields gap marker (optional; default yields
  contiguous prefix).

### 12. Observability hooks
- Metric `workflow_ui_run_history_bytes_written_total`.
- Metric `workflow_ui_run_history_segments_total`.
- Log `"RunHistory {RunId} rotated to segment {N}"`.

### 13. Risks & mitigations
- **R7.2.3-A — disk fill**: 10 MB rotation + Stage 7.2.4 cleanup.
- **R7.2.3-B — meta not updated on crash**: reader tolerates missing fields.

### 14. Evaluator gap-analysis prompts
1. Is rotation truly atomic at 10 MB?
2. Does the reader correctly merge multiple segments?
3. Is the meta file the single source of truth for segment order?
4. Does terminal status propagate to meta?
5. Is backpressure bounded?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, restart durability test green.

---

## Step 7.2.4 — Run cleanup background job (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Implement `RunCleanupHostedService` — a `BackgroundService` that every hour scans `RunsRoot`,
parses each `meta.json`, and deletes run directories whose `terminalUtc < clock.UtcNow -
TimeSpan.FromDays(30)`. Uses the injected `IClock` so tests can drive the clock synthetically.
Retention window is configurable (`RunsOptions.RetainDays`, default 30).

### 4. Depends on
- Step 7.2.3

### 5. Inputs
- `RunsRoot` directory listing.
- `IClock.UtcNow`.

### 6. Deliverables
- `RunCleanupHostedService.cs` — `PeriodicTimer`-based.
- `IClock` + `SystemClock`.
- `RunCleanupTests.cs`.

### 7. Acceptance criteria
- [ ] Run with `terminalUtc > now - RetainDays` is retained.
- [ ] Run with `terminalUtc <= now - RetainDays` is deleted (both directory AND all segments).
- [ ] Run with `terminalUtc == null` (still running or unknown) is retained.
- [ ] Cleanup runs on an hourly timer; manual trigger via `POST /api/admin/cleanup` (Editor-only)
      available for tests.
- [ ] Deletion is atomic per run (removed via `Directory.Delete(runDir, recursive:true)`).
- [ ] Clock skew doesn't cause premature deletion — uses `DateTime.UtcNow` from `IClock`.
- [ ] Deletion logs one line per deleted run with count + bytes freed.

### 8. Unit tests
1. `Cleanup_ExpiredRun_Deleted`.
2. `Cleanup_NonExpiredRun_Retained`.
3. `Cleanup_RunWithoutTerminal_Retained`.
4. `Cleanup_CorruptMeta_SkipsWithWarning`.
5. `Cleanup_DryRunMode_CountsOnly`.
6. `Cleanup_TriggeredManually_RunsImmediately`.
7. `Cleanup_DoesNotDeleteOtherFilesystemContent`.
8. `Cleanup_Idempotent` (running twice with no expired runs is no-op).

Three AAA sketches:

```csharp
// #1 Cleanup_ExpiredRun_Deleted
var clock = new FakeClock(new DateTime(2025,1,31,0,0,0,DateTimeKind.Utc));
var fs = new MockFileSystem();
fs.AddFile("C:/runs/r1/meta.json", new MockFileData(
    """{"runId":"r1","segments":[],"terminalUtc":"2024-12-01T00:00:00Z","terminalStatus":"Completed"}"""));
fs.AddFile("C:/runs/r1/events.1.jsonl", new MockFileData("ev"));
var svc = new RunCleanupHostedService(fs, clock,
    Options.Create(new RunsOptions { RunsRoot="C:/runs", RetainDays=30 }),
    NullLogger<RunCleanupHostedService>.Instance);
// Act
await svc.RunOnceAsync(default);
// Assert
fs.Directory.Exists("C:/runs/r1").Should().BeFalse();

// #3 Cleanup_RunWithoutTerminal_Retained
fs.AddFile("C:/runs/r1/meta.json", new MockFileData(
    """{"runId":"r1","segments":[],"terminalUtc":null}"""));
await svc.RunOnceAsync(default);
fs.Directory.Exists("C:/runs/r1").Should().BeTrue();

// #4 Cleanup_CorruptMeta_SkipsWithWarning
var logger = new ListLogger<RunCleanupHostedService>();
fs.AddFile("C:/runs/r1/meta.json", new MockFileData("not json"));
var svc = new RunCleanupHostedService(fs, clock, opts, logger);
await svc.RunOnceAsync(default);
fs.Directory.Exists("C:/runs/r1").Should().BeTrue();
logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
```

### 9. Integration tests
- None required (unit tests cover via `MockFileSystem`).

### 10. Golden-file / parity tests
- Not required.

### 11. Edge cases & negative tests
- Directory deletion fails (file locked on Windows): log error, retry next cycle.
- Meta file missing but segments present: skipped with warning (not deleted — avoids data loss).
- Clock moves backward (NTP adjustment): cleanup skips if computed age negative.
- Partial directory layout (only `events.1.jsonl`, no meta): skipped.
- `RetainDays=0` (eager deletion mode): supported; deletes all terminal runs immediately.

### 12. Observability hooks
- Metric `run_cleanup_runs_deleted_total`.
- Metric `run_cleanup_bytes_freed_total`.
- Log `"Cleanup deleted {Count} runs, freed {Bytes}"` at Information on each cycle.

### 13. Risks & mitigations
- **R7.2.4-A — accidental mass deletion**: require `RunsRoot` non-empty and under host-owned
  path; unit test enforces.
- **R7.2.4-B — clock drift via DI misconfig**: `SystemClock` default; `IClock` interface lets
  tests inject.

### 14. Evaluator gap-analysis prompts
1. Is `IClock` actually injected (not `DateTime.UtcNow` inline)?
2. Does cleanup skip runs without `terminalUtc`?
3. Is the retention window configurable?
4. Is deletion restricted to `RunsRoot` subtrees?
5. Is dry-run mode available for safety audits?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, all 8 unit tests green, clock-advanced verification stable.
