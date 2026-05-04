# Agent Memory Architecture

## 1. Overview

### 1.1 Purpose

This document specifies the architecture for a **graph-based agent memory
system** that builds structured expertise from repository source code. The
system constructs a multi-layer knowledge graph from two primary inputs —
**top-down repository structure** (packages, modules, classes, functions) and
**call-chain context** (invocation paths, data flow, dependency edges) — and
exposes that graph as retrievable, evolving memory that coding agents consume
to produce higher-quality completions, refactors, and reviews.

The core value proposition: agents that interact with the same codebase across
sessions accumulate institutional knowledge — patterns discovered, pitfalls
flagged, architectural context mapped — so that every subsequent session starts
with richer understanding than the one before.

### 1.2 Scope

| In scope | Out of scope |
|---|---|
| Static analysis extraction (AST, call graph, imports) | IDE integration surfaces (UI, keybindings) |
| Graph construction and persistent storage | LLM inference hosting |
| Context retrieval for agent prompts | User authentication and billing |
| Expertise write-back and consolidation | Runtime / dynamic analysis |
| Incremental updates on commit | Multi-repo federated graphs |

---

## 2. Research Foundations

The design is grounded in the graph-based agent memory taxonomy from Yang et
al., 2026 (*Graph-based Agent Memory: Taxonomy, Techniques, and Applications*,
[arXiv:2602.05665](https://arxiv.org/abs/2602.05665)), which identifies four
lifecycle phases — **Extraction**, **Storage**, **Retrieval**, and
**Evolution** — and catalogs techniques under each. The traceability matrix
below maps specific research findings to the design decisions in this
architecture.

### 2.1 Research Traceability Matrix

| ID | Research source | Finding adopted | Design decision | Component(s) | Adaptation / limitation |
|---|---|---|---|---|---|
| R-1 | MAGMA (arXiv:2601.03236) | Separating semantic, temporal, causal, and entity graphs prevents retrieval signal entanglement. | Four orthogonal graph layers — Structural, Call-chain, Semantic, Expertise — each with distinct edge types and independent query paths. | Graph Store, Retrieval Engine | We replace MAGMA's "temporal graph" with commit-range versioning on all layers, and "causal graph" with explicit call-chain edges, since code causality maps naturally to invocation. |
| R-2 | HiAgent (ACL '25, aclanthology.org/2025.acl-long.1575) | Hierarchical working memory reduces token cost for long-horizon agent tasks by letting the agent operate at different abstraction levels. | The Structural layer mirrors the repo containment tree (Repo → Package → Module → Symbol). The Budget Controller collapses children into parent summaries when tokens are scarce. | Extraction Pipeline (Structure Builder), Context Assembler | HiAgent targets general tasks; we specialize the hierarchy to source-code containment which is deterministic, not LLM-inferred. |
| R-3 | G-Memory (arXiv:2506.07398) | Hierarchical memory with tracing supports multi-agent coordination by scoping memory visibility. | Retrieval queries accept a `scope` parameter (qualified-name prefix) that limits traversal to a subtree, enabling per-module or per-package focus. | Retrieval Engine (Query Planner) | We do not implement multi-agent access control; scope is advisory, not enforced. |
| R-4 | Zep / Graphiti (arXiv:2501.13956) | Temporal knowledge graphs with timestamped edges enable cross-session reasoning and staleness detection. | Every node and edge carries `version_range {since, until}` (commit SHAs) and `last_touched` (timestamp). Stale edges are flagged and excluded from default retrieval. | Graph Store, Incremental Sync, Evolution Engine | Zep uses wall-clock time; we use commit SHAs as the temporal axis since code changes are commit-atomic. |
| R-5 | Mem0 (arXiv:2504.19413) | Production-ready agent memory requires write-back: agents that record learnings produce compounding value over sessions. | The Evolution Engine accepts agent-authored ExpertiseEntry records, persists them as Expertise-layer nodes, and links them to code via APPLIES_TO edges. | Evolution Engine | Mem0 stores flat key-value memories; we store structured expertise nodes with typed edges, confidence scores, and evidence citations. |
| R-6 | Reflexion (NeurIPS '23) | Verbal reinforcement learning — agents that reflect on past failures improve on subsequent attempts. | Cross-session learning flow: agents write back pitfalls and insights which are surfaced in future sessions via the Expertise layer. Consolidation merges overlapping learnings. | Evolution Engine, Retrieval Engine | Reflexion operates within a single episode; we extend the feedback loop across sessions via persistent graph storage. |
| R-7 | GraphRAG (arXiv:2404.16130) | Community detection + LLM summarization over graph partitions produces useful high-level summaries for RAG. | Community Summarization in the Evolution Engine detects tightly-coupled clusters via graph partitioning and generates Topic nodes with prose summaries. | Evolution Engine, Graph Store (Semantic layer) | GraphRAG summarizes document graphs; we summarize code structure + call-chain graphs, which are denser and more deterministic. |
| R-8 | SGMem (arXiv:2509.21212) | Sentence-graph memory supports efficient long-term conversational agents by linking utterance fragments. | Semantic layer uses SIMILAR_TO edges between code nodes whose embeddings are within a cosine threshold, enabling "find related code" queries without free-text sentences. | Extraction Pipeline (Semantic Indexer), Retrieval Engine (Vector Searcher) | SGMem links natural-language sentences; we link code symbols using code-specific embedding models. |
| R-9 | MemGPT (arXiv:2310.08560) | Treating memory as a virtual address space with explicit read/write operations gives the agent fine-grained control. | The Retrieval Engine exposes discrete retrieval modes (STRUCTURAL_DRILL, CALL_TRACE, SEMANTIC_SEARCH, EXPERTISE_RECALL, HYBRID) that the agent or its orchestrator can select explicitly. | Retrieval Engine (Query Planner) | We do not implement MemGPT's paging/eviction model; we use a token Budget Controller instead. |

---

## 2. Components

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Agent Memory System                          │
│                                                                       │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────────┐  │
│  │  Extraction   │──▶│  Graph Store  │◀──│  Retrieval Engine        │  │
│  │  Pipeline     │   │  (4 layers)  │   │  (query planner + fetch) │  │
│  └──────┬───────┘   └──────┬───────┘   └────────────┬─────────────┘  │
│         │                  │                         │                │
│         │           ┌──────┴───────┐          ┌──────┴───────┐       │
│         │           │  Evolution   │          │  Context      │       │
│         │           │  Engine      │          │  Assembler    │       │
│         │           └──────────────┘          └──────────────┘       │
│         │                                                            │
│  ┌──────┴───────────────────────────────────────────────────────┐    │
│  │  Incremental Sync (watches repo, triggers extraction)        │    │
│  └──────────────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────────────┘
         ▲                                          │
         │  git events / file-system watch          │  assembled context
         │                                          ▼
    ┌─────────┐                              ┌─────────────┐
    │  Source  │                              │  Agent / LLM │
    │  Repo   │                              │  Consumer    │
    └─────────┘                              └─────────────┘
```

### 2.1 Extraction Pipeline

**Responsibility:** Parse source code into typed graph nodes and edges across
the four graph layers.

| Sub-component | Input | Output | Notes |
|---|---|---|---|
| **Language Parser** | Source files | Unresolved AST facts (symbols, scopes, imports) | One parser adapter per language. Uses tree-sitter grammars for breadth; LSP for type-resolved facts where available. |
| **Structure Builder** | AST facts | Structural-layer nodes + `CONTAINS` / `IMPORTS` edges | Builds the repo → package → module → symbol hierarchy. |
| **Call-Chain Analyzer** | AST facts + resolved types | Call-chain-layer `CALLS` / `OVERRIDES` / `IMPLEMENTS` edges | Static call graph. Edges carry arity, argument types, and whether the call is conditional/async. |
| **Semantic Indexer** | Docstrings, comments, symbol names | Semantic-layer embedding vectors attached to nodes | Produces dense vector embeddings (e.g., code-embedding model) for similarity retrieval. |
| **Fact Normalizer** | Raw nodes/edges from above | Canonical graph facts with `version_range` and `content_hash` | De-duplicates, assigns stable IDs, attaches commit metadata. |

**Key design choice — tree-sitter first:** Tree-sitter provides
language-agnostic AST parsing with sub-second latency per file and works
offline. The LSP adapter is optional and additive — it enriches the graph
with type-resolved edges when a language server is running. The pipeline must
never block on an LSP.

### 2.2 Graph Store

**Responsibility:** Persist the four graph layers, serve traversal and
pattern queries, manage versioned snapshots.

The store is logically a single property graph partitioned into four
**layers**. Each layer has distinct node labels and edge types but shares the
same node ID namespace so cross-layer joins are natural.

#### Layer definitions

| Layer | Node labels | Edge types | Purpose |
|---|---|---|---|
| **Structural** | `Repo`, `Package`, `Module`, `Class`, `Function`, `Variable` | `CONTAINS`, `IMPORTS`, `RE_EXPORTS` | Top-down containment and dependency hierarchy. |
| **Call-Chain** | (shares symbol nodes from Structural) | `CALLS`, `CALLED_BY`, `OVERRIDES`, `IMPLEMENTS`, `INSTANTIATES` | Directed invocation graph. Captures who-calls-whom and with what signature. |
| **Semantic** | `SemanticCluster`, `Topic` | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` | Grouping by embedding similarity. Enables "find all code related to X" queries. |
| **Expertise** | `Pattern`, `Pitfall`, `Preference`, `Insight` | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | Agent-authored knowledge. Grows over time through the Evolution Engine. |

**Storage backend:** The reference implementation uses a labeled property
graph database (e.g., Neo4j / Memgraph) for topology queries and a separate
vector index (e.g., pgvector or Qdrant) for embedding search. A lightweight
SQLite + in-memory adjacency list mode is provided for local / CI use.

### 2.3 Retrieval Engine

**Responsibility:** Accept a natural-language or structured query from the
agent, plan the retrieval strategy, traverse the graph, and return ranked
memory fragments.

| Sub-component | Role |
|---|---|
| **Query Planner** | Classifies the query intent (structural lookup, call-path trace, semantic search, expertise recall) and selects which graph layers to traverse. Inspired by MAGMA's policy-guided traversal. |
| **Graph Traverser** | Executes Cypher / Gremlin-style traversal on the selected layers. Supports depth-limited BFS, shortest-path, and community-walk strategies. |
| **Vector Searcher** | Runs ANN search over the Semantic layer embeddings. Returns top-k similar nodes. |
| **Ranker** | Merges results from Graph Traverser and Vector Searcher. Applies scoring signals: structural proximity, call-chain distance, recency (temporal decay), expertise confidence, and embedding cosine similarity. |
| **Budget Controller** | Enforces a token budget. Prunes or summarizes fragments to fit the agent's context window. Uses the structural hierarchy to substitute a parent summary when child details exceed budget. |

**Retrieval modes** (selected by the Query Planner):

| Mode | Technique | Example query |
|---|---|---|
| `STRUCTURAL_DRILL` | Top-down BFS from repo root | "What does the `auth` package contain?" |
| `CALL_TRACE` | Forward/reverse DFS on call-chain layer | "What calls `validateToken`?" / "What does `login()` invoke?" |
| `SEMANTIC_SEARCH` | Vector ANN + cluster expansion | "Find code related to rate limiting" |
| `EXPERTISE_RECALL` | Pattern/pitfall lookup filtered by `APPLIES_TO` scope | "Known pitfalls for the payments module?" |
| `HYBRID` | Fan-out across ≥2 modes, merge via Ranker | "Explain the authentication flow and flag any known issues" |

### 2.4 Context Assembler

**Responsibility:** Transform ranked memory fragments into a structured
prompt section ready for the consuming LLM.

The assembler applies three transformations:

1. **Hierarchy compression.** If a parent node and all its children are
   selected, collapse to the parent summary + child signature list.
2. **Deduplication.** Identical code spans reached via different graph paths
   are emitted once, with a footnote listing the paths.
3. **Formatting.** Output a deterministic Markdown document with sections:
   `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`.
   The agent system prompt specifies which sections to include.

### 2.5 Evolution Engine

**Responsibility:** Accept agent-authored learnings and merge them into the
Expertise layer, consolidating, de-duplicating, and versioning knowledge
over time.

| Operation | Trigger | Description |
|---|---|---|
| **Write-back** | Agent emits a structured annotation (pattern, pitfall, preference). | Creates an Expertise node, links it via `APPLIES_TO` edges to the structural/call-chain nodes the annotation references. |
| **Consolidation** | Scheduled (e.g., post-PR-merge) or manual. | LLM-powered pass that reads overlapping expertise nodes and merges them: confirms, supersedes, or retracts. Produces `SUPERSEDES` edges. |
| **Decay** | On every retrieval. | Expertise nodes whose `APPLIES_TO` targets have been deleted or substantially rewritten have their confidence scores reduced. Nodes below a threshold are archived (soft-deleted). |
| **Community Summarization** | On structural change exceeding a threshold. | Inspired by GraphRAG community summaries. Detects tightly coupled clusters in the structural + call-chain layers and generates or updates a prose summary stored as a `Topic` node in the Semantic layer. |

### 2.6 Incremental Sync

**Responsibility:** Keep the graph current with the repository without
full re-extraction.

**Mechanism:**
1. On each commit (or file-save in watch mode), diff the changed files.
2. For each changed file, re-run the Extraction Pipeline on that file only.
3. Compute a node/edge diff against the current graph snapshot.
4. Apply inserts, updates, and tombstones. Shift `version_range.end` on
   deleted nodes rather than hard-deleting them.
5. Propagate invalidation: if a function signature changed, mark downstream
   `CALLS` edges as `stale` until the call-chain analyzer re-confirms them.

**Performance target:** Incremental sync for a single-file change completes
in < 2 seconds for repositories up to 500 K LOC.

---

## 3. Data Model

### 3.1 Core Entities

```
┌─────────────────────────────────────────────────────────────────┐
│                         Node (base)                             │
├─────────────────────────────────────────────────────────────────┤
│  id             : UUID          — stable across renames (via    │
│                                    content_hash fallback)       │
│  qualified_name : string        — e.g., "auth.jwt.validate"    │
│  kind           : NodeKind      — enum (see below)             │
│  layer          : LayerTag[]    — which layers this node is in  │
│  file_path      : string | null — source location              │
│  span           : {start, end}  — byte range in file           │
│  content_hash   : bytes         — hash of the source span       │
│  summary        : string        — LLM-generated one-liner      │
│  version_range  : {since, until} — commit SHA range             │
│  last_touched   : timestamp     — last extraction time          │
│  embedding      : float[768]    — dense vector (Semantic layer) │
│  metadata       : JSON          — language-specific extras      │
└─────────────────────────────────────────────────────────────────┘

NodeKind enum:
  REPO | PACKAGE | MODULE | CLASS | FUNCTION | METHOD | VARIABLE |
  INTERFACE | ENUM | TYPE_ALIAS |
  SEMANTIC_CLUSTER | TOPIC |
  PATTERN | PITFALL | PREFERENCE | INSIGHT
```

```
┌─────────────────────────────────────────────────────────────────┐
│                         Edge (base)                             │
├─────────────────────────────────────────────────────────────────┤
│  id             : UUID                                          │
│  source         : NodeID                                        │
│  target         : NodeID                                        │
│  relation       : RelationType   — enum (see below)            │
│  layer          : LayerTag                                      │
│  weight         : float          — confidence / frequency      │
│  properties     : JSON           — relation-specific payload   │
│  version_range  : {since, until} — commit SHA range             │
│  stale          : boolean        — pending re-confirmation     │
└─────────────────────────────────────────────────────────────────┘

RelationType enum:
  — Structural: CONTAINS | IMPORTS | RE_EXPORTS
  — Call-chain: CALLS | CALLED_BY | OVERRIDES | IMPLEMENTS | INSTANTIATES
  — Semantic:   SIMILAR_TO | BELONGS_TO_CLUSTER
  — Expertise:  APPLIES_TO | SUPERSEDES | DERIVED_FROM
```

### 3.2 Edge Property Schemas

Each `RelationType` defines the shape of its `properties` JSON:

| Relation | Properties | Example |
|---|---|---|
| `CALLS` | `{ arg_count: int, is_async: bool, is_conditional: bool, call_site: Span }` | `{ arg_count: 2, is_async: true, is_conditional: false, call_site: {start:120, end:145} }` |
| `IMPORTS` | `{ alias: string?, is_wildcard: bool, is_type_only: bool }` | `{ alias: "jwt", is_wildcard: false, is_type_only: false }` |
| `APPLIES_TO` | `{ confidence: float, scope: "exact" \| "subtree" }` | `{ confidence: 0.85, scope: "subtree" }` |
| `SUPERSEDES` | `{ reason: string, superseded_at: timestamp }` | `{ reason: "Merged duplicate pattern", superseded_at: "2026-05-01T..." }` |
| `SIMILAR_TO` | `{ cosine_sim: float }` | `{ cosine_sim: 0.92 }` |

### 3.3 Expertise Node Schema

Expertise nodes carry richer payloads than code-derived nodes:

```
ExpertiseNode extends Node {
  expertise_kind : "pattern" | "pitfall" | "preference" | "insight"
  title          : string        — human-readable label
  body           : string        — detailed description (Markdown)
  confidence     : float [0, 1]  — decays over time
  source_agent   : string        — which agent/session authored it
  evidence       : Citation[]    — references to code spans or commits
  tags           : string[]      — free-form taxonomy
}

Citation {
  node_id     : NodeID
  commit_sha  : string
  snippet     : string     — verbatim code excerpt (≤ 500 chars)
}
```

### 3.4 Version Snapshot

A `Snapshot` is a logical point-in-time view of the graph, keyed by commit:

```
Snapshot {
  id          : UUID
  commit_sha  : string
  parent_sha  : string | null
  created_at  : timestamp
  stats       : { nodes: int, edges: int, expertise_nodes: int }
}
```

Snapshots are not full copies; they are commit-SHA bookmarks. The
`version_range` on each node/edge allows the store to reconstruct any
snapshot view via range filtering.

---

## 4. Interfaces

### 4.1 Extraction Pipeline Interface

```typescript
interface ExtractionPipeline {
  /**
   * Full extraction for an entire repository.
   * Returns a stream of GraphFact (node or edge) objects.
   */
  extractAll(repoRoot: string, options: ExtractionOptions): AsyncIterable<GraphFact>;

  /**
   * Incremental extraction for changed files since a base commit.
   * Returns a GraphDiff: inserts, updates, and tombstones.
   */
  extractIncremental(
    repoRoot: string,
    baseCommit: string,
    headCommit: string,
    changedFiles: string[]
  ): Promise<GraphDiff>;
}

interface ExtractionOptions {
  languages: LanguageId[];       // which languages to parse
  enableLSP: boolean;            // enrich with type-resolved edges
  embeddingModel: string;        // model ID for semantic vectors
  maxFileSize: number;           // skip files larger than this (bytes)
}

type GraphFact = NodeFact | EdgeFact;

interface GraphDiff {
  inserts: GraphFact[];
  updates: GraphFact[];          // matched by id, carries new fields
  tombstones: UUID[];            // node/edge IDs to close version_range
}
```

### 4.2 Graph Store Interface

```typescript
interface GraphStore {
  /** Apply a batch of facts (idempotent via content_hash). */
  applyDiff(diff: GraphDiff, commitSha: string): Promise<Snapshot>;

  /** Traverse from a starting node with a query plan. */
  traverse(start: NodeID, plan: TraversalPlan): Promise<TraversalResult>;

  /** ANN vector search on the Semantic layer. */
  vectorSearch(query: float[], topK: number, filter?: NodeFilter): Promise<ScoredNode[]>;

  /** Read a node by ID, optionally at a specific snapshot. */
  getNode(id: NodeID, atCommit?: string): Promise<Node | null>;

  /** Find nodes by qualified name prefix (autocomplete). */
  findByName(prefix: string, kinds?: NodeKind[], limit?: number): Promise<Node[]>;

  /** Get community / cluster summary for a subgraph. */
  getClusterSummary(clusterId: NodeID): Promise<string>;
}

interface TraversalPlan {
  direction: "outbound" | "inbound" | "both";
  edgeTypes: RelationType[];
  maxDepth: number;
  filters?: NodeFilter;
  limit?: number;
}

interface TraversalResult {
  nodes: Node[];
  edges: Edge[];
  paths: NodeID[][];            // discovered traversal paths
}
```

### 4.3 Retrieval Engine Interface

```typescript
interface RetrievalEngine {
  /**
   * Primary entry-point for agents.
   * Accepts a query (natural-language or structured) and a token budget.
   * Returns assembled context ready for prompt insertion.
   */
  retrieve(query: RetrievalQuery): Promise<RetrievalResult>;
}

interface RetrievalQuery {
  text: string;                         // natural-language question
  scope?: string;                       // qualified-name prefix to scope search
  mode?: RetrievalMode;                 // override auto-detected mode
  tokenBudget: number;                  // max tokens in the response
  includeExpertise: boolean;            // whether to include expertise layer
  atCommit?: string;                    // optional snapshot pinning
}

type RetrievalMode =
  | "STRUCTURAL_DRILL"
  | "CALL_TRACE"
  | "SEMANTIC_SEARCH"
  | "EXPERTISE_RECALL"
  | "HYBRID";

interface RetrievalResult {
  /** Markdown-formatted context block for prompt insertion. */
  contextBlock: string;

  /** Token count of contextBlock. */
  tokenCount: number;

  /** Provenance: which nodes contributed, for debugging / feedback. */
  citations: Citation[];

  /** The retrieval mode that was actually used. */
  resolvedMode: RetrievalMode;
}
```

### 4.4 Evolution Engine Interface

```typescript
interface EvolutionEngine {
  /** Agent writes back a learning. */
  recordExpertise(entry: ExpertiseEntry): Promise<NodeID>;

  /** Trigger consolidation pass over a scope (package / module). */
  consolidate(scope: string): Promise<ConsolidationReport>;

  /** Run decay pass: reduce confidence of expertise whose targets changed. */
  decayStaleExpertise(headCommit: string): Promise<number>;  // count of decayed nodes
}

interface ExpertiseEntry {
  kind: "pattern" | "pitfall" | "preference" | "insight";
  title: string;
  body: string;
  appliesTo: NodeID[];           // structural / call-chain nodes this applies to
  scope: "exact" | "subtree";
  evidence: Citation[];
  sourceAgent: string;
}

interface ConsolidationReport {
  merged: number;
  superseded: number;
  retracted: number;
  newTopics: string[];           // community summaries that were created/updated
}
```

### 4.5 Incremental Sync Interface

```typescript
interface IncrementalSync {
  /** Start watching a repo for changes (file-save or commit). */
  watch(repoRoot: string, options: WatchOptions): void;

  /** Stop watching. */
  unwatch(repoRoot: string): void;

  /** Manual trigger: sync to a specific commit. */
  syncToCommit(repoRoot: string, commitSha: string): Promise<Snapshot>;
}

interface WatchOptions {
  /** Debounce interval for file-save events (ms). Default: 1000. */
  debounceMs: number;

  /** Also run Evolution Engine decay on each sync. Default: true. */
  autoDecay: boolean;
}
```

---

## 5. End-to-End Sequence Flows

### 5.1 Initial Repository Onboarding

```
Actor: Developer triggers "onboard repo" command.

1. IncrementalSync.syncToCommit(repoRoot, HEAD)
   ├─ 2. ExtractionPipeline.extractAll(repoRoot, options)
   │     ├─ 2a. Language Parser produces AST facts for each file.
   │     ├─ 2b. Structure Builder emits Structural-layer nodes/edges.
   │     ├─ 2c. Call-Chain Analyzer emits Call-chain-layer edges.
   │     ├─ 2d. Semantic Indexer computes embeddings, emits vectors.
   │     └─ 2e. Fact Normalizer deduplicates, assigns IDs, attaches
   │            version_range = {since: HEAD, until: null}.
   │
   ├─ 3. GraphStore.applyDiff(diff, HEAD)
   │     ├─ 3a. Inserts all nodes and edges.
   │     ├─ 3b. Builds vector index for Semantic layer.
   │     └─ 3c. Creates initial Snapshot record.
   │
   └─ 4. EvolutionEngine.consolidate("*")
         ├─ 4a. Community detection on Structural + Call-chain layers.
         ├─ 4b. LLM generates summary for each community.
         └─ 4c. Stores Topic + SemanticCluster nodes in Semantic layer.

Result: Full graph available; Snapshot created; community summaries populated.
```

### 5.2 Incremental Sync on Commit

```
Trigger: Git post-commit hook or file-system watcher detects changes.

1. IncrementalSync detects changed files between baseCommit and headCommit.

2. ExtractionPipeline.extractIncremental(repoRoot, base, head, changedFiles)
   ├─ 2a. Re-parse only changed files.
   ├─ 2b. Diff new facts against current graph → produces GraphDiff.
   └─ 2c. Mark removed symbols' version_range.until = headCommit.

3. GraphStore.applyDiff(diff, headCommit)
   ├─ 3a. Insert new nodes/edges.
   ├─ 3b. Update modified nodes (new content_hash, re-embedded vectors).
   ├─ 3c. Tombstone deleted nodes/edges.
   └─ 3d. Flag downstream CALLS edges as stale if source signature changed.

4. EvolutionEngine.decayStaleExpertise(headCommit)
   ├─ 4a. Find expertise nodes whose APPLIES_TO targets are tombstoned
   │       or have new content_hash.
   └─ 4b. Reduce their confidence by a decay factor (default: 0.5 per
           major change, 0.9 per minor change).

5. [Conditional] If structural change exceeds community-change threshold:
   └─ EvolutionEngine.consolidate(affectedPackage)
      └─ Re-generates community summaries for affected clusters.

Result: Graph reflects new commit; stale expertise decayed; summaries refreshed.
```

### 5.3 Agent Retrieves Context for a Coding Task

```
Actor: Coding agent needs context to implement a change in module "payments.checkout".

1. Agent calls RetrievalEngine.retrieve({
     text: "How does the checkout flow work, and are there known pitfalls?",
     scope: "payments.checkout",
     tokenBudget: 8000,
     includeExpertise: true
   })

2. Query Planner analyzes the query:
   ├─ 2a. Detects structural intent ("checkout flow") → selects STRUCTURAL_DRILL.
   ├─ 2b. Detects call-chain intent ("flow") → selects CALL_TRACE.
   ├─ 2c. Detects expertise intent ("pitfalls") → selects EXPERTISE_RECALL.
   └─ 2d. Resolves to mode = HYBRID.

3. Graph Traverser (parallel fan-out):
   ├─ 3a. STRUCTURAL_DRILL: BFS from "payments.checkout" Module node,
   │       depth 3, collecting CONTAINS children (classes, functions).
   ├─ 3b. CALL_TRACE: Forward DFS from each public function in checkout,
   │       depth 5, collecting CALLS paths.
   └─ 3c. EXPERTISE_RECALL: Find PATTERN/PITFALL nodes with
          APPLIES_TO edges scoped to "payments.checkout" subtree.

4. Vector Searcher:
   └─ 4a. Embed query text, ANN search for top-20 similar nodes
          filtered to payments.* namespace.

5. Ranker merges results:
   ├─ 5a. Score = 0.35 × structural_proximity
   │     + 0.25 × call_chain_distance⁻¹
   │     + 0.20 × cosine_similarity
   │     + 0.10 × recency_score
   │     + 0.10 × expertise_confidence
   └─ 5b. Deduplicate by node ID; keep highest-scored path.

6. Budget Controller:
   ├─ 6a. Estimates tokens per fragment.
   ├─ 6b. If over budget, collapses low-level functions into parent
   │       class/module summaries.
   └─ 6c. Drops lowest-ranked fragments until within budget.

7. Context Assembler formats the output:
   ├─ ## Structure
   │  payments/checkout/
   │  ├── CheckoutService (class) — orchestrates the checkout flow
   │  ├── PriceCalculator (class) — applies discounts, tax
   │  └── ...
   ├─ ## Call Graph
   │  CheckoutService.process() → PriceCalculator.compute()
   │                             → InventoryClient.reserve()
   │                             → PaymentGateway.charge()
   ├─ ## Related Code
   │  [ranked code snippets with file:line references]
   └─ ## Known Expertise
      ⚠ PITFALL: PaymentGateway.charge() is not idempotent — must use
                 idempotency key. (confidence: 0.91)
      ✓ PATTERN: CheckoutService uses saga pattern for rollback.
                 (confidence: 0.88)

8. Returns RetrievalResult { contextBlock, tokenCount: 6420, citations, resolvedMode: "HYBRID" }.

Result: Agent inserts contextBlock into its system prompt and proceeds with the task.
```

### 5.4 Agent Writes Back Expertise

```
Actor: Agent completes a task and records a learning.

1. Agent calls EvolutionEngine.recordExpertise({
     kind: "pitfall",
     title: "Race condition in concurrent checkout",
     body: "When two requests hit CheckoutService.process() for the same
            cart simultaneously, InventoryClient.reserve() can double-reserve.
            Fix: acquire a distributed lock keyed on cart_id before the saga.",
     appliesTo: [nodeId("CheckoutService.process"), nodeId("InventoryClient.reserve")],
     scope: "exact",
     evidence: [{ node_id: ..., commit_sha: "abc123", snippet: "..." }],
     sourceAgent: "copilot-session-42"
   })

2. Evolution Engine:
   ├─ 2a. Creates a new PITFALL node with confidence = 1.0.
   ├─ 2b. Creates APPLIES_TO edges to CheckoutService.process and
   │       InventoryClient.reserve.
   ├─ 2c. Checks for existing pitfall nodes on the same targets:
   │       ├─ If semantically overlapping (cosine > 0.85): create
   │       │   DERIVED_FROM edge and schedule consolidation.
   │       └─ If no overlap: standalone node.
   └─ 2d. Returns new NodeID.

3. [Deferred] Next consolidation pass:
   ├─ 3a. Finds the new pitfall and the earlier "PaymentGateway not
   │       idempotent" pitfall both touch the checkout saga.
   ├─ 3b. LLM reviews both, determines they are distinct issues.
   └─ 3c. No merge; both remain active.

Result: Expertise graph grows by one node; future retrievals for checkout
        code will surface this pitfall.
```

### 5.5 Cross-Session Learning (Agent Gets Smarter)

```
Timeline: Sessions S1 through Sn interact with the same repo over weeks.

S1: Agent onboards repo. Graph has 0 expertise nodes.
    Agent completes a refactor in payments module.
    Writes back: PATTERN "saga-based rollback in checkout" (confidence 1.0).

S2: Different agent session. Retrieves context for payments.
    Sees the saga pattern in the Expertise section of the context block.
    Uses that knowledge to correctly extend the checkout flow without
    breaking the rollback chain.
    Writes back: INSIGHT "saga steps must be registered in order in
    SagaRegistry; out-of-order causes silent rollback failures."

S3: Code changes land — CheckoutService.process() is refactored.
    Incremental Sync triggers decay:
    ├─ Saga pattern node: target's content_hash changed but pattern
    │   still holds → confidence reduced 1.0 → 0.9.
    └─ Race condition pitfall: target signature unchanged → no decay.

S5: Consolidation runs after a batch of PRs merge.
    ├─ Finds 3 expertise nodes related to the payments saga.
    ├─ LLM merges them into a single PATTERN with higher confidence.
    ├─ Old nodes get SUPERSEDED edges pointing to the merged node.
    └─ A new Topic "Payments Saga Orchestration" is created in the
       Semantic layer with a community summary.

S8: A new agent session retrieves context for payments.
    Gets the consolidated, high-confidence pattern and the community
    summary. Produces a higher-quality completion because the memory
    has been refined through multiple sessions.

Result: The system accumulates expertise that compounds across sessions.
        Each agent session is smarter than the last for the same codebase.
```

---

## 6. Deployment Topology

```
┌──────────────────────────────────────────────────────────────────┐
│  Lightweight (single-dev, local)                                 │
│                                                                  │
│  SQLite graph store + in-process vector index (usearch)          │
│  File-system watcher for incremental sync                        │
│  All components in a single process / CLI daemon                 │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│  Team / CI (shared)                                              │
│                                                                  │
│  Neo4j / Memgraph for graph store                                │
│  Qdrant / pgvector for vector index                              │
│  Git webhook for incremental sync                                │
│  Retrieval Engine exposed as gRPC / HTTP service                 │
│  Evolution Engine runs as async worker (queue-driven)            │
└──────────────────────────────────────────────────────────────────┘
```

---

## 7. Key Design Decisions & Trade-offs

| Decision | Rationale | Trade-off |
|---|---|---|
| **Four separate graph layers sharing a node namespace** | Avoids the entanglement problem identified by MAGMA. Each layer can be queried, indexed, and evolved independently. | Cross-layer queries require joins; slight overhead vs. a single unified graph. |
| **Tree-sitter as primary parser, LSP as optional enrichment** | Tree-sitter is fast, offline, and supports 100+ languages. Guarantees the pipeline always works, even without a running language server. | Loses type-resolved call edges for dynamic languages when LSP is unavailable. |
| **Version ranges on nodes/edges instead of full graph snapshots** | Space-efficient: O(changed entities) per commit instead of O(total graph). Enables time-travel queries. | Point-in-time reconstruction requires range filtering, which adds query complexity. |
| **LLM-powered consolidation in the Evolution Engine** | Semantic merge of expertise nodes is inherently a natural-language task. Rules-based merging misses nuance. | Adds LLM cost; consolidation is batched and async to amortize. |
| **Token-budget-aware retrieval with hierarchical fallback** | Agents have finite context windows. The system must be useful even at small budgets (2K tokens). | Summaries lose detail. The system compensates by providing citations so the agent can drill down. |
| **Confidence decay on expertise nodes** | Prevents stale advice from poisoning agent context after code refactors. | Aggressive decay can lose still-valid insights. Tunable decay factor mitigates this. |

---

## 8. Appendix: Glossary

| Term | Definition |
|---|---|
| **Graph Fact** | A single node or edge record produced by the Extraction Pipeline. |
| **Graph Diff** | A batch of inserts, updates, and tombstones applied atomically to the Graph Store. |
| **Tombstone** | A soft-delete: the node/edge's `version_range.until` is set to the current commit SHA. The record is retained for time-travel. |
| **Community** | A cluster of tightly-coupled nodes detected by graph partitioning (e.g., Louvain algorithm) on the Structural + Call-chain layers. |
| **Expertise Node** | An agent-authored knowledge record (pattern, pitfall, preference, or insight) stored in the Expertise layer. |
| **Snapshot** | A logical point-in-time bookmark keyed by commit SHA, used for time-travel retrieval. |
| **Decay Factor** | A multiplier (0 < d ≤ 1) applied to an expertise node's confidence when its associated code changes. |
| **Context Block** | The final Markdown document returned by the Context Assembler, ready for insertion into an LLM prompt. |
