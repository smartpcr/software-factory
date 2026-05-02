# Agent Memory — Technical Specification

**Story:** `code-intelligence:AGENT-MEMORY`
**Points:** 10
**Status:** Draft
**Author:** Yuan Shi Tianzun (Architect)

---

## 1  Problem Statement

LLM-based coding agents are stateless across sessions. Every new session
re-discovers repository structure, re-traces call chains, and re-learns
codebase idioms. The cost is concrete:

| Metric | Current state | Impact |
|---|---|---|
| Orientation latency | 30–120 s per session start | Blocks first meaningful action |
| Token spend on re-discovery | 5 000–40 000 tokens per session | ~40 % of context window consumed before productive work |
| Repeated errors | Same misunderstanding of a module surfaces across sessions | Agent never "learns" |

The problem compounds with repository scale. A 500-file repo demands minutes
of orientation; a 10 000-file monorepo can exceed the context window before the
agent locates the right file.

**Root cause:** There is no persistent, structured representation of what the
agent has already understood about a codebase. Flat-text memory (e.g.,
session transcripts, RAG over files) captures surface tokens but not the
relational structure — containment hierarchies, call dependencies, type
relationships — that determines how code *works*.

**Proposed solution:** Build a graph-based agent memory system that
persistently captures repository structure (top-down hierarchy) and call-chain
context as a knowledge graph. Agents query this graph to orient instantly,
retrieve structurally relevant context, and accumulate expertise over time.

### 1.1  Why Graph Memory

Research on graph-based agent memory (Yang et al., 2026, arXiv:2602.05665)
establishes that graphs outperform flat memory stores along three axes relevant
to code intelligence:

1. **Relational fidelity.** Code is inherently a graph: modules contain files,
   files define classes, classes expose methods, methods call other methods.
   A knowledge graph preserves these relationships natively.
2. **Hierarchical retrieval.** Top-down repo structure maps to hierarchical
   graph traversal — exactly the MAGMA (arXiv:2601.03236) pattern of
   orthogonal views (structural, semantic, temporal) with query-adaptive
   retrieval.
3. **Incremental evolution.** Graph structures support localized update on
   code change without full rebuild — critical for the Zep/Graphiti
   (arXiv:2501.13956) insight that memory must track temporal evolution to
   remain useful.

---

## 2  Scope

### 2.1  In Scope (v1)

The following capabilities constitute the minimum viable memory system.

| # | Capability | Description |
|---|---|---|
| S1 | **Structural graph** | Nodes for repository, package/namespace, module/file, class/struct, function/method, type/interface. Edges for `contains`, `imports`, `exports`, `re-exports`. |
| S2 | **Call-chain overlay** | Edges for `calls`, `extends`, `implements`, `type-references`, `instantiates`. Derived from static analysis (AST + LSP). Each edge carries a confidence tier: `exact`, `heuristic`, `unresolved`. |
| S3 | **Retrieval interface** | Given a task context (file path, symbol name, natural-language intent), return the relevant subgraph: the target node, its structural ancestors, its call-chain neighbors (N-hop configurable, default 2), and summary metadata. |
| S4 | **Incremental refresh** | On file save or commit, re-parse only changed files and update affected nodes/edges. Full rebuild only on explicit user request or branch switch. |
| S5 | **Versioning and freshness** | Memory keyed by `(repo, branch/worktree)`. Each node/edge carries a `last_validated_commit` SHA. Stale nodes are flagged but not silently served as current. |
| S6 | **Indexing boundaries** | Configurable exclusion rules: generated code, vendored dependencies, build outputs, test fixtures (opt-in), binary files. Default exclusions derived from `.gitignore` plus a built-in deny list. |

### 2.2  In Scope (v2 — Deferred)

These are designed into the schema now but implemented after v1 proves value.

| # | Capability | Description |
|---|---|---|
| D1 | **Semantic annotation layer** | LLM-generated summaries (purpose, patterns, idioms) attached to graph nodes. Confidence-scored and invalidated on code change. |
| D2 | **Episodic event log** | Minimal records of past agent interactions: changed files/symbols, outcome status (success/failure/abandoned), timestamp, session link. Full intent modeling ("why") deferred to v3. |
| D3 | **Cross-session learning** | Aggregation of episodic events into pattern nodes — e.g., "function X is frequently modified alongside function Y" — enabling proactive context inclusion. |

### 2.3  Out of Scope

| Item | Rationale |
|---|---|
| Multi-repo / cross-repo memory | Single-repo memory must prove value first. Cross-repo introduces identity-resolution complexity that is a separate initiative. |
| Runtime / dynamic call traces | Requires instrumented execution, raises security concerns, and is language-runtime-specific. Static analysis provides sufficient signal for v1. |
| LLM fine-tuning on memory data | Memory is consumed via prompt context, not weight updates. Fine-tuning is a separate research track. |
| Multi-user collaborative memory | Single-agent-per-repo-per-user. Shared memory introduces conflict resolution and access control that is orthogonal to the core graph. |
| Conversational / dialogue memory | Session-level dialogue context is managed by the agent runtime, not by this system. This system owns *code* knowledge, not *conversation* knowledge. |

---

## 3  Non-Goals

1. **Replacing LSP or existing code intelligence.** This system *consumes*
   LSP and AST data as inputs. It does not replicate symbol resolution,
   go-to-definition, or diagnostics. If the LSP is available, the memory
   graph defers to it for live queries; the graph captures what the LSP
   forgets between sessions.

2. **Building a general-purpose knowledge graph engine.** The graph schema,
   storage format, and query patterns are purpose-built for code structure.
   There is no ambition to support arbitrary ontologies.

3. **Achieving perfect recall.** Dynamic dispatch, reflection,
   metaprogramming, and code generation create call-chain edges that static
   analysis cannot resolve. The system classifies these as `unresolved` and
   does not hallucinate edges. Useful approximation is the target; false
   certainty is worse than a gap.

4. **Real-time collaboration.** The memory graph is a single-writer system.
   Concurrent agent sessions against the same repo are serialized at the
   write path.

---

## 4  Hard Constraints

### 4.1  Functional Constraints

| ID | Constraint | Verification |
|---|---|---|
| C1 | Must consume existing LSP and AST parser output; must not require a custom language server. | Integration test per language. |
| C2 | Must support at minimum: TypeScript, Python, C#, Java, Go. | Language-specific test suites. |
| C3 | Memory is keyed by `(repo, branch/worktree)`. Branch switch triggers full freshness revalidation (but not necessarily full rebuild). | Branch-switch integration test. |
| C4 | Every graph edge carries a confidence tier (`exact` · `heuristic` · `unresolved`). Retrieval API exposes confidence so the consumer can filter. | Schema enforcement + API contract test. |
| C5 | Indexing exclusions must honor `.gitignore`, a built-in deny list (e.g., `node_modules/`, `bin/`, `obj/`), and user-configurable overrides. | Exclusion integration test. |

### 4.2  Performance Constraints

All benchmarks measured on a repo of 10 000 files / 100 000 symbols, warm
cache, single concurrent reader, commodity hardware (4-core CPU, 16 GB RAM,
SSD).

| ID | Constraint | Target |
|---|---|---|
| P1 | Subgraph retrieval latency (graph query + assembly, excluding any LLM reranking) | ≤ 200 ms p95 |
| P2 | End-to-end retrieval latency (including optional semantic reranking) | ≤ 500 ms p95 |
| P3 | Incremental refresh for a single-file edit (parse + update) | ≤ 5 s |
| P4 | Full initial index of 10K-file repo | ≤ 10 min |
| P5 | Storage budget per repo | ≤ 500 MB for 10K files |

### 4.3  Operational Constraints

| ID | Constraint |
|---|---|
| O1 | Must not require GPU for any memory operation. LLM calls (used for v2 summarization) are external and async. |
| O2 | Must run locally on developer workstation. No mandatory cloud dependency. |
| O3 | Memory persisted to local disk. Encryption at rest is required. |
| O4 | Graph data must be deletable per-repo with a single operation (full purge). |

---

## 5  Architecture Overview

### 5.1  Graph Schema (v1)

```
Node types:
  Repository      — root sentinel
  Package         — namespace / directory / module group
  File            — source file
  Symbol          — class, function, method, type, interface, enum, variable
                    (discriminated by `symbol_kind` attribute)

Edge types (structural):
  CONTAINS        — parent → child in the file-system / module hierarchy
  IMPORTS         — file → file or file → symbol
  EXPORTS         — file → symbol (public API surface)
  RE_EXPORTS      — file → symbol (re-export from another module)

Edge types (call-chain):
  CALLS           — symbol → symbol (function/method invocation)
  EXTENDS         — symbol → symbol (class inheritance)
  IMPLEMENTS      — symbol → symbol (interface implementation)
  TYPE_REF        — symbol → symbol (type annotation, generic parameter)
  INSTANTIATES    — symbol → symbol (constructor invocation)

Edge attributes:
  confidence      — enum: exact | heuristic | unresolved
  source_location — file:line:col of the reference site
  last_validated  — commit SHA when this edge was last confirmed

Node attributes:
  qualified_name  — fully-qualified identifier
  file_path       — relative path within repo
  span            — byte range in source file
  signature       — function/method signature string (if applicable)
  doc_comment     — extracted doc comment (raw, not LLM-summarized)
  last_validated  — commit SHA
  staleness       — enum: current | stale | deleted
```

### 5.2  Memory Lifecycle

The lifecycle follows the taxonomy established by Yang et al. (2026):

```
 ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐
 │ EXTRACT  │────▶│  STORE   │────▶│ RETRIEVE │────▶│  EVOLVE  │
 └──────────┘     └──────────┘     └──────────┘     └──────────┘
   AST + LSP       Graph DB        Subgraph          Incremental
   parse            (local)        selection          update on
                                   + assembly         code change
```

1. **Extract.** Parse source files via AST and LSP. Emit typed nodes and
   edges. Tag each edge with a confidence tier based on resolution quality.

2. **Store.** Persist the graph to a local embedded store (e.g., SQLite with
   adjacency-list tables, or an embedded graph DB). Indexes on
   `qualified_name`, `file_path`, and `symbol_kind`.

3. **Retrieve.** Accept a query context (file, symbol, intent). Walk the
   graph: locate the target node, expand N hops along call-chain edges,
   collect structural ancestors up to Package level. Assemble and return the
   subgraph as a serialized context block.

4. **Evolve.** On file change, diff the old and new AST. Remove deleted
   nodes/edges, add new ones, update modified spans and signatures. Mark
   downstream edges as `stale` if their source node changed. On branch
   switch, revalidate all nodes against the new HEAD.

### 5.3  Language Support Tiers

Not all languages yield equal static-analysis quality. The system defines
three support tiers:

| Tier | Languages | Structural graph | Call-chain: `exact` | Call-chain: `heuristic` |
|---|---|---|---|---|
| **1 — Full** | TypeScript, Java, C# | ✓ | ✓ (LSP resolves most calls) | Dynamic dispatch, reflection |
| **2 — Strong** | Go, Python (typed) | ✓ | ✓ (with type stubs / type hints) | Untyped Python, interface dispatch |
| **3 — Structural only** | Other languages | ✓ (AST only) | ✗ | File-level import edges only |

Tier 3 is a degraded mode: the structural graph is still valuable for
orientation, but call-chain edges are not produced.

### 5.4  Retrieval Strategy

Retrieval follows the multi-signal pattern from MAGMA (2026), adapted for
code:

1. **Structural proximity.** Walk `CONTAINS` edges upward and downward from
   the target node. Include siblings (other symbols in the same file/class).

2. **Call-chain traversal.** Walk `CALLS`, `EXTENDS`, `IMPLEMENTS`,
   `TYPE_REF` edges outward N hops. Filter by confidence tier (configurable;
   default: include `exact` and `heuristic`, exclude `unresolved`).

3. **Recency weighting.** Prefer nodes whose `last_validated` commit is
   closer to HEAD. Stale nodes are included but demoted.

4. **Interaction frequency** (v2, when episodic layer exists). Prefer nodes
   that the agent has queried or modified in prior sessions.

The retrieval output is a **context block**: a serialized subgraph containing
node metadata and edge relationships, formatted for inclusion in an LLM
prompt. The format is designed for token efficiency — qualified names and
signatures, not full source code.

---

## 6  Freshness and Versioning Model

This section addresses the primary correctness risk: memory diverging from
actual code.

### 6.1  Identity Key

Every memory graph instance is identified by `(repo_root, branch_or_worktree)`.
Switching branches creates or loads a separate graph instance. Worktrees are
treated as independent branches.

### 6.2  Freshness Protocol

| Event | Action |
|---|---|
| **File saved** | Re-parse the file. Diff against prior AST. Update nodes/edges for that file. Mark downstream references `stale`. |
| **File renamed** | Update `file_path` on the File node and all contained Symbol nodes. Update inbound `IMPORTS` edges. |
| **File deleted** | Mark the File node and all contained nodes `deleted`. Remove outbound edges. Mark inbound edges `stale`. |
| **Branch switch** | Load the graph for the target branch (create if absent). Walk all nodes and compare `last_validated` against new HEAD. Re-parse any file whose content differs. |
| **Rebase / force-push** | Treat as branch switch. All `last_validated` SHAs that no longer exist in the reflog trigger re-parse of the associated files. |
| **Dependency update** | If `package.json`, `requirements.txt`, `go.mod`, or equivalent changes, mark all `IMPORTS` edges targeting external packages as `stale`. Do not attempt to index external packages. |
| **Git clean / repo re-clone** | Full rebuild. |

### 6.3  Staleness Semantics

A `stale` node or edge is still served by retrieval but is annotated as such.
The consuming agent decides whether to trust it or re-verify via live LSP.
A `deleted` node is excluded from retrieval results.

Garbage collection: nodes in `deleted` state for > 30 days are physically
removed. Nodes in `stale` state are re-validated on next retrieval hit
(lazy revalidation).

---

## 7  Failure Modes

| Failure | Behavior | Mitigation |
|---|---|---|
| **LSP unavailable** | Fall back to AST-only parsing. Produce structural graph; call-chain edges are `heuristic` or `unresolved` only. | Degrade gracefully; log warning. |
| **AST parse failure** (syntax error) | Skip the unparseable file. Mark its nodes `stale`. | Retry on next save. Do not block other files. |
| **Partial indexing** (timeout during initial build) | Persist whatever has been indexed so far. Resume from the last indexed file on next startup. | Progress is checkpointed per-file. |
| **Branch switch during active update** | Abort the in-progress update. Load the target branch graph. | Write operations are single-threaded; abort is safe. |
| **Large-scale refactor** (rename across 500 files) | Incremental refresh processes files serially. May take > 5 s. | Batch mode: coalesce changes, apply as a single graph transaction. Relax the 5 s SLA for batch commits (target: ≤ 60 s for 500-file change). |
| **Unsupported language** | Treat as Tier 3. Produce File-level nodes only. No symbol-level nodes. | Documented limitation. |
| **Corrupted graph store** | Detect on startup via checksum. If corrupt, delete and rebuild from scratch. | Startup integrity check. |

---

## 8  Security and Privacy

### 8.1  Data Scope

The memory graph contains structural metadata (names, signatures, doc
comments) — not full source code. Source code is read on-demand from the
filesystem during retrieval assembly.

### 8.2  Encryption

Graph storage is encrypted at rest using AES-256. Key management follows the
host platform's credential store (OS keychain / DPAPI / keyring).

### 8.3  Repo Isolation

Memory graphs are strictly scoped per `(repo, branch)`. There is no cross-repo
query path. Deleting a repo's memory is a single atomic operation.

### 8.4  Secret Filtering

Before persisting any text content (doc comments, signatures), the extraction
pipeline runs a secret-detection scan (regex-based patterns for API keys,
tokens, connection strings). Detected secrets are redacted and replaced with
a `[REDACTED]` placeholder.

### 8.5  Provenance

Every node and edge carries a `source` attribute indicating its origin:
`ast_parse`, `lsp_resolve`, `heuristic_inference`. In v2, LLM-generated
summaries carry source `llm_summary` with a model identifier and timestamp.
Retrieval results include provenance so the consuming agent can calibrate
trust.

### 8.6  Agent-Generated Content

When episodic memory is introduced (v2), agent-generated annotations are
stored with source `agent_annotation` and are never ranked above code-derived
facts in retrieval. This prevents memory poisoning: an incorrect agent
observation cannot override what static analysis confirms.

---

## 9  Evaluation Criteria and Success Metrics

The memory system must demonstrate measurable improvement in agent
performance. The following metrics define success for v1.

### 9.1  Primary Metrics

| Metric | Baseline (no memory) | Target (with memory) | Measurement |
|---|---|---|---|
| **Orientation time** — time from session start to first productive action | 30–120 s | ≤ 5 s | Instrumented agent sessions on benchmark repos |
| **Token spend on context discovery** | 5 000–40 000 tokens | ≤ 2 000 tokens | Token counter in retrieval path |
| **Retrieval precision** — % of retrieved nodes actually used by the agent | Unmeasured | ≥ 70 % | Post-hoc analysis of agent actions vs. retrieved context |
| **Task success delta** — SWE-Bench-style task pass rate with vs. without memory | Baseline | ≥ +5 pp improvement | A/B evaluation on internal benchmark |

### 9.2  System Health Metrics

| Metric | Target |
|---|---|
| **Staleness rate** — % of retrieved nodes marked `stale` | ≤ 5 % in steady state |
| **Retrieval latency p95** (graph query only) | ≤ 200 ms |
| **Incremental refresh latency** (single file) | ≤ 5 s |
| **Initial index throughput** | ≥ 1 000 files / min |
| **Storage efficiency** | ≤ 50 bytes / symbol average |

### 9.3  Evaluation Protocol

1. Select 5 benchmark repositories spanning Tier 1 and Tier 2 languages,
   ranging from 500 to 10 000 files.
2. Define 20 coding tasks per repo (bug fix, feature addition, refactor).
3. Run each task twice: once with memory, once without.
4. Measure orientation time, token spend, retrieval precision, task success.
5. Report paired differences with confidence intervals.

---

## 10  Identified Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **Graph staleness** — memory diverges from code after edits, branch switches, or rebases. | High | High | Event-driven incremental updates (§6.2). Staleness annotation on all served data. Lazy revalidation on retrieval hit. |
| R2 | **Static analysis gaps** — dynamic dispatch, reflection, metaprogramming produce `unresolved` edges, leaving holes in the call graph. | High | Medium | Confidence tiers on all edges (§5.1). Language support tiers (§5.3). Agent is informed of gaps, not presented with false completeness. |
| R3 | **Graph explosion** — large monorepos produce graphs that exceed memory or degrade query latency. | Medium | High | Hierarchical summarization: collapse subtrees below a configurable depth. Lazy expansion on query. Indexing exclusions (§2.1 S6). Storage budget constraint (P5). |
| R4 | **Cold-start cost** — initial graph build for a large repo blocks agent use. | Medium | Medium | Progressive indexing: prioritize files the agent references. Checkpoint progress per-file so indexing can resume. Background indexing with partial availability. |
| R5 | **Privacy / secret leakage** — memory persists metadata that could expose secrets or leak across contexts. | Low | Critical | Secret filtering (§8.4). Repo isolation (§8.3). Encryption at rest (§8.2). Provenance tracking (§8.5). Single-operation purge (O4). |
| R6 | **Retrieval noise** — subgraph selection includes too many irrelevant nodes, wasting context tokens. | Medium | Medium | Multi-signal retrieval with configurable hop depth and confidence filter (§5.4). Retrieval precision metric (§9.1) drives tuning. |
| R7 | **Memory poisoning** — incorrect agent-generated annotations corrupt future retrievals. | Low (v1) | High (v2+) | Agent-generated content ranked below code-derived facts (§8.6). Provenance on all data. Invalidation on code change. |
| R8 | **Language-specific quality variance** — TypeScript graph is rich; Python graph is sparse; Go is in between. Users may not understand why. | High | Medium | Per-language support tiers documented and surfaced to the user. Retrieval results annotated with language tier. |
| R9 | **Generated / vendored code noise** — auto-generated files and vendor directories pollute the graph with low-value nodes. | Medium | Medium | Indexing exclusion rules (§2.1 S6). `.gitignore` honored by default. Built-in deny list. User-configurable overrides. |
| R10 | **Concurrent access** — multiple agent sessions or IDE instances writing to the same graph. | Low | Medium | Single-writer architecture with file-level lock. Concurrent reads are safe. Second writer queues behind first. |

---

## 11  Open Questions

These require resolution during implementation, not before.

1. **Embedded graph store selection.** SQLite with adjacency-list tables
   is the conservative choice (zero external dependencies, proven at scale).
   An embedded graph DB (e.g., KùzuDB, DuckDB with graph extensions) may
   offer better traversal performance. Decision deferred to spike.

2. **Context block format.** The serialized subgraph needs a token-efficient
   format for LLM consumption. Options: compressed JSON, custom DSL,
   indented tree. Requires experimentation with actual LLM performance.

3. **Hop-depth tuning.** Default 2-hop call-chain traversal is a hypothesis.
   May need per-language or per-task adjustment. Instrumented A/B testing
   during evaluation phase.

4. **Interaction with existing session store.** The agent runtime already
   maintains a per-session SQLite store (turns, checkpoints, files). The
   memory graph is a separate store. Integration points — e.g., linking
   episodic events to session IDs — need interface design.

5. **Incremental parse strategy.** Tree-sitter provides incremental parsing.
   LSP provides file-level re-analysis. The optimal combination depends on
   language and change granularity.

---

## 12  Key References

| Reference | Relevance |
|---|---|
| Yang et al., "Graph-based Agent Memory: Taxonomy, Techniques, and Applications" (arXiv:2602.05665, 2026) | Foundational taxonomy: extraction → storage → retrieval → evolution lifecycle adopted in §5.2. |
| Jiang et al., "MAGMA: A Multi-Graph based Agentic Memory Architecture" (arXiv:2601.03236, ACL 2026) | Multi-graph pattern with orthogonal structural/temporal/causal/entity views. Directly informs the retrieval strategy (§5.4). |
| Rasmussen et al., "Zep: A Temporal Knowledge Graph Architecture for Agent Memory" (arXiv:2501.13956, 2025) | Temporal freshness model and the Graphiti engine for dynamic knowledge integration. Informs the versioning model (§6). |
| Chhikara et al., "Mem0: Production-Ready AI Agents with Scalable Long-Term Memory" (arXiv:2504.19413, 2025) | Production architecture for extraction/consolidation pipeline. Graph memory enhancement yielding +2% over flat memory. Validates the graph approach for real systems. |
| Qian et al., "HiAgent: Hierarchical Working Memory Management" (ACL 2025) | Hierarchical memory maps naturally to repo→module→file→function structure. Informs the structural graph design. |
| Zhu et al., "G-Memory: Tracing Hierarchical Memory for Multi-Agent Systems" (arXiv:2506.07398, 2025) | Hierarchical memory tracing. Relevant for future multi-agent extension (v3+). |

---

## Appendix A  Phased Delivery Plan

| Phase | Scope | Depends on |
|---|---|---|
| **v1** | Structural graph + call-chain overlay + retrieval API + incremental refresh + versioning | AST / LSP infrastructure |
| **v2** | Semantic annotation layer (LLM summaries) + minimal episodic event log | v1 proven valuable via evaluation (§9) |
| **v3** | Cross-session learning (pattern aggregation from episodic log) + interaction-frequency retrieval signal | v2 |
| **v4** | Multi-repo memory + cross-repo symbol resolution | v3 + identity-resolution design |

Each phase gate requires the success metrics from §9 to be met for the
preceding phase before investment in the next.

---

*End of specification.*
