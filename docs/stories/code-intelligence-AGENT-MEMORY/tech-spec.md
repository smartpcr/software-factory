# Tech Spec: Agent Memory via Graph-Based Repo-Structure & Call-Chain Knowledge

**Story:** `code-intelligence:AGENT-MEMORY`  
**Points:** 10  
**Author:** Wen Zhong — Architect  
**Status:** DRAFT  

---

## 1. Problem Statement

Coding agents today are stateless across sessions: every invocation starts from scratch, re-discovering repository topology, ownership boundaries, call chains, and architectural invariants through brute-force search. This wastes tokens, inflates latency, and — critically — prevents the agent from *learning*. An agent that has fixed three null-dereference bugs in `PaymentService.processRefund()` should know, without re-reading every file, that `processRefund` has fragile null-handling and that its callers in `OrderController` rely on a non-null return.

The goal is a **persistent, graph-structured agent memory** that:

1. **Models the repository top-down** — from workspace → package → module → class → function, capturing containment, dependency, and call-chain edges.
2. **Accumulates expertise** — per-node annotations (bug-density signals, ownership hints, complexity observations) that grow across agent sessions.
3. **Serves as the primary context-construction layer** — given a task description, the memory graph enables subgraph retrieval that is both structurally aware (follow the call chain) and semantically relevant (embed the task, match nodes), replacing or augmenting flat file-grep.

This draws directly from the *Graph-based Agent Memory* taxonomy (Yang et al., arXiv:2602.05665) — specifically its lifecycle of **extraction → storage → retrieval → evolution** — and adapts it from conversational/game-playing domains to the code-intelligence domain where the primary data sources are ASTs, call graphs, and version-control history.

---

## 2. In-Scope

| # | Capability | Detail |
|---|-----------|--------|
| S1 | **Top-down structural graph extraction** | Parse repository into a hierarchical knowledge graph: `Repo → Workspace → Package → Module → Class/Interface → Function/Method`. Edges model containment (`CONTAINS`), import/dependency (`DEPENDS_ON`), and inheritance (`EXTENDS`, `IMPLEMENTS`). |
| S2 | **Call-chain overlay** | Static-analysis pass that adds `CALLS`, `CALLED_BY`, and `REFERENCES` edges between function-level nodes across the structural hierarchy. |
| S3 | **Expertise annotation layer** | Each node carries a mutable property bag (e.g., `bug_count`, `last_touched_by_agent`, `complexity_score`, `known_issues[]`, `architectural_notes`). Annotations are written by the agent during and after task execution. |
| S4 | **Temporal versioning** | Every edge and annotation carries a `valid_from` / `valid_until` timestamp pair (Zep-style temporal KG — Rasmussen et al., arXiv:2501.13956). Enables "what did the call graph look like when the bug was introduced?" queries. |
| S5 | **Hybrid retrieval** | Two retrieval paths that are merged: (a) **structural traversal** — given a seed node, walk N hops along call-chain or containment edges; (b) **semantic similarity** — embed node summaries and match against an embedded task prompt. Inspired by MAGMA's orthogonal-graph traversal policy (Jiang et al., arXiv:2601.03236). |
| S6 | **Memory evolution pipeline** | After each agent session, a consolidation step: merge new observations, decay stale annotations (Ebbinghaus-curve weighting), promote frequently-accessed nodes to a hot tier. Follows the Internal Self-Evolving pattern from the survey taxonomy. |
| S7 | **Context window construction** | Given a task + memory graph, produce a ranked, token-budgeted context window: structural skeleton first, then call-chain neighbors, then semantically-related nodes, then expertise annotations. Output is a prompt-ready markdown/code block. |

---

## 3. Out of Scope

| # | Excluded | Rationale |
|---|----------|-----------|
| O1 | **Runtime / dynamic call-graph tracing** | We instrument only static analysis (AST, import resolution). Dynamic tracing (profiling, coverage) is a future layer; mixing them now doubles the storage surface and complicates the versioning model. |
| O2 | **Multi-repository federation** | The memory graph is scoped to a single repository root. Cross-repo dependency resolution (e.g., following a `npm` package into `node_modules` source) is deferred. |
| O3 | **Natural-language conversational memory** | The survey covers chat/dialogue memory extensively. Our scope is *code-structural* memory only. Agent conversation logs may be referenced as provenance metadata but are not first-class graph nodes. |
| O4 | **Fine-tuning / weight updates** | The agent "learns" by writing to the graph, not by updating model weights. RL-based memory construction (Mem-α, Memory-R1) is architecturally interesting but out of scope for this story. |
| O5 | **UI / dashboard for memory exploration** | Observability into the graph is via CLI queries and telemetry, not a visual explorer. |
| O6 | **Multi-agent shared memory** | The graph is per-agent-instance for now. G-Memory-style hierarchical sharing across agent swarms is deferred. |

---

## 4. Non-Goals

These are things the system **explicitly will not attempt**, even if technically feasible:

- **Replace the LSP or language server.** The memory graph complements language tooling; it does not re-implement go-to-definition, find-references, or type checking. It *consumes* outputs from those tools.
- **Guarantee 100 % call-graph completeness.** Static analysis of dynamic languages (Python `getattr`, JS `eval`) is inherently incomplete. The graph tolerates missing edges and annotates confidence levels.
- **Be a general-purpose knowledge graph.** The schema is purpose-built for code structure. Extending it to model business-domain ontologies, user preferences, or conversation history is not a goal.
- **Enforce deterministic context construction.** Two identical queries against the same graph may produce slightly different context windows due to embedding non-determinism. This is acceptable; the agent's reasoning layer handles variation.

---

## 5. Hard Constraints

| ID | Constraint | Source |
|----|-----------|--------|
| HC1 | **Token budget ceiling.** Context windows produced by the retrieval layer must respect a configurable max-token limit (default: 32 K tokens). The system truncates gracefully, preserving structural skeleton over detail. | Operational: avoids blowing LLM context limits. |
| HC2 | **Incremental update, not full rebuild.** After a `git pull` or file save, only changed files trigger re-extraction. Full-repo re-parse is an explicit admin action, not the default path. | Performance: large repos (100 K+ files) cannot afford full re-index on every change. |
| HC3 | **Language-agnostic core, language-specific extractors.** The graph schema and storage engine are language-neutral. Each supported language (TypeScript, Python, C#, Go, Java — minimum viable set) provides an extractor plugin. Adding a language must not require schema changes. | Extensibility. |
| HC4 | **Storage must survive agent process restarts.** The graph is persisted to disk (not in-memory only). Acceptable back-ends: SQLite (+ FTS5 for text search) for single-machine, or a pluggable adapter for Neo4j/Memgraph in scaled deployments. | Durability. |
| HC5 | **No external network calls during retrieval.** Embedding vectors are pre-computed at extraction/evolution time. Retrieval is a local graph traversal + vector-similarity operation. The agent must not block on an API call to construct its context. | Latency SLO: < 500 ms p95 for context construction. |
| HC6 | **Deterministic graph schema.** The node types (`Repo`, `Package`, `Module`, `Class`, `Function`) and core edge types (`CONTAINS`, `CALLS`, `DEPENDS_ON`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`) are fixed in the schema. Extension is via the annotation property bag, not by adding ad-hoc node/edge types. | Schema stability for downstream consumers. |
| HC7 | **Audit trail.** Every write to the graph (node creation, annotation update, edge addition/removal) is logged with agent-session-id, timestamp, and operation type. This is the post-mortem trail. | Operational: we instrument before we optimise. |

---

## 6. Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                     Agent Runtime                        │
│  ┌──────────┐   ┌──────────────┐   ┌──────────────────┐ │
│  │ Task      │──▶│ Context      │──▶│ LLM Reasoning    │ │
│  │ Ingress   │   │ Constructor  │   │ Loop             │ │
│  └──────────┘   └──────┬───────┘   └────────┬─────────┘ │
│                        │ query                │ annotate  │
│                        ▼                      ▼          │
│  ┌─────────────────────────────────────────────────────┐ │
│  │              Memory Graph Service                   │ │
│  │  ┌───────────┐ ┌────────────┐ ┌──────────────────┐  │ │
│  │  │ Retriever │ │ Annotation │ │ Evolution Engine │  │ │
│  │  │ (struct + │ │ Writer     │ │ (consolidate,    │  │ │
│  │  │  semantic)│ │            │ │  decay, promote) │  │ │
│  │  └─────┬─────┘ └─────┬──────┘ └────────┬─────────┘  │ │
│  │        │              │                 │            │ │
│  │        ▼              ▼                 ▼            │ │
│  │  ┌──────────────────────────────────────────────┐   │ │
│  │  │         Graph Storage Engine                 │   │ │
│  │  │  (Nodes · Edges · Annotations · Embeddings)  │   │ │
│  │  │  ┌────────────────┐  ┌────────────────────┐  │   │ │
│  │  │  │ SQLite + FTS5  │  │ Vector Index (HNSW)│  │   │ │
│  │  │  └────────────────┘  └────────────────────┘  │   │ │
│  │  └──────────────────────────────────────────────┘   │ │
│  └─────────────────────────────────────────────────────┘ │
│                        ▲                                 │
│                        │ incremental                     │
│  ┌─────────────────────┴───────────────────────────────┐ │
│  │              Extraction Pipeline                     │ │
│  │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────┐ │ │
│  │  │ TS       │ │ Python   │ │ C#       │ │ Go     │ │ │
│  │  │ Extractor│ │ Extractor│ │ Extractor│ │Extract.│ │ │
│  │  └──────────┘ └──────────┘ └──────────┘ └────────┘ │ │
│  │                  ▲                                   │ │
│  │            File Watcher / Git Diff                   │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

---

## 7. Graph Schema (Core)

### 7.1 Node Types

| Node Type | Key Properties | Notes |
|-----------|---------------|-------|
| `Repo` | `root_path`, `vcs_url`, `default_branch` | Singleton root per graph. |
| `Workspace` | `name`, `path`, `manager` (npm, pip, cargo…) | Monorepo child. Absent in single-package repos. |
| `Package` | `name`, `version`, `path` | Maps to `package.json`, `pyproject.toml`, `.csproj`, `go.mod`. |
| `Module` | `file_path`, `language`, `hash` | One node per source file. `hash` enables change detection. |
| `Class` | `name`, `fqn`, `is_abstract`, `is_exported` | Covers classes, interfaces, structs, enums. |
| `Function` | `name`, `fqn`, `signature`, `line_start`, `line_end`, `is_exported` | Leaf-level structural node. |

### 7.2 Edge Types

| Edge Type | Source → Target | Semantics |
|-----------|----------------|-----------|
| `CONTAINS` | Any parent → child in the hierarchy | Structural containment. |
| `CALLS` | Function → Function | Static call-site edge. |
| `DEPENDS_ON` | Module → Module, Package → Package | Import / require / using. |
| `EXTENDS` | Class → Class | Inheritance. |
| `IMPLEMENTS` | Class → Class/Interface | Interface implementation. |
| `REFERENCES` | Function → Class, Function → Function | Non-call references (type annotations, variable types). |

### 7.3 Annotation Schema

Annotations live in a JSONB property bag on every node. They are the *mutable, agent-written* layer.

```jsonc
{
  // Written by agent during sessions
  "known_issues": [
    { "description": "Null-deref on empty refund list", "session": "abc-123", "ts": "2026-05-01T..." }
  ],
  "architectural_notes": "Entry point for the payment retry saga. Callers assume non-null return.",
  "complexity_score": 14,          // cyclomatic, computed by extractor
  "bug_density": 0.32,             // bugs-per-KLOC, accumulated over sessions
  "last_agent_session": "abc-123",
  "access_count": 7,               // retrieval hot-path signal
  "confidence": 0.85,              // extraction confidence (lower for dynamic-dispatch edges)
  "embedding": [0.012, -0.034, …]  // pre-computed vector (dim = 256 or 384)
}
```

### 7.4 Temporal Model

Every edge and annotation row carries:

| Field | Type | Semantics |
|-------|------|-----------|
| `valid_from` | ISO-8601 timestamp | When this fact became true. |
| `valid_until` | ISO-8601 timestamp \| `NULL` | When this fact was superseded. `NULL` = current. |
| `provenance` | `{ agent_session_id, git_commit?, operation }` | Audit trail. |

Point-in-time queries filter on `valid_from <= t AND (valid_until IS NULL OR valid_until > t)`.

---

## 8. Lifecycle Stages (Mapped to Survey Taxonomy)

### 8.1 Extraction

**Input:** Source files (changed since last extraction, identified via `git diff` or file-watcher).

**Process:**

1. Language-specific extractor parses AST, emits structural nodes and containment edges.
2. Import/dependency resolver emits `DEPENDS_ON` edges.
3. Call-graph analyzer walks function bodies, emits `CALLS` / `REFERENCES` edges. Confidence is tagged: direct calls = 1.0; dynamic dispatch / callback patterns = 0.5–0.7.
4. Embedding generator produces a dense vector per node by encoding `"{node_type} {fqn}: {signature or first-line docstring}"`.
5. Delta is applied to the graph store: new nodes inserted, removed nodes get `valid_until = now`, modified nodes trigger re-embedding.

**Constraint alignment:** HC2 (incremental), HC3 (pluggable extractors), HC5 (embeddings pre-computed here).

### 8.2 Storage

**Primary back-end:** SQLite with the following tables:

- `nodes (id, type, fqn, properties_json, embedding_blob, hash, valid_from, valid_until)`
- `edges (id, type, source_id, target_id, properties_json, confidence, valid_from, valid_until)`
- `annotations (node_id, key, value_json, session_id, valid_from, valid_until)`
- `audit_log (id, operation, entity_type, entity_id, session_id, timestamp, detail_json)`

FTS5 virtual table over `nodes.fqn` and `annotations.value_json` for keyword search.  
HNSW index (via `sqlite-vss` or a bundled Rust HNSW library) over `nodes.embedding_blob` for ANN queries.

**Pluggable adapter** for Neo4j/Memgraph when graph traversals dominate and the node count exceeds ~500 K.

**Constraint alignment:** HC4 (durable), HC6 (fixed schema), HC7 (audit_log table).

### 8.3 Retrieval

Given a task description `T` and optional seed node(s):

1. **Embed T** → vector `v_T`.
2. **Semantic fan-out:** ANN query on node embeddings → top-K candidate nodes (K = 50 default).
3. **Structural expansion:** For each candidate, walk `CALLS` / `CALLED_BY` edges up to depth D (default 2). Walk `CONTAINS` up to the enclosing `Package` for hierarchy context.
4. **Score fusion:** Each retrieved node gets a combined score:  
   `score = α·sim(v_T, v_node) + β·structural_proximity + γ·expertise_signal`  
   where `expertise_signal` draws from `bug_density`, `access_count`, `known_issues` relevance.
5. **Token-budget packing:** Rank nodes by fused score, serialize top nodes (structural skeleton → call-chain neighbors → annotations) until token budget (HC1) is filled.
6. **Output:** A structured context block ready for prompt injection.

This is the MAGMA-inspired two-phase pattern: orthogonal graph views (semantic + structural + temporal) with policy-guided traversal, adapted for code structures.

### 8.4 Evolution

Runs as a post-session consolidation job:

| Operation | Trigger | Effect |
|-----------|---------|--------|
| **Merge** | Agent writes new annotation during session | Upsert into `annotations`, old row gets `valid_until = now`. |
| **Decay** | Scheduled (daily or on-demand) | Annotations older than decay threshold without recent access get their `confidence` reduced. After confidence < threshold, they are archived (moved to cold partition). Inspired by Ebbinghaus forgetting-curve models used in MemoryBank (AAAI'24). |
| **Promote** | Node `access_count` crosses hot-tier threshold | Node summary is pre-expanded (longer embedding context, richer annotation) so future retrievals are faster. |
| **Prune** | Git branch deleted, file removed | Structural nodes and their edges get `valid_until = now`. They remain queryable for historical analysis but are excluded from default retrieval. |
| **Re-embed** | Node content changed, or embedding model upgraded | Re-compute embedding vector; old vector row gets `valid_until`. |

---

## 9. Identified Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R1 | **Stale graph after refactor** — A large rename or directory restructure invalidates many nodes simultaneously. Incremental update may produce dangling edges or orphan nodes. | Medium | High | Implement a `reconcile` pass that runs after any diff affecting > N files (default 50). Reconcile does a bounded full-re-extract of affected subtrees, not the entire repo. |
| R2 | **Embedding drift** — If the embedding model is upgraded, old and new vectors are incomparable, breaking similarity retrieval. | Medium | Medium | Version the embedding model in the `nodes` table. On model change, schedule background re-embedding. Retrieval filters to the current model version. |
| R3 | **Call-graph incompleteness in dynamic languages** — Python `getattr`, JS dynamic `import()`, Ruby `send`. Agents may build incorrect mental models from missing edges. | High | Medium | Tag every edge with `confidence`. Retrieval scoring down-weights low-confidence paths. Extractors for dynamic languages should emit heuristic edges (e.g., duck-typed method calls) with `confidence = 0.5`. |
| R4 | **Annotation noise** — Agents may write contradictory or low-quality annotations across sessions (e.g., one session says "this function is safe", another says "known null-deref"). | Medium | Medium | Annotations carry `session_id` provenance. Evolution's merge step uses recency + frequency to resolve conflicts: most-recent annotation from the most-frequent-access session wins. Contradictions are flagged for human review via telemetry. |
| R5 | **Storage growth on large monorepos** — A 200 K-file monorepo with temporal versioning could produce multi-GB databases. | Medium | Medium | Temporal archival: annotations and edges older than a configurable retention window (default 90 days, no recent access) are moved to a cold archive table. Active graph stays lean. |
| R6 | **Latency regression under concurrent access** — Multiple agent instances querying / writing the same SQLite database. | Low | High | SQLite WAL mode for concurrent reads. Write serialization via an in-process mutex. For multi-agent scenarios (out of scope but future-proofing), the Neo4j adapter path handles concurrency natively. |
| R7 | **Privacy / secret leakage into graph** — Extractors might capture string literals, API keys, or secrets embedded in code as part of node summaries or annotations. | Low | Critical | Extractors strip string literals and comments containing secret-like patterns (regex for API keys, tokens, passwords) before writing to the graph. Node summaries are structural (signature + docstring), never full function bodies. |
| R8 | **Over-reliance on graph memory** — Agents may trust stale annotations over fresh file content, producing incorrect fixes. | Medium | High | Context constructor always includes the *current file content* for the primary target of a task. Graph memory supplements but never replaces reading the actual code. A staleness warning is injected into the prompt when `valid_from` of retrieved annotations is older than the latest git commit touching that file. |

---

## 10. Success Criteria

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Context relevance** | Retrieval precision ≥ 0.75 (human-judged: ≥ 75 % of retrieved nodes are relevant to the task) | Offline eval on 50 curated task–file pairs from internal repos. |
| **Latency** | Context construction p95 < 500 ms on a repo with 50 K nodes | Benchmark harness, SQLite back-end, single machine. |
| **Incremental update** | Re-extraction after a 10-file commit completes in < 2 s | Benchmark harness. |
| **Agent task accuracy lift** | ≥ 10 % improvement in first-attempt success rate on SWE-Bench-Lite tasks when memory is enabled vs. baseline (no memory) | A/B experiment with matched task sets. |
| **Storage efficiency** | Active graph for a 100 K-file repo fits in < 500 MB (excluding cold archive) | Size measurement after full extraction + 30-day simulated annotation workload. |

---

## 11. Open Questions

1. **Embedding model selection.** Should we use a code-specific embedding model (e.g., CodeBERT, StarEncoder) or a general-purpose one (e.g., `text-embedding-3-small`)? Code-specific models may capture structural semantics better, but general-purpose models handle natural-language task descriptions more fluently. A hybrid (code model for nodes, general model for tasks) adds complexity.

2. **Graph query language.** For the SQLite back-end, graph traversals are recursive CTEs. Should we expose a higher-level query DSL (Cypher-like) to the agent, or keep the interface purely programmatic (function calls like `get_callers(fqn, depth=2)`)?

3. **Cross-branch memory.** When the agent works on a feature branch, should annotations written there be visible on `main`? Merging annotation branches mirrors the git merge problem. Initial stance: annotations are branch-scoped; merging is a future concern.

4. **Annotation schema governance.** Who defines which annotation keys are valid? A free-form property bag is flexible but risks fragmentation. Consider a registry of well-known keys with typed schemas, plus an `extra` bag for ad-hoc data.

5. **Decay curve calibration.** The Ebbinghaus-inspired decay model needs empirical tuning. What half-life is appropriate for code annotations? A function that hasn't been touched in 90 days may still be critical infrastructure. Decay should factor in structural centrality (PageRank on the call graph), not just recency.

---

## 12. References

| Key | Citation |
|-----|----------|
| Survey | Yang, C. et al. "Graph-based Agent Memory: Taxonomy, Techniques, and Applications." arXiv:2602.05665, 2026. |
| Zep | Rasmussen, P. et al. "Zep: A Temporal Knowledge Graph Architecture for Agent Memory." arXiv:2501.13956, 2025. |
| MAGMA | Jiang, D. et al. "MAGMA: A Multi-Graph based Agentic Memory Architecture for AI Agents." arXiv:2601.03236, ACL 2026. |
| Mem0 | Chhikara, P. et al. "Mem0: Building Production-Ready AI Agents with Scalable Long-Term Memory." arXiv:2504.19413, 2025. |
| GraphRAG | Edge, D. et al. "From Local to Global: A Graph RAG Approach to Query-Focused Summarization." arXiv:2404.16130, 2024. |
| HiAgent | "HiAgent: Hierarchical Working Memory Management for Solving Long-Horizon Agent Tasks." ACL 2025. |
| MemoryBank | "MemoryBank: Enhancing Large Language Models with Long-Term Memory." AAAI 2024. |
| G-Memory | "G-Memory: Tracing Hierarchical Memory for Multi-Agent Systems." arXiv:2506.07398, 2025. |

---

*End of tech spec.*
