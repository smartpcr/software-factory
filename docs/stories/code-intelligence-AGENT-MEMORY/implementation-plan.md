# Implementation Plan — Agent Memory

**Story:** code-intelligence:AGENT-MEMORY  
**Points:** 10  
**Approach:** Build a graph-based agent memory system that captures top-down repo structure and call-chain context, enabling agents to accumulate expertise and construct richer context over time.

---

## Problem Statement

Current code-intelligence agents operate statelessly — each session starts from scratch with no memory of prior interactions, codebase structure, or learned patterns. This means:

- Agents repeatedly re-discover the same architectural patterns, module boundaries, and call flows.
- Context construction relies on naive chunked-file retrieval (RAG), which misses relational structure.
- There is no mechanism to reinforce successful problem-solving paths or prune ineffective ones.

We need a **graph-based memory layer** that models code as a structured, hierarchical knowledge graph — capturing entities (files, modules, classes, functions), their relationships (calls, imports, contains, inherits), and temporal evolution (commits, edits). This memory enables agents to retrieve structurally-aware context and improve over time.

## Research Foundation

This plan is grounded in the taxonomy from Yang et al., "Graph-based Agent Memory: Taxonomy, Techniques, and Applications" (arXiv:2602.05665) and validated against production systems:

| System | Key Insight Adopted |
|--------|-------------------|
| **Zep / Graphiti** | Temporally-aware knowledge graph with dynamic entity resolution; version-stamped edges tied to commit history |
| **Mem0** | Scalable extraction → consolidation → retrieval pipeline; graph memory variant outperforms flat memory |
| **MAGMA** | Orthogonal graph views (semantic, structural, temporal) with policy-guided traversal |
| **GraphRAG** | Community-based summarization for global sensemaking over large corpora |
| **HiAgent** | Hierarchical working memory — zoom from function → class → module → package |
| **G-Memory** | Hierarchical tracing for multi-agent memory with similarity + graph retrieval |
| **Reflexion** | Verbal reinforcement — persist success/failure signals as experiential memory |

**Design choice rationale:** We adopt a **single typed-edge property graph** (not MAGMA's multi-graph) stored in **SQLite** (not a full graph DB). This minimises infrastructure cost while preserving the key structural advantages. Multi-graph decomposition is deferred to a follow-up story if query ergonomics demand it.

---

## Phase 0 — Evaluation Baseline & Gold Set

**Goal:** Establish a measurable baseline before building anything, so every subsequent phase can demonstrate value.

**Deliverables:**
1. **Gold query set** (20–30 queries) against a reference codebase (our own repo or a well-known OSS project like `fastapi` or `flask`):
   - "What calls `authenticate_user`?"
   - "What is the module structure of the auth subsystem?"
   - "Trace the data flow from HTTP request to database write in the user-creation endpoint."
   - "What are the main architectural layers?"
2. **Expected answers** — manually curated list of relevant symbols/files/relationships per query.
3. **Naive RAG baseline** — chunked-file embeddings (chunk size 512, overlap 64) with top-k cosine similarity retrieval. Measure precision@5, recall@10, and MRR against the gold set.
4. **Evaluation harness** — a script that runs queries against a retriever implementation and scores against the gold set. Outputs a summary table.

**Effort:** ~1 day  
**Dependencies:** None  
**Parallel-safe:** Yes — can run concurrently with Phase 1A schema design.

---

## Phase 1 — Graph Schema & Storage Abstraction

### Phase 1A — Entity/Edge Model & Canonical IDs

**Goal:** Define the schema that all extractors emit into and all retrievers read from.

**Node types (entity kinds):**

| Kind | Properties |
|------|-----------|
| `repository` | name, url, default_branch |
| `package` | name, path |
| `module` / `file` | path, language, loc, hash |
| `class` | name, qualified_name, docstring_summary |
| `function` | name, qualified_name, signature, docstring_summary, is_method, is_async |
| `variable` / `constant` | name, qualified_name, type_annotation |
| `external_dep` | package_name, version_spec |

**Edge types (relationship kinds):**

| Edge | Source → Target | Properties |
|------|----------------|-----------|
| `CONTAINS` | package/module/class → child | ordinal (ordering) |
| `CALLS` | function → function | call_site (file:line), is_dynamic (bool) |
| `IMPORTS` | module → module/symbol | alias, is_reexport |
| `INHERITS` | class → class | — |
| `IMPLEMENTS` | class → interface/protocol | — |
| `REFERENCES` | function → variable/class | access_kind (read/write) |
| `DEPENDS_ON` | module → external_dep | — |
| `DATA_FLOW` | function → function | via_param, via_return |

**Provenance fields on every node and edge:**

```
source_file: str          # file that was parsed to produce this fact
source_span: (line, col, end_line, end_col)  # location in source
extractor: str            # name + version of the extractor that produced this
confidence: float         # 1.0 for resolved static calls; <1.0 for inferred/dynamic
commit_sha: str           # git commit at extraction time
last_seen_at: datetime    # timestamp of most recent extraction confirming this fact
```

**Canonical ID scheme:** `{entity_kind}:{qualified_name}@{commit_sha_short}` — e.g., `function:auth.service.authenticate_user@a3b1c2d`. The `@commit` suffix is optional for latest-HEAD queries.

**Effort:** ~0.5 day  
**Dependencies:** None

### Phase 1B — SQLite Persistence Layer

**Goal:** Implement the storage abstraction with SQLite as the backend.

**Schema (3 core tables + 2 index tables):**

```sql
CREATE TABLE nodes (
    id            TEXT PRIMARY KEY,   -- canonical ID
    kind          TEXT NOT NULL,      -- entity kind enum
    qualified_name TEXT NOT NULL,
    properties    JSON NOT NULL,      -- kind-specific props
    embedding     BLOB,              -- vector embedding (nullable, filled later)
    source_file   TEXT,
    source_span   TEXT,              -- "line:col-end_line:end_col"
    extractor     TEXT,
    confidence    REAL DEFAULT 1.0,
    commit_sha    TEXT,
    last_seen_at  TEXT DEFAULT (datetime('now')),
    created_at    TEXT DEFAULT (datetime('now'))
);

CREATE TABLE edges (
    id            TEXT PRIMARY KEY,   -- deterministic hash of (source, target, kind)
    source_id     TEXT NOT NULL REFERENCES nodes(id),
    target_id     TEXT NOT NULL REFERENCES nodes(id),
    kind          TEXT NOT NULL,      -- edge kind enum
    properties    JSON,
    source_file   TEXT,
    source_span   TEXT,
    extractor     TEXT,
    confidence    REAL DEFAULT 1.0,
    commit_sha    TEXT,
    last_seen_at  TEXT DEFAULT (datetime('now')),
    created_at    TEXT DEFAULT (datetime('now'))
);

CREATE TABLE community_summaries (
    id            TEXT PRIMARY KEY,
    level         INTEGER NOT NULL,   -- hierarchy level (0=leaf, higher=coarser)
    member_ids    JSON NOT NULL,      -- list of node IDs in this community
    summary       TEXT NOT NULL,      -- LLM-generated natural language summary
    commit_sha    TEXT,
    created_at    TEXT DEFAULT (datetime('now'))
);

-- Indexes for traversal
CREATE INDEX idx_edges_source ON edges(source_id, kind);
CREATE INDEX idx_edges_target ON edges(target_id, kind);
CREATE INDEX idx_nodes_kind   ON nodes(kind);
CREATE INDEX idx_nodes_qname  ON nodes(qualified_name);
CREATE INDEX idx_nodes_file   ON nodes(source_file);
```

**Storage abstraction API:**

```python
class MemoryGraph:
    def upsert_node(self, node: Node) -> None
    def upsert_edge(self, edge: Edge) -> None
    def batch_upsert(self, nodes: list[Node], edges: list[Edge]) -> None
    def get_node(self, node_id: str) -> Node | None
    def get_neighbors(self, node_id: str, edge_kinds: list[str], direction: str) -> list[Node]
    def get_subgraph(self, root_id: str, max_hops: int, edge_kinds: list[str]) -> SubGraph
    def get_ancestors(self, node_id: str) -> list[Node]  # walk CONTAINS edges upward
    def prune_stale(self, before_commit: str) -> int  # remove nodes not seen since commit
    def vector_search(self, query_embedding: list[float], top_k: int) -> list[Node]
```

**Effort:** ~1.5 days  
**Dependencies:** Phase 1A (schema)

---

## Phase 2 — Memory Extraction Pipeline

**Goal:** Parse source code into graph triples. Scope to **two languages**: one statically-typed (TypeScript), one dynamically-typed (Python). Define a **language-neutral intermediate representation (IR)** at the output boundary so future languages plug in without changing downstream.

### Phase 2A — Extraction IR & Orchestrator

**Intermediate representation:** Each extractor emits a list of `ExtractionResult`:

```python
@dataclass
class ExtractionResult:
    nodes: list[Node]
    edges: list[Edge]
    source_language: str
    source_files: list[str]
    extractor_name: str
    extractor_version: str
    commit_sha: str
```

**Orchestrator responsibilities:**
1. Discover files by language (glob patterns + `.gitignore` respect).
2. Run the correct language extractor per file.
3. Merge results — deduplicate nodes by qualified name, merge edges, keep highest-confidence version.
4. Batch-upsert into the `MemoryGraph`.
5. Mark nodes/edges not seen in this extraction run as potentially stale.
6. Support **incremental mode**: accept a list of changed files (from `git diff`) and re-extract only those files plus their direct dependents.

**Effort:** ~1 day  
**Dependencies:** Phase 1B (storage layer)  
**Parallel-safe:** IR design can start during Phase 1.

### Phase 2B — Python Extractor

**Tool:** Python `ast` module (stdlib) + optional `jedi` for cross-module resolution.

**Extractions:**
- **Containment hierarchy:** `repository → package (directories with `__init__.py`) → module (.py files) → class → function/method → nested function`
- **Call graph:** Walk `ast.Call` nodes, resolve to qualified names where possible. Mark `confidence=0.5` for dynamic/unresolved calls.
- **Imports:** `ast.Import` and `ast.ImportFrom` → `IMPORTS` edges. Distinguish stdlib, third-party, local.
- **Inheritance:** `ast.ClassDef.bases` → `INHERITS` edges.
- **References:** Variable reads/writes within function bodies → `REFERENCES` edges (best-effort).
- **Docstrings:** Extract first-line summary for `docstring_summary` property.

**Known hard cases (explicit handling):**
- `getattr()` / dynamic dispatch → emit edge with `is_dynamic=true`, `confidence=0.3`.
- `*args/**kwargs` forwarding → emit `DATA_FLOW` edge with `confidence=0.5`.
- Decorators (`@app.route`, `@pytest.fixture`) → extract as `REFERENCES` edge to the decorator.
- Generated code / `exec()` → skip, log warning.
- Circular imports → handle in merge step (both directions of edge are valid).

**Effort:** ~2 days  
**Dependencies:** Phase 2A (IR + orchestrator)

### Phase 2C — TypeScript Extractor

**Tool:** TypeScript Compiler API (`ts.createProgram`) or `tree-sitter-typescript` for AST parsing.

**Extractions (same categories as Python):**
- Containment: `repository → package (package.json scope) → module (.ts/.tsx files) → class/interface → method/function`
- Call graph: Walk `ts.CallExpression`, resolve via type checker where available.
- Imports: `import` / `require` statements → `IMPORTS` edges.
- Inheritance: `extends` / `implements` → `INHERITS` / `IMPLEMENTS` edges.
- Type references: Type annotations create `REFERENCES` edges.
- Exports: `export` statements → mark nodes with `is_exported=true`.

**Known hard cases:**
- `any` types → unresolved references, `confidence=0.3`.
- Re-exports (`export * from`) → create transitive `IMPORTS` edge.
- JSX component usage → `CALLS` edge to component function/class.
- Declaration merging / module augmentation → merge into single node.

**Effort:** ~2 days  
**Dependencies:** Phase 2A (IR + orchestrator)  
**Parallel-safe:** Can run concurrently with Phase 2B.

### Phase 2D — Embedding Generation

**Goal:** Generate vector embeddings for every node to enable similarity-based retrieval.

**Strategy:**
- **Input text per node:** `"{kind}: {qualified_name}\n{docstring_summary}\n{signature}"` — compact but semantically rich.
- **Model:** Use a code-aware embedding model (e.g., `text-embedding-3-small` or `code-search-ada-002`, or a local model like `all-MiniLM-L6-v2` for offline use).
- **Storage:** Write embeddings into the `nodes.embedding` BLOB column.
- **Incremental:** Only re-embed nodes whose `qualified_name` or `docstring_summary` changed since last run.

**Effort:** ~0.5 day  
**Dependencies:** Phase 2B or 2C (needs nodes to exist)  
**Parallel-safe:** Yes — can run after either extractor.

---

## Phase 3 — Memory Retrieval & Context Assembly

**Goal:** Given an agent query (natural language or symbol reference), retrieve a structurally-aware subgraph and assemble it into prompt-ready context within a token budget.

### Phase 3A — Retrieval Strategies

Implement three retrieval strategies and a **rank fusion** layer:

#### 1. Vector Similarity Retrieval
- Embed the query using the same model as Phase 2D.
- ANN search over `nodes.embedding` (brute-force cosine for SQLite; upgrade to FAISS/HNSW index if >100k nodes).
- Return top-k nodes with similarity scores.

#### 2. Graph Traversal Retrieval
- Given a focal node (from vector search or explicit symbol lookup), perform BFS/DFS up to N hops.
- **Configurable edge filters:** e.g., "follow only CALLS and CONTAINS edges" vs. "follow everything".
- Weight edges by confidence and recency (prefer high-confidence, recently-seen edges).
- Return the subgraph (nodes + edges) within the traversal frontier.

#### 3. Hierarchical Retrieval
- Given a focal node, walk `CONTAINS` edges **upward** to build the containment path (function → class → module → package → repo).
- At each level, include sibling nodes (other functions in the same class, other classes in the same module) as context.
- Apply a **zoom heuristic**: deeper levels get more detail (full signatures), higher levels get summaries only.

#### Rank Fusion
- Normalize scores from each strategy to [0, 1].
- Apply **Reciprocal Rank Fusion (RRF)** across the three ranked lists.
- Deduplicate nodes that appear in multiple lists.
- Return a unified ranked list.

**Effort:** ~2 days  
**Dependencies:** Phases 1B, 2D

### Phase 3B — Token-Budgeted Context Assembly

**Goal:** Convert a retrieved subgraph into a prompt-ready string that fits within a configurable token budget (default: 8,000 tokens; max: 32,000).

**Assembly algorithm:**

```
1. Start with the focal node(s) — always included.
2. Add the containment path (ancestors) — always included.
3. Sort remaining retrieved nodes by fused rank score.
4. Greedily add nodes in rank order until budget is exhausted:
   a. For each node, choose a representation level:
      - "full": signature + docstring + source snippet (≤30 lines)
      - "summary": signature + one-line docstring
      - "mention": qualified name only
   b. Higher-ranked nodes get "full"; lower-ranked get "summary" or "mention".
5. Include edges between included nodes as a relationship section.
6. If community summaries exist for included nodes, prepend the coarsest
   relevant summary as an architectural overview.
```

**Output format (structured markdown):**

```markdown
## Architectural Context
{community summary if available}

## Focal Entity
{full representation of the primary entity}

## Call Chain
{callers → focal → callees, with signatures}

## Containment Path
repository / package / module / class / function

## Related Entities
{ranked list of related nodes at appropriate detail level}

## Relationships
- function:foo CALLS function:bar (confidence: 1.0)
- class:Baz INHERITS class:Base (confidence: 1.0)
```

**Effort:** ~1.5 days  
**Dependencies:** Phase 3A

### Phase 3C — Query Intent Classification

**Goal:** Different query types need different retrieval strategies.

| Query Intent | Primary Strategy | Example |
|-------------|-----------------|---------|
| **Symbol lookup** | Exact match on `qualified_name` → graph traversal | "What does `UserService.create` do?" |
| **Call-chain trace** | Graph traversal (CALLS edges, bidirectional, deep) | "Trace the auth flow from login to token issue" |
| **Architecture overview** | Community summaries + hierarchical retrieval | "What are the main modules?" |
| **Bug investigation** | Vector similarity + graph traversal (wide fan-out) | "Where might a null pointer come from in the payment flow?" |
| **Dependency analysis** | Graph traversal (IMPORTS/DEPENDS_ON edges only) | "What depends on the config module?" |

**Implementation:** Lightweight LLM classifier (few-shot prompt, ~200 tokens) that maps a query to an intent enum. Each intent configures retrieval parameters (edge filters, hop depth, token budget allocation).

**Effort:** ~0.5 day  
**Dependencies:** Phase 3A

---

## Phase 4 — Lightweight Memory Evolution

**Goal:** Enable the memory to improve over time without full reinforcement learning. Scoped to what's deliverable within this story's budget.

> **Scope control:** Full experience-graph consolidation, RL-based retrieval tuning, and cross-session pattern learning are deferred to follow-up stories. This phase implements only the foundation they require.

### Phase 4A — Session Access Log

**What:** Record every retrieval the agent performs and the outcome.

```sql
CREATE TABLE access_log (
    id            TEXT PRIMARY KEY,
    session_id    TEXT NOT NULL,
    query         TEXT NOT NULL,
    intent        TEXT,              -- classified query intent
    retrieved_ids JSON NOT NULL,     -- list of node IDs returned
    context_used  BOOLEAN,          -- did the agent actually use this context?
    outcome       TEXT,             -- 'success' | 'failure' | 'partial' | null
    feedback      TEXT,             -- optional agent/user annotation
    created_at    TEXT DEFAULT (datetime('now'))
);
```

**Effort:** ~0.5 day  
**Dependencies:** Phase 3A

### Phase 4B — Recency/Frequency Boosting

**What:** Adjust retrieval ranking based on access patterns.

- **Frequency boost:** Nodes accessed more often across sessions get a score bonus (logarithmic scale, capped).
- **Recency boost:** Nodes accessed recently get a decaying bonus (half-life: 7 days configurable).
- **Success boost:** Nodes associated with `outcome='success'` entries get a small bonus.
- Boosts are applied as additive terms in the rank fusion step (Phase 3A).

**Effort:** ~0.5 day  
**Dependencies:** Phase 4A

### Phase 4C — Incremental Re-indexing & Staleness Management

**What:** Keep the graph fresh as code changes.

- **Trigger:** On `git pull` or file-change event, compute `git diff --name-only` against the last-indexed commit.
- **Action:** Re-run extractors only for changed files + files that import/reference changed files (1-hop dependents).
- **Staleness:** Nodes/edges whose `last_seen_at` is older than N extraction cycles are marked `stale=true` and deprioritized in retrieval. After 2N cycles, they are pruned.
- **Commit association:** Store `commit_sha` on every node/edge so the graph can be queried at a specific point in time.

**Effort:** ~1 day  
**Dependencies:** Phase 2A (orchestrator), Phase 1B (storage)

### Phase 4D — Community Summary Generation

**What:** Periodically generate natural-language summaries for clusters of related entities (inspired by GraphRAG).

- **Clustering:** Run a simple community detection algorithm (e.g., label propagation or connected components on `CALLS`+`CONTAINS` edges) to identify module-level clusters.
- **Summarization:** For each community, pass member node names + signatures + edge list to an LLM with a summarization prompt. Store in `community_summaries` table.
- **Hierarchy:** Generate summaries at multiple granularity levels (function-cluster → module → package → repo).
- **Refresh:** Re-generate summaries when >20% of member nodes have changed since last generation.

**Effort:** ~1 day  
**Dependencies:** Phase 2B/2C (needs extracted graph), Phase 1B (storage)  
**Parallel-safe:** Can run independently once extraction data exists.

---

## Phase 5 — Integration & Validation

**Goal:** Expose the memory system as a callable service and validate it beats the Phase 0 baseline.

### Phase 5A — Agent-Facing API

**Interface:** Expose memory as a tool / MCP server with these operations:

| Operation | Input | Output |
|-----------|-------|--------|
| `memory_query` | natural language query, token_budget | assembled context string |
| `memory_lookup` | qualified_name | node details + immediate neighborhood |
| `memory_trace` | start_symbol, end_symbol | shortest call-chain path between them |
| `memory_overview` | scope (package/module name) | community summary + top entities |
| `memory_feedback` | session_id, query_id, outcome | ack (updates access log) |
| `memory_index` | repo_path, incremental (bool) | indexing status |

**Effort:** ~1 day  
**Dependencies:** Phases 3B, 4A

### Phase 5B — Evaluation Against Baseline

**What:** Re-run the Phase 0 gold query set against the graph-based retriever and compare.

**Metrics:**
- **Precision@5** — are the top-5 returned entities relevant?
- **Recall@10** — do the top-10 cover all expected entities?
- **MRR** — where does the first relevant entity appear?
- **Context quality** (LLM-as-judge) — given the query and the assembled context, rate usefulness 1–5.
- **Token efficiency** — tokens used vs. naive RAG for equivalent recall.
- **Latency** — p50 and p95 retrieval time.

**Pass criteria:** Graph retriever must beat naive RAG baseline on ≥3 of the 4 accuracy metrics.

**Effort:** ~0.5 day  
**Dependencies:** Phase 0 (baseline), Phase 5A (API)

### Phase 5C — Documentation & Operational Runbooks

**Deliverables:**
1. **Architecture doc** — system diagram, data flow, component responsibilities.
2. **Schema reference** — node kinds, edge kinds, property descriptions.
3. **Runbook: Full re-index** — how to rebuild the graph from scratch.
4. **Runbook: Incremental update** — how to update after code changes.
5. **Runbook: Troubleshooting retrieval** — how to debug "why didn't the agent find X?"
6. **API reference** — operation signatures, examples, error codes.

**Effort:** ~0.5 day  
**Dependencies:** All prior phases

---

## Dependency Graph

```
Phase 0 (Eval Baseline) ─────────────────────────────────────┐
                                                              │
Phase 1A (Schema) ──► Phase 1B (Storage) ──► Phase 2A (Orch) │
                                               │    │         │
                                    ┌──────────┘    │         │
                                    ▼               ▼         │
                              Phase 2B (Py)    Phase 2C (TS)  │
                                    │               │         │
                                    └───────┬───────┘         │
                                            ▼                 │
                                      Phase 2D (Embed)        │
                                            │                 │
                              ┌─────────────┼──────────┐      │
                              ▼             ▼          ▼      │
                        Phase 3A      Phase 4C    Phase 4D    │
                        (Retrieval)   (Incremental)(Community)│
                              │                               │
                              ▼                               │
                        Phase 3B (Context Assembly)           │
                              │                               │
                         ┌────┤                               │
                         ▼    ▼                               │
                   Phase 3C  Phase 4A (Access Log)            │
                              │                               │
                              ▼                               │
                        Phase 4B (Boosting)                   │
                              │                               │
                              ▼                               │
                        Phase 5A (API)                        │
                              │                               │
                              ├───────────────────────────────┘
                              ▼
                        Phase 5B (Eval)
                              │
                              ▼
                        Phase 5C (Docs)
```

## Parallel Execution Opportunities

| Stream A | Stream B | Notes |
|----------|----------|-------|
| Phase 1A → 1B → 2A | Phase 0 (Eval Baseline) | Baseline is independent |
| Phase 2B (Python extractor) | Phase 2C (TypeScript extractor) | Both depend on 2A only |
| Phase 3A (Retrieval) | Phase 4C (Incremental) + Phase 4D (Community) | All depend on extraction, independent of each other |
| Phase 3C (Intent classifier) | Phase 4A → 4B (Access log + boosting) | Both depend on 3A |

**Critical path:** 1A → 1B → 2A → 2B → 2D → 3A → 3B → 5A → 5B → 5C  
**Estimated critical-path effort:** ~11 days (single engineer)  
**With parallelism (2 engineers):** ~7 days

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| Static analysis misses dynamic calls (Python `getattr`, TS `any`) | High | Medium | Confidence scores + `is_dynamic` flag; retrieval deprioritizes low-confidence edges but doesn't drop them |
| Graph grows too large for SQLite vector search | Medium | Medium | Phase 2D: add FAISS/HNSW index alongside SQLite for vectors; keep relational data in SQLite |
| LLM community summarization is expensive at scale | Medium | Low | Summarize only on schema change (>20% delta); cache aggressively; use smaller model for summarization |
| Incremental re-indexing misses transitive dependents | Medium | Medium | Phase 4C: 1-hop dependent re-extraction catches direct callers; log gaps and expand hops if recall drops |
| Embedding model drift (model upgrade changes vector space) | Low | High | Store model version alongside embeddings; full re-embed on model change |

---

## Out of Scope (Deferred to Follow-up Stories)

These capabilities are architecturally planned for but not implemented in this story:

1. **RL-based retrieval tuning** — training a policy to optimise retrieval paths based on outcome signal (ref: MAGMA, Memory-R1).
2. **Experience graph** — storing reusable problem-solving patterns as graph templates (ref: Reflexion, ExpEL).
3. **Cross-session pattern learning** — aggregating access patterns across multiple users/agents.
4. **Multi-graph decomposition** — splitting into separate semantic/temporal/causal views (ref: MAGMA).
5. **Additional language extractors** — Java, Go, Rust, C# (plug into the Phase 2A IR boundary).
6. **Real-time file-watcher indexing** — streaming updates vs. batch re-index.
7. **Multi-repo graph federation** — linking graphs across repositories.

---

## Acceptance Criteria

1. **Extraction completeness:** For the reference codebase, ≥90% of functions and classes appear as nodes; ≥80% of static call relationships appear as edges.
2. **Retrieval quality:** Graph retriever beats naive RAG on ≥3 of 4 accuracy metrics (precision@5, recall@10, MRR, LLM-judge).
3. **Token efficiency:** Assembled context achieves equivalent recall to naive RAG using ≤50% of the tokens.
4. **Latency:** p95 retrieval + assembly completes in <2 seconds for repos up to 100k LOC.
5. **Incrementality:** Re-indexing after a single-file change completes in <5 seconds.
6. **API contract:** All six operations in Phase 5A are implemented and documented.
7. **Provenance:** Every node and edge carries source_file, extractor, confidence, and commit_sha.
