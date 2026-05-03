# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 8)
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
| **ContextBuilder AgentId/Branch propagation** | All `EpisodeFilter` instances in `GenerateCandidatesAsync` must propagate `request.AgentId` and `request.Branch`. Add `Branch` field to `EpisodeFilter` and `ContextRequest`. |
| **Corrupted JSON quarantine, not silent recovery** | `FileSystemAgentMemoryStore.LoadAsync` renames corrupted files to `{path}.quarantine.{timestamp}`, logs a warning, returns empty store in degraded mode. `MemoryStatistics` gains `QuarantinedFileCount` and `IsDegradedStartup`. |
| **Graph context assembly fix** | `AssembleContextWindow` must populate `ContextWindow.StructuralNodes` and `RelationalEdges` from candidates, not silently drop them. |
| **`WarnOnTierCorrection`** | When `false` (default): `Normalize` corrects invalid tiers silently. When `true`: `Normalize` corrects AND logs `ILogger.LogWarning` with the original and corrected values. Both modes always enforce invariants — invalid records are **never** persisted. The flag controls **observability only** (whether corrections are logged), not enforcement behavior. |

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

- **Step 1.1.1**: Make `ExpertiseRecord` fully immutable and introduce `TierPolicy`

  Fixes D1, D2, D3. **ExpertiseRecord.cs** changes:
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

  **Deserialization strategy — `ExpertiseRecordJsonConverter`**: Add a custom `JsonConverter<ExpertiseRecord>` registered in `FileSystemAgentMemoryStore.JsonOptions`. The converter handles:
  - Missing `tier` field → defaults to `MemoryTier.Interaction` (corrected by `Normalize` on load).
  - Unknown `tier` value → defaults to `MemoryTier.Interaction` with warning log.
  - Missing `ImmutableArray` fields (`DerivedFromEpisodeIds`, `PositiveEvidence`, `NegativeEvidence`) → deserializes as `ImmutableArray<T>.Empty` (not `default`, avoiding `IsDefault` at source).
  - Missing `ImmutableDictionary` field (`Properties`) → deserializes as `ImmutableDictionary<string, string>.Empty`.
  This converter is the **first line of defense**; `TierPolicy.Normalize` is the **second** on every store load.

  **TierPolicy.cs** — new file:
  - `static class TierPolicy`
  - `AllowedTiers(ExpertiseCategory)` → `IReadOnlySet<MemoryTier>`. Exhaustive switch, 11 cases, no wildcard.
  - `DefaultTierFor(ExpertiseCategory)` → `MemoryTier`. Exhaustive switch, 11 cases, no wildcard.
  - `Normalize(ExpertiseRecord)` → `ExpertiseRecord`. Applies invariant matrix.
  - `Validate(ExpertiseRecord, ExpertiseRecord?)` → throws on demotion or category change.
  - `InvalidTierException : ArgumentException`.

  **MemoryModels.cs** — delete `TierMapping` class (lines 201–231).
  **AgentMemory.csproj** — add `System.Collections.Immutable` package reference.
  **ExpertiseRecordJsonConverter.cs** — new file (see deserialization strategy above).

  Tests: all 11 categories × `DefaultTierFor`; FailurePattern at Interaction → corrected to Query; Confidence clamping; `IsDefault` collections → empty (see below); demotion throws; promotion passes; category change throws; reflection test (no public `set`); JSON round-trip with immutable collections; legacy JSON missing `tier` field deserializes correctly; legacy JSON missing collection fields deserializes to empty (not `IsDefault`).

  `expectedFileChanges`: 9

### Stage 1.2 — Store Enforcement & Evolver Mutation Rewrite

- **Step 1.2.1**: TierPolicy enforcement in stores and MemoryEvolver copy-construction rewrite

  Fixes D4, D5, D6. **InMemoryAgentMemoryStore.cs** `UpsertExpertiseAsync`: `var normalized = TierPolicy.Normalize(record); TierPolicy.Validate(normalized, existing); this.expertise[normalized.Id] = normalized;`. Same for **FileSystemAgentMemoryStore.cs**.

  **MemoryEvolver.cs** — rewrite ALL 8 mutation sites:
  - `DecayAsync` (line 336): replace `expertise.Confidence = newConfidence` with `var decayed = expertise.With(confidence: newConfidence); await this.store.UpsertExpertiseAsync(decayed)`.
  - `ConsolidateFileExpertiseAsync` update (lines 357–383): build new `ExpertiseRecord` via copy-construction — `new ExpertiseRecord { Id = current.Id, Subject = current.Subject, Category = current.Category, Tier = current.Tier, EpisodeCount = episodes.Count, Confidence = ..., DerivedFromEpisodeIds = current.DerivedFromEpisodeIds.AddRange(newIds), ... }`.
  - `ConsolidateSymbolExpertiseAsync` update (lines 452–458): same copy-construction pattern.
  - `ConsolidateFailurePatternAsync` update (lines 513–534): same pattern.
  - `ConsolidateFailurePatternAsync` create (lines 543–561): add explicit `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FailurePattern)` (= `Query`).
  - `GetExpertiseByTierFallbackAsync` (line 726): replace `TierMapping.DefaultTierFor` → `TierPolicy.DefaultTierFor`.
  - All create paths: use `ImmutableArray.CreateRange()` instead of `List<T>`.

  Tests: store rejects demotion; store normalizes invalid tier on upsert; consolidation produces new records (original unchanged); decay produces new record; FailurePattern created at Query tier.

  `expectedFileChanges`: 6

### Stage 1.3 — Idempotent Evolution

- **Step 1.3.1**: Idempotent promotion with deterministic IDs

  Fixes D7. **MemoryEvolver.cs** `CreatePromotedExpertiseAsync`:
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
  - Before adding edge: check `store.GetOutgoingEdgesAsync(sourceFile, CoChangedWith)` — if edge to target exists, update weight/metadata via upsert.
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
  - Add `StructuralNodes = candidates.StructuralNodes.Take(request.MaxGraphDepth * 10).ToList()`.
  - Add `RelationalEdges = candidates.RelationalEdges.Take(request.MaxGraphDepth * 10).ToList()`.

  **ContextWindow.cs** `ContextRequest`: add `public string? Branch { get; init; }`.
  **ContextWindow.cs** `EpisodeFilter`: add `public string? Branch { get; set; }`.
  **InMemoryAgentMemoryStore.cs** `GetEpisodesAsync`: add branch filter clause.
  **FileSystemAgentMemoryStore.cs** `GetEpisodesAsync`: add branch filter clause.

  Tests: AgentId filter applied — agent-scoped query excludes other agents' episodes; Branch filter applied — same-branch preferred; StructuralNodes/RelationalEdges present in output; PatternKnowledge gated by relevance.

  `expectedFileChanges`: 7

### Stage 1.5 — Persistence Resilience & Config

- **Step 1.5.1**: Corrupted JSON quarantine and repair-on-load

  Fixes D10. **FileSystemAgentMemoryStore.cs** `LoadAsync`:
  - Replace `catch (JsonException) { return new ConcurrentDictionary<string, T>(); }` with:
    - Rename corrupted file to `{path}.quarantine.{DateTime.UtcNow:yyyyMMddHHmmss}`.
    - Log warning via `ILogger` (add to constructor).
    - Set `this.isDegradedStartup = true`.
    - Return empty dictionary.
  - `EnsureLoadedAsync`: after loading expertise, run `TierPolicy.Normalize` on each record; persist corrected file.
  - **MemoryStatistics**: add `int QuarantinedFileCount`, `int AutoNormalizedCount`, `bool IsDegradedStartup`.

  Tests: corrupted JSON → file quarantined with timestamp; original path has clean data; `IsDegradedStartup` is true; bad-tier records normalized on load.

  `expectedFileChanges`: 5

- **Step 1.5.2**: Minimal memory configuration

  `AgentMemoryOptions` with `WarnOnTierCorrection` (default: false), `StoragePath`, `FallbackToFileSystem` (default: true). When `WarnOnTierCorrection` is false (default), `Normalize` corrects silently. When true, corrections are logged via `ILogger.LogWarning`. Both modes always enforce invariants. Wire into `ServiceCollectionExtensions.AddAgentMemory()`. Tests: default `WarnOnTierCorrection` is false, store selection, warning logged when enabled.

  `expectedFileChanges`: 4

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
| Branch filter in context | D9 | Same-branch episodes ranked higher |
| Graph nodes/edges in ContextWindow | D11 | `StructuralNodes.Count > 0` when graph available |
| PatternKnowledge gated by relevance | D12 | Unrelated patterns excluded |
| Corrupted JSON quarantined | D10 | File renamed to `.quarantine.*`; `IsDegradedStartup=true` |
| Bad-tier records normalized on load | D10 | Deserialized records corrected |

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until Phase 4 metrics show insufficient precision.
- **Full temporal KG** — `ValidFrom`/`ValidTo` on edges deferred. `CommitSha` + validity windows suffice.
- **LLM-generated summaries** — Requires LLM API budget.
- **RL-based memory optimization** (arXiv:2511.07800) — Needs reward signal infrastructure.
- **Multi-agent collaborative memory** — Beyond current single-agent architecture.
- **MemGPT-style paging** — Context windows fit within current limits.
- **Fine-tuning / model adaptation** — Memory is retrieval-time only.
- **`record class` for ExpertiseRecord** — Equality semantic change too risky.
- **`required` keyword on `Tier`** — Breaks legacy JSON deserialization; store-boundary enforcement via `TierPolicy.Normalize` is safer and sufficient.
- **Optimistic concurrency** — Deferred to Phase 4 background service.
- **Full immutability for Episode/MemoryEdge/CallChain** — Lower risk; deferred (noted as future work).
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, hardcodes `:CodeNode` label.