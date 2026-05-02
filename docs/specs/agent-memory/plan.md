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
The memory persistence layer requires its own `IMemoryGraphStore` abstraction with distinct
Neo4j labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`) and Cypher queries
purpose-built for memory lifecycle operations. Memory nodes reference code nodes via
cross-label relationships, but the stores are architecturally separate.

### Symbol Identity — Key Constraint

Episode recording, context building, structural seeding, and call chain extraction all join
on file paths and symbol IDs. These identifiers must be **canonical and stable** across sessions.
The plan includes an explicit identity resolver step that normalizes paths (forward-slash,
repo-relative) and generates deterministic symbol IDs from the hypergraph before any
cross-system joins are attempted.

### Provenance — Key Constraint

Memory records originate from three sources — **git history** (seed priors), **static
analysis** (inferred from code graph), and **agent episodes** (observed at runtime). Each
source has different reliability characteristics. Co-change edges from git are ground truth;
inferred call chains from HypergraphData are structural approximations; agent-observed
chains are runtime behavior. The retrieval pipeline weights these differently, and each
memory record carries its provenance source for disambiguation.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** architecture, combining three paradigms
from the research survey:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural
   views layered on a single graph store. The existing ContextBuilder implements the
   multi-signal candidate scoring pipeline; we extend it with graph-native traversals.

2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight tiers
   already modeled via `MemoryTier`. We complete the tier promotion pipeline and validate
   with retrieval metrics before adding community detection.

3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via CommitSha on
   expertise records. Git-log-seeded co-change grounds temporal edges in real version
   history rather than agent-only observations.

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Early vertical slice before persistence infra | Validate the memory loop (record → retrieve → feedback) end-to-end using existing file-system store before investing in Neo4j/Qdrant |
| Dedicated `IMemoryGraphStore` (not reusing `IGraphStore`) | `IGraphStore` stores `HypergraphNode`/`Hyperedge` as `:CodeNode` and drops `Metadata`; memory entities need domain-specific CRUD with different labels |
| Canonical symbol identity resolver | File paths normalized to repo-relative forward-slash form; symbol IDs generated deterministically so cross-store joins are reliable |
| Provenance on all memory records | Distinguishes `git-seeded`, `inferred`, `observed` sources; retrieval weights them differently |
| Evaluation track before advanced features | Measure retrieval quality (hit rate, context usage, stale-memory rate) before investing in GraphRAG or temporal KG |
| File-system store remains for offline/test | Graceful degradation with feature flags when Neo4j/Qdrant are unavailable |
| Git-seeded co-change as seed priors | Git history is ground truth; agent observations refine it; provenance prevents conflation |

---

## Phase 1 — Identity & Early Integration

Deliver a thin vertical slice: canonical identity, feature flags, orchestrator hooks, and
minimal context injection. Validates the memory loop end-to-end using the existing
file-system store before building persistent infrastructure.

### Stage 1.1 — Identity & Configuration

- **Step 1.1.1**: Canonical symbol identity resolver
  Create `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath) → string`
  and `CanonicalSymbolId(namespace, typeName, memberName) → string`. Normalize all paths to
  repo-relative forward-slash form. Unit tests for edge cases (drive letters, nested
  worktrees). Update `EpisodeRecorder` to use canonical paths.
  `expectedFileChanges`: 6

- **Step 1.1.2**: Feature flags and memory configuration
  Create `AgentMemoryOptions` with flags: `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`.
  Wire into `ServiceCollectionExtensions`. Add `IMemoryStoreHealthCheck`. Tests verify
  correct store selection per configuration.
  `expectedFileChanges`: 5

### Stage 1.2 — Orchestrator Episode Recording

- **Step 1.2.1**: Agent orchestrator episode recording middleware
  Add `IEpisodeRecorder` hook into `CopilotAgentOrchestrator`. Auto-start episode on task
  begin, auto-complete on task end. Track touched files from tool calls (view, edit, create).
  Classify `TouchType` from tool name. Use `SymbolIdentity.NormalizePath` for all paths.
  `expectedFileChanges`: 8

- **Step 1.2.2**: Symbol resolution for touched files
  When an episode records a touched file, resolve it to `HypergraphNode` IDs using
  `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback when graph
  unavailable (file-only episodes).
  `expectedFileChanges`: 5

### Stage 1.3 — Minimal Context Injection

- **Step 1.3.1**: Context injection into agent prompts
  Add `IContextBuilder.BuildContextAsync()` call before task dispatch. Format
  `ContextWindow` as markdown sections: "Prior Experience", "Known Patterns",
  "Relevant Call Chains", "Freshness Warnings". Configurable token budget.
  `expectedFileChanges`: 7

- **Step 1.3.2**: Provenance model and source-of-truth rules
  Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`,
  `CallChain`, and `ExpertiseRecord`. Document canonical source per entity type.
  Define write-through vs async-projection rules. Tests verify provenance is set.
  `expectedFileChanges`: 6

---

## Phase 2 — Persistent Storage Backends

Connect to Neo4j and Qdrant for durable, queryable memory beyond the file-system store.

### Stage 2.1 — Neo4j Memory Graph Store

- **Step 2.1.1**: Neo4j memory schema DDL and migration
  Cypher DDL for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`),
  relationships (`:TOUCHED_FILE`, `:TOUCHED_SYMBOL`, `:PRODUCED_EXPERTISE`,
  `:CO_CHANGED_WITH`, `:PROMOTED_TO`), composite indices, uniqueness constraints.
  Idempotent `MemorySchemaMigration.ApplyAsync()`. Integration tests.
  `expectedFileChanges`: 7

- **Step 2.1.2**: Neo4jMemoryStore — episode and expertise CRUD
  Implement `IAgentMemoryStore` and `ITieredExpertiseStore` against Neo4j. Cypher for
  `RecordEpisodeAsync`, `GetEpisodesAsync`, `UpsertExpertiseAsync`,
  `GetExpertiseByCategoryAsync`. Integration tests with Testcontainers.
  `expectedFileChanges`: 10

- **Step 2.1.3**: Neo4jMemoryStore — edges, call chains, and statistics
  `AddMemoryEdgeAsync`, `GetOutgoingEdgesAsync`/`GetIncomingEdgesAsync` via Cypher
  traversals. `ICallChainStore` with ordered list serialization. `GetStatisticsAsync`
  via aggregate Cypher. Integration tests.
  `expectedFileChanges`: 8

- **Step 2.1.4**: Neo4j memory store DI registration
  `services.AddNeo4jAgentMemory(connectionString)`. Health checks, configuration binding,
  docker-compose entry for memory Neo4j container.
  `expectedFileChanges`: 5

### Stage 2.2 — Vector Memory Integration

- **Step 2.2.1**: Vector memory adapter and embedding pipeline
  `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`,
  `SearchSimilarEpisodesAsync`. Implement against `IVectorStore` (Qdrant). Collections:
  `agent_memory_episodes`, `agent_memory_expertise`. Tests with mock vector store.
  `expectedFileChanges`: 7

- **Step 2.2.2**: ContextBuilder semantic retrieval integration
  Add semantic search as candidate generation step after exact matches and graph neighbors.
  Graceful fallback when vector store unavailable. Adjust reranking weights for semantic
  candidates.
  `expectedFileChanges`: 5

### Stage 2.3 — Backfill & Index Consistency

- **Step 2.3.1**: Write-through indexing and backfill job
  `CompositeAgentMemoryStore` wraps primary store and dispatches writes to Neo4j + Qdrant.
  `MemoryBackfillJob` reads all records from file-system store and indexes them. Idempotent.
  `expectedFileChanges`: 8

- **Step 2.3.2**: Delete/update propagation and reindex command
  Propagate deletes/updates to Neo4j and Qdrant. `memory reindex` command for CLI/MCP.
  Failure semantics: log and continue when one backend fails. Tests verify consistency.
  `expectedFileChanges`: 5

---

## Phase 3 — Code Graph Memory Extraction

Build the initial memory graph from top-down repo structure, call chains, and git history.

### Stage 3.1 — Structural Memory Seeder

Walk `HypergraphData` to create initial `ExpertiseRecord`s at each tier. Bootstrapped
expertise starts at confidence 0.3 and gains confidence through agent interaction.

- **Step 3.1.1**: Repo structure walker and project-level expertise
  Traverse `HypergraphData.Nodes` grouped by project. Create `ProjectExpertise` records
  with dependency summaries. `ContainedIn` edges from namespace → project. Canonical
  paths via `SymbolIdentity`. Tests with sample hypergraph fixture.
  `expectedFileChanges`: 7

- **Step 3.1.2**: Namespace and type-level expertise seeding
  Group nodes by namespace. Create `NamespaceExpertise` (Query tier) and `TypeExpertise`
  (Interaction tier). Detect public API surface. Connect via `ContainedIn` edges.
  `expectedFileChanges`: 5

- **Step 3.1.3**: Method-level expertise and API surface seeding
  `MethodExpertise` for public/internal methods. Detect entry points and API endpoints
  from `HyperedgeType.ApiEndpoint`, `EntryPoint`. Seed `CallChainPattern` expertise.
  `expectedFileChanges`: 6

### Stage 3.2 — Call Chain Extraction

- **Step 3.2.1**: Call chain extractor from hypergraph edges
  Walk `MethodInvocation` edges depth-first from entry points. Build `CallChain` objects
  with `CallChainSource.Inferred`. Configurable max depth (default 8). Cycle detection.
  Canonical IDs via `SymbolIdentity`. Tests with cyclic and acyclic fixtures.
  `expectedFileChanges`: 7

- **Step 3.2.2**: DI registration chain extraction
  Follow `ServiceRegistration` edges to build DI chains. Create `CallChainPattern`
  expertise for common patterns (controller → service → repository). Link via `MemoryEdge`.
  `expectedFileChanges`: 5

### Stage 3.3 — Git Co-Change Seeding

- **Step 3.3.1**: Git log co-change analyzer with provenance
  Parse `git log --name-only` for files modified together. Compute Jaccard similarity.
  Create `CoChangedWith` edges with `Provenance.GitSeeded`. `CoChangeGroup` expertise
  for pairs exceeding thresholds. Normalize paths via `SymbolIdentity`.
  `expectedFileChanges`: 7

- **Step 3.3.2**: Incremental git co-change updates
  Track last-processed commit SHA as watermark. Process only new commits on subsequent
  runs. Update existing co-change edges (reinforce or decay). Idempotent replay.
  `expectedFileChanges`: 5

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop, schedule memory evolution, and add metrics to validate that
memory actually improves agent performance.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based expertise reinforcement
  After episode completion, reinforce expertise confidence for touched subjects on
  `Success`, record negative evidence on `Failed`. Update `LastValidated`, `CommitSha`,
  and `AccessCount` on accessed expertise. Tests verify confidence changes.
  `expectedFileChanges`: 5

- **Step 4.1.2**: Access tracking and stale-memory detection
  Track `AccessCount` increments when expertise is retrieved. Compare `CommitSha` against
  HEAD for staleness detection. Stale expertise gets freshness warning in `ContextWindow`.
  `expectedFileChanges`: 4

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing and idempotent batching
  High-water mark tracking in `MemoryEvolver`: last-processed episode timestamp,
  last-decay-run, last-promotion-run stored as metadata. Skip already-processed episodes.
  Tests verify no reprocessing.
  `expectedFileChanges`: 6

- **Step 4.2.2**: Background memory evolution hosted service
  `IHostedService` running `ConsolidateAsync()`, `DecayAsync()`, `PromoteAsync()` on
  configurable intervals. Lease/lock to prevent concurrent runs. Log evolution statistics.
  `expectedFileChanges`: 5

### Stage 4.3 — Evaluation & Observability

- **Step 4.3.1**: Retrieval metrics and context-budget tracking
  Counters: hit rate (% of context queries with ≥1 relevant result), retrieval latency,
  context token usage vs budget, stale-memory rate. Exposed via `IMemoryMetrics` interface.
  Integration with existing logging. Tests with known fixtures.
  `expectedFileChanges`: 6

- **Step 4.3.2**: Memory health dashboard and A/B comparison
  `memory health` command showing hit rate, stale rate, tier distribution, top expertise.
  Feature-flag for with-memory vs without-memory comparison via prompt injection toggle.
  Log outcome differences.
  `expectedFileChanges`: 5

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query tools
  `memory_search` (find expertise), `memory_context` (build full context window),
  `memory_stats` (statistics). Register as MCP tool handlers. Tests with mock store.
  `expectedFileChanges`: 8

- **Step 5.1.2**: MCP record and evolve tools
  `memory_record_observation`, `memory_consolidate`, `memory_decay`.
  Tests with mock store.
  `expectedFileChanges`: 6

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI memory inspect commands
  `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`.
  Table-formatted output with confidence bars and tier indicators via Spectre.Console.
  `expectedFileChanges`: 8

- **Step 5.2.2**: CLI memory management commands
  `memory consolidate`, `memory decay`, `memory seed`, `memory reindex`, `memory clear`.
  Progress bars for long operations.
  `expectedFileChanges`: 6

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Louvain/Leiden community detection
  for automatic module discovery. Deferred until retrieval metrics (Phase 4.3) demonstrate
  that simple graph + vector retrieval is insufficient.
- **Full temporal knowledge graph** (arXiv:2501.13956) — `ValidFrom`/`ValidTo` temporal
  windows on edges. Current `CommitSha` + `LastValidated` provides sufficient staleness
  detection. Deferred until temporal queries are demonstrably needed.
- **LLM-generated summaries for communities** — Requires LLM API budget; deferred until
  cost model is established.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights require a
  reward signal infrastructure that doesn't yet exist.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Access control and memory
  sharing between agents is beyond the current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Current context windows fit within
  limits; revisit if models with smaller windows are adopted.
- **Video/multimodal memory** — Codebase is text/code only.
- **Fine-tuning or model adaptation** — Memory is retrieved at inference time, not baked
  into model weights.
- **Benchmarking against academic datasets** (LoCoMo, LongMemEval) — Useful for validation
  but not actionable for the product.
- **Reusing `Ast.GraphDb.IGraphStore` for memory** — The code-graph store lacks domain CRUD,
  drops metadata, and hardcodes `:CodeNode` labels.