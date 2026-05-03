# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 11)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. We need a graph-based agent memory system that builds persistent expertise on top-down repository structure and call-chain context, so agents can learn and get smarter across sessions.

The `AgentMemory` library already provides a domain model (`Episode`, `ExpertiseRecord`, `CallChain`, `MemoryEdge`), two storage backends (in-memory, file-system), a staged `ContextBuilder`, and a `MemoryEvolver` with consolidation, decay, promotion, and co-change detection. However, the library has **twelve correctness defects** (D1–D12) that must be fixed before further features are safe to add.

### Root Defects

| # | Defect | Location | Impact |
|---|--------|----------|--------|
| D1 | `ExpertiseRecord` has 8 mutable `set` properties (`Tier`, `Confidence`, `EpisodeCount`, `Summary`, `LastValidated`, `CommitSha`, `Branch`, `AccessCount`) | ExpertiseRecord.cs:36–95 | Callers mutate live store references |
| D2 | Collections `DerivedFromEpisodeIds`, `PositiveEvidence`, `NegativeEvidence`, `Properties` are mutable `List<>`/`Dictionary<>` | ExpertiseRecord.cs:52–101 | MemoryEvolver calls `.AddRange()`/`.Add()` on retrieved objects |
| D3 | `TierMapping` is passive — never called on upsert or creation | MemoryModels.cs:201–231 | Invalid tier/category combos persist silently |
| D4 | Stores persist without normalization/validation | InMemoryAgentMemoryStore.cs:126, FileSystemAgentMemoryStore.cs:160 | `this.expertise[record.Id] = record` with no checks |
| D5 | Read paths return live mutable references | InMemoryAgentMemoryStore.cs:131–163, FileSystemAgentMemoryStore.cs:165–196 | Caller mutates → store state corrupted |
| D6 | `FailurePattern` records created without `Tier` | MemoryEvolver.cs:543–561 | Defaults to `Interaction`; `DefaultTierFor(FailurePattern)` is `Query` |
| D7 | `PromoteAsync` creates new promoted records every run — not idempotent | MemoryEvolver.cs:671–716 | Duplicate stale records accumulate |
| D8 | `DetectCoChangesAsync` creates new `CoChangedWith` edges and co-change groups every run — not idempotent | MemoryEvolver.cs:837–879 | Duplicate edges distort retrieval |
| D9 | `ContextBuilder.GenerateCandidatesAsync` never applies `ContextRequest.AgentId` to episode filters | ContextBuilder.cs:90–152 | Agent-scoped context includes unrelated agents' episodes |
| D10 | `FileSystemAgentMemoryStore.LoadAsync` catches `JsonException` and returns empty dict — silently discards data | FileSystemAgentMemoryStore.cs:492–496 | Corrupted files appear as valid empty memory |

### Additional Defects Discovered

| # | Defect | Location | Impact |
|---|--------|----------|--------|
| D11 | `ContextBuilder.AssembleContextWindow` collects `StructuralNodes`/`RelationalEdges` but never includes them in output | ContextBuilder.cs:312–373 | Graph-backed retrieval produces no structural context |
| D12 | `ContextBuilder` always pulls high-confidence `PatternKnowledge` regardless of task relevance | ContextBuilder.cs:181–187 | Unrelated institutional knowledge crowds out task-specific context |

### Complete MemoryEvolver Mutation Site Audit

| Method | Lines | Mutation |
|--------|-------|----------|
| `DecayAsync` | 336 | `expertise.Confidence = newConfidence` |
| `ConsolidateFileExpertiseAsync` | 357–383 | `current.EpisodeCount=...; current.Confidence=...; current.DerivedFromEpisodeIds.AddRange(...); current.PositiveEvidence.Add(...); current.Summary=...` |
| `ConsolidateSymbolExpertiseAsync` | 452–458 | `current.EpisodeCount=...; current.Confidence=...; current.DerivedFromEpisodeIds.AddRange(...); current.Summary=...` |
| `ConsolidateFailurePatternAsync` (update) | 513–534 | `current.EpisodeCount=...; current.LastValidated=...; current.DerivedFromEpisodeIds.AddRange(...); current.NegativeEvidence.Add(...); current.Summary=...` |
| `ConsolidateFailurePatternAsync` (create) | 543–561 | `new ExpertiseRecord { ... }` without `Tier` (D6) |
| `CreatePromotedExpertiseAsync` | 682–697 | Creates new record with `Guid` ID every run (D7) |
| `DetectCoChangesAsync` (edges) | 837–851 | Creates new `CoChangedWith` edges with `Guid` ID every run (D8) |
| `DetectCoChangesAsync` (groups) | 864–877 | Creates new `CoChangeGroup` expertise with `Guid` ID every run (D8) |
| `GetExpertiseByTierFallbackAsync` | 726 | References `TierMapping.DefaultTierFor` (will break when TierMapping deleted) |

### Key Constraints

- **Persistence boundary**: Memory needs separate labels from `Ast.GraphDb.IGraphStore` (which hardcodes `:CodeNode`).
- **Cross-backend invariant**: Tier enforcement must be uniform across InMemory, FileSystem, and future Neo4j.
- **Graph coexistence**: Memory graph and code graph share a Neo4j instance; separate labels, clearing one must not affect the other.
- **Branch scoping**: Multi-worktree repos require branch-aware retrieval. `EpisodeFilter` and `ContextRequest` need explicit `Branch` fields.
- **Single-writer**: Stores are single-writer per session; no optimistic concurrency in Phase 1.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

| Source | Key Idea | Applied Here |
|--------|----------|--------------|
| **G-Memory** (arXiv:2506.07398) | 3-tier hierarchy: Insight → Query → Interaction | `MemoryTier` enum + `TierPolicy` enforcement mapping to repo structure |
| **MAGMA** (arXiv:2601.03236) | Multi-graph with orthogonal views | Multi-signal reranking in `ContextBuilder` with branch-aware scoring |
| **Zep/Graphiti** (arXiv:2501.13956) | Temporally-aware KG | `CommitSha` freshness, half-life decay |
| **Mem0** (arXiv:2504.19413) | Scalable graph consolidation with write gating | `MemoryEvolver` consolidation + decay + episode quality filter |
| **HyperGraphRAG** (arXiv:2503.21322) | N-ary relation modeling | `CallChain` (ordered n-ary method sequences) |
| **ExpeL** (AAAI'24) | Insight distillation from experience | Episode → expertise consolidation |

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **`ExpertiseRecord` stays `class`, NOT `record class`** | `record class` changes equality semantics (value equality), which would alter `HashSet`/`Dictionary` behavior in ContextBuilder and MemoryEvolver. Instead: keep `class`, change all `set` → `init`, add `With(...)` copy helper, use `ImmutableArray<T>`/`ImmutableDictionary<TK,TV>` for collections. |
| **`TierPolicy` replaces `TierMapping`** | Single authority. `AllowedTiers(category)` → per-category allowed tier sets. `Normalize(record)` returns corrected copy. `Validate(record, previous?)` rejects invalid combos. Exhaustive `switch` on all 11 `ExpertiseCategory` values — no wildcard default. |
| **Deterministic IDs for idempotent evolution** | `PromoteAsync` uses deterministic ID `$"exp-promo-{targetTier}-{ComputeStableHash(category + '|' + sourceTier + '|' + groupKey)}"` — includes category and source tier to prevent collisions between unrelated source groups sharing a groupKey. `DetectCoChangesAsync` uses `edge-cc-{ComputeStableHash(sortedPair)}` for edges and `exp-cc-{ComputeStableHash(sortedFileList)}` for groups. Repeated runs converge to same state. Upsert semantics recompute confidence/metadata. Source IDs are used for **reconciliation** (merging new sources into existing promoted records), not for identity. |
| **`AddMemoryEdgeAsync` is upsert-by-ID** | Both `InMemoryAgentMemoryStore` and `FileSystemAgentMemoryStore` already implement `AddMemoryEdgeAsync` with dictionary-keyed-by-ID semantics (`this.edges[edge.Id] = edge`), making it an implicit upsert. When `PromoteAsync` or `DetectCoChangesAsync` uses deterministic edge IDs, calling `AddMemoryEdgeAsync` with an existing ID replaces the edge (updating `Weight`, `Metadata`, etc.). No new `UpsertMemoryEdgeAsync` method is needed — the existing interface semantics are sufficient. The `IAgentMemoryStore` XML doc for `AddMemoryEdgeAsync` should be updated to document this upsert behavior explicitly. |
| **ContextBuilder AgentId/Branch propagation** | All `EpisodeFilter` instances in `GenerateCandidatesAsync` must propagate `request.AgentId` and `request.Branch`. Add `Branch` field to `EpisodeFilter` and `ContextRequest`. |
| **Corrupted JSON quarantine, not silent recovery** | `FileSystemAgentMemoryStore.LoadAsync` renames corrupted files to `{path}.quarantine.{timestamp}`, logs a warning, returns empty store in degraded mode. `MemoryStatistics` gains `QuarantinedFileCount` and `IsDegradedStartup`. |
| **Graph context assembly fix** | `AssembleContextWindow` must populate `ContextWindow.StructuralNodes` and `RelationalEdges` from candidates, not silently drop them. |
| **`WarnOnTierCorrection`** | When `false` (default): `Normalize` corrects invalid tiers silently. When `true`: `Normalize` corrects AND logs `ILogger.LogWarning` with the original and corrected values. Both modes always enforce invariants — invalid records are **never** persisted. The flag controls **observability only** (whether corrections are logged), not enforcement behavior. Logging occurs exclusively in `TierPolicy.Normalize(record, logger, options)` — the `ExpertiseRecordJsonConverter` does NOT log and does NOT consume this flag (it is logger-free). |

### ExpertiseRecord Invariant Matrix

Every store `UpsertExpertiseAsync` enforces via `TierPolicy`:

| Input Condition | Behavior | Method |
|----------------|----------|--------|
| `Tier` not in `AllowedTiers(Category)` | Replace with `DefaultTierFor(Category)` | `Normalize` |
| `Confidence < 0` | Clamp to `0.0` | `Normalize` |
| `Confidence > 1` | Clamp to `1.0` | `Normalize` |
| `DerivedFromEpisodeIds.IsDefault` (struct default) | Replace with `ImmutableArray<string>.Empty` | `Normalize` |
| `PositiveEvidence.IsDefault` / `NegativeEvidence.IsDefault` | Replace with `ImmutableArray<EvidenceItem>.Empty` | `Normalize` |
| `Properties` is null (reference type) | Replace with `ImmutableDictionary<string,string>.Empty` | `Normalize` |
| Duplicate entries in `DerivedFromEpisodeIds` | Deduplicate via `Distinct()` | `Normalize` |
| `previous` exists and `record.Tier < previous.Tier` | Throw `InvalidTierException` (demotion) | `Validate` |
| `previous` exists and `record.Category != previous.Category` | Throw `InvalidCategoryChangeException` | `Validate` |

---

## Phase 1 — Domain Hardening & Context Correctness

**Goal**: Fix all twelve defects. Ordered: immutability first (prevents regressions), then policy, then store enforcement, then evolver mutation rewrite, then context correctness, then persistence resilience.

### Stage 1.1 — Immutable ExpertiseRecord & TierPolicy

- **Step 1.1.1**: Make `ExpertiseRecord` immutable-after-initialization with store-boundary validation, and introduce `TierPolicy`

  Fixes D1, D2, D3. **ExpertiseRecord.cs** changes (`init` properties allow construction and JSON deserialization but prevent post-construction mutation; `TierPolicy.Normalize` at the store boundary ensures all persisted records satisfy invariants):
  - `Tier { get; set; }` → `Tier { get; init; } = MemoryTier.Interaction;`. Keep the default (do **not** add `required` — see deserialization strategy below).
  - `Confidence { get; set; }` → `Confidence { get; init; }` (default 0.5 stays).
  - `EpisodeCount { get; set; }` → `EpisodeCount { get; init; }`.
  - `Summary { get; set; }` → `Summary { get; init; }`.
  - `LastValidated { get; set; }` → `LastValidated { get; init; }`.
  - `CommitSha { get; set; }` → `CommitSha { get; init; }`.
  - `Branch { get; set; }` → `Branch { get; init; }`.
  - `AccessCount { get; set; }` → `AccessCount { get; init; }`.
  - `List<string> DerivedFromEpisodeIds` → `ImmutableArray<string> DerivedFromEpisodeIds { get; init; } = ImmutableArray<string>.Empty`.
  - `List<EvidenceItem> PositiveEvidence` → `ImmutableArray<EvidenceItem> PositiveEvidence { get; init; } = ImmutableArray<EvidenceItem>.Empty`.
  - `List<EvidenceItem> NegativeEvidence` → `ImmutableArray<EvidenceItem> NegativeEvidence { get; init; } = ImmutableArray<EvidenceItem>.Empty`.
  - `Dictionary<string, string> Properties` → `ImmutableDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty`.
  - Add `public ExpertiseRecord With(...)` copy helper — accepts optional params, defaults to current values.

  **Why not `required Tier`**: Making `Tier` a `required init` property breaks legacy JSON deserialization — files missing a `tier` field would throw `JsonException` before `TierPolicy.Normalize` can repair them. Instead, we keep the `= MemoryTier.Interaction` default and enforce correctness at the **store boundary** via `TierPolicy.Normalize` on every load and upsert. New code paths that forget `Tier` will get the default, which `Normalize` corrects to `DefaultTierFor(category)`.

  **Deserialization strategy — `ExpertiseRecordJsonConverter`**: Add a custom `JsonConverter<ExpertiseRecord>` registered in `FileSystemAgentMemoryStore.JsonOptions` (which remains `static readonly`). The converter is **logger-free** — it performs silent defaulting only, with no dependency on `ILogger` or `AgentMemoryOptions`:
  - Missing `tier` field → silently defaults to `MemoryTier.Interaction` (corrected by `Normalize` on store load/upsert).
  - Unknown `tier` value → silently defaults to `MemoryTier.Interaction` (corrected by `Normalize` on store load/upsert).
  - Missing `ImmutableArray` fields (`DerivedFromEpisodeIds`, `PositiveEvidence`, `NegativeEvidence`) → deserializes as `ImmutableArray<T>.Empty` (not `default`, avoiding `IsDefault` at source).
  - Missing `ImmutableDictionary` field (`Properties`) → deserializes as `ImmutableDictionary<string, string>.Empty`.
  The converter is the **first line of defense** (safe deserialization); `TierPolicy.Normalize` at the **store boundary** is the **second** (invariant enforcement + optional logging). All warning/observability for tier corrections happens in `Normalize`, never in the converter. This cleanly separates deserialization (static, no DI) from policy enforcement (instance-scoped, DI-aware).

  **TierPolicy.cs** — new file:
  - `static class TierPolicy`
  - `AllowedTiers(ExpertiseCategory category)` → `IReadOnlySet<MemoryTier>`. Exhaustive switch, 11 cases, no wildcard.
  - `DefaultTierFor(ExpertiseCategory category)` → `MemoryTier`. Exhaustive switch, 11 cases, no wildcard.
  - `Normalize(ExpertiseRecord record)` → `ExpertiseRecord`. Logger-free overload for tests, converters, and non-DI call sites. Implementation: delegates to `Normalize(record, null, null)`.
  - `Normalize(ExpertiseRecord record, ILogger? logger, AgentMemoryOptions? options)` → `ExpertiseRecord`. Core implementation — applies invariant matrix identically regardless of arguments. When `options?.WarnOnTierCorrection == true && logger is not null`, logs `logger.LogWarning(...)` for each correction (original tier → corrected tier, clamped confidence, etc.). **This is the sole call site for `WarnOnTierCorrection` behavior.** Store call pattern: `TierPolicy.Normalize(record, this.logger, this.options)` where `this.logger` is `ILogger` and `this.options` is `AgentMemoryOptions` (unwrapped from `IOptions<AgentMemoryOptions>.Value` in the store constructor — see constructor migration in Step 1.2.1).
  - `Validate(ExpertiseRecord record, ExpertiseRecord? previous)` → throws `InvalidTierException` on demotion (e.g., Query→Interaction), throws `InvalidCategoryChangeException` on category mismatch.
  - `InvalidTierException : ArgumentException`.
  - `InvalidCategoryChangeException : ArgumentException`.

  **MemoryModels.cs** — delete `TierMapping` class (lines 201–231).

  **AgentMemory.csproj** — add two package references (versions managed by `Directory.Packages.props`, which already has central `PackageVersion` entries for both):
  - `Microsoft.Extensions.Logging.Abstractions` — required for `ILogger<T>` and `NullLogger<T>` in store constructors (Step 1.2.1) and `TierPolicy.Normalize` logging (Step 1.5.2). This package provides the `Microsoft.Extensions.Logging` namespace (interfaces) and `Microsoft.Extensions.Logging.Abstractions` namespace (`NullLogger<T>`). Central version: `Directory.Packages.props` line 64 (`10.0.6`).
  - `Microsoft.Extensions.Options` — required for `IOptions<AgentMemoryOptions>` in store DI constructors (Step 1.2.1) and `services.AddOptions<AgentMemoryOptions>()` in `ServiceCollectionExtensions` (Step 1.5.2). This package provides the `Microsoft.Extensions.Options` namespace. Central version: `Directory.Packages.props` line 59 (`10.0.6`).

  **`System.Collections.Immutable` does NOT need a package reference**: The project targets `net8.0` (set in `Directory.Build.props`). `System.Collections.Immutable` is part of the .NET 8 shared framework — `ImmutableArray<T>`, `ImmutableDictionary<TK,TV>`, and all related types are available via the implicit framework reference without any NuGet package. Adding a `PackageReference` would be unnecessary and would also fail `dotnet restore` because `Directory.Packages.props` has no central `PackageVersion` entry for `System.Collections.Immutable`. Only a `using System.Collections.Immutable;` directive is needed in source files.

  **Why both packages are added in Step 1.1.1** (not deferred): Step 1.2.1 immediately introduces `ILogger<T>`, `NullLogger<T>`, and `IOptions<AgentMemoryOptions>` in store constructors. Package references must exist before those `using` directives compile. Adding them in Step 1.1.1 ensures the csproj is ready.

  **Using directives** added to source files (listed here for completeness):
  - `ExpertiseRecord.cs`: add `using System.Collections.Immutable;` (framework namespace, no NuGet package).
  - `InMemoryAgentMemoryStore.cs`: add `using Microsoft.Extensions.Logging;`, `using Microsoft.Extensions.Logging.Abstractions;`, `using Microsoft.Extensions.Options;`.
  - `FileSystemAgentMemoryStore.cs`: add `using Microsoft.Extensions.Logging;`, `using Microsoft.Extensions.Logging.Abstractions;`, `using Microsoft.Extensions.Options;`.
  - `TierPolicy.cs` (new file): add `using Microsoft.Extensions.Logging;`.

  **ExpertiseRecordJsonConverter.cs** — new file (see deserialization strategy above).

  Tests: all 11 categories × `DefaultTierFor`; FailurePattern at Interaction → corrected to Query; Confidence clamping; `IsDefault` collections → empty (see below); demotion throws; promotion passes; category change throws; reflection test (no public `set`); JSON round-trip with immutable collections; legacy JSON missing `tier` field deserializes correctly; legacy JSON missing collection fields deserializes to empty (not `IsDefault`).

  `expectedFileChanges`: 9

### Stage 1.2 — Store Enforcement & Evolver Mutation Rewrite

- **Step 1.2.1**: TierPolicy enforcement in stores and MemoryEvolver copy-construction rewrite

  Fixes D4, D5, D6. Both stores add private fields `private readonly ILogger logger` and `private readonly AgentMemoryOptions options`, with **backward-compatible constructor overloads** to avoid breaking existing tests and callers.

  **AgentMemoryOptions.cs** — new file (minimal, created here because stores need it for default construction; DI registration deferred to Step 1.5.2):
  - `public class AgentMemoryOptions` with property: `bool WarnOnTierCorrection { get; set; }` (default: `false`). Additional properties (`StoragePath`, `FallbackToFileSystem`) added in Step 1.5.2.

  **InMemoryAgentMemoryStore.cs** — constructor migration:
  - Add explicit parameterless constructor (replaces implicit default): `public InMemoryAgentMemoryStore()` — sets `this.logger = NullLogger<InMemoryAgentMemoryStore>.Instance; this.options = new AgentMemoryOptions();`. Existing callers (`new InMemoryAgentMemoryStore()`) continue to compile and work identically.
  - Add DI constructor: `public InMemoryAgentMemoryStore(ILogger<InMemoryAgentMemoryStore> logger, IOptions<AgentMemoryOptions> options)` — sets `this.logger = logger; this.options = options.Value;`. Note: `options.Value` unwraps `IOptions<AgentMemoryOptions>` to plain `AgentMemoryOptions`, matching `TierPolicy.Normalize`'s `AgentMemoryOptions?` parameter type.

  **FileSystemAgentMemoryStore.cs** — constructor migration:
  - Existing constructor `public FileSystemAgentMemoryStore(string basePath)` — add to body: `this.logger = NullLogger<FileSystemAgentMemoryStore>.Instance; this.options = new AgentMemoryOptions();`. Existing callers (`new FileSystemAgentMemoryStore(path)`) continue to compile and work identically.
  - Add DI constructor: `public FileSystemAgentMemoryStore(string basePath, ILogger<FileSystemAgentMemoryStore> logger, IOptions<AgentMemoryOptions> options)` — sets `this.logger = logger; this.options = options.Value;`.

  Both stores' `UpsertExpertiseAsync`: `var normalized = TierPolicy.Normalize(record, this.logger, this.options); TierPolicy.Validate(normalized, existing); this.expertise[normalized.Id] = normalized;`. The `this.options` field is type `AgentMemoryOptions` (not `IOptions<AgentMemoryOptions>`), so the call `TierPolicy.Normalize(record, this.logger, this.options)` is type-consistent with the `Normalize(ExpertiseRecord, ILogger?, AgentMemoryOptions?)` overload.

  **ServiceCollectionExtensions** — **no changes in this step**. Factories continue to instantiate stores via backward-compatible constructors (NullLogger + default `AgentMemoryOptions`). DI constructor wiring is deferred to Step 1.5.2, which registers `AgentMemoryOptions` in DI and updates factories to use DI constructors. This avoids a forward dependency on `IOptions<AgentMemoryOptions>` DI registration.

  **MemoryEvolver.cs** — rewrite ALL 8 mutation sites:
  - `DecayAsync` (line 336): replace `expertise.Confidence = newConfidence` with `var decayed = expertise.With(confidence: newConfidence); await this.store.UpsertExpertiseAsync(decayed)`.
  - `ConsolidateFileExpertiseAsync` update (lines 357–383): build new `ExpertiseRecord` via copy-construction — `new ExpertiseRecord { Id = current.Id, Subject = current.Subject, Category = current.Category, Tier = current.Tier, EpisodeCount = episodes.Count, Confidence = ..., DerivedFromEpisodeIds = current.DerivedFromEpisodeIds.AddRange(newIds), ... }`.
  - `ConsolidateSymbolExpertiseAsync` update (lines 452–458): same copy-construction pattern.
  - `ConsolidateFailurePatternAsync` update (lines 513–534): same pattern.
  - `ConsolidateFailurePatternAsync` create (lines 543–561): add explicit `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FailurePattern)` (= `Query`).
  - `GetExpertiseByTierFallbackAsync` (line 726): replace `TierMapping.DefaultTierFor` → `TierPolicy.DefaultTierFor`.
  - All create paths: use `ImmutableArray.CreateRange()` instead of `List<T>`.

  Tests: store rejects demotion; store normalizes invalid tier on upsert; consolidation produces new records (original unchanged); decay produces new record; FailurePattern created at Query tier; backward-compatible `new InMemoryAgentMemoryStore()` calls `Normalize` with NullLogger/default options.

  `expectedFileChanges`: 8

### Stage 1.3 — Idempotent Evolution

- **Step 1.3.1**: Idempotent promotion with deterministic IDs

  Fixes D7. **MemoryEvolver.cs** `CreatePromotedExpertiseAsync`:
  - Derive `sourceTier` from `sources[0].Tier` (all sources share the same tier within a promotion group — `PromoteAsync` groups by tier before calling this method).
  - Replace `$"exp-promo-{Guid.NewGuid():N}"` with deterministic `$"exp-promo-{targetTier}-{ComputeStableHash(category + '|' + sourceTier + '|' + groupKey)}"`.
  - The hash input includes `category` and `sourceTier` alongside `groupKey` to prevent collisions between unrelated source groups that share a groupKey (e.g., two different categories promoting to the same tier with the same file path as groupKey).
  - Before creating: check if `store.GetExpertiseAsync(promotedId)` returns existing record.
  - If exists: **upsert** with updated confidence, episode count, and summary. Reconcile source membership: merge new `DerivedFromEpisodeIds` into existing. Reconcile `PromotedTo` edges (skip existing, add missing).
  - If not exists: create new record + edges as before.
  - `ComputeStableHash` uses SHA256 truncated to 16 hex chars for deterministic IDs.

  Tests: two consecutive `PromoteAsync` runs → same record IDs, updated fields; new source episodes → existing promoted record updated; no duplicate `PromotedTo` edges.

  `expectedFileChanges`: 4

- **Step 1.3.2**: Idempotent co-change detection with deterministic IDs

  Fixes D8. **MemoryEvolver.cs** `DetectCoChangesAsync`:
  - Edge IDs: `$"edge-cc-{ComputeStableHash(sortedPair)}"` instead of `Guid`.
  - Since `AddMemoryEdgeAsync` is upsert-by-ID (see design decisions), calling it with a deterministic ID automatically replaces any existing edge — update weight/metadata in the new `MemoryEdge` object. No separate "check then update" needed.
  - Group IDs: `$"exp-cc-{ComputeStableHash(sortedFileList)}"` instead of `Guid`.
  - Before creating group expertise: check `store.GetExpertiseAsync(groupId)` — if exists, upsert with updated confidence.

  Tests: two consecutive `ConsolidateAsync` runs → same edge/group IDs; weight updated on second run; no duplicate edges.

  `expectedFileChanges`: 3

### Stage 1.4 — Context Correctness

- **Step 1.4.1**: ContextBuilder AgentId/Branch filtering and graph assembly fix

  Fixes D9, D11, D12. **ContextBuilder.cs** `GenerateCandidatesAsync`:
  - Lines 90–98: add `AgentId = request.AgentId, Branch = request.Branch` to `EpisodeFilter`.
  - Lines 112–119: same.
  - Lines 147–152: same (neighbor episodes).
  - Lines 181–187: gate `PatternKnowledge` retrieval on subject relevance — only include if pattern subject overlaps with request files/symbols or if no task-specific candidates exist.

  **ContextBuilder.cs** `AssembleContextWindow` (lines 312–373):
  - Change method signature from `AssembleContextWindow(List<ScoredCandidate> rankedCandidates, ContextRequest request)` to `AssembleContextWindow(List<ScoredCandidate> rankedCandidates, ContextRequest request, RetrievalCandidates candidates)` — adding the third parameter to pass through graph data that is already collected in `GenerateCandidatesAsync` but unreachable from `ScoredCandidate`.
  - Update the caller in `BuildContextAsync` (line 81): `return this.AssembleContextWindow(rankedCandidates, request, candidates);`.
  - Add `StructuralNodes = candidates.StructuralNodes.Take(request.MaxGraphDepth * 10).ToList()` to the returned `ContextWindow`.
  - Add `RelationalEdges = candidates.RelationalEdges.Take(request.MaxGraphDepth * 10).ToList()` to the returned `ContextWindow`.
  - No new types needed — `RetrievalCandidates` (internal class, line 405) already has `StructuralNodes` and `RelationalEdges` lists populated by `GenerateCandidatesAsync` (lines 140–141).

  **ContextWindow.cs** `ContextRequest`: add `public string? Branch { get; init; }`.
  **ContextWindow.cs** `EpisodeFilter`: add `public string? Branch { get; set; }`.
  **InMemoryAgentMemoryStore.cs** `GetEpisodesAsync`: add branch filter clause — when `filter.Branch` is non-null, include only episodes matching that branch OR episodes with no branch tag (legacy/untagged data). This is **inclusive filtering**, not scoring/preference. Cross-branch episodes with an explicit different branch are excluded.
  **FileSystemAgentMemoryStore.cs** `GetEpisodesAsync`: same branch filter clause as above.

  Tests: AgentId filter applied — agent-scoped query excludes other agents' episodes; Branch filter applied — cross-branch episodes excluded when filter set, untagged episodes included; StructuralNodes/RelationalEdges present in output; PatternKnowledge gated by relevance.

  **Note**: Branch **scoring/preference** (same-branch 1.0×, default-branch 0.7×, cross-branch 0.5×) is introduced in Phase 2.3 (Step 2.3.1), which replaces the hard filter with weighted ranking across all branches. Phase 1 uses simple inclusive filtering only.

  `expectedFileChanges`: 7

### Stage 1.5 — Persistence Resilience & Config

- **Step 1.5.1**: Corrupted JSON quarantine and repair-on-load

  Fixes D10. **FileSystemAgentMemoryStore.cs** `LoadAsync`:

  **Static → instance method migration**: The current `LoadAsync<T>` is `private static async Task<ConcurrentDictionary<string, T>> LoadAsync<T>(string path, CancellationToken cancellationToken)` (line 474). It must be changed to a **private instance method** (remove `static` keyword) so it can access `this.logger` and `this.isDegradedStartup`. New signature: `private async Task<ConcurrentDictionary<string, T>> LoadAsync<T>(string path, CancellationToken cancellationToken)`. All existing call sites within `FileSystemAgentMemoryStore` already call `LoadAsync` via `this` (implicit or explicit), so removing `static` requires no call-site changes.

  - Replace `catch (JsonException) { return new ConcurrentDictionary<string, T>(); }` with:
    - Rename corrupted file to `{path}.quarantine.{DateTime.UtcNow:yyyyMMddHHmmss}`.
    - Log warning via `this.logger.LogWarning("Quarantined corrupted file {Path}", path)` (now accessible because method is instance, not static).
    - Set `this.isDegradedStartup = true` (new `private bool` field on the class).
    - Return empty `ConcurrentDictionary`.
    - **Do NOT recreate the file at the original path** — the renamed quarantine file preserves the corrupt data for manual inspection. The original path remains absent (`File.Exists(path)` returns `false`) until the next `SaveAsync` writes clean data.
  - `EnsureLoadedAsync`: after loading expertise, run `TierPolicy.Normalize(record, this.logger, this.options)` on each record; if any records were corrected, immediately `SaveAsync` the corrected collection. This is where `WarnOnTierCorrection` fires for legacy data repair.
  - **MemoryStatistics**: add `int QuarantinedFileCount`, `int AutoNormalizedCount`, `bool IsDegradedStartup`.

  **Quarantine lifecycle** (precise state machine):
  1. `LoadAsync` detects `JsonException` → renames `{path}` to `{path}.quarantine.{ts}` → returns empty dict → sets `isDegradedStartup = true`.
  2. Original path does **not** exist until the next write. `File.Exists(path)` returns `false`.
  3. Next `SaveAsync(path, data)` creates a new file at original path with clean serialized data.
  4. Quarantined file is preserved indefinitely for manual recovery.

  Tests: (a) corrupted JSON → quarantine file exists with `.quarantine.` suffix; (b) original path does NOT exist immediately after `LoadAsync`; (c) after a subsequent `SaveAsync`, original path exists with valid JSON; (d) `IsDegradedStartup` is `true`; (e) bad-tier records normalized on `EnsureLoadedAsync` via `TierPolicy.Normalize`.

  `expectedFileChanges`: 5

- **Step 1.5.2**: Minimal memory configuration

  **AgentMemoryOptions.cs** — update (created in Step 1.2.1, expanded here):
  - Add properties: `string? StoragePath { get; set; }`, `bool FallbackToFileSystem { get; set; }` (default: `true`).

  **Type flow** (closes the `IOptions<T>` vs `T` contract):
  - DI container provides `IOptions<AgentMemoryOptions>` to store constructors (via `services.AddOptions<AgentMemoryOptions>()`).
  - Store DI constructors unwrap: `this.options = options.Value` → field type is `AgentMemoryOptions`.
  - Store methods call: `TierPolicy.Normalize(record, this.logger, this.options)` → `this.options` is `AgentMemoryOptions`, matching the `AgentMemoryOptions?` parameter.
  - Backward-compatible constructors (parameterless/path-only) use `this.options = new AgentMemoryOptions()` → `WarnOnTierCorrection` defaults to `false` → `Normalize` corrects silently.
  - The `ExpertiseRecordJsonConverter` is NOT affected — stays in static `JsonOptions`, no DI dependencies.

  **ServiceCollectionExtensions** changes (DI constructor wiring, deferred from Step 1.2.1):
  - All three `AddAgentMemory` overloads add `services.AddOptions<AgentMemoryOptions>()` before store registration. This ensures `IOptions<AgentMemoryOptions>` is always resolvable via DI.
  - **Logger resolution uses `GetService` + fallback, NOT `GetRequiredService`**: Because `AddAgentMemory` must work standalone (`new ServiceCollection().AddAgentMemory().BuildServiceProvider()`) without requiring callers to register logging first, all factory lambdas resolve loggers via `sp.GetService<ILogger<T>>() ?? NullLogger<T>.Instance`. This matches the backward-compatible constructor pattern (Step 1.2.1) where parameterless constructors use `NullLogger<T>.Instance`. Callers who have registered `services.AddLogging()` get proper logging; callers who haven't get silent NullLogger. **Do NOT use `GetRequiredService<ILogger<T>>()`** — that would throw if logging is not registered.
  - `AddAgentMemory()`: register `InMemoryAgentMemoryStore` via factory `sp => new InMemoryAgentMemoryStore(sp.GetService<ILogger<InMemoryAgentMemoryStore>>() ?? NullLogger<InMemoryAgentMemoryStore>.Instance, sp.GetRequiredService<IOptions<AgentMemoryOptions>>())`. Note: `IOptions` uses `GetRequiredService` because `AddOptions` is called above; `ILogger` uses `GetService` + fallback.
  - `AddAgentMemoryWithFileStorage(storagePath)`: register via factory `sp => new FileSystemAgentMemoryStore(storagePath, sp.GetService<ILogger<FileSystemAgentMemoryStore>>() ?? NullLogger<FileSystemAgentMemoryStore>.Instance, sp.GetRequiredService<IOptions<AgentMemoryOptions>>())`.
  - `AddAgentMemory<TStore>()`: unchanged (generic TStore resolved by DI; must have compatible constructor).
  - Add `AddAgentMemory(Action<AgentMemoryOptions> configure)` overload: calls `services.Configure(configure)` then registers in-memory store with DI constructor (using `GetService<ILogger>() ?? NullLogger.Instance` pattern). This avoids an `IConfiguration` dependency — callers bind options via a lambda: `services.AddAgentMemory(o => o.WarnOnTierCorrection = true)`. `IConfiguration`-based binding (e.g., `services.Configure<AgentMemoryOptions>(configuration.GetSection("AgentMemory"))`) is **deferred** — it requires `Microsoft.Extensions.Options.ConfigurationExtensions` (which provides the `Configure<TOptions>(IConfiguration)` extension method), not `Microsoft.Extensions.Configuration.Binder`. `Directory.Packages.props` already has a central version for `Microsoft.Extensions.Options.ConfigurationExtensions` (line 60, version `9.0.0`), so a future step only needs to add the `PackageReference` to `AgentMemory.csproj`. Callers needing appsettings binding today can manually call `services.Configure<AgentMemoryOptions>(section)` before `AddAgentMemory()` if they have the package.

  Tests: (a) `new AgentMemoryOptions().WarnOnTierCorrection` is `false`; (b) DI-resolved store has logger and options injected (with and without `services.AddLogging()`); (c) warning logged when `WarnOnTierCorrection=true` and tier corrected; (d) no warning when `WarnOnTierCorrection=false`; (e) backward-compatible `new InMemoryAgentMemoryStore()` works with NullLogger and default options; (f) `new ServiceCollection().AddAgentMemory().BuildServiceProvider().GetRequiredService<IAgentMemoryStore>()` succeeds without `AddLogging()` (logger resolves to NullLogger via fallback).

  `expectedFileChanges`: 5

---

## Phase 2 — Identity, Integration & Context Injection

Wire agent memory into the agent lifecycle with correct symbol identity.

### Stage 2.1 — Symbol Identity & Graph Interop

- **Step 2.1.1**: Language-aware symbol identity resolver

  `SymbolIdentity` static class: `NormalizePath(repoRoot, absolutePath)` → repo-relative forward-slash path; `CanonicalSymbolId(...)` → Roslyn doc-comment ID format for C#; fallback `path:line`. Add `FirstSeenCommit`/`LastSeenCommit` validity window fields on ExpertiseRecord. Update EpisodeRecorder path normalization. Tests: overloads, generics, nested types, drive letters, worktrees.

  `expectedFileChanges`: 8

- **Step 2.1.2**: Graph coexistence contract

  Memory uses `:MemoryEpisode`/`:MemoryExpertise` labels; cross-graph references via foreign-key IDs; code graph clear/reseed does not delete memory nodes. Add `IGraphStoreAdapter.IsCodeNodeAlive(symbolId)` for orphan detection. Tests: clear code graph → memory intact.

  `expectedFileChanges`: 6

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware with write gating

  Hook `IEpisodeRecorder` into orchestrator lifecycle. Write gating: dedup near-identical episodes (same agent + files within 5 min), require ≥1 observation, suppress trivial read-only, require non-InProgress outcome. Tests: lifecycle, dedup, suppression.

  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files

  Resolve file paths → `HypergraphNode` IDs via `IGraphStoreAdapter`. Graceful degraded mode. Tests: with/without graph store.

  `expectedFileChanges`: 5

### Stage 2.3 — Context Enhancement

- **Step 2.3.1**: Branch-aware tier-ordered retrieval with provenance

  Query `ITieredExpertiseStore` by tier: Insight → Query → Interaction. Token budget per tier. Branch scoring: same-branch 1.0×, default-branch 0.7×, cross-branch 0.5×. `Provenance` enum (`AgentObserved`, `StaticAnalysis`, `GitSeeded`, `Consolidated`, `Promoted`) on `MemoryEdge`, `CallChain`, `ExpertiseRecord`. Retrieval weights by provenance. Tests: branch preference, provenance scoring.

  `expectedFileChanges`: 10

- **Step 2.3.2**: Minimal retrieval evaluation harness

  Golden-replay test suite: 5–10 curated queries with expected results. Measures precision@k, recall, tier distribution. Integration test baseline. Tests: golden queries pass at threshold.

  `expectedFileChanges`: 5

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL, migration, and DI

  Cypher DDL for `:MemoryEpisode`, `:MemoryExpertise` with indexes. Label-scoped constraints. DI with `AgentMemoryOptions.EnableNeo4j`. Docker-compose update.

  `expectedFileChanges`: 10

- **Step 3.1.2**: Episode and expertise CRUD in Neo4j

  `Neo4jMemoryStore` methods with `TierPolicy` enforcement on upsert. Deserialized copies. Testcontainers integration tests.

  `expectedFileChanges`: 12

- **Step 3.1.3**: Edges, call chains, and statistics

  Memory edge relationships, Cypher traversals, `ICallChainStore`, tier distribution queries. Integration tests.

  `expectedFileChanges`: 10

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution/project/namespace hierarchy seeding

  Walk `HypergraphData` to create `ProjectExpertise` (Query), `NamespaceExpertise` (Query), `TypeExpertise` (Interaction) records. `ContainedIn` edges. Idempotent with deterministic IDs. Tests: idempotent reseeding.

  `expectedFileChanges`: 10

- **Step 3.2.2**: Rename detection and rebinding

  Compare old symbol IDs → new extraction. Repoint memory edges/expertise. Mark old IDs as aliases. Tests: rename → edges rebound.

  `expectedFileChanges`: 7

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor

  DFS through `Hyperedge` MethodInvocation. `CallChain` with `Provenance.StaticAnalysis`. Max depth 8, cycle detection. Tests: cycles, depth limit.

  `expectedFileChanges`: 8

- **Step 3.3.2**: Git co-change seeder

  Parse `git log --name-only`. Jaccard with min count/threshold gates. `CoChangedWith` edges with `Provenance.GitSeeded`. SHA watermark. Tests: Jaccard calculation, watermark.

  `expectedFileChanges`: 7

---

## Phase 4 — Feedback, Evolution & Observability

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement and staleness detection

  Reinforce expertise confidence on episode completion. Compare `CommitSha` against HEAD for staleness. Freshness warnings in `ContextWindow`. Tests: boost/penalty/cap, stale detection.

  `expectedFileChanges`: 8

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver background service

  `MemoryEvolverService : IHostedService`. `SemaphoreSlim` serialization. High-water marks. Checkpoint/resume. Tests: serialization, incremental run.

  `expectedFileChanges`: 7

### Stage 4.3 — Vector Integration & Observability

- **Step 4.3.1**: Vector memory adapter

  `IVectorMemoryAdapter` bridging to `Ast.VectorDb.IVectorStore`. Qdrant collections. Semantic search in `ContextBuilder`. Tests: semantic retrieval.

  `expectedFileChanges`: 9

- **Step 4.3.2**: Metrics and health dashboard

  `IMemoryMetrics`: episode rate, consolidation rate, stale %, retrieval latency, tier distribution. Golden-replay regression suite. Tests: metric collection.

  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query and record tools

  Six tools: `memory_search`, `memory_context`, `memory_stats`, `memory_record_observation`, `memory_consolidate`, `memory_decay`. JSON schema + handlers. Tests: round-trips.

  `expectedFileChanges`: 10

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect and management commands

  Subcommands: `memory stats/list-episodes/list-expertise/show/consolidate/decay/seed/reindex/clear`. Spectre.Console output. Tests: command parsing.

  `expectedFileChanges`: 10

---

## Verification Matrix

| Test Case | Defect | Assertion |
|-----------|--------|-----------|
| No public `set` on ExpertiseRecord (reflection) | D1 | All properties are `init`-only |
| `ImmutableArray.Add` returns new array | D2 | Original unchanged |
| JSON round-trip preserves immutable types | D2 | Deserialized collections are `ImmutableArray`/`ImmutableDictionary` |
| Legacy JSON missing `tier` field deserializes | D1, D2 | `ExpertiseRecordJsonConverter` defaults to `Interaction`; `Normalize` corrects |
| Legacy JSON missing collection fields | D2 | Deserializes to `.Empty`, not `.IsDefault` |
| `ImmutableArray.IsDefault` normalization | D2 | `Normalize` replaces `IsDefault` arrays with `.Empty` |
| `TierPolicy.Normalize`: FailurePattern at Interaction | D3, D6 | Corrected to Query |
| `TierPolicy.Normalize`: Confidence clamping | D3 | -0.5 → 0.0; 1.5 → 1.0 |
| `TierPolicy.Validate`: demotion throws | D3 | Query→Interaction throws `InvalidTierException` |
| Store upsert normalizes invalid tier | D4 | Record persisted with corrected tier |
| Store read returns immutable instance | D5 | Attempt to mutate → compile error |
| FailurePattern creation at Query tier | D6 | Consolidation creates with correct tier |
| Repeated `PromoteAsync` — same IDs | D7 | Second run returns same expertise ID (category+sourceTier+groupKey stable); fields updated |
| Promotion ID includes category+sourceTier | D7 | Different categories with same groupKey produce distinct IDs |
| Repeated `DetectCoChangesAsync` — same edge IDs | D8 | Second run returns same edge ID; weight updated |
| Agent-scoped context excludes other agents | D9 | Query with AgentId="A" → no episodes from AgentId="B" |
| Branch filter in context | D9 | Cross-branch episodes excluded when Branch filter set; untagged episodes included |
| Graph nodes/edges in ContextWindow | D11 | `StructuralNodes.Count > 0` when graph available |
| PatternKnowledge gated by relevance | D12 | Unrelated patterns excluded |
| Corrupted JSON quarantined | D10 | File renamed to `.quarantine.*`; `IsDegradedStartup=true` |
| `LoadAsync` is instance method (not static) | D10 | `this.logger` and `this.isDegradedStartup` accessible; no call-site changes needed |
| Bad-tier records normalized on load | D10 | Deserialized records corrected |
| `AddMemoryEdgeAsync` upsert-by-ID | D7, D8 | Calling with same ID replaces edge; weight updated |
| `WarnOnTierCorrection=true` logs correction | D3 | `ILogger.LogWarning` emitted when tier corrected |
| `WarnOnTierCorrection=false` silent correction | D3 | No log emitted; record still corrected |
| Standalone DI without `AddLogging()` | All | `new ServiceCollection().AddAgentMemory().BuildServiceProvider()` resolves store with NullLogger fallback; no throw |
| DI with `AddLogging()` uses real logger | All | `services.AddLogging().AddAgentMemory()` → store receives real `ILogger<T>`, not NullLogger |
| Branch scoring in Phase 2.3 | D9 | Same-branch 1.0×, default-branch 0.7×, cross-branch 0.5× |

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until Phase 4 metrics show insufficient precision.
- **Full temporal KG** — `ValidFrom`/`ValidTo` on edges deferred. `CommitSha` + validity windows suffice.
- **LLM-generated summaries** — Requires LLM API budget.
- **RL-based memory optimization** (arXiv:2511.07800) — Needs reward signal infrastructure.
- **Multi-agent collaborative memory** — Beyond current single-agent architecture.
- **MemGPT-style paging** — Context windows fit within current limits.
- **Fine-tuning / model adaptation** — Memory is retrieval-time only.
- **`IConfiguration`-based `AgentMemoryOptions` binding** — Requires `Microsoft.Extensions.Options.ConfigurationExtensions` package (which provides `services.Configure<TOptions>(IConfiguration)`). `Directory.Packages.props` already has a central version (line 60, `9.0.0`), so a future step only needs to add the `PackageReference` to `AgentMemory.csproj`. Callers can manually `services.Configure<AgentMemoryOptions>(section)` before `AddAgentMemory()` if they have the package. Deferred to Phase 4.
- **`record class` for ExpertiseRecord** — Equality semantic change too risky.
- **`required` keyword on `Tier`** — Breaks legacy JSON deserialization; store-boundary enforcement via `TierPolicy.Normalize` is safer and sufficient.
- **Optimistic concurrency** — Deferred to Phase 4 background service.
- **Full immutability for Episode/MemoryEdge/CallChain** — Lower risk; deferred (noted as future work).
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, hardcodes `:CodeNode` label.