# Implementation Plan: Agent Memory via Graph-Based Code Intelligence

## Problem Statement

Agents operating on codebases lack persistent, structured memory. Each session
starts cold — the agent re-discovers the same call chains, module boundaries,
and architectural patterns every time. This plan delivers a **graph-based
agent memory** system that captures **top-down repo structure** and
**call-chain context** as a queryable knowledge graph, enabling agents to
accumulate expertise and retrieve relevant context efficiently.

The design draws on the taxonomy from the [Graph-based Agent Memory
survey](https://arxiv.org/abs/2602.05665) (Yang et al., 2026) and its
companion resource list at
[DEEP-PolyU/Awesome-GraphMemory](https://github.com/DEEP-PolyU/Awesome-GraphMemory),
adapting the memory lifecycle (extraction → storage → retrieval → evolution) to
the code-intelligence domain.

---

## Architecture Overview

The graph is organized into four trust-separated layers:

| Layer | Contents | Source | Trust Level |
|-------|----------|--------|-------------|
| **Fact** | Structural hierarchy (repo → dir → file → class → function), call edges, import edges, inheritance/implements edges | AST parsing, language analysis | High — deterministic |
| **Derived** | Embeddings, LLM-generated summaries, computed centrality scores | Embedding models, LLM | Medium — regenerable |
| **Interaction** | Agent query logs, retrieval hits, usefulness signals, annotations | Agent runtime | Medium — empirical |
| **Temporal** | Commit provenance, modification timestamps, version snapshots | Git history | High — deterministic |

Separating facts from derived knowledge ensures agents can trust structural
data while treating summaries and patterns as advisory.

### Graph Schema (Core Node and Edge Types)

**Node types:**

| Node Type | Key Properties |
|-----------|----------------|
| `Repository` | name, root_path, default_branch |
| `Directory` | path, depth |
| `File` | path, language, hash, loc |
| `Module` | qualified_name, file_ref |
| `Class` | name, qualified_name, docstring_hash |
| `Function` | name, qualified_name, signature, is_exported, complexity |
| `Type` | name, qualified_name, kind (interface/enum/alias) |
| `Symbol` | name, kind, scope |
| `ExpertiseNote` | summary, pattern_type, confidence, source |

**Edge types:**

| Edge Type | From → To | Layer |
|-----------|-----------|-------|
| `CONTAINS` | Directory → File, File → Class, Class → Function, etc. | Fact |
| `CALLS` | Function → Function | Fact |
| `IMPORTS` | File → File (or Module → Module) | Fact |
| `INHERITS` | Class → Class | Fact |
| `IMPLEMENTS` | Class → Type | Fact |
| `REFERENCES` | Function → Symbol | Fact |
| `DEPENDS_ON` | Module → Module (transitive) | Derived |
| `SIMILAR_TO` | any → any (embedding cosine > threshold) | Derived |
| `MODIFIED_IN` | any → Commit | Temporal |
| `ANNOTATED_BY` | any → ExpertiseNote | Interaction |

**Identity model:** Every code node carries a **canonical ID** formed as
`{repo}:{language}:{qualified_name}:{content_hash_short}`. The qualified name
provides stability across commits; the content hash disambiguates when
signatures diverge. A reconciliation table maps old IDs to current IDs after
renames/moves (detected via git rename tracking + AST signature matching).

---

## Phase 0 — Evaluation Contract & Schema Foundation

**Goal:** Establish success criteria before writing extraction code. Define the
queries the memory system must answer, build a gold-standard test set, and lock
the storage schema.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 0.1 | Define **top-10 agent queries** the memory must support (see below) | Yes |
| 0.2 | Build **2–3 fixture repos** with known structure for validation | Yes |
| 0.3 | Create **gold-standard context sets**: for each query on each fixture, the expected nodes/edges returned | Depends on 0.1, 0.2 |
| 0.4 | Lock **graph schema** (node types, edge types, property contracts) | Yes |
| 0.5 | Implement **storage layer** — graph persistence with CRUD | Depends on 0.4 |
| 0.6 | Define **canonical identity** scheme and reconciliation table | Yes |
| 0.7 | Set up **evaluation harness** — precision/recall/MRR over gold sets | Depends on 0.3, 0.5 |

### Target Agent Queries (initial set)

1. "What does function `X` do and who calls it?"
2. "Show me the call chain from endpoint `A` down to data access layer."
3. "What modules does file `F` depend on (transitively)?"
4. "Find all implementations of interface `I`."
5. "What changed in module `M` since commit `C`?"
6. "Find functions similar to `X` (by signature/embedding)."
7. "What is the architectural pattern used in directory `D`?"
8. "Which functions are most central (highest fan-in) in this repo?"
9. "Show me the error-handling pattern in this codebase."
10. "What did the agent learn about module `M` from previous sessions?"

### Hard Dependencies

- Phase 1 cannot start until 0.4 (schema) and 0.5 (storage) are complete.
- Evaluation harness (0.7) should be ready before Phase 1 ends, so extraction
  output can be validated immediately.

### Deliverables

- `schema.ts` (or equivalent): typed graph schema with node/edge definitions
- `storage/` module: graph CRUD abstraction (single backend — no premature
  abstraction)
- `eval/` module: harness + fixture repos + gold sets
- `IDENTITY.md`: canonical ID specification

---

## Phase 1 — Structural Extraction (Top-Down Repo Graph)

**Goal:** Parse a repository top-down and populate the Fact layer with
structural nodes and `CONTAINS` edges. **Single language first** (TypeScript),
with a parser-plugin interface for adding languages later.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 1.1 | Implement **repo walker**: enumerate directories and files, create `Repository`, `Directory`, `File` nodes with `CONTAINS` edges | Yes |
| 1.2 | Implement **TypeScript AST parser plugin**: extract `Module`, `Class`, `Function`, `Type` nodes from each file | Yes |
| 1.3 | Wire parser output into **graph storage**: create nodes with canonical IDs, attach to file via `CONTAINS` | Depends on 0.5 |
| 1.4 | Extract **exported symbol** metadata (is_exported, visibility) | Part of 1.2 |
| 1.5 | Generate **provenance metadata** for each node (source=AST, extraction_version, file_hash) | Part of 1.3 |
| 1.6 | Run **evaluation harness** against fixture repos — validate structural queries (#1, #8 from query list) | Depends on 1.3 |
| 1.7 | Implement **Python AST parser plugin** (second language) | Parallel with 1.3–1.6 |

### Parser Plugin Interface

```
interface LanguageParser {
  language: string;
  supportedExtensions: string[];
  parse(filePath: string, content: string): ParseResult;
}

interface ParseResult {
  modules: ModuleNode[];
  classes: ClassNode[];
  functions: FunctionNode[];
  types: TypeNode[];
  symbols: SymbolNode[];
}
```

Each parser plugin returns a flat list of typed nodes; the orchestrator wires
them into the graph with `CONTAINS` edges and canonical IDs.

### Hard Dependencies

- Requires Phase 0 schema and storage.
- Parser plugin interface must be defined before 1.2 and 1.7 can start.

### Deliverables

- `parsers/typescript.ts`: TS parser plugin
- `parsers/python.ts`: Python parser plugin
- `extraction/structural.ts`: repo walker + orchestrator
- Fixture repos fully indexed and passing eval queries

---

## Phase 2 — Semantic Extraction (Call Chains & Relationships)

**Goal:** Add `CALLS`, `IMPORTS`, `INHERITS`, `IMPLEMENTS`, and `REFERENCES`
edges to the Fact layer. This is the call-chain context the story specifically
targets.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 2.1 | Extract **intra-file call edges** from AST (function → function within same file) | Yes |
| 2.2 | Extract **import declarations** and resolve to target files/modules → create `IMPORTS` edges | Yes |
| 2.3 | Resolve **cross-file call edges** using import graph + symbol table | Depends on 2.2 |
| 2.4 | Extract **inheritance** (`extends`) and **implements** edges from class declarations | Yes |
| 2.5 | Build **transitive dependency edges** (`DEPENDS_ON`) via depth-limited BFS over imports+calls (max depth configurable, default 5) | Depends on 2.3 |
| 2.6 | Implement **call-chain query**: given a function, return the full call tree up/down to configurable depth | Depends on 2.5 |
| 2.7 | Run **evaluation harness** — validate call-chain queries (#2, #3, #4 from query list) | Depends on 2.6 |

### Cross-File Resolution Strategy

1. Parse all import statements to build a file-level import graph.
2. Build a global symbol table: `{ qualified_name → node_id }`.
3. For each unresolved call in a function body, look up the symbol in the
   import chain, then in the global table.
4. Unresolved calls are stored as `CALLS` edges with `resolved: false` and
   a `target_name` property for later resolution.

**Scope exclusion (deferred):** Data-flow analysis (parameter passing, return
value tracking) requires compiler/type-checker integration beyond AST walking.
Deferred to a later iteration to avoid drag on the MVP.

### Hard Dependencies

- Phase 1 structural nodes must exist to attach edges.
- Items 2.1–2.4 are parallel-safe once Phase 1 is complete.
- Item 2.5 depends on 2.3 (needs cross-file edges).

### Deliverables

- `extraction/semantic.ts`: call/import/type edge extractor
- `extraction/symbol-table.ts`: global symbol resolution
- `queries/call-chain.ts`: call-chain traversal query
- Eval queries #2, #3, #4 passing on fixture repos

---

## Phase 3 — Retrieval & Context Assembly

**Goal:** Build the retrieval system that agents actually call. Combines graph
traversal with optional vector similarity for hybrid search.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 3.1 | Implement **graph traversal queries**: neighbors, ancestors, descendants, shortest path between nodes | Yes |
| 3.2 | Implement **node embedding pipeline**: generate embeddings for function signatures + docstrings, store as node properties | Yes |
| 3.3 | Implement **hybrid retrieval**: given a natural-language query, (a) embed the query, (b) find top-K similar nodes, (c) expand each via graph neighbors | Depends on 3.1, 3.2 |
| 3.4 | Implement **context assembly**: rank retrieved nodes, serialize into a token-budgeted context window (respecting LLM context limits) | Depends on 3.3 |
| 3.5 | Implement **relevance scoring** function: `score = w1 * structural_distance + w2 * semantic_similarity + w3 * centrality + w4 * recency` | Depends on 3.1 |
| 3.6 | Implement **multi-round retrieval**: initial broad search → agent feedback → refine via graph neighbors (inspired by the "multi-round retrieval" strategies in the survey) | Depends on 3.3 |
| 3.7 | Run **full evaluation** against gold sets — measure precision@K, recall@K, MRR | Depends on 3.4 |

### Context Assembly Algorithm

```
1. Parse agent query → extract intent + mentioned symbols
2. If symbols found → anchor search on matching graph nodes
3. If no symbols → fall back to embedding similarity search
4. For each anchor node:
   a. Walk CALLS edges up/down (configurable depth)
   b. Walk CONTAINS edges up to file/module level
   c. Walk IMPORTS edges to direct dependencies
5. Score all collected nodes via relevance function
6. Sort by score, pack into context window until token budget exhausted
7. Return context + provenance metadata (which nodes, why selected)
```

### Hard Dependencies

- Requires populated graph from Phase 1 + 2.
- Embedding pipeline (3.2) can start as soon as Phase 1 nodes exist.
- Hybrid retrieval (3.3) needs both graph traversal and embeddings.

### Deliverables

- `retrieval/traversal.ts`: graph query primitives
- `retrieval/embedding.ts`: embedding generation + similarity search
- `retrieval/hybrid.ts`: hybrid retrieval orchestrator
- `retrieval/context.ts`: context assembly + token budgeting
- `retrieval/scoring.ts`: relevance scoring function
- Eval report with precision/recall/MRR numbers

---

## Phase 4 — Incremental Updates & Temporal Tracking

**Goal:** The graph must stay current as code changes. Implement diff-aware
incremental updates with proper dependency invalidation, and attach temporal
metadata for recency-aware retrieval.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 4.1 | Implement **change detection**: given two commits, compute changed files via `git diff --name-status` | Yes |
| 4.2 | Implement **dependency invalidation**: when file F changes, identify all nodes that import/call into F and mark them `dirty` | Depends on 4.1 |
| 4.3 | Implement **incremental re-extraction**: re-parse dirty files, reconcile nodes (update existing, create new, tombstone deleted) using canonical IDs | Depends on 4.2 |
| 4.4 | Implement **rename/move detection**: use git rename tracking + AST signature matching to update canonical ID mappings | Depends on 4.3 |
| 4.5 | Attach **`MODIFIED_IN`** temporal edges linking nodes to commits | Depends on 4.3 |
| 4.6 | Implement **staleness scoring**: combine recency, centrality (high fan-in code is important even if old), and retrieval frequency — avoid naive "not touched = stale" | Depends on 4.5 |
| 4.7 | Implement **full-rebuild fallback**: periodic reconciliation pass that re-indexes from scratch and diffs against existing graph to catch drift | Depends on 4.3 |
| 4.8 | Implement **branch awareness**: memory queries scoped to a branch; graph overlays for feature branches that merge back to main | Depends on 4.5 |

### Invalidation Model

```
On file change (F):
  1. Re-parse F → extract new nodes/edges
  2. Diff against existing nodes for F → apply creates/updates/deletes
  3. For each deleted/changed exported symbol in F:
     a. Find all IMPORTS edges pointing to F
     b. Find all CALLS edges targeting symbols in F
     c. Mark source nodes of those edges as "needs-reverify"
  4. Re-resolve edges for "needs-reverify" nodes
  5. Update MODIFIED_IN temporal edges
```

### Hard Dependencies

- Requires Phase 1 + 2 extraction pipeline.
- Can run in parallel with Phase 3 retrieval work.
- Branch awareness (4.8) depends on temporal edges (4.5).

### Deliverables

- `update/change-detector.ts`: git-diff-based change detection
- `update/invalidator.ts`: dependency invalidation engine
- `update/reconciler.ts`: incremental node reconciliation
- `update/temporal.ts`: commit provenance + staleness scoring
- Validation: modify fixture repos, verify graph stays consistent

---

## Phase 5 — Agent API & Integration

**Goal:** Expose the memory system as a callable service that agents use to
retrieve context and write back learnings.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 5.1 | Define **Agent Memory API** contract (OpenAPI or MCP tool schema) | Yes |
| 5.2 | Implement **read endpoints**: query context, get call chain, find similar, get module summary | Depends on Phase 3 |
| 5.3 | Implement **write endpoints**: annotate node, record useful/not-useful signal, add expertise note | Depends on 5.1 |
| 5.4 | Implement **agent interaction logging**: record every query + retrieved context + agent feedback into Interaction layer | Depends on 5.2, 5.3 |
| 5.5 | Build **MCP tool wrapper** so the memory is usable from MCP-compatible agents | Depends on 5.2 |
| 5.6 | Implement **observability**: structured logs tracing query → retrieval path → context assembly → agent decision | Depends on 5.4 |
| 5.7 | End-to-end **integration test**: agent performs a multi-turn coding task using memory, verify context quality | Depends on all above |

### API Surface (Draft)

```
memory.query(
  query: string,
  options: {
    repo: string,
    branch?: string,
    maxTokens?: number,
    depth?: number,
    includeCallChain?: boolean,
    includeImports?: boolean,
  }
) → ContextResult {
  nodes: RankedNode[],
  edges: Edge[],
  provenance: ProvenanceTrace[],
  tokenCount: number,
}

memory.annotate(
  nodeId: string,
  annotation: {
    type: "useful" | "not_useful" | "expertise_note",
    content?: string,
    agentSessionId: string,
  }
) → void

memory.getCallChain(
  functionId: string,
  direction: "callers" | "callees" | "both",
  depth: number,
) → CallChainResult

memory.findSimilar(
  query: string | nodeId: string,
  topK: number,
) → SimilarNode[]
```

### Hard Dependencies

- Read endpoints depend on Phase 3 retrieval system.
- Write endpoints depend on graph storage (Phase 0).
- MCP wrapper depends on read/write endpoints.

### Deliverables

- `api/memory-api.ts`: API implementation
- `api/mcp-tool.ts`: MCP tool definition
- `api/logging.ts`: interaction logging
- Integration test suite

---

## Phase 6 — Expertise & Learning (Post-MVP)

**Goal:** Enable the memory to accumulate cross-cutting expertise over time —
the "agent gets smarter" requirement. This phase is explicitly **post-MVP**;
it depends on having working retrieval (Phase 3), interaction logging
(Phase 5), and enough usage data to extract meaningful patterns.

### Work Items

| ID | Item | Parallel-safe? |
|----|------|----------------|
| 6.1 | Implement **usage analytics**: aggregate which nodes are queried most, which lead to successful agent outcomes | Depends on Phase 5 interaction logs |
| 6.2 | Implement **pattern extraction**: identify recurring code patterns (error handling, middleware chains, etc.) via clustering over frequently co-retrieved subgraphs | Depends on 6.1 |
| 6.3 | Create **ExpertiseNote nodes**: attach summarized patterns to relevant modules/directories as `ANNOTATED_BY` edges | Depends on 6.2 |
| 6.4 | Implement **memory consolidation**: merge redundant expertise notes, prune low-confidence notes, update summaries as patterns evolve | Depends on 6.3 |
| 6.5 | Implement **LLM-generated summaries** for modules/directories (background, async, cached, regenerable) | Parallel with 6.1–6.4 |
| 6.6 | Implement **cross-session learning**: load expertise from previous sessions, let agents build on prior knowledge | Depends on 6.3 |

### Key Design Decisions

- **Expertise is derived, not asserted.** Expertise nodes are generated from
  observed retrieval patterns and agent feedback, not from one-shot LLM
  generation. This ensures the memory learns from actual usage.
- **Confidence model.** Each ExpertiseNote carries a confidence score based on
  the number of supporting observations, recency, and agent feedback
  (useful/not-useful signals).
- **LLM summaries are optional.** They are generated in the background, cached,
  and tagged with provenance. They do not block core graph operations and can
  be regenerated at any time.

### Hard Dependencies

- Requires substantial interaction data from Phase 5 to be meaningful.
- Memory consolidation (6.4) requires expertise notes (6.3).

### Deliverables

- `expertise/analytics.ts`: usage aggregation
- `expertise/patterns.ts`: pattern extraction
- `expertise/consolidation.ts`: merge/prune engine
- `expertise/summaries.ts`: LLM summary pipeline
- Evaluation: measure whether expertise improves retrieval quality over time

---

## Dependency Graph

```
Phase 0: Schema & Eval Foundation
  │
  ├──→ Phase 1: Structural Extraction ──→ Phase 2: Semantic Extraction
  │         │                                    │
  │         │              ┌─────────────────────┤
  │         │              │                     │
  │         ▼              ▼                     ▼
  │    Phase 4: Incremental Updates    Phase 3: Retrieval & Context
  │         │                                    │
  │         └──────────┬─────────────────────────┘
  │                    │
  │                    ▼
  │              Phase 5: Agent API & Integration
  │                    │
  │                    ▼
  └──────────── Phase 6: Expertise & Learning (post-MVP)
```

**Critical path:** 0 → 1 → 2 → 3 → 5

**Parallel tracks:**
- Phase 4 (incremental updates) can proceed alongside Phase 3 once Phase 2 is
  complete.
- Embedding pipeline (3.2) can start as soon as Phase 1 nodes exist.
- Python parser (1.7) is parallel with TS parser integration testing.
- Evaluation harness improvements run continuously.
- Fixture repo construction is parallel with Phase 0 schema work.

---

## MVP Boundary

The **MVP** (Phases 0–5) delivers:

| Capability | Status |
|------------|--------|
| Top-down repo structure graph | ✅ MVP |
| Call-chain context (calls, imports, inherits) | ✅ MVP |
| Hybrid retrieval (graph + vector) | ✅ MVP |
| Token-budgeted context assembly | ✅ MVP |
| Incremental graph updates | ✅ MVP |
| Agent-callable API (MCP tool) | ✅ MVP |
| Branch-aware queries | ✅ MVP |
| TypeScript support | ✅ MVP |
| Python support | ✅ MVP |
| Data-flow analysis | ❌ Deferred |
| Expertise/pattern learning | ❌ Post-MVP (Phase 6) |
| Multi-language parity beyond TS/Python | ❌ Deferred |
| Agent write-back & self-evolution | ⚠️ Basic in MVP, full in Phase 6 |

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Cross-file symbol resolution accuracy | High — incorrect call edges poison the graph | Start with direct imports only; track resolution confidence; keep unresolved edges explicit |
| Graph size for large repos (100K+ files) | Medium — slow queries, high storage | Implement lazy loading; index only changed subtrees; use graph partitioning by module |
| LLM summary quality/cost | Medium — hallucinated summaries degrade trust | Make summaries optional, background, regenerable; tag provenance; keep them out of core retrieval ranking |
| Incremental update consistency | High — stale graph worse than no graph | Dependency invalidation; periodic full reconciliation; dirty-state tracking |
| Symbol identity across renames | Medium — broken IDs fragment history | Git rename tracking + AST signature matching; reconciliation table; fallback to full rebuild |
| Branch isolation complexity | Medium — wrong-branch data in retrieval | Scope all queries to a branch; use graph overlays for feature branches |
| Embedding model drift | Low — similarity rankings shift | Version embeddings; re-embed on model change; A/B test retrieval quality |

---

## Technology Recommendations

| Concern | Recommendation | Rationale |
|---------|---------------|-----------|
| Graph storage | **Neo4j Community** (dev/staging) with bolt protocol; evaluate lighter alternatives (SQLite + adjacency tables) for embedded use | Mature Cypher query language, good traversal performance, large ecosystem |
| AST parsing | **TypeScript Compiler API** (ts-morph) for TS; **tree-sitter** for Python and future languages | ts-morph gives type-checked AST; tree-sitter is fast, multi-language, incremental |
| Embeddings | **OpenAI text-embedding-3-small** or **Nomic Embed** for code | Good code understanding, reasonable cost, small vector size |
| Vector index | **In-graph property** (small repos) or **pgvector/Qdrant sidecar** (large repos) | Avoid a separate vector DB until scale demands it |
| API surface | **MCP tool protocol** with TypeScript SDK | Native integration with MCP-compatible agents |
| Observability | **OpenTelemetry spans** on every retrieval path | Structured tracing without vendor lock-in |

---

## Open Questions

1. **Language priority beyond TS/Python.** Should we target Go, Java, C# next?
   Driven by which codebases agents operate on most.
2. **Graph storage at scale.** At what repo size does Neo4j become a bottleneck?
   Need benchmarking data from Phase 0 fixture repos.
3. **Embedding model choice.** Code-specialized models (CodeBERT, StarCoder
   embeddings) vs. general-purpose (OpenAI). Need A/B comparison on retrieval
   quality.
4. **Expertise extraction triggers.** How many interaction samples are needed
   before pattern extraction is meaningful? Needs empirical calibration in
   Phase 6.
5. **Multi-repo memory.** Should the graph span multiple repositories (monorepo
   scenario)? Architectural decision needed before Phase 1 if cross-repo edges
   are required.

---

## References

- Yang et al., "Graph-based Agent Memory: Taxonomy, Techniques, and
  Applications," arXiv:2602.05665, 2026.
- [Awesome-GraphMemory](https://github.com/DEEP-PolyU/Awesome-GraphMemory) —
  curated resource list.
- Zep/Graphiti — temporal knowledge graph for agent memory (arXiv:2501.13956).
- Mem0 — production-ready agent memory with graph storage (arXiv:2504.19413).
- MAGMA — multi-graph agentic memory architecture (arXiv:2601.03236).
- HiAgent — hierarchical working memory for long-horizon tasks (ACL'25).
- Microsoft GraphRAG — graph-based RAG for summarization (arXiv:2404.16130).
