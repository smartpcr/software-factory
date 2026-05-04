# Tech Spec: Agent Memory via Graph-Based Repo-Structure & Call-Chain Knowledge

**Story:** `code-intelligence:AGENT-MEMORY`  
**Points:** 10  
**Status:** DRAFT — Iteration 10  

> **Companion documents** (written in parallel — referenced by name, not duplicated):
> - [`architecture.md`](architecture.md) — canonical authority for component boundaries, data model, edge/layer naming, and ID format. Currently at iteration 14.
> - [`implementation-plan.md`](implementation-plan.md) — phased work items, deliverables, evaluation harness.
> - [`e2e-scenarios.md`](e2e-scenarios.md) — BDD Gherkin acceptance scenarios (7 features, 30+ scenarios covering Extraction, Storage, Retrieval, Evolution, End-to-End, and Performance).
>
> When this document and `architecture.md` disagree on naming, the architecture
> document governs. This document owns the problem statement, scope/non-goals,
> hard constraints, risks, and success criteria.

---

## 1. Problem Statement

Coding agents today are stateless across sessions: every invocation starts from scratch, re-discovering repository topology, ownership boundaries, call chains, and architectural invariants through brute-force search. This wastes tokens, inflates latency, and — critically — prevents the agent from *learning*. An agent that has fixed three null-dereference bugs in `PaymentService.processRefund()` should know, without re-reading every file, that `processRefund` has fragile null-handling and that its callers in `OrderController` rely on a non-null return.

The goal is a **persistent, graph-structured agent memory** that:

1. **Models the repository top-down** — from Repo → Workspace → Package → Module → Class → Function, capturing containment, dependency, and call-chain edges across four orthogonal graph layers (Structural, Call-Chain, Semantic, Expertise — see `architecture.md` §2.1).
2. **Accumulates expertise** — per-node annotations (bug-density signals, ownership hints, complexity observations) stored as typed Expertise-layer nodes that grow across agent sessions via the Evolution Engine.
3. **Serves as the primary context-construction layer** — given a task description, the memory graph enables subgraph retrieval that is both structurally aware (follow the call chain) and semantically relevant (embed the task, match nodes), replacing or augmenting flat file-grep.

This draws directly from the *Graph-based Agent Memory* taxonomy (Yang et al., arXiv:2602.05665) — specifically its lifecycle of **Extraction → Storage → Retrieval → Evolution** — and adapts it from conversational/game-playing domains to the code-intelligence domain where the primary data sources are ASTs, call graphs, and version-control history.

### 1.1 Existing Infrastructure

The system builds on the existing Code Intelligence stack in `src/ast/` (24 `.csproj` projects, 13 test projects, 970+ tests). **Two existing projects receive additive-only changes** (per `architecture.md` §3.0):

1. **`Ast.Core`** — one new enum value `HyperedgeType.MemoryRelation` in `HypergraphModels.cs`.
2. **`Ast.Cli`** — additive `config.AddBranch("memory", ...)` lines in `Program.cs` to register memory sub-commands (Spectre.Console.Cli manual registration — consistent with how the existing 7 commands are registered). MCP tool registration and handler logic live in `Ast.Memory/Mcp/`.

All other existing projects are referenced (as project dependencies) but never modified:

| Existing project | Reuse strategy |
|---|---|
| **Ast.Core** (`HypergraphNode`, `Hyperedge`, `HyperedgeType` — 44 enum values) | Reference types via `Metadata` dict for extension. One additive enum value `MemoryRelation`. No changes to `HypergraphNode` or `Hyperedge` class signatures. |
| **Ast.GraphDb** (`IGraphStore`, `Neo4jGraphStore`) | Reference only — no modifications. `Ast.Memory` defines `IMemoryGraphStore` (extending `IGraphStore`) and `SqliteGraphStore` / `Neo4jMemoryGraphStore` in its own project. |
| **Ast.Embedding** (`IEmbeddingGenerator`, `CodeEmbeddingService`, `EnrichedEmbeddingPipeline`) | Reuse directly for embedding code and expertise nodes. `CodeEmbeddingService.GenerateNodeEmbeddingsAsync()` batches via `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()`. `EnrichedEmbeddingPipeline` chains optional LSP enrichment with embedding generation. Existing `EmbeddingGeneratorOptions.ModelName` defaults to `"nomic-embed-text"` (general-purpose, consistent with D1). |
| **Ast.Query** (`ICodeQuery`, `HybridQueryService`) | Reference only — no modifications. `Ast.Memory` defines its own `IMemoryRetrievalEngine` that internally composes `ICodeQuery` when needed. |
| **Ast.VectorDb** (`IVectorStore`, `QdrantVectorStore`) | Reference only — no modifications. Reuse for team mode; `Ast.Memory` adds in-process HNSW adapter for local mode. |
| **Ast.Lsp** (LSP integration) | Optional enrichment — pipeline never blocks on LSP. No modifications. |
| **CodeIntelligence** (27 analyzers, 4 detectors, `RelationshipGraphBuilder`) | Consume as call-chain / structural edge source. No modifications. |

New code is delivered as `Ast.Memory` (single new project) plus `Ast.Memory.UnitTests` and `Ast.Memory.IntegrationTests`, following the established naming convention under `src/ast/`.

---

## 2. In-Scope

| # | Capability | Detail |
|---|-----------|--------|
| S1 | **Top-down structural graph extraction** | Parse repository into the **Structural layer** (`layer = "structural"`): `Repo → Workspace → Package → Module → Class/Interface → Function/Method`. Edges: `CONTAINS` (mapped from `HyperedgeType.ClassDefinition`, `MethodDefinition`, etc.), `DEPENDS_ON` (from `UsingDirective`, `ModuleImport`, `DotSource`). Reuses existing `Ast.Core` / `Ast.Csharp` / `Ast.PowerShell` parsers as the primary extraction adapter. |
| S2 | **Call-chain overlay** | The **Call-Chain layer** (`layer = "call_chain"`) adds `CALLS` (from `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`), `EXTENDS`, `IMPLEMENTS`, and `REFERENCES` (from `DataFlow`, `Assignment`, `CrossLanguageTypeReference`) edges between symbol nodes. Note: `CALLED_BY` is a reverse traversal of `CALLS`, not a stored edge (see `architecture.md` §2.3 retired names). |
| S3 | **Expertise layer** | The **Expertise layer** (`layer = "expertise"`) stores agent-authored knowledge as typed nodes (`Pattern`, `Pitfall`, `Preference`, `Insight`) linked to code nodes via `APPLIES_TO` edges. Agent sessions write back learnings which are consolidated via `SUPERSEDES` and `DERIVED_FROM` edges. This replaces the per-node property-bag model with structured expertise nodes that have their own embeddings. |
| S4 | **Temporal versioning** | Every node and edge carries a `VersionRange` with commit SHA + monotonic `commit_index` (not ISO-8601 timestamps — see `architecture.md` §4.3 for the dual-axis temporal model). Point-in-time queries use integer range comparison: `WHERE valid_from_idx <= $target AND (valid_until_idx IS NULL OR valid_until_idx > $target)`. Wall-clock timestamps are available via join to the `commits` table. Inspired by Zep temporal KG (Rasmussen et al., arXiv:2501.13956). |
| S5 | **Hybrid retrieval** | Five retrieval modes merged by a Ranker: `STRUCTURAL_DRILL`, `CALL_TRACE`, `SEMANTIC_SEARCH`, `EXPERTISE_RECALL`, `HYBRID`. The **Semantic layer** (`layer = "semantic"`) clusters code via `SIMILAR_TO` and `BELONGS_TO_CLUSTER` edges using pre-computed embeddings (general-purpose model — see D1). Embeddings are generated via the existing `Ast.Embedding.IEmbeddingGenerator` interface; the `CodeEmbeddingService.GenerateNodeEmbeddingsAsync()` method calls `HypergraphNode.GetEmbeddingText()` (lines 93–113, structural-only: node type, language, file path, scope, modifiers) or `GetEnhancedEmbeddingText()` when LSP enrichment is available (lines 120–186; includes signatures, docstrings, caller/callee context, and optionally up to 2000 chars of source code from `Metadata["SourceCode"]` — see `HypergraphModels.cs`). Inspired by MAGMA's orthogonal-graph traversal policy (Jiang et al., arXiv:2601.03236). Edge filtering uses `Metadata["SemanticRelation"]` uniformly across all layers (see `architecture.md` §3.3). Scoring uses a 5-signal weighted model: structural proximity (0.30), semantic similarity (0.25), centrality (0.20), recency (0.15), expertise signal (0.10) — canonical weights defined in `architecture.md` §3.3. At query time, the query text is embedded via a single local call to the embedding service (see HC5); all node embeddings are pre-computed at extraction/write-back time. If the embedding service is unavailable at query time, semantic search is gracefully skipped. |
| S6 | **Memory evolution pipeline** | Post-session consolidation: write-back (create Expertise nodes), consolidation (LLM-powered merge with `SUPERSEDES` edges), decay (3-year half-life with structural centrality boost — see D5; plus one-time `StaleDecayFactor = 0.5` when target `ContentHash` changes), and community summarization (GraphRAG-inspired cluster prose stored as `Topic` nodes in the Semantic layer). |
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
- **Modify the base `HypergraphNode` or `Hyperedge` classes.** The memory layer extends these types via the existing `Metadata` dictionary and via composition wrappers (`MemoryNode`, `MemoryEdge`), never via inheritance or new properties on the base classes. This preserves backward compatibility with the existing 970+ tests (see `architecture.md` §4.1).

---

## 5. Hard Constraints

| ID | Constraint | Source |
|----|-----------|--------|
| HC1 | **Token budget ceiling.** Context windows produced by the Context Assembler must respect a configurable max-token limit (system ceiling: 32 K tokens; per-query default: 8 K tokens). Agents may request up to the ceiling; the Budget Controller never exceeds it. The Budget Controller truncates gracefully using the structural hierarchy: parent summaries replace child details when budget is tight (HiAgent pattern). | Operational: avoids blowing LLM context limits. Canonical scoring weights are in `architecture.md` §3.3. |
| HC2 | **Incremental update, not full rebuild.** After a `git pull` or file save, only changed files trigger re-extraction. Full-repo re-parse is an explicit admin action, not the default path. The Incremental Sync component (see `architecture.md` §3.6) diffs changed files, re-extracts, and propagates invalidation to downstream `CALLS` edges. Target: < 2 s for a single-file change on repos ≤ 500 K LOC. | Performance: large repos (100 K+ files) cannot afford full re-index on every change. |
| HC3 | **Language-agnostic core, language-specific extractors.** The graph schema, storage engine, and all four layers are language-neutral. Each supported language provides an extractor adapter behind a unified `ILanguageParser` interface. MVP leverages existing `Ast.Csharp` and `Ast.PowerShell`; TypeScript, Python, and Go adapters are additive and require no schema changes. | Extensibility. |
| HC4 | **Storage must survive agent process restarts.** The graph is persisted to disk. SQLite + FTS5 + sqlite-vss for local/single-machine use. Neo4j + Qdrant for team/CI deployment (via existing `Ast.GraphDb` and `Ast.VectorDb` adapters). Backend selection is via DI configuration; all backends implement `IMemoryGraphStore`. | Durability. |
| HC5 | **No cloud / internet calls during retrieval.** Code-node and expertise-node embeddings are pre-computed at extraction and write-back time using the local Ollama embedding service (`http://localhost:11434`, model `nomic-embed-text` — see D1). At retrieval time, the system makes exactly **one local HTTP call** to the same Ollama instance to embed the query text; all subsequent operations — graph traversal, vector-similarity ranking, context assembly — use only pre-computed vectors and local storage. The constraint means: the retrieval path **never contacts a cloud API, remote service, or the internet**. The local Ollama call is permitted because it runs on the same machine with sub-10 ms latency and zero availability dependency on external infrastructure. If Ollama is unavailable, retrieval degrades gracefully: semantic search is skipped and the system falls back to structural + call-chain + expertise retrieval modes only. | Latency SLO: < 500 ms p95 for context construction. |
| HC6 | **Deterministic graph schema.** Node types and core edge types are fixed (see `architecture.md` §2.2 for the canonical edge taxonomy mapping to `HyperedgeType` enum values). Extension is via `Metadata` keys on existing types and via the Expertise layer's typed nodes, not by adding ad-hoc node/edge types. A single new enum value `HyperedgeType.MemoryRelation` is added to `Ast.Core` for memory-only edges; the true semantic type lives in `Metadata["SemanticRelation"]`. | Schema stability for downstream consumers. |
| HC7 | **Audit trail.** Every write to the graph (node creation, annotation update, edge addition/removal) is logged in an `audit_log` table with agent-session-id, timestamp, operation type, and detail JSON. This is the post-mortem trail. | Operational: we instrument before we optimise. |
| HC8 | **Non-breaking extension of Ast.Core.** `MemoryNode` and `MemoryEdge` are composition wrappers that delegate to `HypergraphNode` / `Hyperedge` respectively (i.e., `MemoryNode` contains a `HypergraphNode Inner { get; }` property — **not** class inheritance via `: HypergraphNode`). They provide strongly-typed accessors for memory `Metadata` keys but all inter-component interfaces accept and return the base types. No changes to `HypergraphNode` or `Hyperedge` class signatures. | Backward compatibility: 970+ existing tests must pass unmodified. |
| HC9 | **Hyperedge semantics preserved.** `Hyperedge` is a multi-node hyperedge with `NodeIds: List<string>` and `Roles: Dictionary<string, string>`, not a binary source→target edge. The memory layer preserves this model. For binary relationships (e.g., CALLS), the hyperedge has exactly 2 `NodeIds` with semantic roles (e.g., `"caller"` / `"callee"`). | Data model consistency with Ast.Core. |
| HC10 | **Cross-branch expertise visibility.** Expertise-layer nodes are globally visible regardless of the current git branch (see D3). Retrieval does not filter by branch lineage. Staleness is detected via `ContentHash` comparison against the current branch's version of the target code. | Knowledge reuse: insights from feature branches inform work on `main` immediately. |
| HC11 | **Curated annotation key registry.** `Metadata` keys on memory nodes are governed by `AnnotationKeyRegistry` — a code-defined registry of well-known keys with typed schemas (see D4, §12.4). Agent-authored extension keys use the `ext:` prefix and surface a validation warning. | Schema stability: prevents ad-hoc key proliferation while preserving extensibility. |

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
> are in `architecture.md` §2.2 and §4.2.** This section summarizes the
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

Annotations live in `HypergraphNode.Metadata` (the existing `Dictionary<string, object?>` on `Ast.Core.HypergraphNode`). Keys are governed by the **curated `AnnotationKeyRegistry`** (see D4, §12.4). Well-known memory-specific keys:

| Key | Type | Governance | Description |
|-----|------|-----------|-------------|
| `MemoryLayer` | `string` | **Registry** | `"structural"` \| `"call_chain"` \| `"semantic"` \| `"expertise"` |
| `VersionRange` | `{ Since, SinceIndex, Until?, UntilIndex? }` | **Registry** | Commit SHA + monotonic index. `Until = null` = current. |
| `ContentHash` | `string` | **Registry** | SHA-256 of source span. |
| `LastTouched` | `string` (ISO-8601) | **Registry** | Wall-clock time of last extraction (informational). |
| `Embedding` | `float[]` | **Registry** | Dense vector (general-purpose model — see D1). |
| `EmbeddingModel` | `string` | **Registry** | Model identifier (e.g., `text-embedding-3-small`). Enables re-embedding on model upgrade. |
| `Summary` | `string` | **Registry** | LLM-generated one-liner for the node. |
| `QualifiedName` | `string` | **Registry** | Fully-qualified name. |
| `Confidence` | `double` | **Registry** | 0.0–1.0 for edges: direct calls = 1.0; dynamic dispatch = 0.5–0.7. |
| `Stale` | `bool` | **Registry** | Pending re-confirmation after upstream signature change. |
| `SemanticRelation` | `string` | **Registry** | Canonical edge name (e.g., `CALLS`, `APPLIES_TO`). Primary retrieval filter. |
| `OriginalEdgeType` | `string` | **Registry** | Original `HyperedgeType` enum value. Preserved for lossless round-trip. |
| `ext:*` | `object?` | **Extension** | Agent-authored ad-hoc keys. Prefixed `ext:` to avoid collisions. Surfaced with validation warning. |

### 7.4 Temporal Model

> **Canonical definition in `architecture.md` §4.3.**

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
4. **Semantic Indexer** (reusing `Ast.Embedding.IEmbeddingGenerator`) produces a dense vector per node, stored in `Metadata["Embedding"]`. Embedding input text is produced by `HypergraphNode.GetEmbeddingText()` (structural-only: node type, language, file path, scope, modifiers) or `GetEnhancedEmbeddingText()` when LSP enrichment is enabled (adds signature, docstring, return type, parameters, caller/callee context, and optionally `Metadata["SourceCode"]` truncated to 2000 chars — see `HypergraphModels.cs:120–186`). The `CodeEmbeddingService.GenerateNodeEmbeddingsAsync()` method batches embedding generation via `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()`.
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
- `edges (id, type, node_ids_json, roles_json, properties_json, confidence, valid_from_idx, valid_until_idx)`
- `audit_log (id, operation, entity_type, entity_id, session_id, timestamp, detail_json)`

FTS5 virtual table over `nodes.fqn` and `properties_json` for keyword search.

**Constraint alignment:** HC4 (durable), HC6 (fixed schema), HC7 (audit_log table), HC9 (hyperedge semantics in edges table).

### 8.3 Retrieval

Given a task description `T` and optional seed node(s), the Retrieval Engine (`architecture.md` §3.3) executes:

1. **Query Planner** classifies intent → selects retrieval mode(s) and `SemanticRelations` filter.
2. **Query Embedding** (the sole HTTP call in the retrieval path): embed `T` via the local Ollama service (`http://localhost:11434`, model `nomic-embed-text`). If Ollama is unavailable, this step is skipped and semantic search is disabled — the remaining retrieval modes (structural, call-chain, expertise) proceed without it.
3. **Vector Searcher** (Semantic layer): ANN query with the query embedding → top-K candidate nodes (K = 50 default). Uses pre-computed node embeddings stored in `Metadata["Embedding"]`.
4. **Graph Traverser**: for each candidate, walk edges matching the selected `SemanticRelations` up to depth D (default 2).
5. **Ranker**: fused score = `α·sim(v_T, v_node) + β·structural_proximity + γ·expertise_signal` where `expertise_signal` draws from confidence, access frequency, and known-issues relevance.
6. **Budget Controller** (HC1): rank by fused score, pack nodes into token budget. Uses hierarchy compression — parent summary replaces child details when budget is tight.
7. **Context Assembler**: output deterministic Markdown with `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise` sections.

This is the MAGMA-inspired multi-layer pattern: orthogonal graph views with policy-guided traversal, adapted for code structures.

### 8.4 Evolution

Runs as a post-session consolidation job (see `architecture.md` §3.5):

| Operation | Trigger | Effect |
|-----------|---------|--------|
| **Write-back** | Agent emits structured annotation (Pattern, Pitfall, Preference, Insight). | Creates Expertise-layer node, links via `APPLIES_TO` edges to code targets. Embeds title + body via `IEmbeddingGenerator`. |
| **Consolidation** | Scheduled (post-PR-merge) or manual. | LLM-powered pass merges overlapping expertise nodes. Produces `SUPERSEDES` / `DERIVED_FROM` edges. |
| **Decay** | On every retrieval. | Expertise nodes whose `APPLIES_TO` targets have been deleted or substantially rewritten get reduced confidence. Below threshold → archived (soft-deleted). **Two decay mechanisms:** (1) **Time-based:** Ebbinghaus-inspired with 3-year half-life (1095 days), factored by structural centrality — high-centrality nodes (PageRank) decay ~50 % slower. Formula: `confidence × 2^(−age_days / 1095) × (1.0 + 0.5 × normalized_pagerank)`. (2) **Event-based:** one-time `StaleDecayFactor = 0.5` multiplicative penalty when target `ContentHash` changes; `MinorDecayFactor = 0.9` for whitespace/comment-only changes (per `architecture.md` §3.5). Configurable via `MemoryStoreOptions.DecayHalfLifeDays`. |
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
| R7 | **Privacy / secret leakage into graph** — Extractors might capture string literals, API keys, or secrets embedded in code as part of node summaries or embedding text. | Low | Critical | **Actual source-code surface area:** `HypergraphNode.GetEmbeddingText()` (lines 93–113) produces structural-only text (node type, language, file path, scope, modifiers). However, `GetEnhancedEmbeddingText()` (lines 120–186, used when LSP enrichment is enabled) includes `Metadata["SourceCode"]` if present — up to 2000 characters of source code (see `HypergraphModels.cs` line 175). This means function bodies *can* appear in embedding input text and potentially in stored `Metadata`. **Mitigations:** (1) The Extraction Pipeline controls what goes into `Metadata["SourceCode"]` — extractors should omit string literals and only store signatures + docstrings when the privacy-safe mode is enabled. (2) A pre-write filter rejects nodes whose summary or `SourceCode` metadata matches secret-like patterns (regex for API keys, tokens, connection strings, passwords). (3) The `Embedding` vector itself is a dense float array and cannot be reverse-engineered into source text, so stored vectors are safe even if the input text contained secrets. (4) For high-security deployments, disable `SourceCode` metadata population entirely via `ExtractionOptions`. |
| R8 | **Over-reliance on graph memory** — Agents may trust stale expertise over fresh file content, producing incorrect fixes. | Medium | High | Context Assembler always includes the *current file content* for the primary task target (read live, not from graph). Graph memory supplements but never replaces reading actual code. A staleness warning is injected into the prompt when expertise `VersionRange.Since` is older than the latest commit touching that file. |
| R9 | **Breaking existing Ast.Core tests** — Adding `HyperedgeType.MemoryRelation` or modifying `HypergraphNode` could break the 970+ existing tests. | Low | High | HC8 mandates composition-over-inheritance. `MemoryNode` / `MemoryEdge` are wrappers (composition, not inheritance — see `architecture.md` §4.1). Only one additive enum value (`MemoryRelation`) is added to `HyperedgeType` (which currently has 44 values). No properties added to base classes. Implementation-plan Phase 1 gates on zero test regression. |
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
| **Test regression** | 0 failures in existing 970+ Ast.* and CodeIntelligence tests (across 13 test projects) after memory layer integration | `dotnet test` on all existing test projects — gated in CI. |
| **Expertise accumulation** | After 10 agent sessions on the same repo, ≥ 5 expertise nodes with confidence > 0.7 are retrievable and relevant | Manual validation on the Forge repo. |

---

## 11. Resolved Decisions (from Operator)

The following open questions from Iteration 1 have been resolved by operator input. Each decision is final and governs the implementation.

| # | Question | Decision | Rationale / Impact |
|---|----------|----------|--------------------|
| D1 | **Embedding model selection** — code-specific (CodeBERT, StarEncoder) vs. general-purpose (e.g., `text-embedding-3-small`)? | **General-purpose model.** Use a general-purpose text-embedding model for all nodes — code, expertise, and topics alike. | Simplifies the pipeline (single model, single vector dimension), avoids the complexity of a hybrid approach, and handles natural-language task descriptions and expertise prose fluently alongside code symbols. The `IEmbeddingGenerator` abstraction in `Ast.Embedding` makes model swapping a config change if retrieval quality needs improvement later. The existing `Ast.Core.EmbeddingConfiguration` defaults to `"nomic-embed-text"` (768 dims, served via Ollama at `localhost:11434`); `architecture.md` §4.7 and §5.1 also default to `"nomic-embed-text"` — all consistent with this decision. The specific model is a deployment-time choice; the memory layer is model-agnostic and tracks the model used per-node in `Metadata["EmbeddingModel"]`. |
| D2 | **Graph query language** — Cypher-like DSL vs. purely programmatic API? | **Purely programmatic.** The agent interacts via typed function calls: `get_callers(fqn, depth)`, `get_callees(fqn, depth)`, `find_related(query, top_k)`, `get_expertise(fqn)`, `record_expertise(...)`. No query DSL. | Keeps the MCP tool surface minimal and predictable. Graph traversals use recursive CTEs internally (SQLite) or Cypher internally (Neo4j), but this is an implementation detail hidden behind `IMemoryGraphStore`. Aligns with `architecture.md` §3.3 retrieval modes (`STRUCTURAL_DRILL`, `CALL_TRACE`, `SEMANTIC_SEARCH`, `EXPERTISE_RECALL`, `HYBRID`) which are programmatic, not query-string-based. |
| D3 | **Cross-branch memory** — annotations branch-scoped or visible across branches? | **Annotations are visible cross-branch.** Expertise-layer nodes written on any branch are globally queryable, regardless of which branch the agent is currently on. | This maximizes knowledge reuse — an insight discovered while working on a feature branch should inform work on `main` and other branches immediately. The `commits` table still tracks which commit introduced an annotation (provenance), but retrieval does NOT filter by branch lineage. Staleness detection uses `ContentHash` comparison against the current branch's version of the target code, so an annotation whose target has diverged on the current branch is flagged as potentially stale. |
| D4 | **Annotation schema governance** — free-form property bag vs. curated registry? | **Curated well-known tag registry** with an `extra` bag for ad-hoc data. | A `AnnotationKeyRegistry` defines the set of well-known `Metadata` keys with typed schemas (e.g., `MemoryLayer: string`, `Confidence: double`, `ContentHash: string` — see §7.3 for the full list). Each key entry specifies: name, CLR type, allowed values (if enum), and a short description. Agent-authored keys that are not in the registry are stored under a namespaced prefix (`ext:`) in `Metadata` and surfaced with a validation warning. This prevents fragmentation while preserving flexibility. The registry is defined in code (`AnnotationKeyRegistry.cs` in `Ast.Memory/Schema/`) and versioned with the project — not runtime-configurable. |
| D5 | **Decay curve calibration** — what half-life for expertise annotations? | **3-year half-life**, factored by structural centrality (PageRank on call graph). | A 3-year half-life reflects the reality that code annotations remain relevant far longer than conversation memory. Even untouched functions may be critical infrastructure. The decay formula: `decayed_confidence = initial_confidence × 2^(−age_days / (3 × 365)) × centrality_boost`, where `centrality_boost = 1.0 + 0.5 × normalized_pagerank` (range 1.0–1.5). This means a highly central function's annotations decay ~50 % slower than a leaf function's. The `StaleDecayFactor` (0.5, architecture.md §3.5) applies as a one-time multiplicative penalty when a target's `ContentHash` changes — this is separate from the time-based decay curve. Configurable via `MemoryStoreOptions.DecayHalfLifeDays` (default: 1095). |
| D6 | **`MemoryRelation` enum value** — single new enum vs. overloading `DataFlow`? | **Single new `HyperedgeType.MemoryRelation` enum value.** | Per `architecture.md` §2.2 — cleaner than overloading `DataFlow`. The true semantic type lives in `Metadata["SemanticRelation"]`. This is the only additive change to `Ast.Core`'s `HyperedgeType` enum. |

---

## 12. Alignment Notes (Cross-Document)

### 12.1 Resolved Across All Documents

| Issue | Prior state | Resolution |
|-------|------------|------------|
| Temporal model type | Tech-spec Iter 1 used ISO-8601 `valid_from`/`valid_until` | All docs now use `commit_index` integers with SHA+index dual axis (architecture.md §4.3). |
| Expertise model | Tech-spec Iter 1 used per-node property bags | All docs now reference typed Expertise-layer nodes (`Pattern`, `Pitfall`, etc.) with edges. |
| `CALLED_BY` edge | Tech-spec Iter 1 listed as stored edge | Is a reverse traversal of `CALLS`, not stored (architecture.md §2.3 retired names). |
| Context Assembler naming | Tech-spec Iter 1 called "Context Constructor" | All docs now use "Context Assembler". |
| Annotation Writer component | Tech-spec Iter 1 diagram showed separate "Annotation Writer" | Write-back is a path inside `IEvolutionEngine.RecordExpertiseAsync` (architecture.md §3.5). |
| Token budget default | Tech-spec HC1 said "default: 32 K tokens" | Clarified: 32 K is the system ceiling; per-query default is 8 K (architecture.md §3.3). |
| Scoring formula | Tech-spec S5 listed retrieval modes without weights | S5 now references architecture.md §3.3 for canonical scoring: structural 0.30, semantic 0.25, centrality 0.20, recency 0.15, expertise 0.10. |
| HyperedgeType count | Tech-spec Iter 1–2 claimed "68+ enum values" | **Corrected in Iter 3**: actual count is **44 enum values** (verified from `Ast.Core/HypergraphModels.cs` lines 12–69). |
| Composition vs. inheritance | Tech-spec Iter 1–2 did not explicitly state composition model | **Clarified in Iter 3**: HC8 now explicitly states `MemoryNode` uses a `HypergraphNode Inner { get; }` property (composition), NOT `: HypergraphNode` inheritance. Aligns with architecture.md §4.1. |
| `GetEnhancedEmbeddingText()` source-code inclusion | Tech-spec Iter 3 R7 claimed both embedding methods produce "structural-only text" | **Corrected in Iter 4**: `GetEmbeddingText()` is structural-only, but `GetEnhancedEmbeddingText()` includes `Metadata["SourceCode"]` (up to 2000 chars). R7 updated with accurate mitigations. |
| Architecture.md embedding model alignment | Tech-spec Iter 3 §12.2 still listed architecture.md §4.4 as pending | **Resolved in Iter 4, fully aligned in Iter 8**: architecture.md §4.7, §5.1 now default to `"nomic-embed-text"` — matching both codebase defaults and this spec's D1 decision. All sources consistent. |
| Source-code default model grounding | Tech-spec did not reference actual `EmbeddingConfiguration` defaults | **Added in Iter 4, updated in Iter 8**: `Ast.Core.EmbeddingConfiguration.ModelName` (in `AstConfiguration.cs:96`) defaults to `"nomic-embed-text"` (768 dims); `Ast.Embedding.EmbeddingGeneratorOptions.ModelName` (in `EmbeddingModels.cs:119`) also defaults to `"nomic-embed-text"`. Architecture.md §4.7 and §5.1 now also default to `"nomic-embed-text"` (corrected in architecture.md iteration 12). All three sources consistent. |
| Architecture.md R-8 SGMem note | Tech-spec Iter 4 flagged architecture.md R-8 as using "code-specific" phrasing | **Resolved in Iter 5**: The R-8/SGMem entry has been removed from architecture.md (as of its Iteration 12 the research table lists R-1 through R-7 only). No conflict remains. |

### 12.2 Alignment with `architecture.md`

All previously-pending conflicts between this document and `architecture.md` are now **resolved**:

| Issue | Prior state | Resolution |
|-------|------------|------------|
| Embedding model default | Tech-spec D1 mandated general-purpose; architecture.md previously defaulted to `code-embedding-v2` | ✅ **Resolved.** Architecture.md §4.7 now defaults to `"nomic-embed-text"` — matching the actual codebase defaults in `EmbeddingGeneratorOptions.ModelName` and `EmbeddingConfiguration.ModelName`. All docs consistent. |
| Cross-branch visibility | Tech-spec D3 mandated cross-branch; architecture.md §3.3 did not cover it | ✅ **Resolved.** Architecture.md §3.3 now includes cross-branch expertise visibility subsection. |
| Time-based decay formula | Tech-spec D5 mandated 3-year half-life; architecture.md §3.5 did not specify | ✅ **Resolved.** Architecture.md §3.5 now includes full dual decay mechanism with `DecayHalfLifeDays = 1095`. |
| SGMem research note wording | Architecture.md R-8 previously said "code-specific embedding models" | ✅ **Resolved.** The SGMem/R-8 entry has been removed from architecture.md. No conflict remains. |
| ExtractionOptions.EmbeddingModel default | Architecture.md §5.1 previously defaulted to `"text-embedding-3-small"` | ✅ **Resolved in architecture.md Iteration 12.** Architecture.md §4.7 and §5.1 now default to `"nomic-embed-text"`, matching the existing codebase defaults in `EmbeddingGeneratorOptions.ModelName` (`src/ast/Ast.Embedding/EmbeddingModels.cs:119`) and `EmbeddingConfiguration.ModelName` (`src/ast/Ast.Core/AstConfiguration.cs:96`). |

**No remaining conflicts** between this document and `architecture.md`.

**Source-code default model note:** The existing `Ast.Core.EmbeddingConfiguration.ModelName` (in `AstConfiguration.cs:96`) defaults to `"nomic-embed-text"` (768 dimensions, via Ollama). The `Ast.Embedding.EmbeddingGeneratorOptions.ModelName` (in `EmbeddingModels.cs:119`) also defaults to `"nomic-embed-text"`. Architecture.md §4.7 and §5.1 (`ExtractionOptions.EmbeddingModel`) also default to `"nomic-embed-text"` — all three are now consistent. The `IEmbeddingGenerator` abstraction means the specific model is a deployment-time configuration choice; the memory layer is model-agnostic and only requires that all nodes use the **same** model within a graph instance (tracked per-node via `Metadata["EmbeddingModel"]`).

### 12.3 Alignment with `implementation-plan.md`

The implementation-plan contains 25 naming, data-model, numeric, and project-mutation divergences from canonical vocabulary defined in `architecture.md` (across 8 categories in architecture.md §10.1.1 through §10.1.8). **These are not unresolved conflicts** — each has a defined canonical resolution, and `architecture.md` governs on all points (see this document's header).

> **Implementer directive:** When the implementation-plan's literal text (class names, interface names, edge names, scoring weights, ID formats, defaults) conflicts with `architecture.md`, **follow `architecture.md`**. The implementation-plan describes *work items and phasing*; the canonical vocabulary and values come from `architecture.md`. The conflicts listed below are documentation debt in the implementation-plan that will be corrected in its next iteration. They do not affect this tech-spec's correctness or completeness.

The 25 items are catalogued in `architecture.md` §10.1 (the single source of truth for cross-document conflicts). This tech-spec mirrors them below in summarized form for implementer awareness. Each item's canonical value is stated inline; the architecture.md section number provides the governing reference.

#### 12.3.1 Naming & Type Conflicts (4 items — architecture.md §10.1.1)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| N-1 | `ExpertiseNote` (Stage 1.1) | **`ExpertiseEntry`** | arch §2.3, §5.4 |
| N-2 | `IMemoryStore` (Stage 1.2) | **`IMemoryGraphStore`** | arch §2.3, §5.2 |
| N-3 | Separate `IExpertiseStore` (Stage 1.2) | Merged into **`IEvolutionEngine`** | arch §2.3, §5.4 |
| N-4 | `AgentMemoryService` composes `IExpertiseStore` (Stage 5.1) | Compose **`IEvolutionEngine`** | arch §5.4 |

#### 12.3.2 Data Model Conflicts (4 items — architecture.md §10.1.2)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| D-1 | `MemoryNode` "extending `HypergraphNode`" (inheritance) | **Composition wrapper** (`HypergraphNode Inner { get; }`) | arch §4.1 |
| D-2 | `MemoryEdge` "extending `Hyperedge`" (inheritance) | **Composition wrapper** (`Hyperedge Inner { get; }`) | arch §4.1 |
| D-3 | `PatternType` enum name | **`ExpertiseKind`** enum; class = `ExpertiseEntry`; field = `Kind` | arch §2.3, §5.4 |
| D-4 | Separate `Annotation.cs` entity | **No separate class** — `Metadata` key/value pairs + `AnnotationKeyRegistry` | arch §4.5 |

#### 12.3.3 ID Format Conflicts (2 items — architecture.md §10.1.3)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| I-1 | `{repo}:{language}:{fqn}:{hash_short}` | **`"{filePath}::{qualifiedName}"`** (path-based) | arch §4.4 |
| I-2 | `IdReconciliationTable` backed by `ID_MAPS_TO` Neo4j edges | ID *format* must follow §4.4; backing is implementation choice | arch §4.4 |

#### 12.3.4 Edge Name Conflicts (4 items — architecture.md §10.1.4)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| E-1 | `CALLED_BY` as stored edge (Stage 2.2) | **Reverse traversal** of `CALLS`, not stored | arch §2.3 |
| E-2 | `INHERITS` edge (Stage 2.2) | **`EXTENDS`** | arch §2.2, §2.3 |
| E-3 | `IMPORTS` edge (Stage 2.2) | **`DEPENDS_ON`** | arch §2.2, §2.3 |
| E-4 | `DependencyInvalidator` walks `IMPORTS` (Stage 4.2) | Walk **`DEPENDS_ON`** | arch §2.2, §2.3 |

#### 12.3.5 Numeric / Threshold Conflicts (5 items — architecture.md §10.1.5)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| T-1 | `semantic=0.3` (Stage 3.2) | **`semantic=0.25`** | arch §2.4 |
| T-2 | `centrality=0.15` (Stage 3.2) | **`centrality=0.20`** | arch §2.4 |
| T-3 | `MaxTokens` default = 32768 (Stage 3.1) | Default = **`8,000`**; ceiling = 32,000 | arch §2.4 |
| T-4 | Decay half-life = 60 days (Stage 6.3) | **`DecayHalfLifeDays = 1095`** (3 years) | arch §2.4 |
| T-5 | Decay formula `e^(-λ × days)` (Stage 6.3) | **`2^(−age/1095) × centrality_boost`** (base-2) | arch §3.5 |

#### 12.3.6 Embedding Model (1 item — architecture.md §10.1.6)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| M-1 | No explicit model specified | **`"nomic-embed-text"`** (768 dims) | arch §4.7 |

> Matches existing `EmbeddingGeneratorOptions.ModelName` (`src/ast/Ast.Embedding/EmbeddingModels.cs:119`) and `EmbeddingConfiguration.ModelName` (`src/ast/Ast.Core/AstConfiguration.cs:96`).

#### 12.3.7 Storage Interface (2 items — architecture.md §10.1.7)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| S-1 | `IMemoryStore` with `UpsertAnnotationAsync`, `GetAnnotationsAsync`, etc. | **`IMemoryGraphStore`** with `ApplyDiffAsync`, `TraverseAsync`, `VectorSearchAsync`, `GetNodeAtCommitAsync`, etc. | arch §5.2 |
| S-2 | `Neo4jMemoryStore` with annotation CRUD | **`Neo4jMemoryGraphStore`** implementing `IMemoryGraphStore` | arch §3.2, §5.2 |

#### 12.3.8 Project Mutation Policy (3 items — architecture.md §10.1.8)

| # | impl-plan says | Canonical value | Governing §|
|---|------|----------------|-----|
| P-1 | `IMemoryStore.cs` placement implied in `Ast.GraphDb` | **`IMemoryGraphStore`** defined in `Ast.Memory/Interfaces/`, not in `Ast.GraphDb`. No files in `Ast.GraphDb` are created or modified. | arch §3.0, §3.2, §5.2 |
| P-2 | `Neo4jMemoryStore.cs` | **`Neo4jMemoryGraphStore`** in `Ast.Memory/Storage/`, **composing** existing `Neo4jGraphStore` from `Ast.GraphDb` — not modifying it. | arch §3.2 |
| P-3 | MCP files created in `Ast.Cli` | MCP tools and handler logic in **`Ast.Memory/Mcp/`**. CLI command classes in `Ast.Memory/Cli/`. `Ast.Cli/Program.cs` receives only additive `config.AddBranch("memory", ...)` lines. | arch §5.6, §5.6.4 |

#### 12.3.9 Net Assessment

All 25 items have **stated canonical resolutions** in architecture.md §10.1. The implementation-plan describes valid *work items and phasing* that remain useful for project management — only the literal names, values, and data-model shapes need updating. Implementers who follow the canonical values from architecture.md will produce correct code. These divergences do not represent ambiguity in the tech-spec or architecture; they represent documentation debt in the implementation-plan.

### 12.4 Annotation Schema: Curated Registry (D4)

Per operator decision, `Metadata` keys on memory nodes are governed by a curated registry. The canonical list of well-known keys is defined in §7.3 above. The governance model:

1. **Well-known keys** (defined in `AnnotationKeyRegistry.cs` in `Ast.Memory/Schema/`): strongly typed, validated at write time. Adding a new key requires a code change.
2. **Extension keys** (prefixed `ext:`): stored in `Metadata` without schema enforcement. Surfaced with a validation warning in telemetry. Intended for agent experimentation; keys that prove useful are promoted to well-known status.
3. **Expertise pattern types** (`Pattern`, `Pitfall`, `Preference`, `Insight`): closed enum (`ExpertiseKind`) for MVP. Agent uses `Insight` as catch-all for novel categories. Promotion to a named type requires a code change to `ExpertiseKind`.

### 12.5 Cross-Branch Memory Model (D3)

Expertise nodes are **globally visible** regardless of the current git branch. Implementation details:

- The `commits` table records which branch each commit belongs to (provenance), but `IMemoryGraphStore.TraverseAsync` does **not** filter by branch lineage.
- When the agent queries expertise, all active (non-tombstoned) expertise nodes are candidates.
- **Staleness heuristic:** If an expertise node's `APPLIES_TO` target has a different `ContentHash` on the current branch vs. the commit where the expertise was written, the Context Assembler injects a `⚠ stale — target code has changed since this expertise was recorded` marker into the prompt.
- **Merge conflicts:** Not applicable — expertise nodes are additive, not merge-conflicting. Two agents writing contradictory expertise on different branches produce two nodes; the Evolution Engine's consolidation step (LLM-powered) resolves contradictions post-merge via `SUPERSEDES` edges.

### 12.6 Alignment with `e2e-scenarios.md`

The [`e2e-scenarios.md`](e2e-scenarios.md) document covers 7 features with 30+ Gherkin scenarios across Extraction, Storage, Retrieval, Evolution, End-to-End, and Performance. Architecture.md §10.2 documents 9 alignment items (E2E-1 through E2E-9) describing divergences between the e2e-scenarios and canonical vocabulary. Key points relevant to this tech-spec:

- **E2E-1:** Scenario 4.1 uses `called_by` as a result-table label. This is a reverse traversal of `CALLS`, not a stored edge — consistent with this spec §7.2 and D2 (retired names). Scenario is behaviorally correct.
- **E2E-2/3/4:** Scenarios use presentation aliases (`direct_call`, `polymorphic`, `recursive`) for edge types. All map to `CALLS` edges with varying `Confidence` values — consistent with §7.2.
- **E2E-5:** Scenario 2.7 uses `service_call` edge type for cross-service RPC. This is out of scope per this spec §3 O1 (runtime/dynamic tracing excluded). Architecture.md §10.2 recommends tagging this scenario `@deferred`.
- **E2E-6:** Scenarios use unqualified symbol IDs (e.g., `AuthService.login`) as abbreviations. Canonical format is path-based per §7.1 (e.g., `/src/auth/login::AuthService.login`).
- **E2E-8:** Scenario 6.4 describes cross-repo memory transfer — out of scope per this spec §3 O2. Should be tagged `@deferred`.
- **E2E-9:** Scenario 3.4 describes conflict errors on concurrent writes. Architecture specifies SQLite WAL mode (serialized writes, no optimistic-concurrency errors) for local and Neo4j for team mode. The scenario should be adjusted to test write serialization, not conflict errors — consistent with R6 in this spec.

### 12.7 Residual Cross-Document Notes

| # | Item | Status |
|---|------|--------|
| X-1 | Architecture.md §10.3 TS-1 confirms tech-spec and architecture list the same implementation-plan conflicts | ✅ **Resolved.** Architecture.md §10.3 TS-1 now says "✅ Consistent — same conflicts listed here in §10.1." This tech-spec §12.3 mirrors all 25 items across 8 categories (§12.3.1 through §12.3.8), matching architecture.md §10.1.1 through §10.1.8. Counts are consistent. |
| X-2 | All implementation-plan conflicts (§12.3) have canonical resolutions | ✅ **Resolved.** Each item has a stated canonical value from architecture.md. §12.3 is reframed as documentation debt in the sibling document, not as unresolved conflicts in this tech-spec. Implementers follow architecture.md canonical values. |
| X-3 | Architecture.md §10.3 TS-4 confirms `ExtractionOptions.EmbeddingModel` default fixed to `"nomic-embed-text"` in iteration 12 | ✅ **Resolved.** Tech-spec §12.2 and source-code model note updated to reflect this alignment. All three sources (codebase, architecture.md, tech-spec) now agree on `"nomic-embed-text"`. |
| X-4 | Architecture.md §10.3 TS-5 confirms project mutation policy alignment | ✅ **Resolved.** Two additive changes to existing projects: (1) `HyperedgeType.MemoryRelation` in `Ast.Core`, (2) `config.AddBranch("memory", ...)` in `Ast.Cli/Program.cs`. All new interfaces/implementations in `Ast.Memory`. Matches HC8. |

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

*End of tech spec — Iteration 10. All open questions from Iteration 1 are resolved via operator decisions D1–D6. Iteration 10 fixes: (1) fixed §1.1 reuse strategy for `Ast.GraphDb` and `Ast.Query` — both are reference-only, no modifications; new interfaces are in `Ast.Memory` (consistent with architecture.md §3.0, §3.2); (2) updated §12.3 implementation-plan conflict count from 20 to 25 — added §12.3.8 Project Mutation Policy (P-1, P-2, P-3 from architecture.md §10.1.8 iteration 13); renumbered Net Assessment to §12.3.9; (3) fixed §12.7 X-1 to reflect architecture.md §10.3 TS-1's current wording (no longer references "12 (IP1–IP12)" count); added X-4 for TS-5 project mutation policy alignment; (4) all cross-document alignment counts now match architecture.md §10.1 exactly (25 items across 8 categories).*
