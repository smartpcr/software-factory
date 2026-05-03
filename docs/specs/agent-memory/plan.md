# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 5)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library already provides a domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver with consolidation, decay, promotion, and co-change detection.

**The library is architecturally sound but has six concrete correctness defects that must be fixed before any further features are safe to add.**

### Root Defects (iteration 3/4 evaluator findings)

| # | Defect | Where (file:line) | Impact |
|---|--------|--------------------|--------|
| D1 | `ExpertiseRecord` has 8 mutable `set` properties | ExpertiseRecord.cs:36–96 | Callers retrieve from store and mutate live reference |
| D2 | Collections `DerivedFromEpisodeIds`, `PositiveEvidence`, `NegativeEvidence`, `Properties` are `List<>`/`Dictionary<>` | ExpertiseRecord.cs:52–101 | Even `init`-only property; underlying collection is mutable. MemoryEvolver calls `AddRange`/`Add` on retrieved objects |
| D3 | `TierMapping` is passive — never called on upsert or creation | MemoryModels.cs:202–231 | Invalid tier/category combinations persist silently |
| D4 | Stores persist without normalization/validation | InMemoryAgentMemoryStore.cs:123, FileSystemAgentMemoryStore.cs:156 | `this.expertise[record.Id] = record` with no checks |
| D5 | All read paths return live mutable references | InMemoryAgentMemoryStore.cs:130–163, FileSystemAgentMemoryStore.cs:165–196 | Caller mutates → store state corrupted |
| D6 | `FailurePattern` records created without `Tier` | MemoryEvolver.cs:543–561 | Defaults to `Interaction`; `DefaultTierFor(FailurePattern)` is `Query` |

**In-place mutation sites in MemoryEvolver** (complete list):

| Method | Lines | Mutation |
|--------|-------|----------|
| `ConsolidateFileExpertiseAsync` | 357–383 | `current.EpisodeCount = ...; current.Confidence = ...; current.DerivedFromEpisodeIds.AddRange(...); current.PositiveEvidence.Add(...); current.Summary = ...` |
| `ConsolidateSymbolExpertiseAsync` | 452–458 | `current.EpisodeCount = ...; current.Confidence = ...; current.DerivedFromEpisodeIds.AddRange(...); current.Summary = ...` |
| `ConsolidateFailurePatternAsync` | 513–534 | `current.EpisodeCount = ...; current.LastValidated = ...; current.DerivedFromEpisodeIds.AddRange(...); current.NegativeEvidence.Add(...); current.Summary = ...` |
| `DecayAsync` | 336 | `expertise.Confidence = newConfidence` |
| `ConsolidateFailurePatternAsync` (create) | 543–561 | `new ExpertiseRecord { ... }` without `Tier` |
| `DetectCoChangesAsync` (co-change group) | 865 | `new ExpertiseRecord { ... }` — sets `Tier` correctly but uses mutable collections |
| `GetExpertiseByTierFallbackAsync` | 726 | References deleted `TierMapping.DefaultTierFor` |

### Key Constraints

- **Persistence boundary**: Memory needs separate labels from `Ast.GraphDb.IGraphStore` (which hardcodes `:CodeNode`).
- **Cross-backend invariant**: Tier enforcement must be uniform across InMemory, FileSystem, and future Neo4j.
- **Concurrency**: Stores are single-writer per session; background evolver acquires a semaphore before mutation runs. No optimistic concurrency needed in Phase 1.
- **Scope**: Phase 1 hardens `ExpertiseRecord` only. `Episode`, `MemoryEdge`, `CallChain` are deferred — they have fewer cross-module mutation risks.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

| Source | Key Idea | Applied Here |
|--------|----------|--------------|
| **G-Memory** (arXiv:2506.07398) | 3-tier hierarchy | `MemoryTier` enum + `TierPolicy` enforcement |
| **MAGMA** (arXiv:2601.03236) | Multi-graph with orthogonal views | Multi-signal reranking in `ContextBuilder` |
| **Zep/Graphiti** (arXiv:2501.13956) | Temporally-aware KG | `CommitSha` freshness, half-life decay |
| **Mem0** (arXiv:2504.19413) | Scalable graph consolidation | `MemoryEvolver` consolidation + decay |
| **HyperGraphRAG** (arXiv:2503.21322) | N-ary relation modeling | `CallChain` (ordered n-ary sequences) |
| **ExpeL** (AAAI'24) | Insight distillation | Episode → expertise consolidation |

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **`ExpertiseRecord` stays `class`, NOT `record class`** | `record class` changes equality semantics (value equality), which would alter `HashSet`/`Dictionary` behavior in `ContextBuilder` and `MemoryEvolver`. Instead: keep `class`, change all `set` → `init`, add explicit `With(...)` copy helper, use `ImmutableArray<T>`/`ImmutableDictionary<TK,TV>` for collections. |
| **`TierPolicy` replaces `TierMapping`** | Single authority. `AllowedTiers(category)` → per-category allowed tier sets. `Normalize(record)` returns corrected copy. `Validate(record, previous?)` rejects invalid combos. Exhaustive `switch` on all 11 `ExpertiseCategory` values — no wildcard default. |
| **Store upsert calls `Normalize` then `Validate`** | Single enforcement point. Producers need not be trusted. Both InMemory and FileSystem stores reject invalid records. |
| **Stores return frozen immutable instances** | Since `ExpertiseRecord` is immutable (init-only + immutable collections), no deep-copy needed. The stored instance IS the canonical copy — no reference mutation is possible. |
| **`MemoryEvolver` uses `With(...)` helper for updates** | Every update path produces a NEW `ExpertiseRecord` via copy helper. No field or collection mutation anywhere. Enforced by `init`-only at compile time. |
| **Legacy repair-on-load** | `FileSystemAgentMemoryStore.EnsureLoadedAsync` normalizes all deserialized records. Repairs bad tiers, persists corrections. |
| **Single-writer concurrency** | `MemoryEvolver` operations are serialized by the caller (or a `SemaphoreSlim`). No optimistic concurrency or version field in Phase 1. Deferred to Phase 4 when background service is introduced. |

### ExpertiseRecord Invariant Matrix

Every store `UpsertExpertiseAsync` enforces these invariants via `TierPolicy`:

| Input Condition | Behavior | Method |
|----------------|----------|--------|
| `Tier` not in `AllowedTiers(Category)` | Replace with `DefaultTierFor(Category)` | `Normalize` |
| `Confidence < 0` | Clamp to `0.0` | `Normalize` |
| `Confidence > 1` | Clamp to `1.0` | `Normalize` |
| `DerivedFromEpisodeIds` is null | Replace with empty `ImmutableArray<string>` | `Normalize` |
| `PositiveEvidence` / `NegativeEvidence` is null | Replace with empty `ImmutableArray<EvidenceItem>` | `Normalize` |
| `Properties` is null | Replace with empty `ImmutableDictionary<string,string>` | `Normalize` |
| Duplicate entries in `DerivedFromEpisodeIds` | Deduplicate via `Distinct()` | `Normalize` |
| `previous` exists and `record.Tier < previous.Tier` | Throw `InvalidTierException` (demotion) | `Validate` |
| `previous` exists and `record.Category != previous.Category` | Throw `InvalidCategoryChangeException` | `Validate` |
| `StrictTierEnforcement` is false and tier is invalid | Log warning, apply `DefaultTierFor` | `Normalize` |

---

## Phase 1 — Domain Hardening

**Goal**: Fix all six root defects. Nothing else. Every step is independently testable. The phase is ordered: immutability first (prevents regressions during later steps), then policy (defines correctness), then store enforcement (guarantees invariants at persistence boundary), then creation-site rewrites (producers comply), then migration (legacy data repaired).

### Stage 1.1 — Immutable ExpertiseRecord & TierPolicy

- **Step 1.1.1**: Make `ExpertiseRecord` fully immutable and introduce `TierPolicy`

  Fixes: D1, D2, D3 (passive TierMapping).

  **ExpertiseRecord.cs** — exact changes:
  - Keep `public class ExpertiseRecord` (NOT record class — avoids equality semantic change).
  - `Tier { get; set; }` → `Tier { get; init; }`. Remove `= MemoryTier.Interaction` default. Make `required`.
  - `Confidence { get; set; }` → `Confidence { get; init; }` (default 0.5 stays).
  - `EpisodeCount { get; set; }` → `EpisodeCount { get; init; }`.
  - `Summary { get; set; }` → `Summary { get; init; }`.
  - `LastValidated { get; set; }` → `LastValidated { get; init; }`.
  - `CommitSha { get; set; }` → `CommitSha { get; init; }`.
  - `Branch { get; set; }` → `Branch { get; init; }`.
  - `AccessCount { get; set; }` → `AccessCount { get; init; }`.
  - `List<string> DerivedFromEpisodeIds { get; init; } = []` → `ImmutableArray<string> DerivedFromEpisodeIds { get; init; } = ImmutableArray<string>.Empty`.
  - `List<EvidenceItem> PositiveEvidence { get; init; } = []` → `ImmutableArray<EvidenceItem> PositiveEvidence { get; init; } = ImmutableArray<EvidenceItem>.Empty`.
  - `List<EvidenceItem> NegativeEvidence { get; init; } = []` → `ImmutableArray<EvidenceItem> NegativeEvidence { get; init; } = ImmutableArray<EvidenceItem>.Empty`.
  - `Dictionary<string, string> Properties { get; init; } = []` → `ImmutableDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty`.
  - Add `public ExpertiseRecord With(...)` copy helper that creates a new instance. Pattern: accepts optional params, defaults to current values.

  **EvidenceItem** (MemoryModels.cs) — make all properties `init`-only (they already are; verify `Timestamp`).

  **TierPolicy.cs** — new file:
  - `static class TierPolicy`.
  - `AllowedTiers(ExpertiseCategory)` → `IReadOnlySet<MemoryTier>`. Exhaustive switch, 11 cases, no wildcard.
  - `DefaultTierFor(ExpertiseCategory)` → `MemoryTier`. Exhaustive switch, 11 cases, no wildcard.
  - `Normalize(ExpertiseRecord)` → `ExpertiseRecord`. Applies invariant matrix above.
  - `Validate(ExpertiseRecord, ExpertiseRecord?)` → throws on invalid tier, demotion, or category change.
  - `InvalidTierException : ArgumentException`.

  **MemoryModels.cs** — delete `TierMapping` class (lines 201–231).

  **AgentMemory.csproj** — add `System.Collections.Immutable` package reference.

  **Tests** (TierPolicyTests.cs, ExpertiseRecordImmutabilityTests.cs):
  - All 11 categories × `DefaultTierFor` → expected value.
  - All 11 categories × `AllowedTiers` → expected set.
  - `Normalize`: FailurePattern at Interaction → corrected to Query.
  - `Normalize`: Confidence -0.5 → clamped to 0.0; Confidence 1.5 → clamped to 1.0.
  - `Normalize`: null collections → empty immutable collections.
  - `Validate`: demotion (Query→Interaction) → throws.
  - `Validate`: promotion (Interaction→Query for FileExpertise) → passes.
  - `Validate`: ArchitecturalKnowledge at Interaction → throws.
  - `Validate`: category change → throws.
  - Reflection test: no public `set` accessor on `ExpertiseRecord`.
  - `ImmutableArray.Add` returns new array (original unchanged).
  - JSON round-trip: serialize → deserialize → all fields equal, collections remain immutable type.

  `expectedFileChanges`: 8

### Stage 1.2 — Store Enforcement

- **Step 1.2.1**: Add `TierPolicy` enforcement to store upsert paths and fix all MemoryEvolver mutation sites

  Fixes: D4 (stores persist without validation), D5 (live mutable references — no longer possible after Step 1.1.1 makes ExpertiseRecord immutable), D6 (FailurePattern wrong tier), plus all in-place mutation in MemoryEvolver.

  **InMemoryAgentMemoryStore.cs**:
  - `UpsertExpertiseAsync`: `var normalized = TierPolicy.Normalize(record); TierPolicy.Validate(normalized, existing); this.expertise[normalized.Id] = normalized;`
  - Read paths (`GetExpertiseAsync`, `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync`, `GetExpertiseByTierAndCategoryAsync`): return stored instance directly — it's immutable. No deep copy needed.

  **FileSystemAgentMemoryStore.cs**: same upsert pattern as InMemory.

  **MemoryEvolver.cs** — rewrite ALL mutation sites:
  - `ConsolidateFileExpertiseAsync` update path: replace `current.EpisodeCount = ...; current.Confidence = ...; current.DerivedFromEpisodeIds.AddRange(...); current.PositiveEvidence.Add(...); current.Summary = ...` with single `var updated = new ExpertiseRecord { Id = current.Id, Subject = current.Subject, Category = current.Category, Tier = current.Tier, EpisodeCount = episodes.Count, Confidence = ..., DerivedFromEpisodeIds = current.DerivedFromEpisodeIds.AddRange(newEpisodeIds), PositiveEvidence = current.PositiveEvidence.AddRange(newEvidence), Summary = ..., LastValidated = DateTime.UtcNow, ... }; await store.UpsertExpertiseAsync(updated, ...)`.
  - `ConsolidateFileExpertiseAsync` create path: add `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FileExpertise)`.
  - `ConsolidateSymbolExpertiseAsync` update path: same pattern as file update — no in-place mutation.
  - `ConsolidateSymbolExpertiseAsync` create path: add explicit `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.TypeExpertise)`.
  - `ConsolidateFailurePatternAsync` update path: `var updated = new ExpertiseRecord { ..., Tier = current.Tier, NegativeEvidence = current.NegativeEvidence.AddRange(newEvidence), ... }`.
  - `ConsolidateFailurePatternAsync` create path: **critical fix** — `Tier = TierPolicy.DefaultTierFor(ExpertiseCategory.FailurePattern)` (resolves to `MemoryTier.Query`). Use `ImmutableArray` for `NegativeEvidence`.
  - `DecayAsync`: replace `expertise.Confidence = newConfidence` with `var decayed = new ExpertiseRecord { ..., Confidence = newConfidence, LastValidated = now, ... }; await store.UpsertExpertiseAsync(decayed, ...)`.
  - `CreatePromotedExpertiseAsync`: verify category/tier pairing passes `TierPolicy.Validate`. Use `ImmutableArray` for `PositiveEvidence`/`DerivedFromEpisodeIds`.
  - `DetectCoChangesAsync` co-change group: use `ImmutableDictionary` for `Properties`, use `ImmutableArray` for collections.
  - `GetExpertiseByTierFallbackAsync`: replace `TierMapping.DefaultTierFor` → `TierPolicy.DefaultTierFor`.

  **Tests** (MemoryEvolverTests.cs, InMemoryAgentMemoryStoreTests.cs, FileSystemAgentMemoryStoreTests.cs):
  - Upsert FailurePattern without explicit tier → store normalizes to Query. Read back → `Tier == Query`.
  - Upsert ArchitecturalKnowledge at Interaction → `InvalidTierException`.
  - Consolidate failure pattern → created record has `Tier == Query`.
  - Consolidate file expertise (update) → original reference unchanged in store.
  - Decay → new record created, original untouched.
  - Demotion attempt via upsert → rejected.
  - Category change via upsert → rejected.
  - File-store JSON round-trip: save record with `ImmutableArray` collections → reload → types preserved.

  `expectedFileChanges`: 8

### Stage 1.3 — Migration & Config

- **Step 1.3.1**: Legacy record migration repair-on-load

  **FileSystemAgentMemoryStore.EnsureLoadedAsync** changes:
  - After `LoadAsync<ExpertiseRecord>`, iterate all loaded records.
  - For each: `var normalized = TierPolicy.Normalize(record)`. If `normalized.Tier != record.Tier` or collections were null-replaced, increment `autoNormalizedCount`, replace in dictionary.
  - Records with `Category` not in `Enum.IsDefined` → quarantine to `expertise-quarantine.json`.
  - If any normalized, call `SaveExpertiseAsync` to persist corrections.

  **MemoryStatistics additions**:
  - `AutoNormalizedCount` (int).
  - `QuarantinedCount` (int).

  **Custom JSON converter** for `ImmutableArray<T>` and `ImmutableDictionary<TK,TV>` if `System.Text.Json` doesn't handle them natively (it does for `ImmutableArray` but verify `ImmutableDictionary`).

  **Tests**:
  - Fixture `expertise.json`: FailurePattern at Interaction, ArchitecturalKnowledge at Interaction, valid FileExpertise at Interaction.
  - Load fixture → FailurePattern at Query, ArchitecturalKnowledge at Insight, FileExpertise unchanged.
  - `AutoNormalizedCount == 2`.
  - Records with null collections → replaced with empty immutable collections after load.

  `expectedFileChanges`: 7

- **Step 1.3.2**: Feature flags and memory configuration

  `AgentMemoryOptions` class: `EnableNeo4j` (false), `EnableQdrant` (false), `FallbackToFileSystem` (true), `StoragePath` (string), `StrictTierEnforcement` (true — when false, `Validate` logs warning and normalizes instead of throwing). Wire into `ServiceCollectionExtensions.AddAgentMemory()`. `IMemoryStoreHealthCheck` with `CheckHealthAsync()`.

  Tests: strict enforcement default; store selection fallback.

  `expectedFileChanges`: 5

---

## Phase 2 — Identity, Orchestrator Integration & Context Injection

Wire agent memory into the agent lifecycle.

### Stage 2.1 — Canonical Symbol Identity

- **Step 2.1.1**: Symbol identity resolver

  `SymbolIdentity` static class: `NormalizePath(repoRoot, absolutePath)` → repo-relative forward-slash path. `CanonicalSymbolId(namespace, typeName, memberName?)` → deterministic dot-separated ID. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`) on `ExpertiseRecord` as `init`-only. Update `EpisodeRecorder` to normalize paths. Tests: drive letters, worktrees, UNC, case sensitivity.

  `expectedFileChanges`: 7

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware

  Hook `IEpisodeRecorder` into agent orchestrator. Auto-start/complete episodes on task lifecycle. Classify `TouchType` from tool name. Persist on `MemoryEdge`. Tests: episode lifecycle.

  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files

  Resolve files → `HypergraphNode` IDs via `IGraphStoreAdapter`. Graceful fallback. Tests: with/without graph.

  `expectedFileChanges`: 5

### Stage 2.3 — Context Injection & Provenance

- **Step 2.3.1**: Tier-ordered retrieval and context injection

  Query `ITieredExpertiseStore` by tier: Insight → Query → Interaction. Token budget per tier. Format `ContextWindow` as markdown with tier labels.

  `expectedFileChanges`: 8

- **Step 2.3.2**: Provenance model

  `Provenance` enum on `MemoryEdge`, `CallChain`, `ExpertiseRecord` (init-only). Retrieval scoring weights. Tests: provenance on all creation paths.

  `expectedFileChanges`: 6

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL, migration, and DI

  Cypher DDL, idempotent migration, tier property on `:MemoryExpertise`, DI, health checks, docker-compose.

  `expectedFileChanges`: 10

- **Step 3.1.2**: Episode and expertise CRUD

  `Neo4jMemoryStore` with `TierPolicy.Normalize` + `Validate` on upsert. Reads return deserialized copies. Testcontainers integration tests.

  `expectedFileChanges`: 12

- **Step 3.1.3**: Edges, call chains, and statistics

  Relationship properties, Cypher traversals, `ICallChainStore`, tier distribution queries. Integration tests.

  `expectedFileChanges`: 10

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution and project hierarchy

  `ProjectExpertise` at Query tier via `TierPolicy.DefaultTierFor`. `ContainedIn` edges. Idempotent.

  `expectedFileChanges`: 8

- **Step 3.2.2**: Namespace, type, and API surface seeding

  `NamespaceExpertise` (Query), `TypeExpertise` (Interaction), `MethodExpertise` (Interaction) via `TierPolicy.DefaultTierFor`. `ContainedIn` edges. Rename detection.

  `expectedFileChanges`: 10

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor

  DFS from entry points. `CallChain` with `Inferred` source. Max depth 8, cycle detection. `CallChainPattern` at Query tier.

  `expectedFileChanges`: 10

- **Step 3.3.2**: Git co-change seeder

  Parse `git log`. Jaccard similarity with gates. `CoChangedWith` edges with `Provenance.GitSeeded`. SHA watermark for incremental.

  `expectedFileChanges`: 8

---

## Phase 4 — Feedback, Evolution & Observability

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement

  Immutable updates via copy constructors. All through store upsert → TierPolicy.

  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation

  Compare `CommitSha` vs HEAD. Validity window. Freshness warnings.

  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing and background service

  `IHostedService`, semaphore for concurrency, high-water marks.

  `expectedFileChanges`: 8

### Stage 4.3 — Vector Integration & Observability

- **Step 4.3.1**: Vector memory adapter

  `IVectorMemoryAdapter`, Qdrant collections, semantic search in `ContextBuilder`.

  `expectedFileChanges`: 10

- **Step 4.3.2**: Metrics, evaluation harness, and health dashboard

  `IMemoryMetrics`, golden-replay tests, stale-memory rate, A/B flags.

  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query and record tools

  `memory_search`, `memory_context`, `memory_stats`, `memory_record_observation`, `memory_consolidate`, `memory_decay`.

  `expectedFileChanges`: 12

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect and management commands

  `memory stats/list-episodes/list-expertise/show/consolidate/decay/seed/reindex/clear`. Spectre.Console output.

  `expectedFileChanges`: 12

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until metrics show retrieval is insufficient.
- **Full temporal knowledge graph** — `ValidFrom`/`ValidTo` on edges deferred. `CommitSha` + `LastValidated` suffices.
- **LLM-generated community summaries** — Requires LLM API budget.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights need reward signals.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Beyond single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Context windows fit within limits.
- **Fine-tuning or model adaptation** — Memory is retrieval-time only.
- **`record class` for ExpertiseRecord** — Equality semantic change too risky; use immutable `class` with `init` + copy helpers.
- **Optimistic concurrency / version fields** — Deferred to Phase 4 background service.
- **Hardening Episode/MemoryEdge/CallChain immutability** — Lower risk; deferred until after ExpertiseRecord is stable.
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, hardcodes `:CodeNode`.