# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 6)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. We need a graph-based agent memory system that builds persistent expertise on top-down repository structure and call-chain context, so agents can learn and get smarter across sessions.

The `AgentMemory` library already provides a domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver with consolidation, decay, promotion, and co-change detection. However, the library has six correctness defects that must be fixed before any further features are safe to add, and several integration gaps must be bridged before agents can actually use the memory.

### Root Defects

| # | Defect | Impact |
|---|--------|--------|
| D1 | `ExpertiseRecord` has 8 mutable `set` properties | Callers retrieve from store and mutate live reference |
| D2 | Collections are mutable `List<>`/`Dictionary<>` | MemoryEvolver calls `AddRange`/`Add` on retrieved objects |
| D3 | `TierMapping` is passive — never called on upsert | Invalid tier/category combinations persist silently |
| D4 | Stores persist without normalization/validation | No checks at persistence boundary |
| D5 | All read paths return live mutable references | Caller mutation corrupts store state |
| D6 | `FailurePattern` records created without `Tier` | Defaults to `Interaction`; should be `Query` |

### Key Constraints

- **Persistence boundary**: Memory needs separate labels from `Ast.GraphDb.IGraphStore` (which hardcodes `:CodeNode`).
- **Cross-backend invariant**: Tier enforcement must be uniform across InMemory, FileSystem, and future Neo4j.
- **Graph coexistence**: Memory graph and code graph share a Neo4j instance but use separate labels — clearing one must not affect the other.
- **Branch scoping**: Multi-worktree repos require branch-aware retrieval to avoid wrong-branch contamination.
- **Single-writer concurrency**: Stores are single-writer per session; no optimistic concurrency in Phase 1.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy, drawing on six key systems:

| Source | Key Idea | Applied Here |
|--------|----------|--------------|
| **G-Memory** (arXiv:2506.07398) | 3-tier hierarchy: Insight → Query → Interaction | `MemoryTier` enum + `TierPolicy` enforcement mapping to repo structure |
| **MAGMA** (arXiv:2601.03236) | Multi-graph with orthogonal views (semantic, temporal, causal) | Multi-signal reranking in `ContextBuilder` with branch-aware scoring |
| **Zep/Graphiti** (arXiv:2501.13956) | Temporally-aware knowledge graph | `CommitSha` freshness, half-life decay, validity windows |
| **Mem0** (arXiv:2504.19413) | Scalable graph consolidation with write gating | `MemoryEvolver` consolidation + decay + episode quality filter |
| **HyperGraphRAG** (arXiv:2503.21322) | N-ary relation modeling via hyperedges | `CallChain` (ordered n-ary method sequences) |
| **ExpeL** (AAAI'24) | Insight distillation from experience without fine-tuning | Episode → expertise consolidation with diversity gates |

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **`ExpertiseRecord` stays `class`, NOT `record class`** | Avoids equality semantic change affecting `HashSet`/`Dictionary` behavior. Instead: `init`-only + `ImmutableArray`/`ImmutableDictionary` + `With(...)` copy helper. |
| **`TierPolicy` replaces `TierMapping`** | Single authority with exhaustive switch on all 11 `ExpertiseCategory` values. Normalize → Validate at upsert boundary. |
| **Language-aware symbol IDs** | Use Roslyn documentation comment IDs (e.g., `M:Namespace.Type.Method(ParamType)`) for C# symbols, with arity/signature to disambiguate overloads, generics, and nested types. Fallback to `path:line` for unresolved symbols. |
| **Separate graph labels for memory vs code** | `:MemoryEpisode`, `:MemoryExpertise` labels in Neo4j alongside existing `:CodeNode`. Cross-graph references via foreign-key IDs, not native Neo4j relationships, to isolate clear/reseed operations. |
| **Branch-aware retrieval** | Context queries prefer same-branch memory, penalize cross-branch results, and include branch mismatch warnings. Prevents wrong-worktree contamination. |
| **Write gating for episodes** | Episode recording middleware deduplicates near-identical episodes, requires minimum evidence/outcome quality, and suppresses trivial read-only operations. Prevents memory flooding. |
| **Single-writer concurrency** | Serialized by `SemaphoreSlim`. Deferred to Phase 4 when background service is introduced. |

### ExpertiseRecord Invariant Matrix

Every store `UpsertExpertiseAsync` enforces these invariants via `TierPolicy`:

| Input Condition | Behavior | Method |
|----------------|----------|--------|
| `Tier` not in `AllowedTiers(Category)` | Replace with `DefaultTierFor(Category)` | `Normalize` |
| `Confidence < 0` or `> 1` | Clamp to `[0.0, 1.0]` | `Normalize` |
| Null collections | Replace with empty immutable collections | `Normalize` |
| Duplicate `DerivedFromEpisodeIds` | Deduplicate via `Distinct()` | `Normalize` |
| Demotion (`record.Tier < previous.Tier`) | Throw `InvalidTierException` | `Validate` |
| Category change from previous record | Throw `InvalidCategoryChangeException` | `Validate` |

---

## Phase 1 — Domain Hardening

**Goal**: Fix all six root defects. Ordered: immutability first, then policy, then store enforcement, then migration.

### Stage 1.1 — Immutable ExpertiseRecord & TierPolicy

- **Step 1.1.1**: Make `ExpertiseRecord` fully immutable and introduce `TierPolicy`

  Fixes D1, D2, D3. Change all `set` → `init` on ExpertiseRecord, replace mutable collections with `ImmutableArray<T>`/`ImmutableDictionary<TK,TV>`, add `With(...)` copy helper. Create `TierPolicy.cs` with exhaustive switch (11 categories) for `AllowedTiers`, `DefaultTierFor`, `Normalize`, `Validate`. Delete passive `TierMapping` from MemoryModels.cs. Add `System.Collections.Immutable` package. Tests: tier matrix, immutability reflection, JSON round-trip.

  `expectedFileChanges`: 8

### Stage 1.2 — Store Enforcement

- **Step 1.2.1**: TierPolicy enforcement in stores and MemoryEvolver mutation rewrite

  Fixes D4, D5, D6. Add `TierPolicy.Normalize` + `Validate` to both `InMemoryAgentMemoryStore` and `FileSystemAgentMemoryStore` upsert paths. Rewrite all 7 MemoryEvolver in-place mutation sites to use immutable copy construction. Tests: upsert validation, consolidation immutability, decay copy semantics.

  `expectedFileChanges`: 8

### Stage 1.3 — Migration & Basic Config

- **Step 1.3.1**: Legacy record repair-on-load

  FileSystemAgentMemoryStore normalizes all deserialized records via `TierPolicy.Normalize` on load. Quarantine records with undefined `Category` values. Add `AutoNormalizedCount` and `QuarantinedCount` to MemoryStatistics. Tests: fixture with bad tiers → corrected.

  `expectedFileChanges`: 7

- **Step 1.3.2**: Minimal memory configuration

  `AgentMemoryOptions` with `StrictTierEnforcement` (true), `StoragePath`, `FallbackToFileSystem` (true). Wire into `ServiceCollectionExtensions.AddAgentMemory()`. Defer `EnableNeo4j`/`EnableQdrant` flags to Phase 3/4 when those backends exist. Tests: strict enforcement default, store selection.

  `expectedFileChanges`: 4

---

## Phase 2 — Identity, Integration & Context Injection

Wire agent memory into the agent lifecycle with correct symbol identity and branch-aware retrieval.

### Stage 2.1 — Symbol Identity & Graph Interop

- **Step 2.1.1**: Language-aware symbol identity resolver

  `SymbolIdentity` static class with: `NormalizePath(repoRoot, absolutePath)` → repo-relative forward-slash path; `CanonicalSymbolId(language, namespace, typeName, memberName?, arity?, paramTypes?)` → language-aware stable ID using Roslyn doc-comment ID format for C#; fallback `path:line` for unresolved symbols. Add `FirstSeenCommit` / `LastSeenCommit` validity window fields on ExpertiseRecord. Update EpisodeRecorder to normalize paths. Tests: overloads, generics, nested types, constructors, partials, drive letters, worktrees, UNC, case sensitivity.

  `expectedFileChanges`: 8

- **Step 2.1.2**: Graph coexistence contract

  Document and implement the rules for memory vs code graph coexistence: memory uses `:MemoryEpisode` / `:MemoryExpertise` labels (separate from `:CodeNode`); cross-graph references use foreign-key string IDs not native relationships; code graph clear/reseed does not delete memory nodes; memory edges to deleted code nodes are flagged as orphaned. Add `IGraphStoreAdapter.IsCodeNodeAlive(symbolId)` for orphan detection. Tests: clear code graph → memory intact; orphan detection.

  `expectedFileChanges`: 6

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware with write gating

  Hook `IEpisodeRecorder` into agent orchestrator lifecycle (start/complete). Classify `TouchType` from tool name. **Write gating**: deduplicate near-identical episodes (same agent + same files within 5 min), require minimum evidence count (≥1 observation), suppress trivial read-only episodes, require non-InProgress outcome. Tests: lifecycle, dedup, suppression.

  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files

  Resolve touched file paths → `HypergraphNode` IDs via `IGraphStoreAdapter`. Graceful degraded mode (file-only episodes when graph unavailable). Tests: with/without graph store.

  `expectedFileChanges`: 5

### Stage 2.3 — Context Injection & Branch Scoping

- **Step 2.3.1**: Branch-aware tier-ordered retrieval

  Query `ITieredExpertiseStore` by tier: Insight → Query → Interaction (top-down). Token budget per tier. **Branch scoping**: prefer same-branch memory (1.0x), penalize cross-branch (0.5x), add branch-mismatch freshness warnings. Format `ContextWindow` as structured markdown with tier section labels. Tests: same-branch preference, cross-branch penalty, budget enforcement.

  `expectedFileChanges`: 8

- **Step 2.3.2**: Provenance model

  `Provenance` enum (`AgentObserved`, `StaticAnalysis`, `GitSeeded`, `Consolidated`, `Promoted`) on `MemoryEdge`, `CallChain`, `ExpertiseRecord` (init-only). Retrieval scoring weights by provenance. Tests: provenance on all creation paths.

  `expectedFileChanges`: 6

- **Step 2.3.3**: Minimal retrieval evaluation harness

  Golden-replay test suite: 5–10 curated queries with expected retrieval results. Measures precision@k, recall, and tier distribution. Runs as integration test. Provides baseline for judging Phase 3/4 improvements. Tests: golden queries pass at threshold.

  `expectedFileChanges`: 5

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL, migration, and DI

  Cypher DDL for `:MemoryEpisode`, `:MemoryExpertise` labels with tier/category/confidence indexes. Idempotent migration. Label-scoped constraints separate from `:CodeNode`. DI registration with `AgentMemoryOptions.EnableNeo4j`. Health check. Docker-compose update.

  `expectedFileChanges`: 10

- **Step 3.1.2**: Episode CRUD in Neo4j

  `Neo4jMemoryStore` episode methods: create, get, query by filter, update, delete. Deserialized copies (no live references). Testcontainers integration tests.

  `expectedFileChanges`: 8

- **Step 3.1.3**: Expertise CRUD in Neo4j

  Expertise upsert with `TierPolicy.Normalize` + `Validate`. Tier-aware queries. Testcontainers integration tests.

  `expectedFileChanges`: 8

- **Step 3.1.4**: Edges, call chains, and statistics

  Memory edge relationships, Cypher traversals, `ICallChainStore` implementation, tier distribution queries. Statistics aggregation. Integration tests.

  `expectedFileChanges`: 10

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution and project hierarchy seeding

  Walk `HypergraphData.Metadata.Projects` to create `ProjectExpertise` records at Query tier via `TierPolicy.DefaultTierFor`. `ContainedIn` edges for project→solution. Idempotent (skip if existing record has higher confidence). Tests: idempotent reseeding.

  `expectedFileChanges`: 7

- **Step 3.2.2**: Namespace and type surface seeding

  `NamespaceExpertise` (Query), `TypeExpertise` (Interaction), `MethodExpertise` (Interaction) from `HypergraphNode` data. `ContainedIn` edges for type→namespace→project. Tests: hierarchy integrity.

  `expectedFileChanges`: 8

- **Step 3.2.3**: Rename detection and rebinding

  Detect renamed symbols by comparing old symbol IDs against new extraction. Repoint memory edges and expertise records to new IDs. Mark old IDs as aliases. Tests: rename → edges rebound, old ID lookup → redirected.

  `expectedFileChanges`: 7

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor

  DFS from entry points through `Hyperedge` with `MethodInvocation` type. `CallChain` with `Provenance.StaticAnalysis`. Max depth 8, cycle detection. `CallChainPattern` expertise at Query tier. Tests: cycles, depth limit, entry point detection.

  `expectedFileChanges`: 8

- **Step 3.3.2**: Git co-change seeder

  Parse `git log --name-only` for co-modified file pairs. Jaccard similarity with minimum count (≥3) and threshold (≥0.3) gates. `CoChangedWith` edges with `Provenance.GitSeeded`. SHA watermark for incremental runs. Tests: Jaccard calculation, watermark skip.

  `expectedFileChanges`: 7

---

## Phase 4 — Feedback, Evolution & Observability

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement

  On episode completion, reinforce expertise for touched symbols: boost confidence for successful outcomes, reduce for failures. Immutable updates via copy construction through store upsert → TierPolicy. Cap reinforcement at 1.0. Tests: success boost, failure penalty, cap.

  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation

  Compare expertise `CommitSha` / `LastSeenCommit` against current HEAD. Mark stale expertise (file changed since last validation). Add freshness warnings to `ContextWindow`. Automatic revalidation on access when graph store available. Tests: stale detection, revalidation flow.

  `expectedFileChanges`: 6

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver background service

  `MemoryEvolverService : IHostedService` with configurable interval. `SemaphoreSlim` for single-writer serialization. High-water marks for incremental consolidation. Checkpoint to resume after restart. Tests: serialization, incremental run.

  `expectedFileChanges`: 7

### Stage 4.3 — Vector Integration & Observability

- **Step 4.3.1**: Vector memory adapter

  `IVectorMemoryAdapter` bridging to `Ast.VectorDb.IVectorStore`. Qdrant collections for episode summaries and expertise summaries. Semantic search integrated into `ContextBuilder` candidate generation. `AgentMemoryOptions.EnableQdrant` flag. Tests: semantic candidate retrieval.

  `expectedFileChanges`: 9

- **Step 4.3.2**: Metrics and health dashboard

  `IMemoryMetrics` interface: episode rate, consolidation rate, stale-memory percentage, retrieval latency, tier distribution. Golden-replay regression suite (extends Phase 2 harness). Health check endpoint aggregation. Tests: metric collection, regression thresholds.

  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query and record tools

  Six MCP tool definitions: `memory_search` (query expertise by subject/category/tier), `memory_context` (build context window for task), `memory_stats` (store statistics), `memory_record_observation` (manual evidence recording), `memory_consolidate` (trigger consolidation), `memory_decay` (trigger decay). JSON schema, handler implementations, integration with `IAgentMemoryStore` and `IContextBuilder`. Tests: tool invocation round-trips.

  `expectedFileChanges`: 10

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect and management commands

  Subcommands: `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`, `memory consolidate`, `memory decay`, `memory seed`, `memory reindex`, `memory clear`. Spectre.Console table/tree output. Confirmation prompts for destructive operations. Tests: command parsing, output formatting.

  `expectedFileChanges`: 10

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until Phase 4 metrics show retrieval precision is insufficient.
- **Full temporal knowledge graph** — `ValidFrom`/`ValidTo` on edges deferred. `CommitSha` + `LastValidated` + validity windows suffice.
- **LLM-generated community summaries** — Requires LLM API budget not yet allocated.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights need reward signal infrastructure.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Beyond current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Context windows fit within current limits.
- **Fine-tuning or model adaptation** — Memory is retrieval-time only.
- **`record class` for ExpertiseRecord** — Equality semantic change too risky for existing `HashSet`/`Dictionary` usage.
- **Optimistic concurrency / version fields** — Deferred to Phase 4 background service.
- **Hardening Episode/MemoryEdge/CallChain immutability** — Lower risk; deferred until after ExpertiseRecord is stable.
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, hardcodes `:CodeNode` label.