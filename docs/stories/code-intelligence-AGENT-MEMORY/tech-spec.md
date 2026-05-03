# Tech Spec — Agent Memory (Graph-Based Code Intelligence)

**Story:** `code-intelligence:AGENT-MEMORY` · **Points:** 10
**Status:** Draft · **Last updated:** 2026-05-03

---

## 1  Problem Statement

Coding agents today operate without persistent, structured memory of the
repositories they work in. Every session is a cold start: the agent must
re-discover module boundaries, call chains, naming conventions, and
architectural patterns from scratch by reading files and grepping symbols.
This has three concrete costs:

| Cost | Impact |
|------|--------|
| **Token waste** | 30–60 % of an agent's context window is spent on orientation work that could be pre-computed. |
| **Inconsistent results** | Without accumulated expertise the agent makes contradictory decisions across sessions (e.g., different error-handling patterns in the same service). |
| **Shallow understanding** | Flat file reads miss the relational structure—*which functions call which*, *which modules depend on which*—that a human developer builds over weeks. |

The graph-based agent memory literature (Yang et al., 2026 — "Graph-based
Agent Memory: Taxonomy, Techniques, and Applications", arXiv:2602.05665)
identifies four lifecycle phases—extraction, storage, retrieval,
evolution—and demonstrates that graph structures outperform flat or
vector-only memory on tasks requiring relational reasoning, hierarchical
navigation, and temporal awareness. This spec applies those findings to a
concrete domain: **top-down repository structure and call-chain context for
code intelligence agents**.

The goal is a memory layer that lets an agent *learn* a codebase
progressively—accumulating structural knowledge, call-chain topology, and
task-specific expertise—so that each successive interaction is faster, more
accurate, and more architecturally consistent than the last.

---

## 2  In-Scope

### 2.1  Graph Schema for Repository Structure (Top-Down Hierarchy)

Define a node/edge schema that captures the top-down decomposition of a
repository:

```
Repository ──contains──▶ Workspace/Package
  Workspace  ──contains──▶ Module/Directory
    Module     ──contains──▶ File
      File       ──defines───▶ Symbol (class, function, type, const)
        Symbol     ──defines───▶ Symbol (nested: methods, inner classes)
```

Each node carries properties: path, language, visibility, docstring digest,
signature hash, last-modified timestamp. Edges carry weight (structural
depth) and cardinality.

### 2.2  Graph Schema for Call-Chain Context

Overlay a directed graph of runtime/static call relationships on the same
symbol nodes:

| Edge type | Semantics |
|-----------|-----------|
| `calls` | Function A invokes function B |
| `imports` | File A imports symbol from file B |
| `implements` | Class A implements interface B |
| `extends` | Class A extends class B |
| `uses-type` | Function A references type T in signature or body |
| `emits` / `handles` | Function A emits event E / Function B handles event E |

These edges enable multi-hop traversal: "given a function under edit, what
is the transitive blast radius?"

### 2.3  Memory Extraction Pipeline

A batch + incremental pipeline that converts source code into graph
artifacts:

1. **Static analysis front-end** — language-specific parsers (Tree-sitter
   grammars or LSP-based symbol providers) produce AST-level symbol tables.
2. **Relation extractor** — resolves imports, call sites, type references,
   and inheritance edges across files.
3. **Summariser** — LLM-generated one-line purpose summaries for modules,
   classes, and non-trivial functions, stored as node properties.
4. **Fingerprinter** — content-hash each symbol body so incremental updates
   can detect what changed.

### 2.4  Memory Storage Layer

A persistent graph store that supports:

- **Hierarchical structure** — the containment tree (§2.1).
- **Knowledge graph overlay** — call-chain and type relationships (§2.2).
- **Temporal annotations** — when each node/edge was created, last
  confirmed, and last modified (following the Zep temporal-KG pattern,
  arXiv:2501.13956).
- **Expertise layer** — agent-authored observations ("this module follows
  the repository pattern", "error handling here uses Result<T,E> not
  exceptions") attached to nodes as property bags.

Storage must support sub-second graph-local queries (neighbourhood
expansion, shortest path) and bulk export to serialised formats (e.g.,
JSON-LD, adjacency list) for embedding in agent prompts.

### 2.5  Memory Retrieval

Given a task context (file path, symbol name, natural-language intent),
retrieve a relevant subgraph to inject into the agent's context window:

- **Structural retrieval** — walk the containment tree upward (file →
  module → package) and downward (siblings, children) to establish
  positional context.
- **Call-chain retrieval** — BFS/DFS from the focal symbol along `calls`,
  `imports`, `implements` edges, bounded by depth and relevance score.
- **Expertise retrieval** — similarity search over the expertise-layer
  annotations using the task intent as query.
- **Temporal retrieval** — bias toward recently-modified or
  recently-confirmed nodes to avoid stale context.

Retrieval output is a ranked, token-budgeted context block: the system
truncates or summarises subgraph regions to fit within a caller-specified
token ceiling.

### 2.6  Memory Evolution

Mechanisms that keep the graph current and let it accumulate agent-learned
expertise:

| Trigger | Action |
|---------|--------|
| **Code change (commit / file save)** | Re-run extraction on affected files; diff against stored fingerprints; update/add/tombstone nodes and edges. |
| **Agent session end** | Persist any new expertise annotations the agent generated during the session (pattern observations, architectural notes, gotchas). |
| **Staleness sweep** | Periodic job marks nodes whose fingerprint no longer matches disk as `stale`; prunes tombstoned nodes older than a configurable TTL. |
| **Conflict resolution** | When two sessions produce contradictory expertise annotations for the same node, apply last-writer-wins with a confidence score tiebreaker. |

### 2.7  Agent Integration Surface

- **Context-construction API** — given `(repo, focal_file, focal_symbol?,
  intent?, token_budget)`, return a serialised context block containing
  the retrieved subgraph, ready for prompt injection.
- **Write-back API** — given `(repo, node_id, expertise_annotation)`,
  persist an agent-learned observation.
- **Health API** — return graph staleness metrics (% stale nodes, last
  extraction timestamp, node/edge counts).

---

## 3  Out of Scope

| Item | Rationale |
|------|-----------|
| **Conversational / personal memory** (user preferences, chat history) | Different data model; handled by session-store infrastructure already in place. |
| **Multi-agent shared-memory coordination** (G-Memory, MAGMA patterns) | Adds distributed-systems complexity; defer until single-agent memory is proven. |
| **LLM fine-tuning on the graph** | The memory is consumed via prompt injection (RAG-style), not weight updates. |
| **IDE plugin / VS Code extension UI** | This spec covers the backend; presentation is a separate deliverable. |
| **Real-time streaming analysis** (watch-mode incremental parse) | Batch + on-demand incremental is sufficient for v1; streaming is an optimisation. |
| **Non-code artifacts** (markdown docs, issue trackers, PR descriptions) | Valuable but increases schema surface; defer to a follow-on story. |
| **Cross-repository memory** (shared libraries, monorepo external deps) | Scoped to a single repository root for v1. |

---

## 4  Non-Goals

1. **Replace existing code search / indexing tools.** The memory graph
   augments grep, LSP, and Tree-sitter—it does not replace them. Those
   tools remain the source-of-truth parsers; the graph is a derived,
   persistent cache of their output plus agent-learned expertise.

2. **Build a general-purpose knowledge-graph platform.** The schema and
   query patterns are purpose-built for code-structure and call-chain
   navigation. We are not building a generic triple store with SPARQL
   endpoints.

3. **Guarantee 100 % graph accuracy.** Static analysis has known
   limitations (dynamic dispatch, reflection, metaprogramming). The graph
   is best-effort; retrieval must tolerate missing edges gracefully.

4. **Support every programming language on day one.** The schema is
   language-agnostic, but extraction requires per-language parser support.
   v1 targets TypeScript/JavaScript and Python; additional languages are
   additive work behind the same schema.

5. **Optimise for multi-gigabyte monorepos.** v1 targets repositories up to
   ~50 K files. Monorepo-scale sharding and federation are deferred.

---

## 5  Hard Constraints

| # | Constraint | Justification |
|---|-----------|---------------|
| HC-1 | **Language-agnostic graph schema.** Node and edge types must not embed language-specific concepts (e.g., "Java package" vs "Python module"). Language is a node property, not a schema axis. | Prevents schema explosion as language support grows. |
| HC-2 | **Incremental update, not full rebuild.** After initial bootstrap, graph updates must process only changed files (detected via content hash or VCS diff). | Full rebuilds on large repos are too slow for interactive use; the Mem0 architecture (arXiv:2504.19413) validates incremental-first design. |
| HC-3 | **Sub-second retrieval latency** for neighbourhood queries (≤ 3-hop expansion from a focal node, ≤ 500 result nodes). | Memory retrieval is on the critical path of agent context construction; latency > 1 s degrades interactive UX. |
| HC-4 | **Bounded storage growth.** Graph size must be O(symbols + relationships), not O(history). Tombstoned nodes are pruned after a configurable TTL (default 30 days). Expertise annotations are capped per node (default 20). | Prevents unbounded disk/memory growth over months of use. |
| HC-5 | **No network calls during retrieval.** The graph store must be local or locally-cached. Retrieval must not depend on an external service being reachable. | Agents must work offline / in air-gapped environments. |
| HC-6 | **Extraction must not modify the repository.** The pipeline is read-only with respect to the user's source tree. All artifacts are written to a dedicated store directory (e.g., `.agent-memory/`). | Safety: users must trust that the memory system cannot corrupt their code. |
| HC-7 | **Deterministic extraction.** Given the same repo state (commit SHA), extraction must produce a byte-identical graph. | Enables cache validation, diffing, and reproducible debugging. |

---

## 6  Identified Risks

### 6.1  Graph Staleness

**Risk:** Code changes faster than the memory graph is updated, causing the
agent to reason over an outdated topology.

**Likelihood:** High — developers save files many times per minute during
active coding.

**Mitigation:**
- Retrieval annotates each returned node with a `freshness` flag
  (current / stale / tombstoned) so the agent can weigh confidence.
- On-demand re-extraction of the focal file and its direct dependents
  before retrieval ("lazy refresh").
- Staleness is surfaced in the Health API so callers can decide whether to
  trust the graph or fall back to live analysis.

### 6.2  Graph Explosion on Large Repositories

**Risk:** Repositories with 50 K+ files and deep dependency trees produce
graphs with millions of nodes, exceeding memory and storage budgets.

**Likelihood:** Medium — most target repos are smaller, but monorepo
workspaces exist.

**Mitigation:**
- Configurable extraction scope (include/exclude globs).
- Hierarchical summarisation: below a configurable depth, collapse
  subtrees into summary nodes ("this directory contains 47 test files
  with 312 test functions") instead of individual nodes.
- Storage backend supports memory-mapped access so the full graph need not
  be resident.

### 6.3  Retrieval Precision — Too Much or Too Little Context

**Risk:** The retrieved subgraph is either so large it blows the token
budget, or so small it misses critical call-chain context.

**Likelihood:** High — this is the central quality challenge identified in
the survey's retrieval section.

**Mitigation:**
- Token-budget-aware retrieval: the retriever operates under a hard token
  ceiling and uses a priority queue (relevance × recency × proximity) to
  decide what to include.
- Adaptive depth: start with 1-hop expansion; if budget remains, expand
  to 2-hop; repeat.
- Post-retrieval summarisation: for nodes included only for structural
  context, return a one-line summary instead of full signatures.
- Feedback loop: if the agent signals that context was insufficient
  (e.g., asks a follow-up question about a symbol not in the subgraph),
  widen retrieval on the next call.

### 6.4  Cold-Start Latency

**Risk:** First-time extraction on a large repo takes minutes, blocking the
agent's first interaction.

**Likelihood:** High — parsing 10 K files with Tree-sitter plus LLM
summarisation is not instant.

**Mitigation:**
- Two-phase bootstrap: Phase 1 (seconds) extracts structural hierarchy
  and import edges only (no LLM calls). Phase 2 (background) adds
  summaries, call-chain edges, and deeper analysis.
- The agent can operate on Phase 1 output immediately with degraded but
  functional context.
- Progress reporting via Health API so the agent/UI can communicate
  "memory is still building" to the user.

### 6.5  Evaluation Gap

**Risk:** No established benchmark measures whether graph-based memory
actually improves coding-agent task quality in our specific domain (repo
structure + call chains).

**Likelihood:** High — existing benchmarks (SWE-Bench, MEMTRACK) test
different dimensions.

**Mitigation:**
- Define an internal evaluation harness: a set of repositories with known
  architectural patterns, plus a set of coding tasks (bug fix, feature
  add, refactor). Measure task-completion accuracy and token efficiency
  with memory on vs. off.
- Track proxy metrics in production: context-window utilisation, agent
  re-query rate, user-reported "agent understood the codebase" score.

### 6.6  Expertise Drift and Hallucinated Annotations

**Risk:** Agent-authored expertise annotations may be incorrect or become
stale as the code evolves, poisoning future sessions.

**Likelihood:** Medium — LLM-generated summaries are known to hallucinate.

**Mitigation:**
- Expertise annotations are always secondary to live code analysis; the
  agent is instructed to verify annotations against current source.
- Annotations carry a confidence score and a `confirmed_at` timestamp;
  retrieval deprioritises low-confidence or old annotations.
- A "skeptic mode" flag lets callers exclude expertise annotations
  entirely and rely only on structural/call-chain data.
- Annotation cap per node (HC-4) limits accumulation of contradictory
  observations.

### 6.7  Parser Coverage Gaps

**Risk:** Static analysis cannot resolve all call-chain edges (dynamic
dispatch, dependency injection, runtime-generated code, FFI boundaries).

**Likelihood:** Certain — this is a fundamental limitation of static
analysis.

**Mitigation:**
- The graph schema supports `confidence` on edges (1.0 for statically
  proven, 0.5 for heuristic, 0.0 for inferred-by-LLM).
- Missing edges are partially recoverable via LLM-based heuristic
  extraction ("this function name matches the event handler pattern →
  likely called by the event bus").
- Retrieval surfaces confidence metadata so the agent can reason about
  uncertainty.

### 6.8  Concurrency and Corruption

**Risk:** Multiple agent sessions or background extraction jobs writing to
the graph store simultaneously cause data corruption or lost updates.

**Likelihood:** Medium — likely in multi-window or CI-triggered scenarios.

**Mitigation:**
- Graph store uses a single-writer / multiple-reader concurrency model
  (e.g., SQLite WAL mode or equivalent).
- Extraction acquires a write lock; retrieval operates on a consistent
  snapshot.
- If a write lock cannot be acquired within a timeout, the extraction job
  queues and retries rather than failing the agent session.

---

## 7  Key Research References

| Short name | Full reference | Relevance |
|------------|---------------|-----------|
| **GraphMemory Survey** | Yang et al., "Graph-based Agent Memory: Taxonomy, Techniques, and Applications", arXiv:2602.05665 (2026) | Foundational taxonomy (extraction → storage → retrieval → evolution) adopted as our lifecycle model. |
| **Zep** | "Zep: A Temporal Knowledge Graph Architecture for Agent Memory", arXiv:2501.13956 (2025) | Temporal annotation pattern for nodes/edges; informs our staleness and freshness design. |
| **Mem0** | "Mem0: Building Production-Ready AI Agents with Scalable Long-Term Memory", arXiv:2504.19413 (2025) | Production patterns for incremental update, bounded storage, and retrieval API design. |
| **HiAgent** | "HiAgent: Hierarchical Working Memory Management for Solving Long-Horizon Agent Tasks", ACL 2025 | Hierarchical memory structure maps directly to top-down repo decomposition. |
| **MAGMA** | "MAGMA: A Multi-Graph based Agentic Memory Architecture", arXiv:2601.03236 (2026) | Multi-graph overlay pattern (structural + semantic + temporal) informs our layered schema. |
| **Reflexion** | Shinn et al., "Reflexion: Language Agents with Verbal Reinforcement Learning", NeurIPS 2023 | Agent self-reflection producing expertise annotations; informs our write-back mechanism. |
| **GraphRAG** | "From Local to Global: A Graph RAG Approach to Query-Focused Summarization", arXiv:2404.16130 (2024) | Community-detection-based summarisation for large graphs; informs our hierarchical collapse strategy for large repos. |
| **Graphiti** | Zep/Graphiti open-source framework | Production-grade temporal knowledge graph library; candidate for storage-layer implementation. |

---

## 8  Success Criteria (Exit Conditions for This Story)

| # | Criterion | Measurement |
|---|-----------|-------------|
| SC-1 | Graph schema is defined and validated against ≥ 2 real repositories (one TS, one Python, each > 500 files). | Schema supports all top-level node/edge types from §2.1–2.2 without ad-hoc extensions. |
| SC-2 | Extraction pipeline produces a correct graph for the validation repos. | Spot-check: 50 randomly sampled call-chain edges verified against manual inspection; ≥ 90 % precision. |
| SC-3 | Incremental update processes only changed files and completes in < 5 s for a 10-file change set on a 5 K-file repo. | Wall-clock benchmark. |
| SC-4 | Retrieval returns a token-budgeted context block in < 500 ms for a 3-hop query on a 5 K-file repo graph. | p95 latency benchmark. |
| SC-5 | Agent using memory-augmented context produces measurably better results than baseline on ≥ 5 representative tasks. | A/B evaluation on internal task suite (defined as part of Risk 6.5 mitigation). |

---

## Appendix A  Glossary

| Term | Definition |
|------|-----------|
| **Focal symbol** | The function, class, or file the agent is currently reasoning about. |
| **Blast radius** | The transitive set of symbols reachable from the focal symbol via call-chain edges. |
| **Expertise annotation** | A natural-language observation authored by an agent and attached to a graph node (e.g., "this class is the main entry point for the billing pipeline"). |
| **Tombstoned node** | A node whose corresponding source-code symbol has been deleted; retained temporarily for relationship continuity, then pruned after TTL. |
| **Lazy refresh** | On-demand re-extraction of a small subgraph (focal file + direct dependents) triggered at retrieval time to ensure freshness. |
| **Token budget** | The maximum number of LLM tokens allocated for the memory-derived context block within a single agent prompt. |

---

## Appendix B  Taxonomy Mapping

The following table maps the lifecycle phases from the GraphMemory survey
(arXiv:2602.05665) to their instantiation in this system:

| Survey phase | Survey sub-categories | Our instantiation |
|-------------|----------------------|-------------------|
| **Extraction** | Textual data, Sequential data | Tree-sitter / LSP parse → symbol tables; git-diff → change sets |
| **Storage** | Knowledge-graph structure, Hierarchical structure, Temporal annotations | Containment tree (hierarchy) + call-chain overlay (KG) + timestamp properties (temporal) |
| **Retrieval** | Similarity-based, Graph-based, Temporal-based | BFS/DFS traversal with relevance scoring; recency bias; adaptive depth under token budget |
| **Evolution** | Internal self-evolving, External self-exploration | Incremental re-extraction on code change; agent write-back of expertise annotations; staleness sweep |
