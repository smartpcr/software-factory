# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 2)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library already provides the domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver.

**Iteration 1 gaps identified by review:**
1. **Tier enforcement not reliable** — `ExpertiseRecord.Tier` always defaults to `MemoryTier.Interaction` regardless of category. `TierMapping.DefaultTierFor()` and `IsValidCombination()` exist but are never called on creation or upsert. Records for `FailurePattern`, `PatternKnowledge`, `NamespaceExpertise`, and `CoChangeGroup` can be stored at incorrect tiers.
2. **No migration for existing invalid records** — Once enforcement begins, persisted records with wrong tiers would fail on load/re-save.
3. **Promotion rules underspecified** — Whether promotion may keep the same category or must transform it is not formalized.

**Remaining work:** robust symbol identity, tier enforcement as a centralized policy, orchestrator integration, persistent graph/vector backends, knowledge extraction from repo structure and call chains, a feedback loop with observability, and CLI/MCP surfaces.

### Key Constraints

- **Persistence boundary**: `Ast.GraphDb.IGraphStore` stores nodes as `:CodeNode` — memory needs separate labels and domain-specific queries. Memory nodes reference code nodes via cross-label relationships.
- **Symbol identity**: Identifiers must be canonical and stable across sessions, handling renames, worktrees, and branch drift. Each symbol carries a validity window (`FirstSeenCommit`, `LastSeenCommit`).
- **Provenance**: Records originate from git history (ground truth), static analysis (inferred), and agent episodes (observed) — each weighted differently in retrieval.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural views; policy-guided traversal for query-adaptive retrieval and multi-signal reranking in ContextBuilder.
2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight tiers with centralized tier enforcement policy and promotion pipeline.
3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via `CommitSha`; git-seeded co-change grounds temporal edges.
4. **Mem0 consolidation** (arXiv:2504.19413) — diversity-gated episode aggregation preventing false expertise.
5. **ExpeL distillation** (AAAI'24) — failure patterns from repeated failures; verbal reflection without fine-tuning.

| Decision | Rationale |
|----------|-----------|
| **Centralized `TierPolicy` normalize-and-validate** | Store-side enforcement as source of truth; model defaults are convenience only. All stores, seeders, and evolver paths call the same policy. Exhaustive category mapping fails fast for unknown categories. |
| **Migration step for legacy records** | Existing records at incorrect tiers are auto-repaired on load via `TierPolicy.Normalize()`. Tests cover upgrade from iteration-1 data. |
| **Promotion transforms both category and tier** | `FileExpertise → NamespaceExpertise` on Interaction→Query; `NamespaceExpertise → ArchitecturalKnowledge` on Query→Insight. Same-category promotion is disallowed to keep tier semantics clean. |
| Early vertical slice with eval harness | Validate record → retrieve → measure loop before investing in Neo4j/Qdrant |
| Separate `IMemoryGraphStore` | Code-graph store lacks memory CRUD, drops metadata, hardcodes `:CodeNode` |
| Canonical identity with validity windows | Handles renames, partial classes, branch drift across sessions |
| Provenance on all records | Retrieval weights `git-seeded` > `inferred` > `observed` differently |

---

## Phase 1 — Identity, Tier Enforcement & Baseline

Deliver a thin vertical slice: canonical identity, centralized tier enforcement, orchestrator hooks, context injection, and a minimal evaluation harness. Validates the memory loop end-to-end using the existing file-system store.

### Stage 1.1 — Identity, Tier Policy & Configuration

- **Step 1.1.1**: Canonical symbol identity resolver — `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath)` and `CanonicalSymbolId(namespace, typeName, memberName)`. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`). Tests for drive letters, worktrees, renames. Update `EpisodeRecorder` to use canonical paths.
  `expectedFileChanges`: 7

- **Step 1.1.2**: Centralized tier enforcement policy — Create `TierPolicy` static class with `Normalize(ExpertiseRecord)` (sets `Tier` from `TierMapping.DefaultTierFor(Category)` when tier equals default `Interaction` and category maps elsewhere) and `Validate(ExpertiseRecord)` (throws `InvalidOperationException` for demotions; rejects unknown categories with exhaustive switch instead of wildcard fallback). Update `InMemoryAgentMemoryStore.UpsertExpertiseAsync()` and `FileSystemAgentMemoryStore` to call `TierPolicy.Normalize` then `TierPolicy.Validate` before persisting. Add migration scan on startup: iterate all persisted records, apply `TierPolicy.Normalize`, persist corrected records. Tests: each `ExpertiseCategory` gets correct default tier; demotion is rejected; explicit valid higher tier is accepted; omitted tier is auto-assigned; legacy records at wrong tier are repaired.
  `expectedFileChanges`: 8

- **Step 1.1.3**: Feature flags and memory configuration — `AgentMemoryOptions` with `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`. Wire into `ServiceCollectionExtensions`. `IMemoryStoreHealthCheck`. Tests for store selection.
  `expectedFileChanges`: 5

### Stage 1.2 — Orchestrator Episode Recording

- **Step 1.2.1**: Episode recording middleware — Hook `IEpisodeRecorder` into `CopilotAgentOrchestrator`. Auto-start/complete episodes on task lifecycle. Track touched files from tool calls (view, edit, create). Classify `TouchType` from tool name (`view` → `Read`, `edit` → `Modified`, `create` → `Created`). Persist `TouchType` on `MemoryEdge` via `EpisodeBuilder.CreateEdgesAsync()`.
  `expectedFileChanges`: 8

- **Step 1.2.2**: Symbol resolution for touched files — Resolve touched files to `HypergraphNode` IDs via `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback for file-only episodes when graph unavailable.
  `expectedFileChanges`: 5

### Stage 1.3 — Context Injection & Retrieval Contract

- **Step 1.3.1**: Retrieval contract and context injection — Define retrieval contract (token budget, max items per source, confidence threshold, low-confidence behavior). ContextBuilder uses `ITieredExpertiseStore` to query by tier: Insight records first (architectural context), then Query-tier (namespace/pattern), then Interaction-tier (file-level). Add `IContextBuilder.BuildContextAsync()` before task dispatch. Format `ContextWindow` as markdown sections with per-source attribution.
  `expectedFileChanges`: 8

- **Step 1.3.2**: Provenance model — Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`, `CallChain`, `ExpertiseRecord`. Source-of-truth rules per entity type. Tests verify provenance is set.
  `expectedFileChanges`: 6

### Stage 1.4 — Baseline Evaluation Harness

- **Step 1.4.1**: Context hit/miss logging and replay scenarios — `IMemoryMetrics` with context hit rate, retrieval latency counters, invalid-tier-encountered counter, auto-normalized-records counter. Structured logging for hit/miss events. 3-5 golden replay scenarios as test fixtures that verify context assembly against known expectations. Tier distribution by category in metrics output.
  `expectedFileChanges`: 7

---

## Phase 2 — Persistent Storage Backends

Connect to Neo4j and Qdrant for durable memory. All store implementations call `TierPolicy.Normalize` + `TierPolicy.Validate` on upsert. Includes write-model consistency rules.

### Stage 2.1 — Neo4j Memory Graph Store

- **Step 2.1.1**: Schema DDL and migration — Cypher for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`), relationships (`:TOUCHED_FILE`, `:TOUCHED_SYMBOL`, `:PRODUCED_EXPERTISE`, `:CO_CHANGED_WITH`), indices, constraints. Idempotent `MemorySchemaMigration.ApplyAsync()`. Include tier property on `:MemoryExpertise` nodes.
  `expectedFileChanges`: 7

- **Step 2.1.2**: Episode and expertise CRUD — `Neo4jMemoryStore` implementing `IAgentMemoryStore` and `ITieredExpertiseStore`. Cypher for CRUD operations. `UpsertExpertiseAsync` calls `TierPolicy.Normalize` + `Validate`. Integration tests with Testcontainers.
  `expectedFileChanges`: 12

- **Step 2.1.3**: Edges, call chains, and statistics — `AddMemoryEdgeAsync` with `TouchType` property on relationship, `GetOutgoingEdgesAsync`/`GetIncomingEdgesAsync` via Cypher traversals. `ICallChainStore` with ordered list serialization. `GetStatisticsAsync` with tier distribution. Integration tests.
  `expectedFileChanges`: 10

- **Step 2.1.4**: DI registration and health checks — `services.AddNeo4jAgentMemory(connectionString)`. Health checks, configuration binding, docker-compose entry.
  `expectedFileChanges`: 5

### Stage 2.2 — Vector Memory Integration

- **Step 2.2.1**: Vector memory adapter — `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`, `SearchSimilarAsync`. Qdrant collections: `agent_memory_episodes`, `agent_memory_expertise`. Tests with mock.
  `expectedFileChanges`: 8

- **Step 2.2.2**: Semantic retrieval in ContextBuilder — Add semantic search as candidate generation step. Graceful fallback when vector store unavailable. Reranking weight for semantic candidates.
  `expectedFileChanges`: 5

### Stage 2.3 — Write Model & Index Consistency

- **Step 2.3.1**: Composite store and write-model rules — `CompositeAgentMemoryStore` with source-of-truth rules per entity. Calls `TierPolicy` before dispatch. Idempotent upsert keys. Outbox-style dispatch to Neo4j + Qdrant. Duplicate detection across seeded/inferred/observed.
  `expectedFileChanges`: 10

- **Step 2.3.2**: Backfill job and reindex command — `MemoryBackfillJob` from filesystem to Neo4j + Qdrant. Applies `TierPolicy.Normalize` to repair legacy records during migration. `memory reindex` command. Log-and-continue on backend failures.
  `expectedFileChanges`: 6

---

## Phase 3 — Code Graph Memory Extraction

Build the initial knowledge graph from top-down repo structure, call chains, and git history. Each seeder uses `TierPolicy.Normalize` when creating records (ensuring category→tier mapping is correct). Each seeder includes invalidation rules for incremental refresh.

### Stage 3.1 — Structural Memory Seeder

- **Step 3.1.1**: Solution and project hierarchy — Walk `HypergraphData.Nodes` grouped by project. Create `ProjectExpertise` records (auto-assigned Query tier via `TierPolicy`). `ContainedIn` edges for solution → project → namespace. Include folder/module boundaries and assembly references. Invalidation: detect removed/added projects.
  `expectedFileChanges`: 8

- **Step 3.1.2**: Namespace and type seeding — `NamespaceExpertise` (auto-assigned Query tier), `TypeExpertise` (auto-assigned Interaction tier). Detect public API surface. `ContainedIn` edges namespace → type. Invalidation: detect renamed/moved types.
  `expectedFileChanges`: 6

- **Step 3.1.3**: Method and API surface seeding — `MethodExpertise` (auto-assigned Interaction tier) for public/internal methods. Detect entry points and API endpoints from `HyperedgeType.ApiEndpoint`, `EntryPoint`. Invalidation: signature changes.
  `expectedFileChanges`: 6

### Stage 3.2 — Call Chain Extraction

- **Step 3.2.1**: Static call chain extractor — Walk `MethodInvocation` edges depth-first from entry points. Build `CallChain` with `CallChainSource.Inferred`. Max depth 8, cycle detection. Separate static invocation edges from DI resolution edges. Confidence-scored, partial graphs.
  `expectedFileChanges`: 8

- **Step 3.2.2**: DI registration chain extraction — Follow `ServiceRegistration` edges. Create `CallChainPattern` expertise (auto-assigned Query tier). Handle open generics and MediatR-style pipelines as lower-confidence chains.
  `expectedFileChanges`: 6

### Stage 3.3 — Git Co-Change Seeding

- **Step 3.3.1**: Co-change analyzer with noise gates — Parse `git log --name-only`. Compute Jaccard similarity. Gates: commit size ≤50 files, file-type filters, repeated co-change count ≥3. `CoChangedWith` edges with `Provenance.GitSeeded`. TouchType filtering: only modification-type commits (not read-only). Low default weight (0.3).
  `expectedFileChanges`: 8

- **Step 3.3.2**: Incremental co-change updates — Last-processed commit SHA watermark. Process only new commits. Reinforce or decay existing co-change edges. Idempotent replay.
  `expectedFileChanges`: 5

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop and schedule memory evolution. Promotion pipeline creates records at correct target tier with mandatory category transformation (FileExpertise→NamespaceExpertise on Interaction→Query, NamespaceExpertise→ArchitecturalKnowledge on Query→Insight). All created records pass through `TierPolicy`.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement — Reinforce expertise confidence on `Success`, record negative evidence on `Failed`. Update `LastValidated`, `CommitSha`, `AccessCount`. Tests verify confidence changes.
  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation — Compare `CommitSha` against HEAD. Detect renamed/moved symbols via `SymbolIdentity` validity window. Stale expertise gets freshness warnings. Auto-decay on confirmed invalidation.
  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing — High-water mark for last-processed episode, last-decay-run, last-promotion-run. Skip already-processed episodes. Tests verify no reprocessing.
  `expectedFileChanges`: 6

- **Step 4.2.2**: Background hosted service — `IHostedService` running consolidation, decay, promotion on configurable intervals. Lease/lock for concurrency. Log evolution statistics including tier distribution changes.
  `expectedFileChanges`: 5

### Stage 4.3 — Extended Observability

- **Step 4.3.1**: Full retrieval metrics — Extend Phase 1 metrics with stale-memory rate, tier distribution by category, per-provenance hit rates, context token budget usage, auto-normalized record count, invalid-tier rejection count. Structured event logging.
  `expectedFileChanges`: 5

- **Step 4.3.2**: Memory health dashboard — `memory health` command: hit rate, stale rate, tier distribution, promotion count by category pair, top expertise. A/B feature flag for with-memory vs without-memory via prompt injection toggle.
  `expectedFileChanges`: 6

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query tools — `memory_search` (find expertise by tier/category/subject), `memory_context` (build context window), `memory_stats` (statistics with tier distribution). Register as MCP tool handlers. Tests with mock store.
  `expectedFileChanges`: 10

- **Step 5.1.2**: MCP record and evolve tools — `memory_record_observation`, `memory_consolidate`, `memory_decay`. Tests with mock store.
  `expectedFileChanges`: 6

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect commands — `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`. Table-formatted output with confidence bars, tier indicators, and category→tier validation status via Spectre.Console.
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
- **Same-category tier promotion** — Promotion must transform category to maintain semantic clarity (e.g., `FileExpertise` cannot be promoted to `Insight` without becoming `ArchitecturalKnowledge`).