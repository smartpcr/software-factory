# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory  
> **Status**: Planning  
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents working on this codebase start every session blank — they re-discover repo structure,
re-trace call chains, and re-learn conventions each time. The `AgentMemory` library already
provides the core domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage
backends (in-memory and file-system JSON), a staged ContextBuilder, and a MemoryEvolver with
consolidation, decay, and tier promotion. What remains is connecting this library to the
existing graph infrastructure (Neo4j, Qdrant), building the initial knowledge graph from
top-down repo structure and call chains, wiring it into agent workflows, and exposing it
through CLI and MCP surfaces.

### Persistence Boundary — Key Constraint

The existing `Ast.GraphDb` (`IGraphStore`, `Neo4jGraphStore`) is code-graph-centric: it stores
`HypergraphNode`/`Hyperedge` as `:CodeNode` labels and materializes code-specific relationships.
It does **not** support arbitrary domain CRUD for memory entities. The memory persistence layer
requires its own `IMemoryGraphStore` abstraction with distinct Neo4j labels (`:MemoryEpisode`,
`:MemoryExpertise`, `:MemoryCallChain`) and Cypher queries purpose-built for memory lifecycle
operations. Memory nodes reference code nodes via cross-label relationships, but the stores
are architecturally separate.

### Symbol Identity — Key Constraint

Episode recording, context building, structural seeding, and call chain extraction all join
on file paths and symbol IDs. These identifiers must be **canonical and stable** across sessions.
The plan includes an explicit identity resolver step that normalizes paths (forward-slash,
repo-relative) and generates deterministic symbol IDs from the hypergraph before any
cross-system joins are attempted.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** architecture, combining three paradigms
from the research survey:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural
   views layered on a single graph store. The existing ContextBuilder already implements the
   multi-signal candidate scoring pipeline; we extend it with graph-native traversals.

2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight tiers
   already modeled via `MemoryTier`. We complete the tier promotion pipeline with
   graph-backed community detection for automatic scope aggregation.

3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via CommitSha on
   expertise records. We add git-log-seeded co-change to ground temporal edges in real
   version history rather than agent-only observations.

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Dedicated `IMemoryGraphStore` (not reusing `IGraphStore`) | `IGraphStore` stores `HypergraphNode`/`Hyperedge` as `:CodeNode` and drops `Metadata`; memory entities need domain-specific CRUD with different labels and property schemas |
| Separate `:MemoryEpisode`, `:MemoryExpertise` labels | Avoids coupling to `:CodeNode` schema; memory has different lifecycle and different indices |
| Canonical symbol identity resolver | File paths normalized to repo-relative forward-slash form; symbol IDs generated deterministically from fully-qualified names so cross-store joins are reliable |
| Qdrant for semantic episode search | Existing `Ast.VectorDb` provides embeddings; episode summaries need semantic retrieval beyond exact matches |
| File-system store remains for offline/test | Not all environments have Neo4j; graceful degradation preserved with feature flags |
| Git-seeded co-change before agent-derived | Git history is ground truth; agent observations refine it |
| MCP tools before CLI commands | Agents consume memory programmatically; CLI is for humans inspecting state |
| Evolver checkpointing before scheduled runs | Consolidation/decay must be idempotent with high-water marks to prevent reprocessing |

---

## Phase 1 — Foundations & Persistence

Establish the memory-specific persistence boundary, canonical identity resolver, and
feature flags for graceful degradation.

### Stage 1.1 — Identity & Configuration

Before any graph integration, define canonical identity rules and feature-flag infrastructure
so memory can be enabled/disabled per environment.

- **Step 1.1.1**: Canonical symbol identity resolver  
  Create `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath) → string`
  and `CanonicalSymbolId(namespace, typeName, memberName) → string`. Normalize all paths to
  repo-relative forward-slash form. Add unit tests for edge cases (drive letters, nested
  worktrees, symlinks). Update `EpisodeRecorder` to use canonical paths.  
  `expectedFileChanges`: 6

- **Step 1.1.2**: Feature flags and memory configuration  
  Create `AgentMemoryOptions` with flags: `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`.
  Wire into `ServiceCollectionExtensions` so the store selected depends on environment. Add
  health check interfaces (`IMemoryStoreHealthCheck`). Tests verify correct store selection.  
  `expectedFileChanges`: 5

### Stage 1.2 — Neo4j Memory Graph Store

Implement a dedicated `IMemoryGraphStore` with Neo4j backend. This is architecturally separate
from `Ast.GraphDb.IGraphStore` — distinct labels, distinct Cypher queries, distinct DI registration.

- **Step 1.2.1**: Neo4j memory schema DDL and migration  
  Define Cypher DDL for node labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`),
  relationship types (`:TOUCHED_FILE`, `:TOUCHED_SYMBOL`, `:PRODUCED_EXPERTISE`,
  `:CO_CHANGED_WITH`, `:PROMOTED_TO`), composite indices, and uniqueness constraints.
  Create `MemorySchemaMigration` class with idempotent `ApplyAsync()`. Integration tests.  
  `expectedFileChanges`: 7

- **Step 1.2.2**: Neo4jMemoryStore — episode and expertise CRUD  
  Implement `IAgentMemoryStore` and `ITieredExpertiseStore` against Neo4j. Cypher for
  `RecordEpisodeAsync`, `GetEpisodesAsync` (with filter translation to WHERE clauses),
  `UpsertExpertiseAsync`, `GetExpertiseByCategoryAsync`. Integration tests with Testcontainers.  
  `expectedFileChanges`: 8

- **Step 1.2.3**: Neo4jMemoryStore — edges, call chains, and statistics  
  Implement `AddMemoryEdgeAsync`, `GetOutgoingEdgesAsync`/`GetIncomingEdgesAsync` via Cypher
  traversals. Implement `ICallChainStore` with ordered list serialization for chain steps.
  `GetStatisticsAsync` via aggregate Cypher. Integration tests.  
  `expectedFileChanges`: 7

- **Step 1.2.4**: Neo4j memory store DI registration  
  Add `services.AddNeo4jAgentMemory(connectionString)` extension. Wire health checks.
  Configuration binding from `appsettings.json`. Docker-compose entry for memory-specific
  Neo4j container if separate from code-graph Neo4j.  
  `expectedFileChanges`: 6

### Stage 1.3 — Vector Memory Integration

Embed episode and expertise summaries into Qdrant for semantic retrieval when exact
file/symbol matches are sparse.

- **Step 1.3.1**: Vector memory adapter and embedding pipeline  
  Define `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`,
  `SearchSimilarEpisodesAsync`, `SearchSimilarExpertiseAsync`. Implement against
  existing `IVectorStore` (Qdrant). Collections: `agent_memory_episodes`,
  `agent_memory_expertise`. Tests with mock vector store.  
  `expectedFileChanges`: 7

- **Step 1.3.2**: ContextBuilder semantic retrieval integration  
  Add semantic search as a candidate generation step in `ContextBuilder` — after exact
  matches and graph neighbors, before reranking. Graceful fallback when vector store
  is unavailable (feature flag). Adjust reranking weights for semantic candidates.  
  `expectedFileChanges`: 5

### Stage 1.4 — Backfill & Index Consistency

Ensure multi-store consistency: when episodes/expertise are created in the primary store,
they are indexed in Neo4j and Qdrant. Backfill job for existing data.

- **Step 1.4.1**: Write-through indexing and backfill job  
  Create `CompositeAgentMemoryStore` that wraps primary store and dispatches writes to
  Neo4j + Qdrant index adapters. Create `MemoryBackfillJob` that reads all records from
  file-system store and indexes them. Idempotent (skip already-indexed records).  
  `expectedFileChanges`: 7

- **Step 1.4.2**: Delete/update propagation and reindex command  
  When episodes/expertise are deleted or updated, propagate to Neo4j and Qdrant. Add
  `memory reindex` command (used by CLI/MCP). Tests verify consistency after CRUD cycles.  
  `expectedFileChanges`: 5

---

## Phase 2 — Code Graph Memory Extraction

Build the initial memory graph by walking the existing code hypergraph top-down: repo →
projects → namespaces → types → methods, then extracting call chains and seeding co-change
from git history.

### Stage 2.1 — Structural Memory Seeder

Walk `HypergraphData` to create initial `ExpertiseRecord`s at each tier level: project-level
summaries (Query tier), namespace-level groupings (Query tier), and type/method-level records
(Interaction tier). These are "bootstrapped" expertise with low initial confidence (0.3) that
gain confidence as agents interact with them.

- **Step 2.1.1**: Repo structure walker and project-level expertise seeding  
  Traverse `HypergraphData.Nodes` grouped by project. Create `ProjectExpertise` records
  with dependency summaries (references to other projects). Memory edges: `ContainedIn`
  from namespace → project. Use canonical paths via `SymbolIdentity`. Tests with sample
  hypergraph fixture.  
  `expectedFileChanges`: 6

- **Step 2.1.2**: Namespace and type-level expertise seeding  
  Group nodes by namespace within each project. Create `NamespaceExpertise` (Query tier)
  and `TypeExpertise` (Interaction tier) records. Connect via `ContainedIn` edges.
  Detect public API surface (public types/methods) and mark as higher priority.  
  `expectedFileChanges`: 5

- **Step 2.1.3**: Method-level expertise and API surface seeding  
  Create `MethodExpertise` for public/internal methods. Detect entry points and API
  endpoints (from `HyperedgeType.ApiEndpoint`, `EntryPoint`). Seed `CallChainPattern`
  expertise for discovered patterns. Tests verify tier assignments.  
  `expectedFileChanges`: 6

### Stage 2.2 — Call Chain Extraction

Extract call chains from the existing code hypergraph's `MethodInvocation` edges. Build
ordered n-ary sequences representing common execution paths. Mark these as `Inferred`
(vs. `Observed` from agent episodes).

- **Step 2.2.1**: Call chain extractor from hypergraph edges  
  Walk `HyperedgeType.MethodInvocation` edges depth-first from entry points. Build
  `CallChain` objects with `CallChainSource.Inferred`. Limit chain depth to configurable
  max (default 8). Cycle detection to avoid infinite loops. Uses `SymbolIdentity` for
  canonical IDs. Tests with cyclic and acyclic graph fixtures.  
  `expectedFileChanges`: 6

- **Step 2.2.2**: DI registration chain extraction  
  Follow `ServiceRegistration` edges to build dependency injection chains. Create
  `CallChainPattern` expertise for common DI patterns (e.g., "controller → service →
  repository"). Link call chains to DI expertise via `MemoryEdge`.  
  `expectedFileChanges`: 5

### Stage 2.3 — Git Co-Change Seeding

Seed co-change edges from `git log` rather than waiting for agent episodes. This provides
ground-truth temporal coupling data from day one.

- **Step 2.3.1**: Git log co-change analyzer  
  Parse `git log --name-only` to extract files modified together per commit. Compute Jaccard
  similarity between file pairs. Create `CoChangedWith` edges and `CoChangeGroup` expertise
  for pairs exceeding thresholds. Normalize paths via `SymbolIdentity`.  
  `expectedFileChanges`: 6

- **Step 2.3.2**: Incremental git co-change updates  
  Track last-processed commit SHA as a watermark. On subsequent runs, only process new
  commits. Update existing co-change edges (reinforce or decay). Idempotent replay.
  Tests with repo fixture.  
  `expectedFileChanges`: 5

---

## Phase 3 — Agent Integration

Wire the memory system into the agent orchestrator so episodes are recorded automatically,
context is injected into prompts, and outcomes feed back into expertise.

### Stage 3.1 — Episode Recording Hooks

Instrument the agent orchestrator to automatically start/complete episodes, recording
which files were touched and what the outcome was.

- **Step 3.1.1**: Agent orchestrator episode recording middleware  
  Add `IEpisodeRecorder` hook into `CopilotAgentOrchestrator`. Auto-start episode on task
  begin, auto-complete on task end. Track touched files from tool calls (view, edit, create).
  Use `SymbolIdentity.NormalizePath` for all paths. Classify `TouchType` from tool name.  
  `expectedFileChanges`: 7

- **Step 3.1.2**: Symbol resolution for touched files  
  When an episode records a touched file, resolve it to `HypergraphNode` IDs using the
  existing code graph (via `IGraphStoreAdapter`). Populate `TouchedSymbolIds` for
  graph-aware retrieval. Graceful fallback when graph unavailable (file-only episodes).  
  `expectedFileChanges`: 5

### Stage 3.2 — Context Injection & Feedback

Before agent tasks begin, build a `ContextWindow` and inject it into the agent's prompt.
After completion, feed outcomes back into expertise.

- **Step 3.2.1**: Context injection into agent prompts  
  Add `IContextBuilder.BuildContextAsync()` call before task dispatch. Format `ContextWindow`
  as markdown sections: "Prior Experience", "Known Patterns", "Relevant Call Chains",
  "Freshness Warnings". Configurable token budget. Tests verify prompt format.  
  `expectedFileChanges`: 6

- **Step 3.2.2**: Feedback loop — outcome-based expertise reinforcement  
  After episode completion, if outcome is `Success`, reinforce expertise confidence for
  touched subjects. If `Failed`, record negative evidence. Update `LastValidated` and
  `CommitSha` on accessed expertise. Tests verify confidence changes.  
  `expectedFileChanges`: 5

### Stage 3.3 — Memory Evolution Scheduling

Run consolidation, decay, and promotion on a schedule with checkpointing for idempotency.

- **Step 3.3.1**: Evolver checkpointing and idempotent batching  
  Add high-water mark tracking to `MemoryEvolver`: store last-processed episode timestamp,
  last-decay-run timestamp, and last-promotion-run timestamp as metadata in the store.
  Skip already-processed episodes during consolidation. Tests verify no reprocessing.  
  `expectedFileChanges`: 6

- **Step 3.3.2**: Background memory evolution hosted service  
  Implement `IHostedService` that runs `ConsolidateAsync()`, `DecayAsync()`, `PromoteAsync()`
  on configurable intervals. Uses checkpoints from 3.3.1. Lease/lock via file or store flag
  to prevent concurrent runs. Log evolution statistics.  
  `expectedFileChanges`: 5

---

## Phase 4 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 4.1 — MCP Memory Tools

Expose memory as MCP tools so agents can explicitly query and update memory.

- **Step 4.1.1**: MCP tool definitions — query memory  
  Define `memory_search` (find relevant expertise), `memory_context` (build full context
  window), `memory_stats` (get memory statistics). Register as MCP tool handlers.
  Tests with mock store.  
  `expectedFileChanges`: 7

- **Step 4.1.2**: MCP tool definitions — record and evolve  
  Define `memory_record_observation` (record an observation), `memory_consolidate`
  (trigger consolidation), `memory_decay` (trigger decay). Tests with mock store.  
  `expectedFileChanges`: 6

### Stage 4.2 — CLI Memory Commands

Add Spectre.Console commands to `Ast.Cli` for inspecting and managing memory.

- **Step 4.2.1**: CLI memory inspect commands  
  Add `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`
  commands. Table-formatted output with confidence bars and tier indicators.  
  `expectedFileChanges`: 7

- **Step 4.2.2**: CLI memory management commands  
  Add `memory consolidate`, `memory decay`, `memory seed` (run structural seeder),
  `memory reindex`, `memory clear` commands. Progress bars for long operations.  
  `expectedFileChanges`: 6

---

## Phase 5 — Advanced Retrieval & Evolution

Add GraphRAG-style community detection and temporal reasoning. This phase is post-MVP —
the core memory loop (record → persist → seed → retrieve → evolve → inspect) is complete
after Phase 4.

### Stage 5.1 — GraphRAG Community Detection

Apply community detection (Louvain/Leiden) to the memory graph to discover natural module
boundaries. Use communities as the basis for automatic tier promotion from Query → Insight.

- **Step 5.1.1**: Community detection on memory graph  
  Implement Louvain community detection over Neo4j's `CoChangedWith` and `CallsMethod`
  edges. Create `ArchitecturalKnowledge` expertise for each community with template-based
  summary describing the module boundary. Tests with known community structures.  
  `expectedFileChanges`: 7

- **Step 5.1.2**: Community-aware context retrieval  
  When building context, expand candidates to include the full community of matched symbols.
  Weight community co-members higher than arbitrary graph neighbors. Tests verify
  community expansion in retrieval.  
  `expectedFileChanges`: 5

### Stage 5.2 — Temporal Knowledge Graph

Add temporal validity windows to edges so the memory graph reflects how code relationships
evolve over time (Zep-style).

- **Step 5.2.1**: Temporal edge model and validity tracking  
  Add `ValidFrom`, `ValidTo`, `IsActive` to `MemoryEdge`. When code changes invalidate an
  edge, close the validity window rather than deleting. Temporal queries filter by active
  edges at a given point in time. Migration for existing edges.  
  `expectedFileChanges`: 6

- **Step 5.2.2**: Temporal decay integration  
  Update `MemoryEvolver.DecayAsync()` to use temporal windows. Edges with closed validity
  windows reduce expertise confidence proportionally. Historical queries can reconstruct
  past memory states. Tests verify temporal filtering.  
  `expectedFileChanges`: 5

---

## WIT Tree Summary

| Phase | Stage | Step | Expected File Changes |
|-------|-------|------|-----------------------|
| 1. Foundations & Persistence | 1.1 Identity & Configuration | 1.1.1 Canonical symbol identity resolver | 6 |
| | | 1.1.2 Feature flags and memory configuration | 5 |
| | 1.2 Neo4j Memory Graph Store | 1.2.1 Neo4j memory schema DDL and migration | 7 |
| | | 1.2.2 Neo4jMemoryStore — episode/expertise CRUD | 8 |
| | | 1.2.3 Neo4jMemoryStore — edges/call chains/stats | 7 |
| | | 1.2.4 Neo4j memory store DI registration | 6 |
| | 1.3 Vector Memory Integration | 1.3.1 Vector memory adapter and embedding pipeline | 7 |
| | | 1.3.2 ContextBuilder semantic retrieval | 5 |
| | 1.4 Backfill & Index Consistency | 1.4.1 Write-through indexing and backfill job | 7 |
| | | 1.4.2 Delete/update propagation and reindex | 5 |
| 2. Code Graph Memory Extraction | 2.1 Structural Memory Seeder | 2.1.1 Repo structure walker and project expertise | 6 |
| | | 2.1.2 Namespace and type-level expertise seeding | 5 |
| | | 2.1.3 Method-level expertise and API surface seeding | 6 |
| | 2.2 Call Chain Extraction | 2.2.1 Call chain extractor from hypergraph | 6 |
| | | 2.2.2 DI registration chain extraction | 5 |
| | 2.3 Git Co-Change Seeding | 2.3.1 Git log co-change analyzer | 6 |
| | | 2.3.2 Incremental git co-change updates | 5 |
| 3. Agent Integration | 3.1 Episode Recording Hooks | 3.1.1 Agent orchestrator episode recording | 7 |
| | | 3.1.2 Symbol resolution for touched files | 5 |
| | 3.2 Context Injection & Feedback | 3.2.1 Context injection into agent prompts | 6 |
| | | 3.2.2 Feedback loop — outcome reinforcement | 5 |
| | 3.3 Memory Evolution Scheduling | 3.3.1 Evolver checkpointing and idempotent batching | 6 |
| | | 3.3.2 Background memory evolution hosted service | 5 |
| 4. CLI & MCP Surfaces | 4.1 MCP Memory Tools | 4.1.1 MCP query tools | 7 |
| | | 4.1.2 MCP record and evolve tools | 6 |
| | 4.2 CLI Memory Commands | 4.2.1 CLI memory inspect commands | 7 |
| | | 4.2.2 CLI memory management commands | 6 |
| 5. Advanced Retrieval & Evolution | 5.1 GraphRAG Community Detection | 5.1.1 Community detection on memory graph | 7 |
| | | 5.1.2 Community-aware context retrieval | 5 |
| | 5.2 Temporal Knowledge Graph | 5.2.1 Temporal edge model and validity tracking | 6 |
| | | 5.2.2 Temporal decay integration | 5 |

---

## Out of Scope

- **LLM-generated summaries for communities** — Requires LLM API budget; deferred until
  cost model is established. Community summaries will use template-based generation initially.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights require a
  reward signal infrastructure (did the agent's action work?) that doesn't yet exist. Tracked
  for a future iteration after feedback loops are instrumented.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Access control and memory sharing
  between agents is beyond the current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Token budget management via OS-style
  paging. Current context windows fit within limits; revisit if models with smaller windows
  are adopted.
- **Video/multimodal memory** — The survey covers multimodal memory but this codebase is
  text/code only.
- **Fine-tuning or model adaptation** — Memory is retrieved at inference time, not baked
  into model weights. No fine-tuning infrastructure planned.
- **Benchmarking against academic datasets** (LoCoMo, LongMemEval, etc.) — Useful for
  validating the approach but not actionable for the product. May add as a research task later.
- **Reusing `Ast.GraphDb.IGraphStore` for memory** — The code-graph store lacks domain CRUD,
  drops metadata, and hardcodes `:CodeNode` labels. A dedicated memory store is required.