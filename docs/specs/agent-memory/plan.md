# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 3)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library already provides the domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver.

**Iteration 2 gaps identified by evaluator:**
1. **Tier model not enforced** — `ExpertiseRecord.Tier` defaults to `Interaction` regardless of category. `TierMapping.DefaultTierFor()` and `IsValidCombination()` exist but are never called on creation or upsert.
2. **All stores accept invalid combinations** — `UpsertExpertiseAsync` in both stores blindly persists whatever tier the caller supplies.
3. **No migration for persisted records** — Existing file-system records with wrong tiers remain incorrect when enforcement begins.
4. **Concrete bug** — `ConsolidateFailurePatternAsync` creates `FailurePattern` at `Interaction` tier; `TierMapping` maps it to `Query`.
5. **Mutable reference bypass** — `Tier` and `Category` are mutable (`get; set;`). Callers can fetch a record from the in-memory store and mutate its properties directly, corrupting shared state. Mutable lists (`PositiveEvidence`, `DerivedFromEpisodeIds`, etc.) compound this — even `init`-only on scalar fields is insufficient.

**Remaining work:** full immutability with replacement semantics, centralized tier policy, creation-site audit, orchestrator integration, persistent graph/vector backends, knowledge extraction from repo structure and call chains, feedback loop with observability, and CLI/MCP surfaces.

### Key Constraints

- **Persistence boundary**: `Ast.GraphDb.IGraphStore` stores nodes as `:CodeNode` — memory needs separate labels and domain-specific queries.
- **Symbol identity**: Canonical, stable across sessions; handles renames, worktrees, branch drift. Each symbol carries a validity window (`FirstSeenCommit`, `LastSeenCommit`).
- **Provenance**: Records from git history (ground truth), static analysis (inferred), and episodes (observed) — each weighted differently.
- **Cross-backend invariant**: Tier enforcement is a cross-cutting concern applied uniformly by every store implementation.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural views; multi-signal reranking in ContextBuilder.
2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight with centralized `TierPolicy` enforcement and explicit allowed-tier sets per category.
3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via `CommitSha`; git-seeded co-change.
4. **Mem0 consolidation** (arXiv:2504.19413) — diversity-gated episode aggregation preventing false expertise.
5. **ExpeL distillation** (AAAI'24) — failure patterns from repeated failures; verbal reflection without fine-tuning.

| Decision | Rationale |
|----------|-----------|
| **Fully immutable `ExpertiseRecord`** | All properties `init`-only. All collections are `IReadOnlyList<>` / `IReadOnlyDictionary<>`. Tier/category changes produce new instances via `with` expressions. `Upsert` is whole-object replacement. This closes mutable-reference and mutable-collection bypasses. |
| **Centralized `TierPolicy` with allowed-tier sets** | `AllowedTiers(category)` returns a set (not just a minimum). `Normalize()` returns a corrected copy. `Validate()` rejects invalid combos and demotions. Exhaustive switch with no wildcard fallback. |
| **Rollout sequence: repair → validate writes → validate reads → new producers** | Legacy data repaired first, then strict write enforcement, then read-time assertions. Prevents cascading failures during adoption. |
| **Creation-site audit as explicit step** | All `new ExpertiseRecord` call sites audited and updated before store enforcement is enabled. Prevents immediate breakage. |
| **Migration with telemetry** | Repair-on-load with `AutoNormalizedCount` counter. Quarantine for unrecoverable records. Versioned migration policy. |
| **Promotion transforms category and tier** | `FileExpertise → NamespaceExpertise` on Interaction→Query; `NamespaceExpertise → ArchitecturalKnowledge` on Query→Insight. Same-category promotion is disallowed. |

---

## Phase 1 — Tier Enforcement & Domain Hardening

**Goal**: Close every evaluator finding from iteration 2. Deliver fully immutable domain model, centralized tier policy, store-level enforcement, creation-site audit, legacy migration, and evaluation harness.

### Stage 1.1 — Immutable Domain Model & TierPolicy

- **Step 1.1.1**: Make `ExpertiseRecord` fully immutable and create `TierPolicy` — Convert all `ExpertiseRecord` properties to `init`-only. Convert mutable `List<>` and `Dictionary<>` to `IReadOnlyList<>` / `IReadOnlyDictionary<>`. Create `TierPolicy` static class: `AllowedTiers(ExpertiseCategory)` returns allowed tier set, `Normalize(ExpertiseRecord)` returns corrected copy, `Validate(ExpertiseRecord, ExpertiseRecord?)` throws on invalid combo or demotion. Exhaustive switch on all 11 `ExpertiseCategory` values. Add `TierPolicy.WithTier()` and `TierPolicy.WithConfidence()` helper methods for producing new record copies. Tests: each category → correct default tier; demotion rejected; valid higher tier accepted; undefined category rejected; immutability verified (no `set` accessors).
  `expectedFileChanges`: 8

- **Step 1.1.2**: Enforce `TierPolicy` in all store upsert and read paths — Update `InMemoryAgentMemoryStore.UpsertExpertiseAsync()` and `FileSystemAgentMemoryStore.UpsertExpertiseAsync()` to call `TierPolicy.Normalize` then `Validate` before persisting. All read methods (`GetExpertiseAsync`, `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync`) return deep copies. Update `DecayAsync` in `MemoryEvolver` to use `TierPolicy.WithConfidence()` instead of mutating `Confidence` in-place. Tests: round-trip upsert/read returns correct tier; mutation of returned copy does not affect store; demotion upsert throws; decay produces new record instances.
  `expectedFileChanges`: 8

### Stage 1.2 — Creation-Site Audit & Migration

- **Step 1.2.1**: Fix all `ExpertiseRecord` creation sites in `MemoryEvolver` — Audit every `new ExpertiseRecord` in `ConsolidateFileExpertiseAsync`, `ConsolidateSymbolExpertiseAsync`, `ConsolidateFailurePatternAsync`, `CreatePromotedExpertiseAsync`, `DetectCoChangesAsync`. Each site sets explicit `Category` and either explicit correct `Tier` or omits tier to rely on `TierPolicy.Normalize` at store. Fix update paths: all existing-record updates produce new instances via `with` expression instead of mutating fields. Tests: ConsolidateFailurePattern produces record at Query tier; promoted records have correct target category/tier; co-change groups at Query tier; updated records are new instances.
  `expectedFileChanges`: 6

- **Step 1.2.2**: Legacy record migration and statistics — `FileSystemAgentMemoryStore` repair-on-load: iterate all persisted expertise records, apply `TierPolicy.Normalize`, persist corrected records. Quarantine records with unrecognized categories. Add `AutoNormalizedCount`, `InvalidTierRejectedCount`, and `QuarantinedCount` to `MemoryStatistics`. Add `TierDistributionByCategory` to statistics. Create fixture files with iteration-2 data. Tests: load fixture with FailurePattern at Interaction → repaired to Query; counters incremented; quarantined records reported.
  `expectedFileChanges`: 7

### Stage 1.3 — Configuration & Evaluation Harness

- **Step 1.3.1**: Feature flags and memory configuration — `AgentMemoryOptions` with `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`, `StoragePath`, `StrictTierEnforcement` (on by default). Wire into `ServiceCollectionExtensions`. `IMemoryStoreHealthCheck` interface. Tests for store selection and fallback.
  `expectedFileChanges`: 5

- **Step 1.3.2**: Baseline evaluation harness — `IMemoryMetrics` with context hit rate, retrieval latency, invalid-tier-encountered, auto-normalized-records counters. Structured logging for hit/miss events. 3 golden replay scenarios as test fixtures verifying context assembly against known expectations (tier-ordered retrieval: Insight → Query → Interaction). Tier distribution by category in metrics output.
  `expectedFileChanges`: 7

---

## Phase 2 — Identity, Orchestrator Integration & Context Injection

Wire agent memory into the agent lifecycle: canonical identity, automatic episode recording, and context injection before task dispatch.

### Stage 2.1 — Canonical Symbol Identity

- **Step 2.1.1**: Symbol identity resolver — `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath)` and `CanonicalSymbolId(namespace, typeName, memberName)`. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`). Tests for drive letters, worktrees, renames, case sensitivity. Update `EpisodeRecorder` to use canonical paths.
  `expectedFileChanges`: 7

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware — Hook `IEpisodeRecorder` into `CopilotAgentOrchestrator`. Auto-start/complete episodes on task lifecycle. Track touched files from tool calls. Classify `TouchType` from tool name (`view` → `Read`, `edit` → `Modified`, `create` → `Created`). Persist `TouchType` on `MemoryEdge` via `EpisodeBuilder.CreateEdgesAsync()`.
  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files — Resolve touched files to `HypergraphNode` IDs via `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback for file-only episodes when graph unavailable.
  `expectedFileChanges`: 5

### Stage 2.3 — Context Injection & Provenance

- **Step 2.3.1**: Tier-ordered retrieval and context injection — `ContextBuilder` queries `ITieredExpertiseStore` by tier: Insight first (architectural), then Query (namespace/pattern), then Interaction (file-level). Define retrieval contract (token budget, max items per source, confidence threshold). Add `BuildContextAsync()` call before task dispatch. Format `ContextWindow` as markdown with per-source attribution.
  `expectedFileChanges`: 8

- **Step 2.3.2**: Provenance model — Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`, `CallChain`, `ExpertiseRecord`. Retrieval weights ground-truth provenance higher. Tests verify provenance set on all creation paths.
  `expectedFileChanges`: 6

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

Connect to Neo4j for durable graph memory. Seed the initial knowledge graph from repo structure, call chains, and git history. All store implementations call `TierPolicy` on upsert (cross-backend invariant).

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL, migration, and DI — Cypher for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`), relationships, indices, constraints. Idempotent `MemorySchemaMigration.ApplyAsync()`. Tier property on `:MemoryExpertise` nodes. DI registration, health checks, docker-compose entry.
  `expectedFileChanges`: 10

- **Step 3.1.2**: Episode and expertise CRUD — `Neo4jMemoryStore` implementing `IAgentMemoryStore` + `ITieredExpertiseStore`. Cypher CRUD. `UpsertExpertiseAsync` calls `TierPolicy.Normalize` + `Validate`. Deep copies on read. Integration tests with Testcontainers.
  `expectedFileChanges`: 12

- **Step 3.1.3**: Edges, call chains, and statistics — `AddMemoryEdgeAsync` with `TouchType` property. Cypher traversals. `ICallChainStore` implementation. `GetStatisticsAsync` with tier distribution. Integration tests.
  `expectedFileChanges`: 10

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution and project hierarchy — Walk `HypergraphData.Nodes` by project. Create `ProjectExpertise` records (Query tier via `TierPolicy`). `ContainedIn` edges for solution → project → namespace. Invalidation: detect removed/added projects.
  `expectedFileChanges`: 8

- **Step 3.2.2**: Namespace, type, and API surface seeding — `NamespaceExpertise` (Query), `TypeExpertise` (Interaction), `MethodExpertise` (Interaction). Detect API endpoints and entry points. `ContainedIn` edges namespace → type. Invalidation for renames/moves.
  `expectedFileChanges`: 10

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor — Walk `MethodInvocation` edges depth-first from entry points. Build `CallChain` with `Inferred` source. Max depth 8, cycle detection. Follow `ServiceRegistration` edges for DI chains. Create `CallChainPattern` expertise (Query tier).
  `expectedFileChanges`: 10

- **Step 3.3.2**: Git co-change seeder — Parse `git log --name-only`. Jaccard similarity with gates (commit size ≤50, file-type filters, count ≥3). `CoChangedWith` edges with `Provenance.GitSeeded`. Incremental via commit SHA watermark.
  `expectedFileChanges`: 8

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop and schedule memory evolution. All created records pass through `TierPolicy`.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement — Reinforce expertise confidence on `Success`, record negative evidence on `Failed`. Update `LastValidated`, `CommitSha`, `AccessCount` via new immutable record copies. Tests verify confidence changes.
  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation — Compare `CommitSha` against HEAD. Detect renamed/moved symbols via validity windows. Freshness warnings for stale expertise. Auto-decay on confirmed invalidation.
  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing and background service — High-water mark for last-processed episode/decay/promotion run. `IHostedService` running on configurable intervals. Lease/lock for concurrency. Log tier distribution changes. Tests verify no reprocessing.
  `expectedFileChanges`: 8

### Stage 4.3 — Vector Integration & Observability

- **Step 4.3.1**: Vector memory adapter — `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`, `SearchSimilarAsync`. Qdrant collections. Semantic search as candidate generation step. Graceful fallback.
  `expectedFileChanges`: 10

- **Step 4.3.2**: Full metrics and health dashboard — Extend Phase 1 metrics: stale-memory rate, per-provenance hit rates, token budget usage, tier distribution by category. `memory health` command. A/B flag for with-memory vs without.
  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query and record tools — `memory_search`, `memory_context`, `memory_stats`, `memory_record_observation`, `memory_consolidate`, `memory_decay`. Register as MCP tool handlers. Tests with mock store.
  `expectedFileChanges`: 12

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect and management commands — `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`, `memory consolidate`, `memory decay`, `memory seed`, `memory reindex`, `memory clear`. Table-formatted output with Spectre.Console. Progress bars.
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