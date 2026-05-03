# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library provides the domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver. What remains is: robust symbol identity, integration with the agent orchestrator, persistent graph/vector backends, knowledge extraction from repo structure and call chains, a feedback loop with observability, and CLI/MCP surfaces.

### Key Constraints

**Persistence boundary**: `Ast.GraphDb.IGraphStore` stores `HypergraphNode`/`Hyperedge` as `:CodeNode` labels. Memory requires a separate `IMemoryGraphStore` with distinct labels (`:MemoryEpisode`, `:MemoryExpertise`) and domain-specific Cypher queries. Memory nodes reference code nodes via cross-label relationships.

**Symbol identity**: Episode recording, context building, and extraction all join on file paths and symbol IDs. Identifiers must be canonical and stable across sessions, handling renames, worktrees, and branch drift. Each symbol carries a validity window (`firstSeenCommit`, `lastSeenCommit`).

**Provenance**: Memory records originate from three sources — git history (ground truth), static analysis (inferred), and agent episodes (observed) — each weighted differently in retrieval.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural views with multi-signal reranking in the ContextBuilder.
2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight tiers via `MemoryTier`, with promotion pipeline.
3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via `CommitSha` on expertise records; git-seeded co-change grounds temporal edges.
4. **Mem0 consolidation** (arXiv:2504.19413) — diversity-gated episode aggregation preventing false expertise from loops.
5. **ExpeL distillation** (AAAI'24) — verbal reflection without fine-tuning; failure patterns from repeated failures.

| Decision | Rationale |
|----------|-----------|
| Early vertical slice with eval harness | Validate record → retrieve → measure loop before investing in Neo4j/Qdrant |
| Separate `IMemoryGraphStore` | Code-graph store lacks memory CRUD, drops metadata, hardcodes `:CodeNode` |
| Canonical identity with validity windows | Handles renames, partial classes, branch drift across sessions |
| Provenance on all records | Retrieval weights `git-seeded` > `inferred` > `observed` differently |
| Retrieval contract before injection | Token budget, confidence thresholds, max items per source defined upfront |
| Write model with source-of-truth rules | Prevents dual-write drift between Neo4j, Qdrant, and filesystem |
| Extraction with invalidation | Each seeder includes incremental refresh for renamed/removed/changed entities |

---

## Phase 1 — Identity, Integration & Baseline Metrics

Deliver a thin vertical slice: canonical identity, orchestrator hooks, context injection, and a minimal evaluation harness. Validates the memory loop end-to-end using the existing file-system store.

### Stage 1.1 — Identity & Configuration

- **Step 1.1.1**: Canonical symbol identity resolver — `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath)` and `CanonicalSymbolId(namespace, typeName, memberName)`. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`). Tests for drive letters, worktrees, renames. Update `EpisodeRecorder` to use canonical paths.
  `expectedFileChanges`: 7

- **Step 1.1.2**: Feature flags and memory configuration — `AgentMemoryOptions` with `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`. Wire into `ServiceCollectionExtensions`. `IMemoryStoreHealthCheck`. Tests for store selection.
  `expectedFileChanges`: 5

### Stage 1.2 — Orchestrator Episode Recording

- **Step 1.2.1**: Episode recording middleware — Hook `IEpisodeRecorder` into `CopilotAgentOrchestrator`. Auto-start/complete episodes on task lifecycle. Track touched files from tool calls (view, edit, create). Classify `TouchType` from tool name.
  `expectedFileChanges`: 8

- **Step 1.2.2**: Symbol resolution for touched files — Resolve touched files to `HypergraphNode` IDs via `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback for file-only episodes when graph unavailable.
  `expectedFileChanges`: 5

### Stage 1.3 — Context Injection & Retrieval Contract

- **Step 1.3.1**: Retrieval contract and context injection — Define retrieval contract (token budget, max items per source, confidence threshold, low-confidence behavior). Add `IContextBuilder.BuildContextAsync()` before task dispatch. Format `ContextWindow` as markdown sections. Per-source attribution in prompt.
  `expectedFileChanges`: 8

- **Step 1.3.2**: Provenance model — Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`, `CallChain`, `ExpertiseRecord`. Source-of-truth rules per entity type. Tests verify provenance is set.
  `expectedFileChanges`: 6

### Stage 1.4 — Baseline Evaluation Harness

- **Step 1.4.1**: Context hit/miss logging and replay scenarios — `IMemoryMetrics` with context hit rate, retrieval latency counters. Structured logging for hit/miss events. 3-5 golden replay scenarios as test fixtures that verify context assembly against known expectations.
  `expectedFileChanges`: 7

---

## Phase 2 — Persistent Storage Backends

Connect to Neo4j and Qdrant for durable memory. Includes write-model consistency rules to prevent dual-write drift.

### Stage 2.1 — Neo4j Memory Graph Store

- **Step 2.1.1**: Schema DDL and migration — Cypher for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`), relationships (`:TOUCHED_FILE`, `:TOUCHED_SYMBOL`, `:PRODUCED_EXPERTISE`, `:CO_CHANGED_WITH`), indices, constraints. Idempotent `MemorySchemaMigration.ApplyAsync()`.
  `expectedFileChanges`: 7

- **Step 2.1.2**: Episode and expertise CRUD — `Neo4jMemoryStore` implementing `IAgentMemoryStore` and `ITieredExpertiseStore`. Cypher for CRUD operations. Integration tests with Testcontainers.
  `expectedFileChanges`: 12

- **Step 2.1.3**: Edges, call chains, and statistics — `AddMemoryEdgeAsync`, `GetOutgoingEdgesAsync`/`GetIncomingEdgesAsync` via Cypher traversals. `ICallChainStore` with ordered list serialization. `GetStatisticsAsync`. Integration tests.
  `expectedFileChanges`: 10

- **Step 2.1.4**: DI registration and health checks — `services.AddNeo4jAgentMemory(connectionString)`. Health checks, configuration binding, docker-compose entry.
  `expectedFileChanges`: 5

### Stage 2.2 — Vector Memory Integration

- **Step 2.2.1**: Vector memory adapter — `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`, `SearchSimilarAsync`. Qdrant collections: `agent_memory_episodes`, `agent_memory_expertise`. Tests with mock.
  `expectedFileChanges`: 8

- **Step 2.2.2**: Semantic retrieval in ContextBuilder — Add semantic search as candidate generation step. Graceful fallback when vector store unavailable. Reranking weight for semantic candidates.
  `expectedFileChanges`: 5

### Stage 2.3 — Write Model & Index Consistency

- **Step 2.3.1**: Composite store and write-model rules — `CompositeAgentMemoryStore` with source-of-truth rules per entity. Idempotent upsert keys. Outbox-style dispatch to Neo4j + Qdrant. Duplicate detection across seeded/inferred/observed.
  `expectedFileChanges`: 10

- **Step 2.3.2**: Backfill job and reindex command — `MemoryBackfillJob` from filesystem to Neo4j + Qdrant. `memory reindex` command. Log-and-continue on backend failures.
  `expectedFileChanges`: 6

---

## Phase 3 — Code Graph Memory Extraction

Build the initial knowledge graph from top-down repo structure, call chains, and git history. Each extractor includes invalidation rules for incremental refresh.

### Stage 3.1 — Structural Memory Seeder

- **Step 3.1.1**: Solution and project hierarchy — Walk `HypergraphData.Nodes` grouped by project. Create `ProjectExpertise` (Query tier) with dependency summaries. `ContainedIn` edges for solution → project → namespace. Include folder/module boundaries and assembly references. Invalidation: detect removed/added projects.
  `expectedFileChanges`: 8

- **Step 3.1.2**: Namespace and type seeding — `NamespaceExpertise` (Query tier), `TypeExpertise` (Interaction tier). Detect public API surface. `ContainedIn` edges namespace → type. Invalidation: detect renamed/moved types.
  `expectedFileChanges`: 6

- **Step 3.1.3**: Method and API surface seeding — `MethodExpertise` for public/internal methods. Detect entry points and API endpoints from `HyperedgeType.ApiEndpoint`, `EntryPoint`. Invalidation: signature changes.
  `expectedFileChanges`: 6

### Stage 3.2 — Call Chain Extraction

- **Step 3.2.1**: Static call chain extractor — Walk `MethodInvocation` edges depth-first from entry points. Build `CallChain` with `CallChainSource.Inferred`. Max depth 8, cycle detection. Separate static invocation edges from DI resolution edges. Confidence-scored, partial graphs.
  `expectedFileChanges`: 8

- **Step 3.2.2**: DI registration chain extraction — Follow `ServiceRegistration` edges. Create `CallChainPattern` for controller → service → repository patterns. Handle open generics and MediatR-style pipelines as lower-confidence chains.
  `expectedFileChanges`: 6

### Stage 3.3 — Git Co-Change Seeding

- **Step 3.3.1**: Co-change analyzer with noise gates — Parse `git log --name-only`. Compute Jaccard similarity. Gates: commit size ≤50 files, file-type filters, repeated co-change count ≥3. `CoChangedWith` edges with `Provenance.GitSeeded`. Low default weight (0.3).
  `expectedFileChanges`: 8

- **Step 3.3.2**: Incremental co-change updates — Last-processed commit SHA watermark. Process only new commits. Reinforce or decay existing co-change edges. Idempotent replay.
  `expectedFileChanges`: 5

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop and schedule memory evolution. Build on the baseline metrics from Phase 1.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement — Reinforce expertise confidence on `Success`, record negative evidence on `Failed`. Update `LastValidated`, `CommitSha`, `AccessCount`. Tests verify confidence changes.
  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation — Compare `CommitSha` against HEAD. Detect renamed/moved symbols via `SymbolIdentity` validity window. Stale expertise gets freshness warnings. Auto-decay on confirmed invalidation.
  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing — High-water mark for last-processed episode, last-decay-run, last-promotion-run. Skip already-processed episodes. Tests verify no reprocessing.
  `expectedFileChanges`: 6

- **Step 4.2.2**: Background hosted service — `IHostedService` running consolidation, decay, promotion on configurable intervals. Lease/lock for concurrency. Log evolution statistics.
  `expectedFileChanges`: 5

### Stage 4.3 — Extended Observability

- **Step 4.3.1**: Full retrieval metrics — Extend Phase 1 metrics with stale-memory rate, tier distribution, per-provenance hit rates, context token budget usage. Structured event logging.
  `expectedFileChanges`: 5

- **Step 4.3.2**: Memory health dashboard — `memory health` command: hit rate, stale rate, tier distribution, top expertise. A/B feature flag for with-memory vs without-memory via prompt injection toggle.
  `expectedFileChanges`: 6

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query tools — `memory_search` (find expertise), `memory_context` (build context window), `memory_stats` (statistics). Register as MCP tool handlers. Tests with mock store.
  `expectedFileChanges`: 10

- **Step 5.1.2**: MCP record and evolve tools — `memory_record_observation`, `memory_consolidate`, `memory_decay`. Tests with mock store.
  `expectedFileChanges`: 6

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect commands — `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`. Table-formatted output with confidence bars and tier indicators via Spectre.Console.
  `expectedFileChanges`: 10

- **Step 5.2.2**: CLI management commands — `memory consolidate`, `memory decay`, `memory seed`, `memory reindex`, `memory clear`. Progress bars.
  `expectedFileChanges`: 7

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until Phase 4.3 metrics show simple graph + vector retrieval is insufficient.
- **Full temporal knowledge graph** (arXiv:2501.13956) — `ValidFrom`/`ValidTo` windows on edges. Current `CommitSha` + `LastValidated` suffices.
- **LLM-generated community summaries** — Requires LLM API budget not yet established.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights need reward signal infrastructure.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Beyond current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Context windows fit within limits.
- **Fine-tuning or model adaptation** — Memory is retrieved at inference time.
- **Academic dataset benchmarking** (LoCoMo, LongMemEval) — Not actionable for the product.
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, drops metadata, hardcodes `:CodeNode`.