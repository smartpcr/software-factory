# Implementation Plan — Agent Memory via Graph-Structured Code Intelligence

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 SP
>
> **Objective:** Build a graph-based agent memory system that captures top-down
> repository structure and call-chain context, enabling agents to accumulate
> expertise and produce increasingly informed responses over time.

---

## 1  Problem Statement

Today's coding agents treat every session as a blank slate. They re-discover
the same architectural patterns, re-trace the same call chains, and re-learn
the same domain idioms on every invocation. The cost compounds: token waste,
latency, and brittle context windows that force aggressive summarisation.

We need a **persistent, graph-structured memory layer** that:

1. Models a repository's top-down structure (repo → packages → modules → classes → functions) as a navigable hierarchy.
2. Captures call-chain and data-flow relationships as directed edges so the agent can reason about causality without re-parsing.
3. Evolves with the codebase — insertions, deletions, refactors — without full rebuilds.
4. Serves retrieval queries at agent-interaction latency (< 500 ms p95).

### Research Basis

This plan is anchored on the taxonomy from the *Graph-based Agent Memory*
survey (Yang et al., arXiv:2602.05665) and draws specifically from:

| Concept | Source | Adaptation |
|---|---|---|
| Multi-graph orthogonal views | MAGMA (arXiv:2601.03236) | Separate structural, call-chain, temporal, and semantic graphs over the same codebase |
| Temporal knowledge graph | Zep / Graphiti (arXiv:2501.13956) | Track code evolution across commits with timestamped edges |
| Hierarchical memory | HiAgent (ACL'25), G-Memory (arXiv:2506.07398) | Map the natural module→class→function hierarchy into a tiered graph |
| Community summarisation | GraphRAG (arXiv:2404.16130) | Pre-generate architectural summaries per package/module community |
| Production memory layer | Mem0 (arXiv:2504.19413) | Extract → consolidate → retrieve lifecycle for agent-facing API |
| RL-guided retrieval | Memory-R1 (arXiv:2508.19828), Mem-α (arXiv:2509.25911) | Train a lightweight retrieval policy that improves with agent feedback |

---

## 2  Architecture Overview

```
┌──────────────────────────────────────────────────────┐
│                    Agent Runtime                     │
│  ┌──────────┐  ┌────────────┐  ┌──────────────────┐ │
│  │ Planning  │  │  Retrieval │  │  Memory Writer   │ │
│  │  Engine   │◄─┤  Gateway   │  │  (post-action)   │ │
│  └──────────┘  └─────┬──────┘  └────────┬─────────┘ │
└──────────────────────┼──────────────────┼────────────┘
                       │                  │
              ┌────────▼──────────────────▼────────┐
              │        Memory Service (gRPC/REST)  │
              │  ┌──────────────────────────────┐  │
              │  │   Query Planner / Router     │  │
              │  └──────┬───────────────────────┘  │
              │         │                          │
              │  ┌──────▼──────────────────────┐   │
              │  │   Graph Store (multi-view)   │  │
              │  │  ┌────────┐ ┌────────────┐  │   │
              │  │  │Struct- │ │ Call-Chain  │  │   │
              │  │  │ural    │ │ (Causal)   │  │   │
              │  │  │Graph   │ │ Graph      │  │   │
              │  │  ├────────┤ ├────────────┤  │   │
              │  │  │Temporal│ │ Semantic   │  │   │
              │  │  │Graph   │ │ Index      │  │   │
              │  │  └────────┘ └────────────┘  │   │
              │  └─────────────────────────────┘   │
              │  ┌─────────────────────────────┐   │
              │  │  Evolution Engine            │   │
              │  │  (consolidation, decay, RL)  │   │
              │  └─────────────────────────────┘   │
              └────────────────────────────────────┘
```

### 2.1  Four Orthogonal Graph Views (MAGMA-inspired)

Following MAGMA's principle of decoupling memory representation from retrieval
logic, we maintain four specialised graph views over the same underlying nodes:

| View | Node types | Edge semantics | Primary query pattern |
|---|---|---|---|
| **Structural** | Repo, Package, Module, Class, Function, Variable | `contains`, `imports`, `exports` | "What's in package X?", "Where is class Y defined?" |
| **Call-Chain (Causal)** | Function, Method, Constructor | `calls`, `instantiates`, `overrides`, `implements` | "What calls function Z?", "Trace the data flow from A → B" |
| **Temporal** | Any node | `added_in`, `modified_in`, `removed_in` (commit SHA + timestamp) | "What changed since last Tuesday?", "Who introduced this pattern?" |
| **Semantic** | Any node (via embedding) | `similar_to` (cosine > threshold), `co-occurs_with` | "Find modules related to authentication", "What else uses this pattern?" |

All four views share a **canonical node identity** (language-agnostic fully-qualified name + file path + span) so cross-view joins are O(1).

### 2.2  Hierarchical Memory Tiers

Inspired by HiAgent's working-memory management and G-Memory's hierarchical
tracing, the graph organises into three tiers with different granularity and
TTL characteristics:

| Tier | Scope | Contents | Retention |
|---|---|---|---|
| **L0 — Session** | Current agent session | Hot working set, recent tool calls, in-flight hypotheses | Session lifetime; promoted to L1 on success |
| **L1 — Repository** | Single repo, persisted | Structural graph, call-chain graph, community summaries, agent annotations | Indefinite; pruned by staleness score |
| **L2 — Organisation** | Cross-repo | Shared architectural patterns, common dependency expertise, org conventions | Indefinite; curated by evolution engine |

---

## 3  Phased Work Breakdown

### Phase 0 — Foundation: Graph Schema & Storage Layer

**Goal:** Stand up the persistent graph store and define the canonical schema
that every subsequent phase writes to.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 0.1 | **Schema design** — Define node types (Repo, Package, Module, Class, Function, Variable, Commit), edge types (see §2.1 table), and property bags (span, hash, embedding vector, staleness score). Produce a machine-readable schema file (JSON-Schema or Protobuf). | — | None |
| 0.2 | **Storage backend selection & setup** — Evaluate Neo4j (mature, Cypher), FalkorDB (Redis-compatible, low-latency), or a lightweight embedded option (e.g., Kuzu, DuckDB + extension). Decision criteria: sub-500ms traversal on a 500K-node graph, vector-index support, temporal edge properties. Stand up the chosen backend with CI. | Parallel with 0.1 | None |
| 0.3 | **Graph SDK** — Thin TypeScript/Python client library wrapping the store. API surface: `upsertNode`, `upsertEdge`, `query(cypher)`, `vectorSearch(embedding, topK)`, `subgraphAround(nodeId, hops)`. Unit-test with fixture data. | — | 0.1, 0.2 |
| 0.4 | **Canonical identity scheme** — Implement the fully-qualified-name resolver that produces stable node IDs across renames and moves (content-hash fallback). | Parallel with 0.3 | 0.1 |

**Exit criteria:** A test harness can create a 10K-node graph, insert 50K edges
across all four view types, and answer a 3-hop traversal in < 200 ms.

---

### Phase 1 — Memory Extraction: Repo-Structure & Call-Chain Ingest

**Goal:** Parse real repositories into the structural and call-chain graph
views. This is the "Memory Extraction" stage of the survey taxonomy.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 1.1 | **Language-server integration harness** — Build an adapter that drives LSP (or Tree-sitter for languages without reliable LSP) to extract: symbol definitions, references, call hierarchies, type hierarchies. Output: a stream of `(node, edge)` tuples in canonical schema. | — | 0.3 |
| 1.2 | **Structural graph builder** — Consume the LSP stream and populate the structural view: repo→package→module→class→function→variable containment tree plus import/export edges. Handle partial parses gracefully (log warnings, don't abort). | — | 1.1 |
| 1.3 | **Call-chain graph builder** — From the same LSP stream, populate `calls`, `instantiates`, `overrides`, `implements` edges. For dynamic languages where static analysis is incomplete, insert edges with a `confidence` property < 1.0. | Parallel with 1.2 | 1.1 |
| 1.4 | **Incremental update pipeline** — Given a git diff (list of changed files), re-extract only affected subtrees and merge into the existing graph via upsert. Deletions mark nodes as `tombstoned` rather than hard-deleting (needed for temporal view). Validate: re-indexing a 10-file diff on a 100K-node graph completes in < 5 s. | — | 1.2, 1.3 |
| 1.5 | **Smoke test on real repos** — Run the full ingest on at least two non-trivial open-source repos (one TypeScript, one Python, each > 50K LoC). Validate node counts against `cloc` / symbol-count baselines. | — | 1.4 |

**Exit criteria:** Structural and call-chain views are populated and queryable
for both test repos. Incremental updates work for single-commit diffs.

---

### Phase 2 — Memory Storage: Temporal & Semantic Views

**Goal:** Complete the four-view model by adding temporal commit tracking and
semantic embeddings. This is the "Memory Storage" stage.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 2.1 | **Temporal edge annotator** — Walk git history (configurable depth, default 90 days) and attach `added_in(commit)`, `modified_in(commit)`, `removed_in(commit)` edges to every node whose file was touched. Store commit metadata (author, timestamp, message) as commit-node properties. | — | 1.4 |
| 2.2 | **Semantic embedding pipeline** — For each function/class node, generate an embedding from: (a) the docstring/comment if present, (b) the signature + first N lines of body, (c) the FQN path. Use a code-embedding model (e.g., `text-embedding-3-small` or `voyage-code-3`). Store vectors in the graph store's vector index or a sidecar index (HNSW via `pgvector` or `usearch`). | Parallel with 2.1 | 1.2 |
| 2.3 | **Community detection & summarisation** — Run Leiden or Louvain community detection on the structural graph. For each community of ≥ 5 nodes, generate a one-paragraph natural-language summary using an LLM (architecture description, responsibility, key public interfaces). Store as a `CommunitySummary` node linked to its members. This follows the GraphRAG approach. | — | 2.2 |
| 2.4 | **Staleness scoring** — Assign each node a staleness score = f(days_since_last_touch, number_of_recent_commits_touching_neighbours, agent_access_count). This score drives eviction in the evolution engine (Phase 5). | Parallel with 2.3 | 2.1 |

**Exit criteria:** All four graph views are populated. Community summaries
exist for the test repos. Vector search returns sensible "related function"
results.

---

### Phase 3 — Memory Retrieval: Query Gateway

**Goal:** Build the retrieval API that the agent runtime calls. This maps to the
"Memory Retrieval" stage, combining similarity-based, graph-based, and
rule-based strategies.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 3.1 | **Query intent classifier** — A lightweight classifier (can be rule-based initially, upgraded to a fine-tuned model in Phase 5) that maps an incoming natural-language or structured query to one or more retrieval strategies: `structural_traversal`, `call_chain_trace`, `temporal_range`, `semantic_search`, `community_summary`. | — | 0.3 |
| 3.2 | **Retrieval strategy executors** — Implement each strategy as a pluggable module: | — | Phase 2 complete |
|  | • `StructuralTraversal` — BFS/DFS from a given node within the containment hierarchy. | | |
|  | • `CallChainTrace` — Forward/backward reachability on the causal graph, bounded by depth. | | |
|  | • `TemporalRange` — Filter nodes/edges by commit timestamp window. | | |
|  | • `SemanticSearch` — Vector-similarity lookup, re-ranked by graph proximity to the query's anchor node. | | |
|  | • `CommunitySummary` — Return pre-generated summaries for relevant communities. | | |
| 3.3 | **Result fusion & ranking** — When multiple strategies fire, merge results with a weighted reciprocal-rank fusion. Weights are initially hand-tuned; Phase 5 learns them. Cap total context returned to a configurable token budget (default 8K tokens). | — | 3.1, 3.2 |
| 3.4 | **Memory Service API** — Expose the retrieval gateway as a gRPC + REST service. Endpoints: | — | 3.3 |
|  | • `POST /query` — Free-form query, returns ranked context snippets. | | |
|  | • `POST /trace` — Given a function ID, returns its call-chain neighbourhood. | | |
|  | • `POST /context` — Given a file path + cursor position, returns the most relevant structural + semantic context for that editing location. | | |
|  | • `GET /summary/{community_id}` — Returns the community summary. | | |
| 3.5 | **Latency validation** — Benchmark all endpoints on the 100K-node test graph. p50 < 100 ms, p95 < 500 ms. Profile and index-tune as needed. | — | 3.4 |

**Exit criteria:** An agent can call the Memory Service and receive relevant,
ranked code context for structural, causal, temporal, and semantic queries
within the latency budget.

---

### Phase 4 — Agent Integration: Read Path & Write Path

**Goal:** Wire the memory system into the actual agent runtime so agents both
*consume* and *produce* memory.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 4.1 | **Read-path integration** — Modify the agent's context-assembly pipeline to call the Memory Service before each LLM invocation. The retrieval gateway response is injected into the system prompt or a dedicated `<memory>` block. Implement a token-budget arbiter that balances memory context vs. user-provided context vs. tool output. | — | 3.4 |
| 4.2 | **Write-path: post-action memory writer** — After the agent completes an action (edit, test run, explanation), extract learnings and write them back to the graph: | — | 3.4 |
|  | • **Structural updates** — If the agent created/moved/deleted files, trigger incremental re-extraction (Phase 1.4). | | |
|  | • **Annotations** — Agent can attach short annotations to nodes: "This function is the main entry point for auth flow", "Known flaky — timeout under load". Stored as `Annotation` nodes with `author=agent`, timestamp. | | |
|  | • **Outcome signals** — Record whether the agent's action succeeded or failed, linked to the memory context that was retrieved. This is the reward signal for Phase 5 RL. | | |
| 4.3 | **Session memory (L0) management** — Implement the session-scoped working-memory tier. On session start, pre-load a configurable seed context (e.g., community summaries for recently-touched packages). On session end, promote successful annotations and discovered patterns to L1. | — | 4.1, 4.2 |
| 4.4 | **MCP tool exposure** — Expose memory operations as MCP (Model Context Protocol) tools so any MCP-compatible agent can call them: `memory_query`, `memory_trace`, `memory_annotate`, `memory_context`. | Parallel with 4.3 | 3.4 |

**Exit criteria:** An agent session that modifies code in repo A can, in a
subsequent session on the same repo, retrieve the annotations and structural
context from the first session without re-parsing.

---

### Phase 5 — Memory Evolution: Consolidation, Decay & Learning

**Goal:** Make the memory system self-improving. This maps to the "Memory
Evolution" stage — the system prunes stale knowledge, consolidates redundant
entries, and learns better retrieval policies from agent outcomes.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 5.1 | **Consolidation engine** — Periodically (configurable, default nightly): | — | Phase 4 complete |
|  | • Merge duplicate or near-duplicate annotations on the same node. | | |
|  | • Re-run community detection if the structural graph changed by > N% since last run. | | |
|  | • Regenerate community summaries for affected communities. | | |
|  | • Compact tombstoned nodes older than the temporal retention window. | | |
| 5.2 | **Decay & eviction** — Nodes and edges whose staleness score exceeds a threshold are demoted: L1 → archived, Annotations → soft-deleted. Access by the agent resets the staleness clock (recency signal). | Parallel with 5.1 | 2.4 |
| 5.3 | **Retrieval-policy learning (RL-lite)** — Collect `(query, retrieved_context, action, outcome)` tuples from 4.2. Train a lightweight reward model (logistic regression or small MLP) that predicts outcome quality from retrieval features (strategy weights, graph distance, staleness, embedding similarity). Use the learned weights to replace the hand-tuned fusion weights in 3.3. Retrain weekly. | — | 4.2, 3.3 |
| 5.4 | **Cross-repo pattern extraction (L2)** — For organisations with multiple repos, identify recurring architectural patterns (e.g., "controller → service → repository" layering) via subgraph isomorphism on the structural graphs. Promote these patterns to L2 as reusable expertise templates. | Parallel with 5.3 | 4.3 |
| 5.5 | **Feedback loop telemetry** — Instrument the full loop: extraction → storage → retrieval → agent action → outcome → evolution. Emit OpenTelemetry spans so we can measure: retrieval hit rate, context utilisation ratio (how much retrieved memory the agent actually used), and outcome correlation. | Parallel with 5.1 | Phase 4 complete |

**Exit criteria:** After 100+ agent sessions on a test repo, the retrieval
fusion weights have shifted measurably from initial values, and retrieval
precision (measured by context-utilisation ratio) has improved by ≥ 10%
relative.

---

### Phase 6 — Hardening & Production Readiness

**Goal:** Make the system reliable, observable, and deployable.

| ID | Work Item | Parallel? | Hard Dependency |
|---|---|---|---|
| 6.1 | **Auth & multi-tenancy** — Ensure graph data is isolated per-organisation. Implement RBAC so agents can only read/write memory for repos they have access to. | — | 3.4 |
| 6.2 | **Backup & restore** — Automated graph snapshots (daily). Point-in-time restore tested. | Parallel with 6.1 | 0.2 |
| 6.3 | **Rate limiting & circuit breakers** — Protect the Memory Service from runaway agent loops. Configurable per-session write rate limits. Circuit breaker on the embedding pipeline (external model calls). | Parallel with 6.1 | 3.4 |
| 6.4 | **Integration test suite** — End-to-end tests: clone a repo → ingest → query → mutate → re-ingest → verify graph consistency. Run in CI on every PR to the memory service. | — | Phase 5 complete |
| 6.5 | **Documentation & runbooks** — Operator guide: deployment, configuration knobs, alerting thresholds. Developer guide: extending the schema, adding a new retrieval strategy, onboarding a new language. | Parallel with 6.4 | All phases |

**Exit criteria:** The system passes a 72-hour soak test under simulated
multi-agent load (10 concurrent sessions, 5 repos, continuous
ingest + query). No data loss, no p95 > 1 s, no memory leaks.

---

## 4  Dependency Graph (Critical Path)

```
Phase 0 (Foundation)
  │
  ├──► Phase 1 (Extraction)
  │       │
  │       ├──► Phase 2 (Storage: temporal + semantic)  ◄── parallel-safe within
  │       │       │
  │       │       └──► Phase 3 (Retrieval Gateway)
  │       │               │
  │       │               └──► Phase 4 (Agent Integration)
  │       │                       │
  │       │                       └──► Phase 5 (Evolution)
  │       │                               │
  │       │                               └──► Phase 6 (Hardening)
  │       │
  │       └──(1.4 incremental update also feeds 2.1 temporal annotator)
  │
  └──► 0.4 (identity scheme) feeds into 1.1
```

**Critical path:** 0.1 → 0.3 → 1.1 → 1.2 → 1.4 → 2.1 → 2.3 → 3.2 → 3.3 → 3.4 → 4.1 → 5.3

**Parallel-safe items** (can be worked by a second engineer at any time once
their dependency is met):

- 0.4 (parallel with 0.3)
- 1.3 (parallel with 1.2)
- 2.2 (parallel with 2.1)
- 2.4 (parallel with 2.3)
- 4.4 (parallel with 4.3)
- 5.1, 5.2, 5.4, 5.5 (all parallel with 5.3)
- 6.1, 6.2, 6.3, 6.5 (all parallel with 6.4)

---

## 5  Key Technical Decisions & Trade-offs

### 5.1  Graph Backend

| Option | Pros | Cons | Recommendation |
|---|---|---|---|
| **Neo4j** | Mature Cypher, APOC library, built-in vector index | Heavy JVM footprint, commercial licence for clustering | Good for production at scale |
| **FalkorDB** | Redis-compatible, sub-ms reads, Apache-2.0 | Younger ecosystem, no native vector index (need sidecar) | Best for latency-sensitive deployments |
| **Kuzu** | Embedded, zero-ops, columnar storage, fast analytics | No server mode, limited tooling | Best for single-machine / dev-loop use |

**Recommendation:** Start with Kuzu for Phase 0–2 (fast iteration, no infra). Migrate to Neo4j or FalkorDB when multi-tenant / multi-service deployment is needed (Phase 6).

### 5.2  Embedding Model

Use a **code-specialised** embedding model. Candidates:

- `voyage-code-3` — best-in-class on code retrieval benchmarks.
- `text-embedding-3-small` (OpenAI) — good balance of cost and quality.
- `nomic-embed-code` — open-weight, self-hostable.

The embedding pipeline must be **swappable** (interface-based) because models
improve rapidly.

### 5.3  LLM Usage Budget

LLM calls are the most expensive part of the pipeline. Minimise them:

- **Community summaries** are generated once per community, re-generated only
  on structural change (not on every query).
- **Annotation extraction** uses a small/fast model (Haiku-class) — this runs
  on every agent session end.
- **Retrieval** is LLM-free (graph traversal + vector search + fusion). The
  LLM is only involved in the final answer generation, which is the agent's
  responsibility, not the memory service's.

### 5.4  Incremental vs. Full Rebuild

Full graph rebuild is O(repo size) and may take minutes for large repos. The
incremental pipeline (Phase 1.4) is critical for developer experience:

- On each `git pull` or file save, only re-extract changed files.
- Use content hashing to detect true changes vs. whitespace-only diffs.
- Cascade: if function F's signature changed, re-check all callers of F in the
  call-chain view (bounded fan-out).

---

## 6  Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| **LSP extraction is unreliable for dynamic languages** | High | Medium | Fall back to Tree-sitter AST parsing; mark extracted edges with `confidence < 1.0`; allow agent annotations to correct errors |
| **Graph grows unbounded for large monorepos** | Medium | High | Staleness-based eviction (Phase 5.2); scope ingest to configured subtrees; archive cold nodes to blob storage |
| **Embedding model quality degrades on niche languages** | Medium | Medium | Make embedding model swappable; support per-language model overrides; supplement with TF-IDF fallback |
| **RL-based retrieval policy overfits to small feedback set** | Medium | Low | Require minimum 100 outcome samples before activating learned weights; always blend with rule-based baseline (floor weight 0.3) |
| **Latency regression as graph scales** | Low | High | Index-tune continuously; enforce p95 SLO in CI; consider graph partitioning by package |
| **Agent writes incorrect annotations** | High | Medium | All annotations carry `confidence` and `source=agent`; consolidation engine (5.1) flags conflicting annotations for human review |

---

## 7  Success Metrics

| Metric | Baseline (no memory) | Target (Phase 4 complete) | Target (Phase 5 complete) |
|---|---|---|---|
| **Context relevance** (% of retrieved tokens the agent actually uses) | N/A | ≥ 40% | ≥ 55% |
| **Retrieval latency** p95 | N/A | < 500 ms | < 500 ms |
| **Incremental ingest time** (10-file diff, 100K-node graph) | N/A | < 5 s | < 5 s |
| **Agent task success rate** (on SWE-Bench-lite subset) | Baseline X% | X + 5% | X + 10% |
| **Token cost per session** (memory-augmented vs. naive re-read) | 1.0× | 0.7× | 0.6× |
| **Retrieval precision improvement** (via RL weights) | — | — | ≥ 10% relative over hand-tuned |

---

## 8  Open Questions (to resolve before Phase 1 starts)

1. **Language scope for V1:** TypeScript + Python? Or include Go / Java / C#
   from the start? (Recommendation: TS + Python for V1; add others in a follow-up story.)
2. **Graph backend:** Confirm Kuzu-first strategy or jump straight to Neo4j
   if the team already has Neo4j operational expertise.
3. **Embedding model hosting:** Use a managed API (OpenAI / Voyage) or
   self-host (Nomic)? Depends on data-residency requirements.
4. **L2 cross-repo memory:** Is this in-scope for this story's 10 SP, or
   should 5.4 be split into a separate story? (Recommendation: split it out.)
5. **MCP tool surface:** Confirm the MCP tool names and schemas align with the
   existing agent tool registry.
