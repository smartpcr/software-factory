# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 4)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library provides a domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver with consolidation, decay, promotion, and co-change detection.

**Iteration 3 root issues identified by evaluator:**

1. **`ExpertiseRecord.Tier` is mutable** — declared `get; set;` (line 36 of ExpertiseRecord.cs), defaults to `MemoryTier.Interaction`. Callers fetch a record from the in-memory store's `ConcurrentDictionary` and mutate `.Tier` directly, corrupting shared state. Similarly, `Confidence`, `EpisodeCount`, `Summary`, `LastValidated`, `CommitSha`, `Branch`, `AccessCount` are all `get; set;`.
2. **`TierMapping` is passive** — `DefaultTierFor()` and `IsValidCombination()` exist but are never called on any creation or upsert path. No code enforces the mapping.
3. **Stores persist invalid tier/category combinations** — `InMemoryAgentMemoryStore.UpsertExpertiseAsync()` (line 123) and `FileSystemAgentMemoryStore.UpsertExpertiseAsync()` (line 156) both do `this.expertise[record.Id] = record` with no normalization or validation.
4. **Reads return live mutable references** — `GetExpertiseAsync`, `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync` in both stores return direct references to stored objects. Callers can mutate the returned object and corrupt the store.
5. **Mutable collections compound the reference bypass** — `DerivedFromEpisodeIds`, `PositiveEvidence`, `NegativeEvidence`, `Properties` are `List<>` and `Dictionary<>`. Even with `init`-only on the property, the collection itself is mutable. `MemoryEvolver.ConsolidateFileExpertiseAsync` calls `current.DerivedFromEpisodeIds.AddRange(newEpisodeIds)` and `current.PositiveEvidence.Add(...)` (lines 364, 373) — these mutate the stored object in-place.
6. **`ConsolidateFailurePatternAsync` creates records at wrong tier** — Lines 543–561 create `new ExpertiseRecord { Category = ExpertiseCategory.FailurePattern }` without setting `Tier`. Default is `MemoryTier.Interaction`, but `TierMapping.DefaultTierFor(FailurePattern)` returns `MemoryTier.Query`.
7. **Persisted records not migrated** — `FileSystemAgentMemoryStore.EnsureLoadedAsync()` deserializes JSON records as-is. Records persisted with invalid tiers remain incorrect forever.
8. **`DecayAsync` mutates in-place** — Line 336: `expertise.Confidence = newConfidence` directly mutates the stored record, bypassing any future validation.

### Key Constraints

- **Persistence boundary**: `Ast.GraphDb.IGraphStore` stores nodes as `:CodeNode` — memory needs separate labels and domain-specific queries.
- **Symbol identity**: Canonical, stable across sessions; handles renames, worktrees, branch drift. Each symbol carries a validity window (`FirstSeenCommit`, `LastSeenCommit`).
- **Provenance**: Records from git history (ground truth), static analysis (inferred), and episodes (observed) — each weighted differently.
- **Cross-backend invariant**: Tier enforcement is a cross-cutting concern applied uniformly by every store implementation — InMemory, FileSystem, and future Neo4j.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural views; multi-signal reranking in ContextBuilder.
2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight with centralized `TierPolicy` enforcement and explicit allowed-tier sets per category.
3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via `CommitSha`; git-seeded co-change.
4. **Mem0 consolidation** (arXiv:2504.19413) — diversity-gated episode aggregation preventing false expertise.
5. **ExpeL distillation** (AAAI'24) — failure patterns from repeated failures; verbal reflection without fine-tuning.

| Decision | Rationale |
|----------|-----------|
| **`ExpertiseRecord` becomes a `record class`** | C# `record class` provides `with` expression support for immutable updates. ALL properties become `init`-only. Collections become `IReadOnlyList<>` and `IReadOnlyDictionary<>` backed by frozen/immutable instances. Construction uses required `init` properties and factory methods. This eliminates mutable-reference bypass, mutable-collection bypass, and in-place mutation in MemoryEvolver. |
| **`TierPolicy` replaces `TierMapping` as sole authority** | `TierPolicy` is a new static class that subsumes and replaces `TierMapping`. It provides: `AllowedTiers(category)` → `IReadOnlySet<MemoryTier>` (per-category allowed tier sets); `DefaultTierFor(category)` → `MemoryTier` (the canonical default); `Normalize(record)` → `ExpertiseRecord` (returns corrected copy with valid tier); `Validate(record, previous?)` → throws `InvalidTierException` on invalid combo or demotion. Exhaustive `switch` on all 11 `ExpertiseCategory` values with no wildcard fallback. `TierMapping` is deleted. |
| **Stores call `TierPolicy.Normalize` + `Validate` on every upsert** | Both `InMemoryAgentMemoryStore` and `FileSystemAgentMemoryStore` normalize then validate BEFORE persisting. This is the single enforcement point — producers need not be trusted. |
| **Deep copies on all read paths** | Every read method (`GetExpertiseAsync`, `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync`) returns a defensive copy via `record with { }` plus collection cloning. Mutations to returned objects cannot affect stored state. |
| **All MemoryEvolver updates use `with` expressions** | `ConsolidateFileExpertiseAsync`, `ConsolidateSymbolExpertiseAsync`, `ConsolidateFailurePatternAsync`, `DecayAsync` — every update produces a NEW record via `with { Confidence = ..., EpisodeCount = ... }` and upserts it. No field mutation anywhere. |
| **Migration repair-on-load** | `FileSystemAgentMemoryStore.EnsureLoadedAsync` iterates all deserialized expertise records, applies `TierPolicy.Normalize`, persists corrected records, increments `AutoNormalizedCount`. Unrecognizable records are quarantined to a separate file. |
| **Rollout sequence: immutability → policy → store enforcement → creation-site audit → migration** | Each step builds on the previous. Immutability prevents regression. Policy defines correctness. Store enforcement guarantees invariants. Creation-site audit ensures producers comply. Migration repairs legacy data. |

---

## Phase 1 — Tier Enforcement & Domain Hardening

**Goal**: Close every evaluator finding from iteration 3. The phase is ordered so each step can be independently validated: immutability first (prevents new regressions), then policy (defines correctness), then enforcement (guarantees invariants), then creation-site fixes (producers comply), then migration (legacy data repaired).

### Stage 1.1 — Immutable Domain Model & TierPolicy

- **Step 1.1.1**: Make `ExpertiseRecord` a fully immutable record class and create `TierPolicy`

  **ExpertiseRecord changes** (ExpertiseRecord.cs):
  - Change `public class ExpertiseRecord` → `public record class ExpertiseRecord`.
  - Convert ALL `get; set;` properties to `get; init;`: `Tier`, `Confidence`, `EpisodeCount`, `Summary`, `LastValidated`, `CommitSha`, `Branch`, `AccessCount`.
  - Convert `List<string> DerivedFromEpisodeIds` → `IReadOnlyList<string>` backed by `ImmutableArray<string>` or frozen list. Same for `List<EvidenceItem> PositiveEvidence`, `List<EvidenceItem> NegativeEvidence`.
  - Convert `Dictionary<string, string> Properties` → `IReadOnlyDictionary<string, string>`.
  - Remove default `= MemoryTier.Interaction` from `Tier`. Make `Tier` a required `init` property so callers cannot omit it. Alternatively: remove default and let `TierPolicy.Normalize` supply it at store boundary.
  - Make `EvidenceItem` a `record class` with all `init` properties.

  **TierPolicy** (new file TierPolicy.cs):
  - `static class TierPolicy` with:
    - `AllowedTiers(ExpertiseCategory)` → `IReadOnlySet<MemoryTier>`. For example: `FileExpertise → {Interaction, Query}`, `FailurePattern → {Query, Insight}`, `ArchitecturalKnowledge → {Insight}`.
    - `DefaultTierFor(ExpertiseCategory)` → `MemoryTier`. Exhaustive switch, no wildcard.
    - `Normalize(ExpertiseRecord)` → `ExpertiseRecord`. If `record.Tier` is not in `AllowedTiers(record.Category)`, returns `record with { Tier = DefaultTierFor(record.Category) }`.
    - `Validate(ExpertiseRecord record, ExpertiseRecord? previous = null)` — throws `InvalidTierException` if tier not in allowed set, or if `previous` is non-null and `record.Tier < previous.Tier` (demotion).
  - Delete `TierMapping` class from MemoryModels.cs. Update all references.

  **Tests** (new TierPolicyTests.cs + updated ExpertiseRecordTests.cs):
  - Each of 11 categories → `DefaultTierFor` returns expected tier.
  - Each category → `AllowedTiers` returns expected set.
  - `Normalize` corrects FailurePattern at Interaction → Query.
  - `Validate` throws on demotion (Query→Interaction).
  - `Validate` accepts promotion (Interaction→Query for FileExpertise).
  - `Validate` throws on ArchitecturalKnowledge at Interaction.
  - `ExpertiseRecord` has no public `set` accessors (reflection test).
  - `ExpertiseRecord` collections are read-only (Add/AddRange throws).
  - `with` expression produces new instance with changed field.

  `expectedFileChanges`: 8

- **Step 1.1.2**: Enforce `TierPolicy` in all store upsert and read paths, fix `DecayAsync`

  **InMemoryAgentMemoryStore** changes:
  - `UpsertExpertiseAsync`: call `TierPolicy.Normalize(record)` to get corrected copy, then `TierPolicy.Validate(corrected, existing)` where `existing` is looked up by ID. Persist the corrected copy.
  - `GetExpertiseAsync`: return `record with { DerivedFromEpisodeIds = record.DerivedFromEpisodeIds.ToList(), ... }` (defensive copy via `with`). Expose a private `DeepCopy(ExpertiseRecord)` helper.
  - `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync`, `GetExpertiseByTierAndCategoryAsync`: all use `DeepCopy` before returning.

  **FileSystemAgentMemoryStore** changes: same pattern as InMemory.

  **MemoryEvolver.DecayAsync** changes:
  - Replace `expertise.Confidence = newConfidence` with `var decayed = expertise with { Confidence = newConfidence, LastValidated = now }`.
  - Call `store.UpsertExpertiseAsync(decayed)` — the store's normalize+validate ensures consistency.

  **Tests**:
  - Round-trip: upsert FailurePattern without explicit tier → read back at Query tier.
  - Mutation isolation: read a record, modify `.Confidence` on the copy → re-read from store → original confidence unchanged. (This verifies deep-copy and `init`-only.)
  - Demotion rejection: upsert ArchitecturalKnowledge at Interaction → `InvalidTierException`.
  - Decay: verify `DecayAsync` returns new record instances, store contains updated confidence, original reference unchanged.

  `expectedFileChanges`: 8

### Stage 1.2 — Creation-Site Audit & Migration

- **Step 1.2.1**: Fix all `ExpertiseRecord` creation sites in `MemoryEvolver`

  **Concrete changes** (MemoryEvolver.cs):
  - `ConsolidateFileExpertiseAsync` — update path (lines 357–383): replace in-place mutation (`current.EpisodeCount = ...; current.Confidence = ...; current.DerivedFromEpisodeIds.AddRange(...)`) with `var updated = current with { EpisodeCount = episodes.Count, Confidence = ..., LastValidated = DateTime.UtcNow, DerivedFromEpisodeIds = current.DerivedFromEpisodeIds.Concat(newEpisodeIds).ToList(), PositiveEvidence = current.PositiveEvidence.Concat(newEvidence).ToList(), Summary = BuildExpertiseSummary(...) }`. Create path (lines 389–407): add `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FileExpertise)`.
  - `ConsolidateSymbolExpertiseAsync` — same pattern: `with` expression for updates, explicit `Tier` for creation.
  - `ConsolidateFailurePatternAsync` — **critical fix**: creation path (lines 543–561) must set `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FailurePattern)` which resolves to `MemoryTier.Query`. Update path (lines 513–537): `with` expression, no in-place mutation.
  - `CreatePromotedExpertiseAsync` — already sets `Tier = targetTier`, but verify category matches: `MemoryTier.Insight → ArchitecturalKnowledge`, `MemoryTier.Query → NamespaceExpertise`. Use `TierPolicy.Validate` after construction.
  - `DetectCoChangesAsync` — co-change group creation (line 865): already sets `Tier = MemoryTier.Query`. Verify `CoChangeGroup` is in `AllowedTiers` for Query.

  **Tests**:
  - `ConsolidateFailurePatternAsync` creates record → assert `Tier == MemoryTier.Query`.
  - `ConsolidateFailurePatternAsync` updates existing record → assert new instance returned, tier unchanged at Query.
  - `ConsolidateFileExpertiseAsync` update path → assert original record reference is not mutated.
  - `CreatePromotedExpertiseAsync` → assert promoted record has correct category for target tier.
  - Compile-time: no `current.Confidence = ...` or `current.EpisodeCount = ...` patterns remain (enforced by `init`-only).

  `expectedFileChanges`: 6

- **Step 1.2.2**: Legacy record migration and repair-on-load

  **FileSystemAgentMemoryStore.EnsureLoadedAsync** changes:
  - After `LoadAsync<ExpertiseRecord>`, iterate all loaded records.
  - For each record: call `TierPolicy.Normalize(record)`. If normalized record differs from original (tier changed), increment `autoNormalizedCount`, replace in dictionary.
  - For records with unrecognized `Category` values (future-proofing): move to quarantine dictionary, increment `quarantinedCount`.
  - After iteration: if any records were normalized, call `SaveExpertiseAsync` to persist corrections.
  - Write quarantined records to `expertise-quarantine.json`.

  **MemoryStatistics** additions:
  - `AutoNormalizedCount` (int): records auto-corrected during migration.
  - `QuarantinedCount` (int): records with unrecognized categories.
  - `TierDistributionByCategory` (`Dictionary<ExpertiseCategory, Dictionary<MemoryTier, int>>`): breakdown of tier distribution per category.

  **Test fixtures**: Create JSON fixture files with iteration-3 data:
  - `expertise.json` containing: a `FailurePattern` at `Interaction` tier, a `FileExpertise` at `Interaction` (valid), an `ArchitecturalKnowledge` at `Interaction` (invalid).
  - Tests: load fixture → assert FailurePattern repaired to Query, ArchitecturalKnowledge repaired to Insight, FileExpertise unchanged. Assert `AutoNormalizedCount == 2`. Assert quarantine file empty (no unrecognized categories).

  `expectedFileChanges`: 7

### Stage 1.3 — Configuration & Evaluation Harness

- **Step 1.3.1**: Feature flags and memory configuration

  `AgentMemoryOptions` class with: `EnableNeo4j` (bool, default false), `EnableQdrant` (bool, default false), `FallbackToFileSystem` (bool, default true), `StoragePath` (string), `StrictTierEnforcement` (bool, default true — when false, `Validate` logs warning instead of throwing). Wire into `ServiceCollectionExtensions.AddAgentMemory()` for DI. `IMemoryStoreHealthCheck` interface with `CheckHealthAsync()`.

  Tests: store selection with Neo4j disabled falls back to file-system; strict enforcement on by default.

  `expectedFileChanges`: 5

- **Step 1.3.2**: Baseline evaluation harness

  `IMemoryMetrics` interface with counters: `ContextHitRate`, `RetrievalLatencyMs`, `InvalidTierEncountered`, `AutoNormalizedRecords`, `TierDistributionByCategory`. Structured logging in `ContextBuilder` for hit/miss events. 3 golden-replay test scenarios as fixture data: (1) file-only context, (2) multi-tier retrieval verifying Insight → Query → Interaction ordering, (3) call-chain included context. Each scenario asserts assembled context against known expectations.

  `expectedFileChanges`: 7

---

## Phase 2 — Identity, Orchestrator Integration & Context Injection

Wire agent memory into the agent lifecycle: canonical identity, automatic episode recording, and context injection before task dispatch.

### Stage 2.1 — Canonical Symbol Identity

- **Step 2.1.1**: Symbol identity resolver

  `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath)` → repo-relative forward-slash path (handles drive letters, worktrees, UNC paths, trailing slashes). `CanonicalSymbolId(namespace, typeName, memberName?)` → deterministic dot-separated ID. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`) on `ExpertiseRecord`. Update `EpisodeRecorder` to use canonical paths for `TouchedFiles`. Tests: drive-letter normalization, worktree path normalization, case sensitivity on Windows vs Linux, rename tracking.

  `expectedFileChanges`: 7

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware

  Hook `IEpisodeRecorder` into the agent orchestrator. Auto-start episodes on task lifecycle begin, auto-complete on end. Track touched files from tool calls. Classify `TouchType` from tool name (`view` → `Read`, `edit` → `Modified`, `create` → `Created`, `grep` → `Read`). Persist `TouchType` on `MemoryEdge` via `EpisodeBuilder.CreateEdgesAsync()`. Tests: episode lifecycle with mocked tool calls.

  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files

  Resolve touched files to `HypergraphNode` IDs via `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback for file-only episodes when graph unavailable. Tests: with and without graph adapter.

  `expectedFileChanges`: 5

### Stage 2.3 — Context Injection & Provenance

- **Step 2.3.1**: Tier-ordered retrieval and context injection

  Update `ContextBuilder` to query `ITieredExpertiseStore` by tier: Insight first (architectural knowledge, conventions), then Query (namespace/project/call-chain patterns), then Interaction (file/method-level). Define retrieval contract: token budget, max items per tier, confidence threshold per tier. Add `BuildContextAsync()` call before task dispatch. Format `ContextWindow` as markdown with per-source attribution and tier labels.

  `expectedFileChanges`: 8

- **Step 2.3.2**: Provenance model

  Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`, `CallChain`, `ExpertiseRecord` (as `init`-only property). Retrieval scoring weights `GitSeeded` > `Observed` > `Inferred`. Tests: provenance set correctly on all creation paths in MemoryEvolver and seeder.

  `expectedFileChanges`: 6

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

Connect to Neo4j for durable graph memory. Seed the initial knowledge graph from repo structure, call chains, and git history. All store implementations call `TierPolicy` on upsert (cross-backend invariant).

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL, migration, and DI

  Cypher DDL for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`), relationships, indices, constraints. Idempotent `MemorySchemaMigration.ApplyAsync()`. Tier property on `:MemoryExpertise` nodes. DI registration via `AddNeo4jMemoryStore()`, health checks, docker-compose entry for Neo4j container.

  `expectedFileChanges`: 10

- **Step 3.1.2**: Episode and expertise CRUD

  `Neo4jMemoryStore` implementing `IAgentMemoryStore` + `ITieredExpertiseStore`. Cypher parameterized CRUD. `UpsertExpertiseAsync` calls `TierPolicy.Normalize` + `Validate` before Cypher MERGE. Reads return deserialized copies (no shared-reference risk with Neo4j). Integration tests with Testcontainers.

  `expectedFileChanges`: 12

- **Step 3.1.3**: Edges, call chains, and statistics

  `AddMemoryEdgeAsync` with `TouchType` property mapped to relationship property. Cypher traversals for incoming/outgoing edges. `ICallChainStore` implementation with ordered UNWIND for steps. `GetStatisticsAsync` with tier distribution aggregation query. Integration tests.

  `expectedFileChanges`: 10

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution and project hierarchy

  Walk `HypergraphData.Nodes` by project. Create `ProjectExpertise` records at Query tier via `TierPolicy.DefaultTierFor`. `ContainedIn` edges for solution → project → namespace. Invalidation: detect removed/added projects by comparing against previous run. Idempotent re-runs.

  `expectedFileChanges`: 8

- **Step 3.2.2**: Namespace, type, and API surface seeding

  `NamespaceExpertise` (Query tier), `TypeExpertise` (Interaction), `MethodExpertise` (Interaction) — all tiers assigned via `TierPolicy.DefaultTierFor`. Detect API controller endpoints and entry points. `ContainedIn` edges namespace → type. Invalidation for renames/moves detected by symbol ID comparison.

  `expectedFileChanges`: 10

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor

  Walk `MethodInvocation` edges depth-first from API controller actions and `Main` entry points. Build `CallChain` with `Inferred` source. Max depth 8, cycle detection via visited set. Follow `ServiceRegistration` edges for DI-resolved call chains. Create `CallChainPattern` expertise at Query tier.

  `expectedFileChanges`: 10

- **Step 3.3.2**: Git co-change seeder

  Parse `git log --name-only --pretty=format:"%H"`. Compute Jaccard similarity with gates: skip commits touching >50 files, filter by tracked file types (`.cs`, `.yaml`, `.json`), require co-occurrence count ≥3. Create `CoChangedWith` edges with `Provenance.GitSeeded`. Incremental processing via commit SHA watermark stored in memory statistics.

  `expectedFileChanges`: 8

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop and schedule memory evolution. All created records pass through `TierPolicy`.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement

  On `EpisodeOutcome.Success`: reinforce related expertise confidence via `record with { Confidence = Math.Min(1.0, record.Confidence + boost), LastValidated = now, AccessCount = record.AccessCount + 1 }`. On `Failed`: add negative evidence via `record with { NegativeEvidence = record.NegativeEvidence.Append(item).ToList() }`. Update `CommitSha` to HEAD. All updates go through store upsert → TierPolicy enforcement. Tests verify confidence changes and immutable update pattern.

  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation

  Compare `ExpertiseRecord.CommitSha` against HEAD. If files referenced by expertise were modified since `CommitSha`, mark as stale. Detect renamed/moved symbols via validity window (`FirstSeenCommit`/`LastSeenCommit`). Add freshness warnings to `ContextWindow`. Auto-decay stale expertise with accelerated half-life.

  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing and background service

  High-water mark for last-processed episode ID, last decay run timestamp, last promotion run timestamp. `MemoryEvolverService : IHostedService` running consolidation/decay/promotion on configurable intervals. Lease/lock via file-based or in-memory semaphore for concurrency safety. Log tier distribution changes after each evolution cycle. Tests verify no reprocessing of already-consolidated episodes.

  `expectedFileChanges`: 8

### Stage 4.3 — Vector Integration & Observability

- **Step 4.3.1**: Vector memory adapter

  `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`, `SearchSimilarAsync`. Qdrant collections for episode summaries and expertise summaries. Semantic search as candidate generation step in `ContextBuilder.GenerateCandidatesAsync`. Graceful fallback when Qdrant unavailable.

  `expectedFileChanges`: 10

- **Step 4.3.2**: Full metrics and health dashboard

  Extend Phase 1 metrics: stale-memory rate, per-provenance hit rates, token budget utilization, tier distribution by category over time. `memory health` CLI command aggregating all metrics. A/B feature flag for with-memory vs without-memory context to measure impact.

  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query and record tools

  MCP tool handlers: `memory_search` (query expertise/episodes by subject, tier, category), `memory_context` (build context window for a task), `memory_stats` (return statistics including tier distribution), `memory_record_observation` (add evidence to existing expertise), `memory_consolidate` (trigger consolidation), `memory_decay` (trigger decay). Register handlers in MCP tool registry. Tests with mock store.

  `expectedFileChanges`: 12

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect and management commands

  CLI commands: `memory stats`, `memory list-episodes [--type X] [--outcome X]`, `memory list-expertise [--tier X] [--category X] [--min-confidence X]`, `memory show <id>`, `memory consolidate`, `memory decay`, `memory seed [--source git|structure]`, `memory reindex`, `memory clear [--confirm]`. Table-formatted output with Spectre.Console. Progress bars for long-running operations.

  `expectedFileChanges`: 12

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until metrics show simple graph + vector retrieval is insufficient.
- **Full temporal knowledge graph** (arXiv:2501.13956) — `ValidFrom`/`ValidTo` windows on edges. Current `CommitSha` + `LastValidated` suffices.
- **LLM-generated community summaries** — Requires LLM API budget not yet established.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights need reward signal infrastructure.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Beyond current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Context windows fit within limits.
- **Fine-tuning or model adaptation** — Memory is retrieved at inference time, not used for training.
- **Academic dataset benchmarking** (LoCoMo, LongMemEval) — Not actionable for the product.
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, drops metadata, hardcodes `:CodeNode`.
- **Same-category tier promotion** — Promotion must transform category to maintain semantic clarity.
- **Composite multi-backend store** — Outbox-style dispatch deferred until both Neo4j and Qdrant backends exist.