# Tech Spec: Agent Memory via Graph-Based Repo-Structure & Call-Chain Knowledge

**Story:** `code-intelligence:AGENT-MEMORY`  
**Points:** 10  
**Status:** DRAFT — Iteration 2  

> **Companion documents** (written in parallel — referenced by name, not duplicated):
> - [`architecture.md`](architecture.md) — canonical authority for component boundaries, data model, edge/layer naming, and ID format.
> - [`implementation-plan.md`](implementation-plan.md) — phased work items, deliverables, evaluation harness.
> - [`e2e-scenarios.md`](e2e-scenarios.md) — BDD Gherkin acceptance scenarios.
>
> When this document and `architecture.md` disagree on naming, the architecture
> document governs. This document owns the problem statement, scope/non-goals,
> hard constraints, risks, and success criteria.

---

## 1. Problem Statement

Coding agents today are stateless across sessions: every invocation starts from scratch, re-discovering repository topology, ownership boundaries, call chains, and architectural invariants through brute-force search. This wastes tokens, inflates latency, and — critically — prevents the agent from *learning*. An agent that has fixed three null-dereference bugs in `PaymentService.processRefund()` should know, without re-reading every file, that `processRefund` has fragile null-handling and that its callers in `OrderController` rely on a non-null return.

The goal is a **persistent, graph-structured agent memory** that:

1. **Models the repository top-down** — from Repo → Workspace → Package → Module → Class → Function, capturing containment, dependency, and call-chain edges across four orthogonal graph layers (Structural, Call-Chain, Semantic, Expertise — see `architecture.md` §1.5).
2. **Accumulates expertise** — per-node annotations (bug-density signals, ownership hints, complexity observations) stored as typed Expertise-layer nodes that grow across agent sessions via the Evolution Engine.
3. **Serves as the primary context-construction layer** — given a task description, the memory graph enables subgraph retrieval that is both structurally aware (follow the call chain) and semantically relevant (embed the task, match nodes), replacing or augmenting flat file-grep.

This draws directly from the *Graph-based Agent Memory* taxonomy (Yang et al., arXiv:2602.05665) — specifically its lifecycle of **Extraction → Storage → Retrieval → Evolution** — and adapts it from conversational/game-playing domains to the code-intelligence domain where the primary data sources are ASTs, call graphs, and version-control history.

### 1.1 Existing Infrastructure

The system builds on the existing Code Intelligence stack in `src/ast/` (437+ tests passing):

| Existing project | Reuse strategy |
|---|---|
| **Ast.Core** (`HypergraphNode`, `Hyperedge`, `HyperedgeType` — 68+ enum values) | Extend via `Metadata` dictionary — no breaking changes to base types. |
| **Ast.GraphDb** (`IGraphStore`, `Neo4jGraphStore`) | Add `IMemoryGraphStore` sub-interface with temporal queries; add `SqliteGraphStore` for local mode. |
| **Ast.Embedding** (`IEmbeddingGenerator`, `CodeEmbeddingService`) | Reuse directly for embedding code and expertise nodes. |
| **Ast.Query** (`ICodeQuery`, `HybridQueryService`) | Extend with memory-aware retrieval modes and token-budgeted context assembly. |
| **Ast.VectorDb** (`IVectorStore`, `QdrantVectorStore`) | Reuse for team mode; add in-process HNSW adapter for local mode. |
| **CodeIntelligence** (28 analyzers, `RelationshipGraphBuilder`) | Consume as a source of call-chain and structural edges. |

New code is delivered as `Ast.Memory` (single new project) plus `Ast.Memory.UnitTests` and `Ast.Memory.IntegrationTests`, following the established naming convention under `src/ast/`.

---

## 2. In-Scope

| # | Capability | Detail |
|---|-----------|--------|
| S1 | **Top-down structural graph extraction** | Parse repository into the **Structural layer** (`layer = "structural"`): `Repo → Workspace → Package → Module → Class/Interface → Function/Method`. Edges: `CONTAINS` (mapped from `HyperedgeType.ClassDefinition`, `MethodDefinition`, etc.), `DEPENDS_ON` (from `UsingDirective`, `ModuleImport`, `DotSource`). Reuses existing `Ast.Core` / `Ast.Csharp` / `Ast.PowerShell` parsers as the primary extraction adapter. |
| S2 | **Call-chain overlay** | The **Call-Chain layer** (`layer = "call_chain"`) adds `CALLS` (from `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`), `EXTENDS`, `IMPLEMENTS`, and `REFERENCES` (from `DataFlow`, `Assignment`, `CrossLanguageTypeReference`) edges between symbol nodes. Note: `CALLED_BY` is a reverse traversal of `CALLS`, not a stored edge (see `architecture.md` §1.5 retired names). |
| S3 | **Expertise layer** | The **Expertise layer** (`layer = "expertise"`) stores agent-authored knowledge as typed nodes (`Pattern`, `Pitfall`, `Preference`, `Insight`) linked to code nodes via `APPLIES_TO` edges. Agent sessions write back learnings which are consolidated via `SUPERSEDES` and `DERIVED_FROM` edges. This replaces the per-node property-bag model with structured expertise nodes that have their own embeddings. |
| S4 | **Temporal versioning** | Every node and edge carries a `VersionRange` with commit SHA + monotonic `commit_index` (not ISO-8601 timestamps — see `architecture.md` §1.6 for the dual-axis temporal model). Point-in-time queries use integer range comparison: `WHERE valid_from_idx <= $target AND (valid_until_idx IS NULL OR valid_until_idx > $target)`. Wall-clock timestamps are available via join to the `commits` table. Inspired by Zep temporal KG (Rasmussen et al., arXiv:2501.13956). |
| S5 | **Hybrid retrieval** | Five retrieval modes merged by a Ranker: `STRUCTURAL_DRILL`, `CALL_TRACE`, `SEMANTIC_SEARCH`, `EXPERTISE_RECALL`, `HYBRID`. The **Semantic layer** (`layer = "semantic"`) clusters code via `SIMILAR_TO` and `BELONGS_TO_CLUSTER` edges using pre-computed embeddings. Inspired by MAGMA's orthogonal-graph traversal policy (Jiang et al., arXiv:2601.03236). Edge filtering uses `Metadata["SemanticRelation"]` uniformly across all layers (see `architecture.md` §3.3). |
| S6 | **Memory evolution pipeline** | Post-session consolidation: write-back (create Expertise nodes), consolidation (LLM-powered merge with `SUPERSEDES` edges), decay (reduce confidence of stale annotations based on Ebbinghaus-curve weighting factored by structural centrality), and community summarization (GraphRAG-inspired cluster prose stored as `Topic` nodes in the Semantic layer). |
| S7 | **Context window construction** | Token-budgeted context assembly via the Context Assembler: hierarchy compression (parent summary when all children selected), deduplication (same code span from multiple paths emitted once), and deterministic Markdown output with sections `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`. Budget Controller collapses children into parent summaries when tokens are scarce (HiAgent pattern). |

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

- **Replace the LSP or language server.** The memory graph complements language tooling; it does not re-implement go-to-definition, find-references, or type checking. It *consumes* outputs from those tools (via the optional `Ast.Lsp` enrichment adapter — the pipeline must never block on an LSP).
- **Guarantee 100 % call-graph completeness.** Static analysis of dynamic languages (Python `getattr`, JS `eval`) is inherently incomplete. The graph tolerates missing edges and tags each with a `Confidence` value: direct calls = 1.0; dynamic dispatch / callback patterns = 0.5–0.7.
- **Be a general-purpose knowledge graph.** The schema is purpose-built for code structure. The node types (`Repo`, `Workspace`, `Package`, `Module`, `Class`, `Function`) and four layers (`structural`, `call_chain`, `semantic`, `expertise`) are fixed. Extension is via the `Metadata` dictionary and the Expertise layer, not by adding ad-hoc node or edge types.
- **Enforce deterministic context construction.** Two identical queries against the same graph may produce slightly different context windows due to embedding non-determinism. This is acceptable; the agent's reasoning layer handles variation.
- **Modify the base `HypergraphNode` or `Hyperedge` classes.** The memory layer extends these types via the existing `Metadata` dictionary and via composition wrappers (`MemoryNode`, `MemoryEdge`), never via inheritance or new properties on the base classes. This preserves backward compatibility with the existing 437+ tests (see `architecture.md` §1.7).

---

## 5. Hard Constraints

| ID | Constraint | Source |
|----|-----------|--------|
| HC1 | **Token budget ceiling.** Context windows produced by the Context Assembler must respect a configurable max-token limit (default: 32 K tokens). The Budget Controller truncates gracefully using the structural hierarchy: parent summaries replace child details when budget is tight (HiAgent pattern). | Operational: avoids blowing LLM context limits. |
| HC2 | **Incremental update, not full rebuild.** After a `git pull` or file save, only changed files trigger re-extraction. Full-repo re-parse is an explicit admin action, not the default path. The Incremental Sync component (see `architecture.md` §3.6) diffs changed files, re-extracts, and propagates invalidation to downstream `CALLS` edges. Target: < 2 s for a single-file change on repos ≤ 500 K LOC. | Performance: large repos (100 K+ files) cannot afford full re-index on every change. |
| HC3 | **Language-agnostic core, language-specific extractors.** The graph schema, storage engine, and all four layers are language-neutral. Each supported language provides an extractor adapter behind a unified `ILanguageParser` interface. MVP leverages existing `Ast.Csharp` and `Ast.PowerShell`; TypeScript, Python, and Go adapters are additive and require no schema changes. | Extensibility. |
| HC4 | **Storage must survive agent process restarts.** The graph is persisted to disk. SQLite + FTS5 + sqlite-vss for local/single-machine use. Neo4j + Qdrant for team/CI deployment (via existing `Ast.GraphDb` and `Ast.VectorDb` adapters). Backend selection is via DI configuration; all backends implement `IMemoryGraphStore`. | Durability. |
| HC5 | **No external network calls during retrieval.** Embedding vectors are pre-computed at extraction time (code nodes) and write-back time (expertise nodes). Retrieval is a local graph traversal + vector-similarity operation. The agent must not block on an API call to construct its context. | Latency SLO: < 500 ms p95 for context construction. |
| HC6 | **Deterministic graph schema.** Node types and core edge types are fixed (see `architecture.md` §1.5 for the canonical edge taxonomy mapping to `HyperedgeType` enum values). Extension is via `Metadata` keys on existing types and via the Expertise layer's typed nodes, not by adding ad-hoc node/edge types. A single new enum value `HyperedgeType.MemoryRelation` is added to `Ast.Core` for memory-only edges; the true semantic type lives in `Metadata["SemanticRelation"]`. | Schema stability for downstream consumers. |
| HC7 | **Audit trail.** Every write to the graph (node creation, annotation update, edge addition/removal) is logged in an `audit_log` table with agent-session-id, timestamp, operation type, and detail JSON. This is the post-mortem trail. | Operational: we instrument before we optimise. |
| HC8 | **Non-breaking extension of Ast.Core.** `MemoryNode` and `MemoryEdge` are composition wrappers that delegate to `HypergraphNode` / `Hyperedge` respectively. They provide strongly-typed accessors for memory `Metadata` keys but all inter-component interfaces accept and return the base types. No changes to `HypergraphNode` or `Hyperedge` class signatures. | Backward compatibility: 437+ existing tests must pass unmodified. |
| HC9 | **Hyperedge semantics preserved.** `Hyperedge` is a multi-node hyperedge with `NodeIds: List<string>` and `Roles: Dictionary<string, string>`, not a binary source→target edge. The memory layer preserves this model. For binary relationships (e.g., CALLS), the hyperedge has exactly 2 `NodeIds` with semantic roles (e.g., `"caller"` / `"callee"`). | Data model consistency with Ast.Core. |

---

## 6. Architecture Overview

> **Full architecture is defined in [`architecture.md`](architecture.md).**
> This section provides a summary for quick reference.

```
┌──────────────────────────────────────────────────────────┐
│                     Agent Runtime                        │
│  ┌──────────┐   ┌──────────────┐   ┌──────────────────┐ │
│  │ Task      │──▶│ Context      │──▶│ LLM Reasoning    │ │
│  │ Ingress   │   │ Assembler    │   │ Loop             │ │
│  └──────────┘   └──────┬───────┘   └────────┬─────────┘ │
│                        │ query                │ write-back│
│                        ▼                      ▼          │
│  ┌─────────────────────────────────────────────────────┐ │
│  │        Memory Graph Service (Ast.Memory)            │ │
│  │  ┌───────────┐ ┌────────────┐ ┌──────────────────┐  │ │
│  │  │ Retrieval │ │ Evolution  │ │ Incremental Sync │  │ │
│  │  │ Engine    │ │ Engine     │ │                  │  │ │
│  │  └─────┬─────┘ └─────┬──────┘ └────────┬─────────┘  │ │
│  │        ▼              ▼                 ▼            │ │
│  │  ┌──────────────────────────────────────────────┐   │ │
│  │  │     Graph Store (4 layers)                   │   │ │
│  │  │  Structural · Call-Chain · Semantic · Expert. │   │ │
│  │  │  ┌────────────────┐  ┌────────────────────┐  │   │ │
│  │  │  │ SQLite/Neo4j   │  │ HNSW/Qdrant Vector │  │   │ │
│  │  │  └────────────────┘  └────────────────────┘  │   │ │
│  │  └──────────────────────────────────────────────┘   │ │
│  └─────────────────────────────────────────────────────┘ │
│                        ▲                                 │
│  ┌─────────────────────┴───────────────────────────────┐ │
│  │      Extraction Pipeline (Ast.Core extractors)      │ │
│  │  C# (Ast.Csharp) · PowerShell (Ast.PowerShell)     │ │
│  │  + future: TS · Python · Go (tree-sitter adapters)  │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

Components map to `src/ast/` projects as detailed in `architecture.md` §3.0.

---

## 7. Graph Schema (Core)

> **Canonical definitions for all node types, edge types, and metadata keys
> are in `architecture.md` §1.5 and §4.1.** This section summarizes the
> schema for quick reference; the architecture document governs on conflicts.

### 7.1 Node Types

| Node Type | Layer | Key Properties | Notes |
|-----------|-------|---------------|-------|
| `Repo` | Structural | `root_path`, `vcs_url`, `default_branch` | Singleton root. ID = `"/"`. |
| `Workspace` | Structural | `name`, `path`, `manager` (npm, pip, cargo…) | Monorepo child. Absent in single-package repos. |
| `Package` | Structural | `name`, `version`, `path` | Maps to `.csproj`, `package.json`, `pyproject.toml`, `go.mod`. |
| `Module` | Structural | `file_path`, `language`, `hash` | One node per source file. `hash` = SHA-256 for change detection. |
| `Class` | Structural | `name`, `fqn`, `is_abstract`, `is_exported` | Covers classes, interfaces, structs, enums, records. |
| `Function` | Structural | `name`, `fqn`, `signature`, `line_start`, `line_end`, `is_exported` | Leaf structural node. |
| `SemanticCluster` | Semantic | `centroid_embedding`, `member_count` | Auto-detected community of similar nodes. |
| `Topic` | Semantic | `title`, `summary_prose` | GraphRAG-inspired cluster summary. |
| `Pattern` | Expertise | `title`, `body`, `confidence`, `source_session` | Agent-authored: recurring code pattern. |
| `Pitfall` | Expertise | `title`, `body`, `confidence`, `source_session` | Agent-authored: known failure mode. |
| `Preference` | Expertise | `title`, `body`, `confidence`, `source_session` | Agent-authored: coding convention / style. |
| `Insight` | Expertise | `title`, `body`, `confidence`, `source_session` | Agent-authored: architectural observation. |

**ID format:** Path-based strings: `"{filePath}::{qualifiedName}"` for symbols (e.g., `/src/auth/login::AuthService.validate`), `"{filePath}"` for files, `"/"` for repo root. All IDs are `string` — no GUIDs or integer IDs.

### 7.2 Edge Types

| Canonical Edge | `HyperedgeType` Mapping | Layer | Semantics |
|----------------|------------------------|-------|-----------|
| `CONTAINS` | `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition`, `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | Structural | Parent→child containment. |
| `DEPENDS_ON` | `UsingDirective`, `ModuleImport`, `DotSource` | Structural | Module/package imports. |
| `CALLS` | `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` | Call-Chain | Function invocation. |
| `EXTENDS` | `Extends` | Call-Chain | Class inheritance. |
| `IMPLEMENTS` | `Implements` | Call-Chain | Interface implementation. |
| `REFERENCES` | `DataFlow`, `Assignment`, `CrossLanguageTypeReference` | Call-Chain | Non-call symbol references. |
| `SIMILAR_TO` | `MemoryRelation` (new) | Semantic | Embedding cosine similarity above threshold. |
| `BELONGS_TO_CLUSTER` | `MemoryRelation` (new) | Semantic | Node membership in a detected community. |
| `APPLIES_TO` | `MemoryRelation` (new) | Expertise | Expertise node annotates a code target. |
| `SUPERSEDES` | `MemoryRelation` (new) | Expertise | Merged expertise replaces an older note. |
| `DERIVED_FROM` | `MemoryRelation` (new) | Expertise | Expertise derived from another. |

**Edge filtering:** Every edge stores its canonical name in `Metadata["SemanticRelation"]`. This is the sole filter used by the Retrieval Engine. The legacy `HyperedgeType` enum is preserved in `Metadata["OriginalEdgeType"]` for lossless round-trips but is never used as a retrieval filter.

### 7.3 Annotation Metadata

Annotations live in `HypergraphNode.Metadata` (the existing `Dictionary<string, object?>` on `Ast.Core.HypergraphNode`). Memory-specific keys include:

| Key | Type | Description |
|-----|------|-------------|
| `MemoryLayer` | `string` | `"structural"` \| `"call_chain"` \| `"semantic"` \| `"expertise"` |
| `VersionRange` | `{ Since, SinceIndex, Until?, UntilIndex? }` | Commit SHA + monotonic index. `Until = null` = current. |
| `ContentHash` | `string` | SHA-256 of source span. |
| `LastTouched` | `string` (ISO-8601) | Wall-clock time of last extraction (informational). |
| `Embedding` | `float[]` | Dense vector (dimension = model-dependent). |
| `Summary` | `string` | LLM-generated one-liner for the node. |
| `QualifiedName` | `string` | Fully-qualified name. |
| `Confidence` | `double` | 0.0–1.0 for edges: direct calls = 1.0; dynamic dispatch = 0.5–0.7. |
| `Stale` | `bool` | Pending re-confirmation after upstream signature change. |

### 7.4 Temporal Model

> **Canonical definition in `architecture.md` §1.6.**

The system uses a **dual-axis temporal model**:

1. **Commit axis** — each processed commit SHA maps to a monotonically increasing `commit_index` integer in a `commits` table. `VersionRange` on nodes/edges stores both the commit SHA (human-readable provenance) and `commit_index` (efficient range queries).
2. **Wall-clock axis** — `LastTouched` (ISO-8601) records when extraction last ran. Informational only; range queries use the commit index.

Point-in-time query: `WHERE valid_from_idx <= $target_idx AND (valid_until_idx IS NULL OR valid_until_idx > $target_idx)`.

**Correction from Iteration 1:** The previous tech-spec used ISO-8601 `valid_from` / `valid_until` fields. The canonical model uses integer `commit_index` values because commit SHAs are opaque and not orderable. Wall-clock timestamps are available via join to the `commits` table.

---

## 8. Lifecycle Stages (Mapped to Survey Taxonomy)

### 8.1 Extraction

**Input:** Source files changed since last extraction, identified via `git diff` or file-watcher (Incremental Sync, `architecture.md` §3.6).

**Process:**

1. Language-specific extractor (existing `Ast.Csharp`, `Ast.PowerShell`, or future tree-sitter adapters) parses AST, emits `HypergraphNode` and `Hyperedge` records with `HyperedgeType` values.
2. **Structure Builder** arranges nodes into the Structural layer hierarchy (Repo → Workspace → Package → Module → Class → Function), emitting `CONTAINS` and `DEPENDS_ON` edges.
3. **Call-Chain Analyzer** walks function bodies, emits `CALLS` / `EXTENDS` / `IMPLEMENTS` / `REFERENCES` edges into the Call-Chain layer. Confidence is tagged: direct calls = 1.0; dynamic dispatch / callback patterns = 0.5–0.7.
4. **Semantic Indexer** (reusing `Ast.Embedding.IEmbeddingGenerator`) produces a dense vector per node, stored in `Metadata["Embedding"]`.
5. **Fact Normalizer** assigns stable path-based IDs, attaches `VersionRange` with commit SHA + `commit_index`, computes `ContentHash`, and de-duplicates.
6. Delta is applied to the graph store: new nodes inserted, removed nodes get `VersionRange.Until = current_commit`, modified nodes trigger re-embedding.

**Constraint alignment:** HC2 (incremental), HC3 (pluggable extractors), HC5 (embeddings pre-computed), HC8 (non-breaking extension).

### 8.2 Storage

**Primary backends (via `IMemoryGraphStore` interface):**

| Deployment | Graph | Vector | Notes |
|---|---|---|---|
| Local/CI | SQLite + FTS5 + recursive CTEs | sqlite-vss or in-process HNSW (usearch) | Single-file database, < 500 MB for 100 K-file repos. |
| Team/Cloud | Neo4j (existing `Neo4jGraphStore`) | Qdrant (existing `QdrantVectorStore`) | Native graph traversals, concurrent access. |

**Core tables (SQLite backend):**

- `commits (commit_sha, commit_index, timestamp, parent_sha)` — temporal axis
- `nodes (id, type, fqn, properties_json, embedding_blob, hash, valid_from_idx, valid_until_idx)`
- `edges (id, type, source_ids_json, target_ids_json, roles_json, properties_json, confidence, valid_from_idx, valid_until_idx)`
- `audit_log (id, operation, entity_type, entity_id, session_id, timestamp, detail_json)`

FTS5 virtual table over `nodes.fqn` and `properties_json` for keyword search.

**Constraint alignment:** HC4 (durable), HC6 (fixed schema), HC7 (audit_log table), HC9 (hyperedge semantics in edges table).

### 8.3 Retrieval

Given a task description `T` and optional seed node(s), the Retrieval Engine (`architecture.md` §3.3) executes:

1. **Query Planner** classifies intent → selects retrieval mode(s) and `SemanticRelations` filter.
2. **Vector Searcher** (Semantic layer): embed `T` → ANN query → top-K candidate nodes (K = 50 default).
3. **Graph Traverser**: for each candidate, walk edges matching the selected `SemanticRelations` up to depth D (default 2).
4. **Ranker**: fused score = `α·sim(v_T, v_node) + β·structural_proximity + γ·expertise_signal` where `expertise_signal` draws from confidence, access frequency, and known-issues relevance.
5. **Budget Controller** (HC1): rank by fused score, pack nodes into token budget. Uses hierarchy compression — parent summary replaces child details when budget is tight.
6. **Context Assembler**: output deterministic Markdown with `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise` sections.

This is the MAGMA-inspired multi-layer pattern: orthogonal graph views with policy-guided traversal, adapted for code structures.

### 8.4 Evolution

Runs as a post-session consolidation job (see `architecture.md` §3.5):

| Operation | Trigger | Effect |
|-----------|---------|--------|
| **Write-back** | Agent emits structured annotation (Pattern, Pitfall, Preference, Insight). | Creates Expertise-layer node, links via `APPLIES_TO` edges to code targets. Embeds title + body via `IEmbeddingGenerator`. |
| **Consolidation** | Scheduled (post-PR-merge) or manual. | LLM-powered pass merges overlapping expertise nodes. Produces `SUPERSEDES` / `DERIVED_FROM` edges. |
| **Decay** | On every retrieval. | Expertise nodes whose `APPLIES_TO` targets have been deleted or substantially rewritten get reduced confidence. Below threshold → archived (soft-deleted). Decay factors in structural centrality (PageRank on call graph), not just recency. |
| **Community Summarization** | Structural change exceeding threshold. | GraphRAG-inspired: detect tightly coupled clusters, generate `Topic` nodes with prose summaries in the Semantic layer. |
| **Prune** | Git branch deleted, file removed. | Structural nodes and edges get `VersionRange.Until = current_commit`. Remain queryable for history but excluded from default retrieval. |
| **Re-embed** | Node content changed, or embedding model upgraded. | Re-compute embedding vector; old vector node gets `VersionRange.Until`. Model version tracked per node. |

---

## 9. Identified Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R1 | **Stale graph after refactor** — A large rename or directory restructure invalidates many nodes simultaneously. Incremental Sync may produce dangling edges or orphan nodes. | Medium | High | Incremental Sync (§3.6 in `architecture.md`) propagates invalidation: changed function signatures mark downstream `CALLS` edges as `Stale = true`. For diffs affecting > N files (default 50), trigger a bounded reconcile pass on affected subtrees. Tombstoned nodes remain queryable for history. |
| R2 | **Embedding drift** — If the embedding model is upgraded, old and new vectors are incomparable, breaking similarity retrieval. | Medium | Medium | Track embedding model version in `Metadata["EmbeddingModel"]` per node. On model change, schedule background re-embedding. Retrieval filters to the current model version. Re-embed runs as an Evolution operation (§8.4). |
| R3 | **Call-graph incompleteness in dynamic languages** — Python `getattr`, JS dynamic `import()`, Ruby `send`. Agents may build incorrect mental models from missing edges. | High | Medium | Tag every edge with `Confidence` in `Metadata`. Ranker down-weights low-confidence paths. Extractors for dynamic languages emit heuristic edges (e.g., duck-typed method calls) with `Confidence = 0.5`. Context Assembler includes a `⚠ partial call graph` marker when confidence thresholds are low. |
| R4 | **Expertise noise / contradictions** — Agents may write contradictory annotations across sessions (e.g., one session says "this function is safe", another says "known null-deref"). | Medium | Medium | Expertise nodes carry `source_session` provenance and `Confidence` scores. Evolution's consolidation step (LLM-powered) detects contradictions and produces `SUPERSEDES` edges. Recency + frequency resolve conflicts; contradictions below auto-resolve confidence are flagged via telemetry for human review. |
| R5 | **Storage growth on large monorepos** — A 200 K-file monorepo with temporal versioning could produce multi-GB databases. | Medium | Medium | Temporal archival: nodes/edges with `VersionRange.Until` older than a configurable retention window (default 90 days, no recent access) are moved to a cold archive table. Active graph target: < 500 MB for 100 K files. The `commits` table enables efficient pruning by commit age. |
| R6 | **Latency regression under concurrent access** — Multiple agent instances querying/writing the same SQLite database. | Low | High | SQLite WAL mode for concurrent reads. Write serialization via in-process mutex. For team deployments, the Neo4j + Qdrant path handles concurrency natively. The `IMemoryGraphStore` interface abstracts backend selection. |
| R7 | **Privacy / secret leakage into graph** — Extractors might capture string literals, API keys, or secrets embedded in code as part of node summaries. | Low | Critical | Extractors process structural information only: signatures, docstrings, FQN paths. Full function bodies are never stored in the graph. The `GetEmbeddingText()` / `GetEnhancedEmbeddingText()` methods on `HypergraphNode` already produce structural-only text. A pre-write filter rejects nodes whose summary matches secret-like patterns (regex for API keys, tokens, passwords). |
| R8 | **Over-reliance on graph memory** — Agents may trust stale expertise over fresh file content, producing incorrect fixes. | Medium | High | Context Assembler always includes the *current file content* for the primary task target (read live, not from graph). Graph memory supplements but never replaces reading actual code. A staleness warning is injected into the prompt when expertise `VersionRange.Since` is older than the latest commit touching that file. |
| R9 | **Breaking existing Ast.Core tests** — Adding `HyperedgeType.MemoryRelation` or modifying `HypergraphNode` could break the 437+ existing tests. | Low | High | HC8 mandates composition-over-inheritance. `MemoryNode` / `MemoryEdge` are wrappers. Only one additive enum value (`MemoryRelation`) is added to `HyperedgeType`. No properties added to base classes. `architecture.md` §1.7 specifies the strategy; implementation-plan Phase 1 gates on zero test regression. |
| R10 | **Hyperedge model mismatch** — External consumers may assume binary edges (source→target) while `Hyperedge` uses multi-node `NodeIds` with `Roles`. | Low | Medium | HC9 preserves hyperedge semantics. For binary relationships, exactly 2 `NodeIds` with semantic roles. The Context Assembler renders edges in a familiar `A → B` format for prompt output regardless of internal representation. Documentation in all four plan docs uses the canonical model. |

---

## 10. Success Criteria

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Context relevance** | Retrieval precision ≥ 0.75 (human-judged: ≥ 75 % of retrieved nodes are relevant to the task) | Offline eval on 50 curated task–file pairs using the evaluation harness in `Ast.Memory.UnitTests` (gold sets defined in `implementation-plan.md` Phase 1 Stage 1.4). |
| **Latency** | Context construction p95 < 500 ms on a repo with 50 K nodes (SQLite backend, single machine) | Benchmark harness in `Ast.Benchmark` (extended with memory-retrieval benchmarks). |
| **Incremental update** | Re-extraction after a 10-file commit completes in < 2 s | Benchmark harness. |
| **Agent task accuracy lift** | ≥ 10 % improvement in first-attempt success rate on SWE-Bench-Lite tasks when memory is enabled vs. baseline (no memory) | A/B experiment with matched task sets. This is a stretch goal — may require multiple iteration cycles. |
| **Storage efficiency** | Active graph for a 100 K-file repo fits in < 500 MB (excluding cold archive) | Size measurement after full extraction + 30-day simulated annotation workload. |
| **Test regression** | 0 failures in existing 437+ Ast.Core / Ast.GraphDb / Ast.Embedding / Ast.Query / CodeIntelligence tests after memory layer integration | `dotnet test` on all existing test projects — gated in CI. |
| **Expertise accumulation** | After 10 agent sessions on the same repo, ≥ 5 expertise nodes with confidence > 0.7 are retrievable and relevant | Manual validation on the Forge repo. |

---

## 11. Open Questions

1. **Embedding model selection.** Should we use a code-specific embedding model (e.g., CodeBERT, StarEncoder) or a general-purpose one (e.g., `text-embedding-3-small`)? The existing `Ast.Embedding.IEmbeddingGenerator` abstracts the model; the choice affects retrieval quality. A hybrid approach (code model for code nodes, general model for expertise nodes) adds complexity. **Recommendation:** Start with the model already configured in `Ast.Embedding` and benchmark.

2. **Graph query language exposure.** For the SQLite backend, graph traversals are recursive CTEs. Should we expose a higher-level query DSL (Cypher-like) to the agent, or keep the interface purely programmatic (retrieval modes like `STRUCTURAL_DRILL`, `CALL_TRACE` — see `architecture.md` §3.3)? **Recommendation:** Programmatic modes only in MVP; Cypher-like DSL is future work.

3. **Cross-branch memory.** When the agent works on a feature branch, should Expertise-layer annotations be visible on `main`? Merging annotation branches mirrors the git merge problem. **Initial stance:** Annotations are branch-scoped; merging is deferred. The `commits` table tracks branch lineage.

4. **Annotation schema governance.** Expertise nodes have typed `PatternType` values (`Pattern`, `Pitfall`, `Preference`, `Insight`). Should additional types be extensible, or is this a closed enum? **Recommendation:** Closed enum for MVP; agent can use `Insight` as a catch-all. Extension requires a code change, not config.

5. **Decay curve calibration.** The Ebbinghaus-inspired decay model needs empirical tuning. What half-life is appropriate for code annotations? A function untouched for 90 days may still be critical infrastructure. **Recommendation:** Decay must factor in structural centrality (PageRank on call graph) and access frequency, not just recency. Default half-life = 90 days; tunable via `MemoryStoreOptions`.

6. **`MemoryRelation` enum value.** Adding a single new value to `HyperedgeType` is the least-invasive extension strategy, but it means the enum grows indefinitely over time. Alternative: memory-only edges use `HyperedgeType.DataFlow` (existing catch-all) with semantic type in `Metadata`. **Recommendation:** Single new enum value per `architecture.md` §1.5 — cleaner than overloading `DataFlow`.

---

## 12. Alignment Notes (Cross-Document)

| Issue | This doc (tech-spec) | Architecture doc | Resolution |
|-------|---------------------|------------------|------------|
| Temporal model type | Iteration 1 used ISO-8601 `valid_from`/`valid_until` | Uses `commit_index` integers with SHA+index dual axis | **Resolved in Iteration 2**: tech-spec now uses `commit_index`. |
| Expertise model | Iteration 1 used per-node property bags | Uses typed Expertise-layer nodes (`Pattern`, `Pitfall`, etc.) with edges | **Resolved in Iteration 2**: tech-spec now references typed nodes. |
| `CALLED_BY` edge | Iteration 1 listed as stored edge | Is a reverse traversal of `CALLS`, not stored | **Resolved in Iteration 2**: removed from stored edge list. |
| Context Assembler naming | Iteration 1 called "Context Constructor" | Uses "Context Assembler" | **Resolved in Iteration 2**: aligned to architecture name. |

---

## 13. References

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
| SGMem | "SGMem: Sentence-Graph Memory for Efficient Long-Term Conversational Agents." arXiv:2509.21212, 2025. |
| MemGPT | Packer, C. et al. "MemGPT: Towards LLMs as Operating Systems." arXiv:2310.08560, 2023. |
| Reflexion | Shinn, N. et al. "Reflexion: Language Agents with Verbal Reinforcement Learning." NeurIPS 2023. |

---

*End of tech spec — Iteration 2.*
