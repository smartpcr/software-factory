# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> **Status:** Iteration 12
>
> This document is the **canonical authority** for component boundaries,
> data model, edge/layer naming, ID format, and interface signatures.
> When sibling documents ([tech-spec.md](tech-spec.md),
> [implementation-plan.md](implementation-plan.md),
> [e2e-scenarios.md](e2e-scenarios.md)) differ, this document governs.
> Tech-spec owns problem statement, constraints, risks, and success criteria.
> Implementation-plan owns phased work items. E2e-scenarios owns Gherkin acceptance tests.
>
> **§10 Cross-Document Alignment** lists every known conflict with
> all sibling documents and states the canonical resolution.
> Implementers MUST consult §10 when following any sibling document.

---

## 1. Purpose & Scope

This architecture specifies a **graph-based agent memory** that encodes
repository structure, call chains, semantic similarity, and accumulated
expertise into a persistent, queryable knowledge graph. Agents consume this
graph to construct richer context and accumulate institutional knowledge
across sessions — patterns, pitfalls, and architectural insights that make
each subsequent session smarter.

Design draws from the graph-based agent-memory taxonomy (Yang et al.,
arXiv:2602.05665) — **Extraction → Storage → Retrieval → Evolution** —
adapted to the code-intelligence domain where inputs are ASTs, call graphs,
and VCS history.

### 1.1 In / Out of Scope

| In scope | Out of scope |
|---|---|
| Static-analysis extraction (AST, call graph, imports) | Runtime / dynamic tracing |
| Multi-layer graph construction & persistent storage | Multi-repo federation |
| Token-budgeted context retrieval for agent prompts | IDE UI / dashboards |
| Expertise write-back, consolidation, decay | LLM inference hosting |
| Incremental sync on commit / file-save | Multi-agent shared memory |

### 1.2 Existing Infrastructure (`src/ast/`)

The system extends the Code Intelligence stack in `src/ast/` (24 projects,
970+ tests across 13 test projects). No existing class signatures are modified.

| Project | Key types (with source paths) | Reuse strategy |
|---|---|---|
| **Ast.Core** | `HypergraphNode`, `Hyperedge`, `HyperedgeType` (44 enum values) — `src/ast/Ast.Core/HypergraphModels.cs`; `RepositoryProcessor` — `src/ast/Ast.Core/RepositoryProcessor.cs` | Extend via `Metadata` dict; add one enum value `MemoryRelation`. |
| **Ast.GraphDb** | `IGraphStore` — `src/ast/Ast.GraphDb/IGraphStore.cs`; `Neo4jGraphStore` — `src/ast/Ast.GraphDb/Neo4jGraphStore.cs`; `NodeFilter`, `GraphPath`, `NodeWithDepth` — `src/ast/Ast.GraphDb/GraphModels.cs` | Add `IMemoryGraphStore` sub-interface; add `SqliteGraphStore`. |
| **Ast.Embedding** | `IEmbeddingGenerator` — `src/ast/Ast.Embedding/IEmbeddingGenerator.cs`; `CodeEmbeddingService` — `src/ast/Ast.Embedding/CodeEmbeddingService.cs`; `EnrichedEmbeddingPipeline` — `src/ast/Ast.Embedding/EnrichedEmbeddingPipeline.cs`; `EmbeddingGeneratorOptions` (default model: `"nomic-embed-text"`) — `src/ast/Ast.Embedding/EmbeddingModels.cs` | Reuse directly for all embedding work. |
| **Ast.Query** | `ICodeQuery` — `src/ast/Ast.Query/ICodeQuery.cs`; `HybridQueryService` — `src/ast/Ast.Query/HybridQueryService.cs`; `ScoredNode` — `src/ast/Ast.Query/QueryModels.cs` | Extend with memory-aware retrieval and token budgeting. |
| **Ast.VectorDb** | `IVectorStore` — `src/ast/Ast.VectorDb/IVectorStore.cs`; `QdrantVectorStore` — `src/ast/Ast.VectorDb/QdrantVectorStore.cs` | Reuse for team mode; add in-process HNSW for local mode. |
| **Ast.Lsp** | LSP integration — `src/ast/Ast.Lsp/` | Optional enrichment — pipeline never blocks on LSP. |
| **CodeIntelligence** | 27 analyzers in `src/ast/CodeIntelligence/Analyzers/`, 4 detectors in `src/ast/CodeIntelligence/Detectors/`, `RelationshipGraphBuilder` — `src/ast/CodeIntelligence/Graph/RelationshipGraphBuilder.cs` | Consume as call-chain / structural edge source. |
| **Ast.Cli** | CLI host — `src/ast/Ast.Cli/` | Extend with MCP tools for retrieve + write-back. |

### 1.3 Research Foundations

| ID | Source | Adopted technique | Where applied |
|---|---|---|---|
| R-1 | MAGMA (arXiv:2601.03236) | Orthogonal graph layers prevent signal entanglement | Four independent layers (§2.1) |
| R-2 | HiAgent (ACL '25) | Hierarchical working memory with abstraction-level control | Budget Controller collapses children into parent summaries (§3.4) |
| R-3 | Zep/Graphiti (arXiv:2501.13956) | Temporal KG with timestamped edges | Dual-axis temporal model: commit-index + wall-clock (§4.3) |
| R-4 | Mem0 (arXiv:2504.19413) | Agent write-back for compounding value | Evolution Engine expertise write-back (§3.5) |
| R-5 | Reflexion (NeurIPS '23) | Verbal RL — reflect on past failures | Cross-session learning via Expertise layer (§6.5) |
| R-6 | GraphRAG (arXiv:2404.16130) | Community detection + LLM summarization | Topic nodes in Semantic layer (§3.5) |
| R-7 | MemGPT (arXiv:2310.08560) | Explicit memory read/write operations | Discrete retrieval modes in Query Planner (§3.3) |

---

## 2. Canonical Vocabulary

All planning documents in this story use these definitions.

### 2.1 Graph Layers

| Layer | Tag value (`MemoryLayer`) | Purpose | Edge types |
|---|---|---|---|
| **Structural** | `structural` | Containment: Repo → Workspace → Package → Module → Class → Function | `CONTAINS`, `DEPENDS_ON` |
| **Call-Chain** | `call_chain` | Invocations and type relationships between symbols | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` |
| **Semantic** | `semantic` | Embedding-similarity clusters and topic summaries | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` |
| **Expertise** | `expertise` | Agent-authored knowledge nodes | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` |

Temporal versioning is **orthogonal** — it applies to nodes/edges on every
layer via `VersionRange` (§4.3). It is not a separate layer.

### 2.2 Edge Taxonomy

**Code-derived edges** — produced by the Extraction Pipeline. Each maps to
existing `HyperedgeType` enum values in `Ast.Core`:

| Canonical edge | `HyperedgeType` values | Layer | Roles |
|---|---|---|---|
| `CONTAINS` | `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition`, `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | Structural | `parent`, `child` |
| `DEPENDS_ON` | `UsingDirective`, `ModuleImport`, `DotSource` | Structural | `importer`, `imported` |
| `CALLS` | `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` | Call-Chain | `caller`, `callee` |
| `EXTENDS` | `Extends` | Call-Chain | `subclass`, `superclass` |
| `IMPLEMENTS` | `Implements` | Call-Chain | `implementor`, `interface` |
| `REFERENCES` | `DataFlow`, `Assignment`, `CrossLanguageTypeReference` | Call-Chain | `source`, `target` |

**Memory-only edges** — produced by the Semantic Indexer or Evolution Engine.
All use `HyperedgeType.MemoryRelation` (a single new enum value added to
`Ast.Core`). The true semantic type is in `Metadata["SemanticRelation"]`:

| Canonical edge | Layer | Created by | Roles |
|---|---|---|---|
| `SIMILAR_TO` | Semantic | Semantic Indexer | `node_a`, `node_b` |
| `BELONGS_TO_CLUSTER` | Semantic | Evolution Engine (community detection) | `member`, `cluster` |
| `APPLIES_TO` | Expertise | Evolution Engine (write-back) | `expertise`, `target` |
| `SUPERSEDES` | Expertise | Evolution Engine (consolidation) | `newer`, `older` |
| `DERIVED_FROM` | Expertise | Evolution Engine (consolidation) | `derived`, `source` |

**Uniform filtering:** Every edge carries `Metadata["SemanticRelation"]` with
its canonical name. This is the **sole** edge-filtering mechanism used by the
Retrieval Engine. The `HyperedgeType` enum is preserved for backward
compatibility but is never used as a retrieval filter.

### 2.3 Retired Names

| Retired name | Canonical replacement | Notes |
|---|---|---|
| `IMPORTS` | `DEPENDS_ON` | |
| `CALLED_BY` | Reverse traversal of `CALLS` | Not a stored edge |
| `INHERITS` | `EXTENDS` | |
| `INSTANTIATES` | `CALLS` (with `ConstructorInvocation`) | |
| `ExpertiseNote` | `ExpertiseEntry` | Class renamed |
| `IMemoryStore` | `IMemoryGraphStore` | Interface renamed |
| `IExpertiseStore` | Part of `IEvolutionEngine` | Merged |

### 2.4 Constants & Thresholds (Single Source of Truth)

All configurable parameters referenced throughout this document and sibling
documents. Implementations read these from `MemoryOptions` configuration.

| Constant | Default | Used by | Description |
|---|---|---|---|
| `SimilarityThreshold` | 0.85 | Semantic Indexer, Evolution Engine | Cosine-similarity floor for `SIMILAR_TO` and `DERIVED_FROM` edges |
| `StaleDecayFactor` | 0.5 | Evolution Engine | Confidence multiplier on major code change |
| `MinorDecayFactor` | 0.9 | Evolution Engine | Confidence multiplier on whitespace/comment-only change |
| `ArchiveConfidenceFloor` | 0.1 | Evolution Engine | Expertise nodes below this are soft-deleted |
| `DecayHalfLifeDays` | 1095 | Evolution Engine | Time-based decay half-life (3 years) |
| `TokenBudgetDefault` | 8,000 | Budget Controller | Per-query default token budget |
| `TokenBudgetCeiling` | 32,000 | Budget Controller | Maximum allowed token budget |
| `IncrementalSyncDebounceMs` | 1,000 | Incremental Sync | Debounce for file-system watcher |

**Scoring weights** (used by the Ranker, sum = 1.00):

| Signal | Default weight | Description |
|---|---|---|
| `structural` | 0.30 | Inverse BFS distance from seed (Structural layer) — bounded [0,1] |
| `semantic` | 0.25 | Cosine similarity of query embedding to node embedding |
| `centrality` | 0.20 | Normalized fan-in from `CALLS` edges (PageRank-light) |
| `recency` | 0.15 | `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped [0,1] |
| `expertise` | 0.10 | Max `Confidence` of any `APPLIES_TO` edge targeting the node; 0.0 when none |

---

## 3. Components & Responsibilities

### 3.0 Project Layout

All new code ships as **one new project** (`Ast.Memory`) plus test projects,
following the `src/ast/Ast.<Name>` convention. Internal namespaces provide
logical separation.

```
src/ast/
  Ast.Memory/                     ← NEW
    Ast.Memory.csproj             ← refs: Ast.Core, Ast.GraphDb, Ast.Embedding
    Models/                       ← MemoryNode, MemoryEdge, ExpertiseEntry, Enums, MemoryMetadataKeys
    Extraction/                   ← StructureBuilder, CallChainAnalyzer, SemanticIndexer, FactNormalizer
    Context/                      ← ContextAssembler, BudgetController
    Evolution/                    ← EvolutionEngine, ConsolidationService, DecayService
    Retrieval/                    ← MemoryRetrievalEngine, QueryPlanner, Ranker, ScoringWeights
    Sync/                         ← IncrementalSync, GitDiffDriver
    Storage/                      ← IMemoryGraphStore (also in Ast.GraphDb), SqliteGraphStore
  Ast.Memory.UnitTests/           ← NEW
  Ast.Memory.IntegrationTests/    ← NEW
  (existing projects unchanged)
```

### 3.0.1 Component Diagram

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Agent Memory System                          │
│                                                                       │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────────┐  │
│  │  Extraction   │──▶│  Graph Store  │◀──│  Retrieval Engine        │  │
│  │  Pipeline     │   │  (4 layers)  │   │  (planner + ranker)      │  │
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
         │  git events / file-system watch          │  context block
         │                                          ▼
    ┌─────────┐                              ┌─────────────┐
    │  Source  │                              │  Agent / LLM │
    │  Repo   │                              │  Consumer    │
    └─────────┘                              └─────────────┘
```

### 3.1 Extraction Pipeline

**Project:** `Ast.Memory` (Extraction namespace)  
**Responsibility:** Parse source code into typed graph nodes and edges.

| Sub-component | Input | Output |
|---|---|---|
| **Language Parser** (existing `Ast.Csharp/CsharpAstParser.cs`, `Ast.PowerShell/PowerShellAstParser.cs`) | Source files | `HypergraphNode` + `Hyperedge` with `HyperedgeType` values |
| **Structure Builder** | Parser output | Structural-layer hierarchy: `CONTAINS` + `DEPENDS_ON` edges |
| **Call-Chain Analyzer** | Parser output + resolved types | Call-chain-layer edges: `CALLS` / `EXTENDS` / `IMPLEMENTS` / `REFERENCES` |
| **Semantic Indexer** | Symbol names, docstrings, signatures | `Metadata["Embedding"]` vectors via `IEmbeddingGenerator` (§1.2); `SIMILAR_TO` edges above `SimilarityThreshold` |
| **Fact Normalizer** | All raw facts | Stable path-based IDs, `VersionRange` stamps, `ContentHash`, deduplication |

**Key design:** Existing `Ast.Core` parsers are the primary extraction
adapters. `RepositoryProcessor` (in `src/ast/Ast.Core/RepositoryProcessor.cs`)
orchestrates file discovery and dispatches to language-specific parsers.
Tree-sitter adapters for TypeScript, Python, Go are additive — no schema
changes needed.

### 3.2 Graph Store

**Project:** `Ast.GraphDb` (interface at `src/ast/Ast.GraphDb/IGraphStore.cs`) + `Ast.Memory/Storage` (SQLite impl)  
**Responsibility:** Persist the four graph layers, serve traversal queries,
manage versioned snapshots.

| Deployment | Graph backend | Vector backend |
|---|---|---|
| Local / CI | SQLite + FTS5 + recursive CTEs | sqlite-vss or in-process HNSW (usearch) |
| Team / Cloud | Neo4j (`src/ast/Ast.GraphDb/Neo4jGraphStore.cs`) | Qdrant (`src/ast/Ast.VectorDb/QdrantVectorStore.cs`) |

Backend selection is via DI (see existing `src/ast/Ast.GraphDb/ServiceCollectionExtensions.cs`
and `src/ast/Ast.VectorDb/ServiceCollectionExtensions.cs`); all backends
implement `IMemoryGraphStore` (§5.2).

### 3.3 Retrieval Engine

**Project:** `Ast.Query` (extended) + `Ast.Memory/Retrieval`  
**Responsibility:** Accept a query, plan retrieval, traverse the graph, rank
and return memory fragments.

| Sub-component | Role |
|---|---|
| **Query Planner** | Classifies query intent → selects retrieval mode(s) and graph layers. |
| **Graph Traverser** | Depth-limited BFS / DFS on selected layers. |
| **Vector Searcher** | ANN search over embeddings via `IVectorStore` (`src/ast/Ast.VectorDb/IVectorStore.cs`). |
| **Ranker** | Merges results using the 5-signal scoring model (§2.4). |
| **Budget Controller** | Enforces token budget; substitutes parent summaries when children exceed budget (HiAgent pattern). |

**Retrieval modes:**

| Mode | Layers | `SemanticRelations` filter | Example query |
|---|---|---|---|
| `StructuralDrill` | `structural` | `CONTAINS`, `DEPENDS_ON` | "What does the auth package contain?" |
| `CallTrace` | `call_chain` | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` | "What calls validateToken?" |
| `SemanticSearch` | `semantic` | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` | "Find code related to rate limiting" |
| `ExpertiseRecall` | `expertise` | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | "Known pitfalls for payments?" |
| `Hybrid` | all | all of the above | "Explain auth flow and flag issues" |

**Edge filtering:** `TraversalPlan.SemanticRelations` (a `string` list) is
the **sole** edge filter. It works uniformly for code-derived and memory-only
edges because every edge carries `Metadata["SemanticRelation"]`. The legacy
`TraversalPlan.EdgeTypes` (`HyperedgeType` list) is **deprecated** — retained
for backward compatibility, never set by memory queries.

**Cross-branch expertise visibility:** Expertise-layer nodes are **globally
visible** regardless of git branch. Staleness is detected by comparing the
target node's `ContentHash` against the current branch's version. When
mismatched, the Context Assembler injects: `⚠ stale — target code changed`.

### 3.4 Context Assembler

**Project:** `Ast.Memory/Context`  
**Responsibility:** Transform ranked fragments into a structured prompt section.

Three transformations:

1. **Hierarchy compression** — parent + all children selected → collapse to
   parent summary + child signatures.
2. **Deduplication** — same code span from multiple paths emitted once.
3. **Formatting** — deterministic Markdown with sections:
   `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`.

### 3.5 Evolution Engine

**Project:** `Ast.Memory/Evolution`  
**Responsibility:** Manage the Expertise layer lifecycle.

| Operation | Trigger | Description |
|---|---|---|
| **Write-back** | Agent emits annotation | Creates Expertise node, computes embedding via `IEmbeddingGenerator`, links via `APPLIES_TO` edges. If cosine similarity to existing expertise > `SimilarityThreshold` (0.85), creates `DERIVED_FROM` edge. |
| **Consolidation** | Post-PR-merge or manual | LLM-powered merge of overlapping expertise nodes. Produces `SUPERSEDES` edges. Community detection (Louvain) generates `Topic` + `SemanticCluster` nodes. |
| **Time-based decay** | On every retrieval | `decayed = initial × 2^(−age_days / DecayHalfLifeDays) × centrality_boost` where `centrality_boost = 1.0 + 0.5 × normalized_pagerank` (range 1.0–1.5). |
| **Event-based decay** | On code change | Major change: confidence × `StaleDecayFactor` (0.5). Minor change (whitespace/comments): × `MinorDecayFactor` (0.9). Below `ArchiveConfidenceFloor` (0.1) → soft-delete. |

### 3.6 Incremental Sync

**Project:** `Ast.Memory/Sync`  
**Responsibility:** Keep graph current without full re-extraction.

1. On commit or file-save, diff changed files (`git diff` or FS watcher).
2. Re-run Extraction Pipeline on changed files only.
3. Compute node/edge diff against current graph.
4. Apply inserts, updates, tombstones (`VersionRange.Until` set, not deleted).
5. Propagate: flag downstream `CALLS` edges as `Stale` if source signature changed.

**Performance target:** < 2 seconds for single-file change on repos ≤ 500K LOC.

---

## 4. Data Model

### 4.1 Model Extension Strategy

The memory layer extends `HypergraphNode` and `Hyperedge` **non-breakingly**
via their existing `Metadata: Dictionary<string, object?>` dictionaries.
No properties are added to the base classes.

**Existing `HypergraphNode` properties** (from `src/ast/Ast.Core/HypergraphModels.cs` lines 74–86):

| Property | Type | Description |
|---|---|---|
| `Id` | `string` (required) | Unique node identifier |
| `Name` | `string` (required) | Display name |
| `NodeType` | `string` (required) | Kind: `"Class"`, `"Function"`, etc. |
| `Language` | `string` (required) | `"C#"`, `"PowerShell"`, etc. |
| `FilePath` | `string` (required) | Source file path |
| `Scope` | `string` | Containing scope |
| `LineStart` / `LineEnd` | `int` | Source location |
| `Modifiers` | `List<string>` | `["public", "async"]`, etc. |
| `Metadata` | `Dictionary<string, object?>` | **Extension point** — memory uses this |

**Existing `Hyperedge` properties** (from `src/ast/Ast.Core/HypergraphModels.cs` lines 199–207):

| Property | Type | Description |
|---|---|---|
| `Id` | `string` (required) | Unique edge identifier |
| `EdgeType` | `HyperedgeType` (required) | One of 44 enum values (or new `MemoryRelation`) |
| `NodeIds` | `List<string>` (required) | Participating node IDs (≥ 2) |
| `Roles` | `Dictionary<string, string>` (required) | Maps node ID → semantic role |
| `Languages` | `List<string>` | Languages of participating nodes |
| `Weight` | `double` | Default 1.0 |
| `Metadata` | `Dictionary<string, object?>` | **Extension point** — memory uses this |

`MemoryNode` and `MemoryEdge` are **composition wrappers** — they contain a
base-type instance as an `Inner` property and provide strongly-typed accessors
for memory Metadata keys. They do **not** use C# class inheritance
(`: HypergraphNode`). All inter-component interfaces accept/return the base
types (`HypergraphNode`, `Hyperedge`) to preserve backward compatibility with
970+ existing tests.

```csharp
// Composition — NOT inheritance
public class MemoryNode
{
    public HypergraphNode Inner { get; }
    public string Layer => (string)this.Inner.Metadata["MemoryLayer"];
    public VersionRange Version => (VersionRange)this.Inner.Metadata["VersionRange"];
    public string? ContentHash => (string?)this.Inner.Metadata.GetValueOrDefault("ContentHash");
    // ... typed accessors for all memory Metadata keys
}
```

### 4.2 Node Types

All nodes are `HypergraphNode` instances. The `NodeType` property and
`Metadata["MemoryLayer"]` together determine the role.

#### Code Nodes (Structural + Call-Chain layers)

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `Repo` | structural | `root_path`, `vcs_url`, `default_branch` | Singleton root. ID = `"/"` |
| `Workspace` | structural | `name`, `path`, `manager` | Monorepo child; absent in single-package repos |
| `Package` | structural | `name`, `version`, `path` | Maps to `.csproj`, `package.json`, etc. |
| `Module` | structural | `file_path`, `language`, `hash` | One per source file |
| `Class` | structural | `name`, `fqn`, `is_abstract`, `is_exported` | Classes, interfaces, structs, enums, records |
| `Function` | structural | `name`, `fqn`, `signature`, `line_start`, `line_end` | Leaf structural node |

#### Semantic Nodes

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `SemanticCluster` | semantic | `centroid_embedding`, `member_count` | Community of similar nodes |
| `Topic` | semantic | `title`, `summary_prose` | GraphRAG-style cluster summary |

#### Expertise Nodes

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `Pattern` | expertise | `Title`, `Body`, `Confidence`, `SourceAgent`, `Evidence`, `Tags` | Recurring code pattern |
| `Pitfall` | expertise | (same fields as Pattern) | Known failure mode |
| `Preference` | expertise | (same fields as Pattern) | Coding convention / style |
| `Insight` | expertise | (same fields as Pattern) | Architectural observation |

### 4.3 Temporal Model

Commit SHAs are opaque and not orderable. The system uses a **dual-axis** model:

1. **Commit axis** — each SHA maps to a monotonic `commit_index` in the `commits` table.
2. **Wall-clock axis** — `LastTouched` (ISO-8601) records extraction time (informational only).

```sql
CREATE TABLE commits (
    commit_sha   TEXT PRIMARY KEY,
    commit_index INTEGER NOT NULL UNIQUE,
    timestamp    TEXT NOT NULL,
    parent_sha   TEXT
);
```

Every node and edge carries a `VersionRange` in its `Metadata`:

```csharp
public record VersionRange(
    string Since,       // commit SHA — provenance display
    int SinceIndex,     // monotonic index — range queries
    string? Until,      // null = current; non-null = tombstoned at this commit
    int? UntilIndex     // null = current; non-null = commit index of tombstone
);
```

Point-in-time query predicate:
`WHERE valid_from_idx <= $target_idx AND (valid_until_idx IS NULL OR valid_until_idx > $target_idx)`

### 4.4 Node ID Format

All IDs are **`string`** — no GUIDs, UUIDs, or integers.

| Node kind | Format | Example |
|---|---|---|
| Repo root | `"/"` | `"/"` |
| Directory | `"{relativePath}"` | `"/src/auth"` |
| File / Module | `"{relativePath}"` | `"/src/auth/login"` |
| Symbol (class, function) | `"{filePath}::{qualifiedName}"` | `"/src/auth/login::AuthService.validate"` |
| Expertise node | `"expertise:{kind}:{hash8}"` | `"expertise:pitfall:a1b2c3d4"` |
| SemanticCluster | `"cluster:{hash8}"` | `"cluster:e5f6a7b8"` |
| Topic | `"topic:{hash8}"` | `"topic:c9d0e1f2"` |

Edge IDs are deterministic: `"{sourceId}→{relation}→{targetId}"`.

### 4.5 Metadata Keys (Curated Registry)

Keys on memory nodes are governed by `AnnotationKeyRegistry` — a code-defined
registry with typed schemas. Agent extension keys use `ext:` prefix.

#### Node Metadata (`HypergraphNode.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | `"structural"` \| `"call_chain"` \| `"semantic"` \| `"expertise"` |
| `VersionRange` | `VersionRange` | Commit SHA + index range (§4.3) |
| `ContentHash` | `string` | SHA-256 of source span |
| `LastTouched` | `string` (ISO-8601) | Wall-clock of last extraction (informational) |
| `Embedding` | `float[]` | Dense vector from `IEmbeddingGenerator` |
| `EmbeddingModel` | `string` | Model identifier (e.g., `nomic-embed-text`) |
| `Summary` | `string` | LLM-generated one-liner |
| `QualifiedName` | `string` | Fully-qualified name |

Expertise nodes carry additional keys: `ExpertiseKind`, `Title`, `Body`,
`Confidence`, `SourceAgent`, `Evidence` (list of `Citation`), `Tags`.

#### Edge Metadata (`Hyperedge.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | Layer tag |
| `SemanticRelation` | `string` | Canonical edge name — **primary retrieval filter** |
| `VersionRange` | `VersionRange` | Temporal range |
| `Confidence` | `double` | 0.0–1.0 (direct calls=1.0, dynamic dispatch=0.5–0.7) |
| `Stale` | `bool` | Pending re-confirmation |
| `OriginalEdgeType` | `string` | Original `HyperedgeType` enum name (lossless round-trip) |

Relation-specific extra keys:

| Relation | Extra keys |
|---|---|
| `CALLS` | `ArgCount`, `IsAsync`, `IsConditional`, `CallSiteStart`, `CallSiteEnd` |
| `DEPENDS_ON` | `Alias`, `IsWildcard`, `IsTypeOnly` |
| `APPLIES_TO` | `Scope` (`"exact"` or `"subtree"`) |
| `SUPERSEDES` | `Reason`, `SupersededAt` (ISO-8601) |
| `SIMILAR_TO` | `CosineSim` |
| `REFERENCES` | `ReferenceKind` (`"type_annotation"`, `"variable"`, `"parameter"`, `"data_flow"`) |

### 4.6 Edge Shape & Hyperedge Semantics

`Hyperedge` from `Ast.Core` is a **multi-node hyperedge** with
`NodeIds: List<string>` and `Roles: Dictionary<string, string>`. It is NOT
a binary source→target edge. The memory layer preserves this model.

For binary relationships (the common case), the hyperedge has exactly 2
`NodeIds` with semantic roles defined in the edge taxonomy (§2.2).

**Memory-only edges** use `HyperedgeType.MemoryRelation` — a single new
enum value added to `Ast.Core/HypergraphModels.cs` (additive, non-breaking).
Existing parsers never emit this value, so no existing code path is affected.

```csharp
// In Ast.Core — single additive change
public enum HyperedgeType
{
    // ... existing 44 values unchanged ...
    MemoryRelation  // new — used for Semantic + Expertise layer edges
}
```

Storage example:
```
Hyperedge {
  Id       = "/src/auth/login::AuthService.login→CALLS→/src/auth/token::verify"
  EdgeType = MemoryRelation (or original HyperedgeType for code-derived)
  NodeIds  = ["/src/auth/login::AuthService.login", "/src/auth/token::verify"]
  Roles    = { "/src/auth/login::AuthService.login": "caller",
               "/src/auth/token::verify": "callee" }
  Weight   = 1.0
  Metadata = { "MemoryLayer": "call_chain",
               "SemanticRelation": "CALLS",
               "VersionRange": { Since: "abc123", SinceIndex: 42, Until: null, UntilIndex: null },
               "Confidence": 1.0,
               "Stale": false,
               "OriginalEdgeType": "MethodInvocation" }
}
```

### 4.7 Embedding Lifecycle

All nodes use the same general-purpose text-embedding model (configured in
`ExtractionOptions.EmbeddingModel`; default: `"nomic-embed-text"` — consistent
with the existing `EmbeddingGeneratorOptions.ModelName` default at
`src/ast/Ast.Embedding/EmbeddingModels.cs` line 119).

| Node origin | When embedded | Input text |
|---|---|---|
| Code nodes | Extraction time (Semantic Indexer) | `HypergraphNode.GetEnhancedEmbeddingText()` |
| Expertise nodes | Write-back time (`RecordExpertiseAsync`) | `"{Kind}: {Title}\n{Body}"` |
| Topic / Cluster nodes | Consolidation time | LLM-generated summary |
| Re-embedding on change | Incremental sync | Same as initial |

Model changes require full re-embedding (`ast memory reindex --reembed`).
Vector index is secondary — recoverable via `RebuildVectorIndexAsync()`.

### 4.8 Snapshot

A logical point-in-time bookmark — not a full copy:

```csharp
public class Snapshot
{
    public required string Id { get; init; }           // "snapshot:{commitSha}"
    public required string CommitSha { get; init; }
    public required int CommitIndex { get; init; }
    public string? ParentSha { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public required SnapshotStats Stats { get; init; }
}

public class SnapshotStats
{
    public int Nodes { get; init; }
    public int Edges { get; init; }
    public int ExpertiseNodes { get; init; }
}
```

---

## 5. Interfaces

All interfaces are C# (.NET 8.0) matching the repository stack.

### 5.1 Extraction Pipeline

**Project:** `Ast.Memory`

```csharp
public interface IExtractionPipeline
{
    IAsyncEnumerable<GraphFact> ExtractAllAsync(
        string repoRoot, ExtractionOptions options,
        CancellationToken ct = default);

    Task<GraphDiff> ExtractIncrementalAsync(
        string repoRoot, string baseCommit, string headCommit,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}

public class ExtractionOptions
{
    public required IReadOnlyList<string> Languages { get; init; }
    public bool EnableLsp { get; init; }
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public long MaxFileSize { get; init; } = 1_048_576;
}

public abstract record GraphFact(string Id, string ContentHash, VersionRange VersionRange);
public record NodeFact(string Id, string ContentHash, VersionRange VersionRange, HypergraphNode Node)
    : GraphFact(Id, ContentHash, VersionRange);
public record EdgeFact(string Id, string ContentHash, VersionRange VersionRange, Hyperedge Edge)
    : GraphFact(Id, ContentHash, VersionRange);

public record GraphDiff(
    IReadOnlyList<GraphFact> Inserts,
    IReadOnlyList<GraphFact> Updates,
    IReadOnlyList<string> Tombstones);

public record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex);
```

### 5.2 Memory Graph Store

**Project:** `Ast.GraphDb` (interface) — extends existing `IGraphStore`

```csharp
public interface IMemoryGraphStore : IGraphStore
{
    Task<Snapshot> ApplyDiffAsync(GraphDiff diff, string commitSha,
        CancellationToken ct = default);

    Task<TraversalResult> TraverseAsync(string startNodeId, TraversalPlan plan,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScoredNode>> VectorSearchAsync(float[] queryVector,
        int topK, NodeFilter? filter = null, CancellationToken ct = default);

    Task<HypergraphNode?> GetNodeAtCommitAsync(string nodeId, string commitSha,
        CancellationToken ct = default);

    Task<IReadOnlyList<HypergraphNode>> FindByNameAsync(string prefix,
        IReadOnlyList<string>? kinds = null, int limit = 20,
        CancellationToken ct = default);

    Task RebuildVectorIndexAsync(CancellationToken ct = default);

    Task<int?> ResolveCommitIndexAsync(string commitSha,
        CancellationToken ct = default);

    Task<int> RegisterCommitAsync(string commitSha, string? parentSha,
        DateTime timestamp, CancellationToken ct = default);
}

public class TraversalPlan
{
    public required string Direction { get; init; } // "outbound" | "inbound" | "both"

    /// Primary edge filter — canonical relation names from §2.2.
    /// Works uniformly for code-derived and memory-only edges.
    public IReadOnlyList<string>? SemanticRelations { get; init; }

    [Obsolete("Use SemanticRelations. Ignored by memory queries.")]
    public IReadOnlyList<HyperedgeType>? EdgeTypes { get; init; }

    public string? LayerFilter { get; init; }
    public int MaxDepth { get; init; } = 3;
    public NodeFilter? Filter { get; init; }
    public int Limit { get; init; } = 100;
}

public class TraversalResult
{
    public required IReadOnlyList<HypergraphNode> Nodes { get; init; }
    public required IReadOnlyList<Hyperedge> Edges { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Paths { get; init; }
}
```

### 5.3 Retrieval Engine

**Project:** `Ast.Query` (extended)

```csharp
public interface IMemoryRetrievalEngine
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query,
        CancellationToken ct = default);
}

public class RetrievalQuery
{
    public required string Text { get; init; }
    public string? Scope { get; init; }
    public RetrievalMode? Mode { get; init; }
    public int TokenBudget { get; init; } = 8_000;
    public bool IncludeExpertise { get; init; } = true;
    public string? AtCommit { get; init; }
}

public enum RetrievalMode
{
    StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall, Hybrid
}

public class RetrievalResult
{
    public required string ContextBlock { get; init; }
    public required int TokenCount { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public required RetrievalMode ResolvedMode { get; init; }
}

public class Citation
{
    public required string NodeId { get; init; }
    public required string CommitSha { get; init; }
    public string? Snippet { get; init; }
}
```

### 5.4 Evolution Engine

**Project:** `Ast.Memory`

```csharp
public interface IEvolutionEngine
{
    Task<string> RecordExpertiseAsync(ExpertiseEntry entry,
        CancellationToken ct = default);

    Task<ConsolidationReport> ConsolidateAsync(string scope,
        CancellationToken ct = default);

    Task<int> DecayStaleExpertiseAsync(string headCommit,
        CancellationToken ct = default);
}

public class ExpertiseEntry
{
    public required string Kind { get; init; }             // "pattern"|"pitfall"|"preference"|"insight"
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<string> AppliesTo { get; init; }
    public string Scope { get; init; } = "exact";          // "exact"|"subtree"
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public required string SourceAgent { get; init; }
}

public class ConsolidationReport
{
    public int Merged { get; init; }
    public int Superseded { get; init; }
    public int Retracted { get; init; }
    public IReadOnlyList<string> NewTopics { get; init; } = [];
}
```

### 5.5 Incremental Sync

**Project:** `Ast.Memory`

```csharp
public interface IIncrementalSync
{
    void Watch(string repoRoot, WatchOptions options);
    void Unwatch(string repoRoot);
    Task<Snapshot> SyncToCommitAsync(string repoRoot, string commitSha,
        CancellationToken ct = default);
}

public class WatchOptions
{
    public int DebounceMs { get; init; } = 1_000;
    public bool AutoDecay { get; init; } = true;
}
```

### 5.6 Agent API / MCP Tool Interface

**Project:** `Ast.Memory/Mcp` + `Ast.Cli`  
**Responsibility:** Expose the memory graph to external agents via the
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) tool surface.
Agent interaction is **purely programmatic** — typed function calls, no query
DSL (per operator decision D2 in tech-spec.md §11).

The MCP layer is a thin adapter: it deserializes MCP tool-call JSON into typed
requests, delegates to the internal C# interfaces (`IMemoryRetrievalEngine`,
`IEvolutionEngine`, `IIncrementalSync`), and serializes results back to MCP
tool-result JSON. No business logic lives in the MCP layer.

#### 5.6.1 MCP Tool Definitions

Five tools are registered via `ServiceCollectionExtensions.AddMemoryMcpTools()`.
Each tool maps 1:1 to an internal interface method.

| MCP Tool Name | Internal Method | Description |
|---|---|---|
| `memory_query` | `IMemoryRetrievalEngine.RetrieveAsync` | Retrieve token-budgeted context for a task description |
| `memory_call_chain` | `IMemoryGraphStore.TraverseAsync` | Trace callers/callees of a symbol to a given depth |
| `memory_find_similar` | `IMemoryGraphStore.VectorSearchAsync` | Find semantically similar code nodes |
| `memory_annotate` | `IEvolutionEngine.RecordExpertiseAsync` | Write an expertise entry (pattern/pitfall/preference/insight) |
| `memory_status` | `IIncrementalSync.SyncToCommitAsync` | Check graph status and trigger sync if needed |

#### 5.6.2 MCP Tool Schemas

```json
{
  "name": "memory_query",
  "description": "Retrieve ranked, token-budgeted context from the agent memory graph.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query":          { "type": "string",  "description": "Natural-language task description or symbol name." },
      "scope":          { "type": "string",  "description": "Optional qualified-name prefix to limit traversal (e.g., 'src/payments')." },
      "mode":           { "type": "string",  "enum": ["structural_drill","call_trace","semantic_search","expertise_recall","hybrid"],
                          "description": "Retrieval mode. Default: hybrid." },
      "tokenBudget":    { "type": "integer", "description": "Max tokens in result. Default: 8000, ceiling: 32000." },
      "includeExpertise": { "type": "boolean", "description": "Include expertise nodes. Default: true." },
      "atCommit":       { "type": "string",  "description": "Optional commit SHA for point-in-time query." }
    },
    "required": ["query"]
  }
}
```

```json
{
  "name": "memory_call_chain",
  "description": "Trace the call chain (callers or callees) of a symbol.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "symbol":    { "type": "string",  "description": "Node ID or qualified name of the target symbol." },
      "direction": { "type": "string",  "enum": ["outbound","inbound","both"], "description": "Traversal direction. Default: both." },
      "depth":     { "type": "integer", "description": "Max traversal depth. Default: 3, max: 10." }
    },
    "required": ["symbol"]
  }
}
```

```json
{
  "name": "memory_find_similar",
  "description": "Find code nodes semantically similar to a query or existing node.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": { "type": "string",  "description": "Text query or node ID to find similar nodes for." },
      "topK":  { "type": "integer", "description": "Number of results. Default: 10, max: 50." },
      "kinds": { "type": "array", "items": { "type": "string" }, "description": "Filter by node kinds (e.g., ['Function','Class'])." }
    },
    "required": ["query"]
  }
}
```

```json
{
  "name": "memory_annotate",
  "description": "Record an expertise entry (pattern, pitfall, preference, or insight) in the memory graph.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "kind":       { "type": "string",  "enum": ["pattern","pitfall","preference","insight"] },
      "title":      { "type": "string",  "description": "Short title for the expertise entry." },
      "body":       { "type": "string",  "description": "Detailed description of the learning." },
      "appliesTo":  { "type": "array",   "items": { "type": "string" }, "description": "Node IDs this expertise applies to." },
      "scope":      { "type": "string",  "enum": ["exact","subtree"], "description": "Scope of applicability. Default: exact." },
      "evidence":   { "type": "array",   "items": {
        "type": "object",
        "properties": {
          "nodeId":    { "type": "string" },
          "commitSha": { "type": "string" },
          "snippet":   { "type": "string" }
        }
      }, "description": "Optional evidence citations." }
    },
    "required": ["kind","title","body","appliesTo"]
  }
}
```

```json
{
  "name": "memory_status",
  "description": "Check memory graph status for a repository and optionally trigger sync.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "repoRoot":   { "type": "string",  "description": "Path to the repository root." },
      "syncToHead": { "type": "boolean", "description": "If true, sync graph to HEAD before returning status. Default: false." }
    },
    "required": ["repoRoot"]
  }
}
```

#### 5.6.3 MCP Response Format

All tools return standard MCP tool-result JSON. The `memory_query` response
wraps a `RetrievalResult` (§5.3):

```json
{
  "content": [
    {
      "type": "text",
      "text": "## Structure\npayments/checkout/\n├── CheckoutService ...\n\n## Call Graph\n...\n\n## Known Expertise\n⚠ PITFALL: ..."
    }
  ],
  "metadata": {
    "tokenCount": 6420,
    "resolvedMode": "hybrid",
    "citationCount": 12,
    "graphCommit": "abc123"
  }
}
```

#### 5.6.4 Internal Class Layout

```
Ast.Memory/
  Mcp/
    MemoryMcpToolRegistration.cs   ← registers 5 tools with MCP discovery
    MemoryMcpHandler.cs            ← dispatches MCP JSON → internal interfaces
    McpSchemas.cs                  ← JSON schema constants for tool definitions
```

`MemoryMcpHandler` composes `IMemoryRetrievalEngine`, `IEvolutionEngine`, and
`IMemoryGraphStore` via constructor injection. It does NOT contain business
logic — only JSON deserialization, delegation, and response formatting.

#### 5.6.5 CLI Commands (non-MCP)

For developer workflows that don't use MCP, the same functionality is exposed
via CLI commands in `Ast.Cli`:

| CLI Command | Maps to |
|---|---|
| `ast memory onboard <repo-root>` | Full extraction + initial sync |
| `ast memory sync [--base-commit <sha>]` | Incremental sync |
| `ast memory query <text> [--budget <tokens>]` | `IMemoryRetrievalEngine.RetrieveAsync` |
| `ast memory callers <symbol> [--depth N]` | `IMemoryGraphStore.TraverseAsync` (inbound) |
| `ast memory callees <symbol> [--depth N]` | `IMemoryGraphStore.TraverseAsync` (outbound) |
| `ast memory annotate --kind <k> --title <t> --body <b> --applies-to <ids>` | `IEvolutionEngine.RecordExpertiseAsync` |
| `ast memory evolve [--scope <prefix>]` | `IEvolutionEngine.ConsolidateAsync` |
| `ast memory reindex [--reembed]` | Full re-extraction with optional re-embedding |
| `ast memory status` | Graph stats, last sync commit, node/edge counts |

---

## 6. End-to-End Sequence Flows

### 6.1 Initial Repository Onboarding

```
Actor: Developer runs `ast memory onboard <repo-root>`.

1. IncrementalSync.SyncToCommitAsync(repoRoot, HEAD)
   ├─ 1a. GraphStore.RegisterCommitAsync(HEAD, parentSha, timestamp)
   │      → assigns monotonic commit_index.
   │
   ├─ 2. ExtractionPipeline.ExtractAllAsync(repoRoot, options)
   │     ├─ 2a. Language Parser (Ast.Csharp / Ast.PowerShell) → AST facts
   │     ├─ 2b. Structure Builder → Structural-layer nodes + edges
   │     ├─ 2c. Call-Chain Analyzer → Call-chain-layer edges
   │     ├─ 2d. Semantic Indexer → embeddings via IEmbeddingGenerator
   │     │      + SIMILAR_TO edges where cosine ≥ SimilarityThreshold
   │     └─ 2e. Fact Normalizer → stable IDs, VersionRange, ContentHash
   │
   ├─ 3. GraphStore.ApplyDiffAsync(diff, HEAD)
   │     ├─ 3a. Insert all nodes and edges
   │     ├─ 3b. Write vectors to vector index (synchronous)
   │     └─ 3c. Create Snapshot record
   │
   └─ 4. EvolutionEngine.ConsolidateAsync("*")
         ├─ 4a. Community detection (Louvain) on Structural + Call-chain
         ├─ 4b. LLM generates summary per community
         └─ 4c. Store Topic + SemanticCluster nodes in Semantic layer

Result: Full graph available, Snapshot created, community summaries populated.
```

### 6.2 Incremental Sync on Commit

```
Trigger: Git post-commit hook or file-system watcher.

1. IncrementalSync detects changed files via git diff.
   ├─ 1a. GraphStore.RegisterCommitAsync(headCommit, baseCommit, ts)

2. ExtractionPipeline.ExtractIncrementalAsync(repo, base, head, changedFiles)
   ├─ 2a. Re-parse changed files only
   ├─ 2b. Diff against current graph → GraphDiff
   └─ 2c. Tombstone removed symbols (VersionRange.Until = head)

3. GraphStore.ApplyDiffAsync(diff, headCommit)
   ├─ 3a. Insert new / update modified nodes (re-embedded vectors)
   ├─ 3b. Tombstone deleted nodes/edges
   └─ 3c. Flag downstream CALLS edges as Stale if signature changed

4. EvolutionEngine.DecayStaleExpertiseAsync(headCommit)
   ├─ 4a. Find expertise with APPLIES_TO targets tombstoned or changed
   └─ 4b. Apply StaleDecayFactor (0.5) or MinorDecayFactor (0.9)

5. [If structural change exceeds threshold]
   └─ EvolutionEngine.ConsolidateAsync(affectedPackage)

Result: Graph updated; stale expertise decayed; summaries refreshed.
Latency target: < 2 s for single-file change on repos ≤ 500K LOC.
```

### 6.3 Agent Retrieves Context

```
Actor: Agent needs context for a task in "payments.checkout".

1. Agent calls RetrievalEngine.RetrieveAsync({
     Text: "How does checkout work? Any known pitfalls?",
     Scope: "payments.checkout",
     TokenBudget: 8000,
     IncludeExpertise: true
   })

2. Query Planner → mode = Hybrid (structural + call-chain + expertise intent)

3. Parallel fan-out:
   ├─ Graph Traverser: StructuralDrill from "payments.checkout", depth 3
   ├─ Graph Traverser: CallTrace forward DFS from public functions, depth 5
   ├─ Graph Traverser: ExpertiseRecall — APPLIES_TO in checkout subtree
   └─ Vector Searcher: ANN top-20, filtered to payments.* namespace

4. Ranker merges results:
   score = 0.30×structural + 0.25×semantic + 0.20×centrality
         + 0.15×recency + 0.10×expertise

5. Budget Controller:
   ├─ Estimate tokens per fragment
   ├─ Collapse low-level functions → parent summaries if over budget
   └─ Drop lowest-ranked until within 8K budget

6. Context Assembler formats:
   ## Structure
   payments/checkout/
   ├── CheckoutService — orchestrates checkout flow
   ├── PriceCalculator — discounts, tax
   └── ...

   ## Call Graph
   CheckoutService.process() → PriceCalculator.compute()
                              → InventoryClient.reserve()
                              → PaymentGateway.charge()

   ## Related Code
   [ranked snippets with file:line]

   ## Known Expertise
   ⚠ PITFALL: PaymentGateway.charge() not idempotent (conf: 0.91)
   ✓ PATTERN: CheckoutService uses saga for rollback (conf: 0.88)

7. Return RetrievalResult { ContextBlock, TokenCount: 6420,
     Citations, ResolvedMode: Hybrid }
```

### 6.4 Agent Writes Back Expertise

```
Actor: Agent records a learning after completing a task.

1. Agent calls EvolutionEngine.RecordExpertiseAsync({
     Kind: "pitfall",
     Title: "Race condition in concurrent checkout",
     Body: "Two concurrent requests to CheckoutService.process() for
            the same cart can double-reserve in InventoryClient.reserve().
            Fix: distributed lock on cart_id before saga.",
     AppliesTo: ["/src/payments/checkout::CheckoutService.process",
                 "/src/payments/inventory::InventoryClient.reserve"],
     Scope: "exact",
     Evidence: [{ NodeId: "...", CommitSha: "abc123", Snippet: "..." }],
     SourceAgent: "copilot-session-42"
   })

2. Evolution Engine:
   ├─ 2a. Create Pitfall node (Confidence = 1.0)
   ├─ 2b. Embed "{Kind}: {Title}\n{Body}" via IEmbeddingGenerator
   ├─ 2c. Write vector to index (synchronous)
   ├─ 2d. Create APPLIES_TO edges to both target nodes
   │       (HyperedgeType = MemoryRelation, SemanticRelation = "APPLIES_TO")
   └─ 2e. Check overlap with existing expertise:
          cosine > SimilarityThreshold → DERIVED_FROM edge + schedule consolidation

3. Return new expertise node ID.

Result: Future retrievals for checkout code surface this pitfall.
```

### 6.5 Cross-Session Learning

```
S1: Agent onboards repo. 0 expertise nodes.
    Completes payments refactor.
    Writes: PATTERN "saga-based rollback" (confidence 1.0)

S2: New session retrieves payments context.
    Sees saga pattern in ## Known Expertise section.
    Uses it to extend checkout correctly.
    Writes: INSIGHT "saga steps must be registered in order"

S3: Code changes land — CheckoutService.process() refactored.
    Incremental Sync triggers decay:
    ├─ Saga pattern: target content_hash changed → conf 1.0 → 0.9 (minor)
    └─ Race pitfall: target unchanged → no decay

S5: Consolidation after PR batch:
    ├─ 3 related expertise nodes found
    ├─ LLM merges → SUPERSEDES edges
    └─ Topic "Payments Saga Orchestration" created

S8: New session gets consolidated, high-confidence expertise.
    Higher-quality completions — memory compounded across sessions.
```

### 6.6 MCP Tool Invocation (External Agent)

```
Actor: An MCP-compatible agent (e.g., Copilot, Cursor) invokes memory_query.

1. Agent sends MCP tool call:
   {
     "name": "memory_query",
     "arguments": {
       "query": "How does checkout work? Any known pitfalls?",
       "scope": "payments.checkout",
       "tokenBudget": 8000,
       "includeExpertise": true
     }
   }

2. MemoryMcpHandler receives the call:
   ├─ 2a. Deserialize JSON → RetrievalQuery (§5.3)
   ├─ 2b. Validate tokenBudget ≤ TokenBudgetCeiling (32K)
   └─ 2c. Delegate to IMemoryRetrievalEngine.RetrieveAsync(query)

3. RetrievalEngine executes (same as §6.3 steps 2–7):
   ├─ Query Planner → mode = Hybrid
   ├─ Parallel fan-out: structural + call-chain + expertise + vector search
   ├─ Ranker merges results using 5-signal scoring model
   ├─ Budget Controller enforces 8K token limit
   └─ Context Assembler formats Markdown

4. MemoryMcpHandler serializes RetrievalResult → MCP tool-result JSON:
   {
     "content": [{ "type": "text", "text": "## Structure\n..." }],
     "metadata": { "tokenCount": 6420, "resolvedMode": "hybrid", ... }
   }

5. Agent receives context block and incorporates it into its LLM prompt.

Result: Agent has structurally-aware, expertise-enriched context
without needing to re-discover the codebase.
```

### 6.7 MCP Expertise Write-Back (External Agent)

```
Actor: Agent records a learning via memory_annotate tool.

1. Agent sends MCP tool call:
   {
     "name": "memory_annotate",
     "arguments": {
       "kind": "pitfall",
       "title": "Race condition in concurrent checkout",
       "body": "Two concurrent requests can double-reserve inventory.",
       "appliesTo": ["/src/payments/checkout::CheckoutService.process"],
       "scope": "exact"
     }
   }

2. MemoryMcpHandler:
   ├─ 2a. Deserialize JSON → ExpertiseEntry (§5.4)
   ├─ 2b. Inject SourceAgent from MCP session context
   └─ 2c. Delegate to IEvolutionEngine.RecordExpertiseAsync(entry)

3. Evolution Engine executes (same as §6.4 steps 2a–2e).

4. MemoryMcpHandler returns MCP result:
   {
     "content": [{ "type": "text", "text": "Expertise recorded: expertise:pitfall:a1b2c3d4" }],
     "metadata": { "expertiseId": "expertise:pitfall:a1b2c3d4" }
   }

Result: Future agents querying checkout code will see this pitfall
in their ## Known Expertise section.
```

---

## 7. Deployment Topology

```
┌────────────────────────────────────────────────────────────┐
│  Local (single-dev)                                        │
│  SQLite graph + in-process HNSW (usearch)                  │
│  File-system watcher for incremental sync                  │
│  All in one CLI process                                    │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│  Team / CI (shared)                                        │
│  Neo4j for graph, Qdrant for vectors                       │
│  Git webhook for sync, gRPC/HTTP for retrieval             │
│  Evolution Engine as async worker (queue-driven)           │
└────────────────────────────────────────────────────────────┘
```

---

## 8. Design Decisions

| Decision | Rationale | Trade-off |
|---|---|---|
| Extend `src/ast/` stack, not greenfield | 970+ tests, proven models | Coupled to `HypergraphNode`/`Hyperedge` |
| Single `Ast.Memory` project with namespaces | Follows repo convention; avoids project sprawl | Larger compilation unit |
| Non-breaking extension via `Metadata` dict | Preserves all existing tests | String-typed keys; mitigated by `MemoryMetadataKeys` constants |
| `HyperedgeType.MemoryRelation` + `SemanticRelation` metadata | One enum value; string identifies specific relation | Not compile-time checked; validated at write time |
| Four layers sharing node namespace | MAGMA: prevents signal entanglement | Cross-layer queries need joins |
| Composition wrappers, not inheritance | `MemoryNode`/`MemoryEdge` compose base types | Extra indirection; no casting needed |
| Dual-axis temporal (commit_index + wall-clock) | Commit SHAs not orderable; integers enable range queries | Requires `commits` table bookkeeping |
| LLM-powered consolidation | Semantic merge needs NL reasoning | LLM cost; batched and async |
| Token-budget-aware retrieval | Finite agent context windows | Summaries lose detail; citations enable drill-down |
| Confidence decay (time + event) | Prevents stale expertise poisoning | Aggressive decay may lose valid insights; tunable |

---

## 9. Glossary

| Term | Definition |
|---|---|
| **GraphFact** | A single node or edge record from the Extraction Pipeline |
| **GraphDiff** | Batch of inserts, updates, tombstones applied atomically |
| **Tombstone** | Soft-delete: `VersionRange.Until` set, record retained for time-travel |
| **Commit Index** | Monotonic integer assigned per commit SHA; enables orderable queries |
| **Community** | Tightly-coupled node cluster from graph partitioning (Louvain) |
| **Expertise Node** | Agent-authored knowledge (Pattern/Pitfall/Preference/Insight) |
| **Snapshot** | Point-in-time bookmark keyed by commit SHA + index |
| **Context Block** | Markdown document from Context Assembler, ready for LLM prompt |
| **SemanticRelation** | Canonical string in `Metadata["SemanticRelation"]` — primary edge filter |
| **Memory-only Edge** | Edge with `HyperedgeType.MemoryRelation`; exists only in Semantic/Expertise layers |

---

## 10. Cross-Document Alignment

This section lists **every known conflict** between this architecture document
and the three sibling documents. Each row states what the sibling says, what
the canonical resolution is (per this document), and the governing section.

When any sibling document and this architecture disagree, **this document
governs** (see document header).

### 10.0 Alignment with tech-spec.md

All previously-pending conflicts between this document and `tech-spec.md` are
**resolved** as of tech-spec.md iteration 5. `tech-spec.md` §12.2 confirms
alignment on: embedding model (D1), cross-branch visibility (D3), decay
formula (D5), composition wrappers (HC8), `HyperedgeType` enum count (44),
`GetEnhancedEmbeddingText()` source-code surface area (R7), and token budget
defaults (HC1). No remaining conflicts.

`tech-spec.md` §11 D2 specifies "purely programmatic" agent interaction via
typed function calls — this architecture fulfills that requirement via the MCP
tool surface defined in §5.6, which maps 1:1 to internal C# interfaces.

### 10.1 Alignment with implementation-plan.md

Implementers following `implementation-plan.md` **MUST** apply the corrections
below. Future iterations of `implementation-plan.md` should incorporate these.

#### 10.1.1 Naming & Type Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| N-1 | Stage 1.1 creates `ExpertiseNote.cs` | Class is named **`ExpertiseEntry`**. `ExpertiseNote` is retired (§2.3). | §2.3, §5.4 |
| N-2 | Stage 1.2 creates `IMemoryStore.cs` | Interface is named **`IMemoryGraphStore`**, extending `IGraphStore`. `IMemoryStore` is retired (§2.3). | §2.3, §5.2 |
| N-3 | Stage 1.2 creates separate `IExpertiseStore.cs` | No separate interface. Expertise write/read methods are part of **`IEvolutionEngine`** (§5.4). `IExpertiseStore` is retired (§2.3). | §2.3, §5.4 |
| N-4 | Stage 5.2 mentions `AgentMemoryService` composing `IExpertiseStore` | Compose `IEvolutionEngine` instead — it subsumes the expertise store role. | §5.4 |

#### 10.1.2 Data Model Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| D-1 | Stage 1.1: `MemoryNode.cs` "extending `HypergraphNode`" (C# inheritance) | `MemoryNode` is a **composition wrapper** with an `Inner: HypergraphNode` property. It does NOT inherit from `HypergraphNode`. | §4.1 |
| D-2 | Stage 1.1: `MemoryEdge.cs` "extending `Hyperedge`" (C# inheritance) | `MemoryEdge` is a **composition wrapper** with an `Inner: Hyperedge` property. It does NOT inherit from `Hyperedge`. | §4.1 |
| D-3 | Stage 1.1: `ExpertiseNote` has `PatternType` enum (Pattern, Pitfall, Preference, Insight) | Enum is named **`ExpertiseKind`** and lives in `Enums.cs`. Values are the same. The class name is `ExpertiseEntry`, field name is `Kind`. | §2.3, §5.4 |
| D-4 | Stage 1.1: `Annotation.cs` with `NodeId`, `Key`, `ValueJson`, `SessionId`, `ValidFrom`, `ValidUntil` | Annotations are not separate entity classes. They are stored as key/value pairs in `HypergraphNode.Metadata`, governed by `AnnotationKeyRegistry`. Agent extension keys use `ext:` prefix. | §4.5 |

#### 10.1.3 ID Format Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| I-1 | Stage 2.1: canonical IDs use `{repo}:{language}:{fqn}:{hash_short}` format | Canonical IDs use **path-based format**: `"{filePath}::{qualifiedName}"` for symbols, `"{relativePath}"` for files/dirs, `"expertise:{kind}:{hash8}"` for expertise nodes. No `{repo}:` or `{language}:` prefix. No `{hash_short}` suffix. | §4.4 |
| I-2 | Stage 2.1: `IdReconciliationTable.cs` backed by `ID_MAPS_TO` edges in Neo4j | ID reconciliation uses the same storage mechanism but edge names and backing can be implementation-plan's choice (file-scope detail). ID *format* must follow §4.4. | §4.4 |

#### 10.1.4 Edge Name Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| E-1 | Stage 2.2: emits `CALLED_BY` as a stored memory edge | **`CALLED_BY` is NOT a stored edge.** It is achieved via reverse traversal of `CALLS`. See retired-names table. | §2.3 |
| E-2 | Stage 2.2: emits `INHERITS` edges | Canonical name is **`EXTENDS`**. `INHERITS` is retired (§2.3). | §2.2, §2.3 |
| E-3 | Stage 2.2: emits `IMPORTS` edges | Canonical name is **`DEPENDS_ON`**. `IMPORTS` is retired (§2.3). | §2.2, §2.3 |
| E-4 | Stage 4.2: `DependencyInvalidator` walks `IMPORTS` and `CALLS` edges | Walk **`DEPENDS_ON`** and `CALLS` edges (same logic, correct name). | §2.2, §2.3 |

#### 10.1.5 Numeric / Threshold Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| T-1 | Stage 3.2 scoring weights: `semantic=0.3` | Canonical weight: **`semantic=0.25`**. Full set: structural=0.30, semantic=0.25, centrality=0.20, recency=0.15, expertise=0.10. | §2.4 |
| T-2 | Stage 3.2 scoring weights: `centrality=0.15` | Canonical weight: **`centrality=0.20`**. | §2.4 |
| T-3 | Stage 3.1: `MemoryQueryOptions.MaxTokens` default = 32K (32768) | The default token budget is **`8,000`** (`TokenBudgetDefault`). The 32K value is the **ceiling** (`TokenBudgetCeiling`), not the default. `MemoryQueryOptions.MaxTokens` should default to 8,000. | §2.4 |
| T-4 | Stage 6.3: decay half-life = 60 days | Canonical half-life: **`DecayHalfLifeDays = 1095`** (3 years). 60 days is far too aggressive — would decay valid insights before they compound. | §2.4 |
| T-5 | Stage 6.3: decay formula `e^(-λ × days)` (exponential) | Canonical formula: **`2^(−age_days / DecayHalfLifeDays) × centrality_boost`** (base-2 exponential with centrality modifier). See §3.5. | §3.5 |

#### 10.1.6 Embedding Model Conflict

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| M-1 | No explicit embedding model specified; defers to tech-spec | Canonical default model is **`"nomic-embed-text"`** — matching the existing `EmbeddingGeneratorOptions.ModelName` default in `src/ast/Ast.Embedding/EmbeddingModels.cs` line 119. Prior iterations of this document incorrectly specified `text-embedding-3-small`; this is now corrected. | §4.7 |

#### 10.1.7 Storage Interface Conflicts

| # | implementation-plan.md says | Canonical resolution (this doc) | Governing section |
|---|---|---|---|
| S-1 | Stage 1.2: `IMemoryStore` has `UpsertAnnotationAsync`, `GetAnnotationsAsync`, `WriteAuditEntryAsync`, `GetAuditLogAsync`, `TombstoneNodeAsync`, `GetNodeAtVersionAsync` | The canonical interface is **`IMemoryGraphStore`** (§5.2). It exposes `ApplyDiffAsync`, `TraverseAsync`, `VectorSearchAsync`, `GetNodeAtCommitAsync`, `FindByNameAsync`, `RebuildVectorIndexAsync`, `ResolveCommitIndexAsync`, `RegisterCommitAsync`. Annotations are Metadata-based, not separate CRUD. Audit is an internal implementation detail, not on the public interface. | §5.2 |
| S-2 | Stage 1.3: `Neo4jMemoryStore` with annotation CRUD and temporal Cypher | Implement **`IMemoryGraphStore`** interface from §5.2. Temporal filtering uses `VersionRange` with `SinceIndex`/`UntilIndex` fields, queried via `GetNodeAtCommitAsync`. | §4.3, §5.2 |

#### 10.1.8 Summary of Required Changes to implementation-plan.md

When `implementation-plan.md` is next updated, these changes should be applied:

1. **Rename** `ExpertiseNote` → `ExpertiseEntry`, `IMemoryStore` → `IMemoryGraphStore` throughout.
2. **Remove** `IExpertiseStore.cs` — merge its methods into `IEvolutionEngine`.
3. **Remove** `Annotation.cs` as a separate model — use `Metadata` dict with `AnnotationKeyRegistry`.
4. **Change** `MemoryNode`/`MemoryEdge` from inheritance to composition wrappers.
5. **Change** canonical ID format from `{repo}:{language}:{fqn}:{hash_short}` to `{filePath}::{qualifiedName}`.
6. **Replace** retired edge names: `CALLED_BY` → reverse traversal, `INHERITS` → `EXTENDS`, `IMPORTS` → `DEPENDS_ON`.
7. **Fix** scoring weights to match §2.4 table.
8. **Fix** `MaxTokens` default from 32K to 8,000.
9. **Fix** decay half-life from 60 days to 1,095 days and formula to base-2.
10. **Align** `IMemoryGraphStore` method signatures to §5.2.
11. **Add** MCP tool definitions matching §5.6 — implementation-plan Stage 5.2
    mentions `MemoryMcpTool.cs` and `MemoryMcpHandler.cs` but does not specify
    tool names or schemas. Canonical tool definitions are in §5.6.1–5.6.2.
12. **Use** `"nomic-embed-text"` as the default embedding model, consistent
    with the existing `EmbeddingGeneratorOptions.ModelName` default in
    `src/ast/Ast.Embedding/EmbeddingModels.cs`.

### 10.2 Alignment with e2e-scenarios.md

The e2e-scenarios document uses presentation-friendly names that diverge from
canonical vocabulary. These are acceptable in Gherkin scenarios (which test
behavior, not internal names) but implementers must map them correctly.

| # | e2e-scenarios.md says | Canonical equivalent (this doc) | Notes |
|---|---|---|---|
| E2E-1 | Scenario 4.1 uses `called_by` as an edge label in result tables | `called_by` is a **reverse traversal** of `CALLS`, not a stored edge (§2.3). Scenario is behaviorally correct — the retrieval engine returns 1-hop callers via reverse `CALLS` traversal. No stored `CALLED_BY` edge needed. | Scenario OK; implementation uses reverse traversal. |
| E2E-2 | Scenario 2.1 uses `direct_call` as `edge_type` | Canonical edge name is **`CALLS`** with `Confidence = 1.0` (§2.2). `direct_call` is a presentation alias. | Implementers emit `CALLS` edges. |
| E2E-3 | Scenario 2.3 uses `polymorphic` as `edge_type` | Canonical edge is **`CALLS`** with `Confidence = 0.5` (§2.2, §3.1 Call-Chain Analyzer). The `polymorphic` label maps to a `CALLS` edge with reduced confidence and `Metadata["IsPolymorphic"] = true`. | Implementers emit `CALLS` edges. |
| E2E-4 | Scenario 2.5 uses `recursive` as `edge_type` | Canonical: a self-referencing **`CALLS`** edge (same node in both `caller` and `callee` roles). | Implementers emit `CALLS` edge. |
| E2E-5 | Scenario 2.7 uses `service_call` as `edge_type` | Cross-service RPC is **out of scope** for MVP (tech-spec §3 O1 — runtime/dynamic tracing excluded). If implemented later, it would be a `CALLS` edge with `Metadata["CrossService"] = true` and `Protocol` metadata. | Deferred per O1. |
| E2E-6 | Scenario 1.2/2.4/4.1 uses unqualified symbol IDs like `AuthService.login` | Canonical node IDs use **path-based format** `"{filePath}::{qualifiedName}"` (§4.4), e.g., `/src/auth/login::AuthService.login`. Scenario table entries are abbreviations for readability. | Implementers use full path-based IDs. |
| E2E-7 | Scenario 2.4 uses `ext::` prefix for boundary nodes | Canonical format for external/boundary nodes is not yet defined (external dependencies are out-of-repo). Recommended: `"ext:{package}:{qualifiedName}"` (e.g., `"ext:@aws-sdk/client-kms:KMS.encrypt"`). | Aligns with §4.4 ID philosophy. |
| E2E-8 | Scenario 6.4 describes cross-repo memory transfer | **Out of scope** per tech-spec §3 O2 (multi-repository federation excluded). This scenario is aspirational and should be tagged `@deferred`. | No conflict — future work. |
| E2E-9 | Scenario 3.4 describes conflict errors on concurrent writes | Architecture specifies **SQLite WAL mode** for local (concurrent reads, serialized writes via in-process mutex) and **Neo4j** for team mode (native concurrency). No "conflict error" is raised — writes are serialized. Scenario should be adjusted to test write serialization, not optimistic-concurrency conflicts. | Scenario needs update. |

### 10.3 Alignment with tech-spec.md

All conflicts resolved as of tech-spec.md iteration 5 (see §10.0 above).
Tech-spec.md §12.2–12.3 contains its own alignment tables that cross-reference
this document. Coordination items:

| # | Item | Status |
|---|---|---|
| TS-1 | tech-spec.md §12.3 lists 12 implementation-plan conflicts (IP1–IP12) | ✅ Consistent — same conflicts listed here in §10.1. Both documents agree on canonical resolutions. |
| TS-2 | tech-spec.md §11 D2 specifies "purely programmatic" agent API | ✅ Fulfilled by §5.6 MCP tool definitions (5 typed tools, no query DSL). |
| TS-3 | tech-spec.md §11 D1 specifies general-purpose embedding model | ✅ Fulfilled by §4.7 (all nodes use same general-purpose model). |
| TS-4 | tech-spec.md §12.2 noted `ExtractionOptions.EmbeddingModel` defaults to `"text-embedding-3-small"` in architecture.md | ✅ **Fixed in iteration 12.** Architecture.md §4.7 and §5.1 now default to `"nomic-embed-text"` — matching the actual codebase defaults in `EmbeddingGeneratorOptions.ModelName` (`src/ast/Ast.Embedding/EmbeddingModels.cs:119`) and `EmbeddingConfiguration.ModelName` (`src/ast/Ast.Core/AstConfiguration.cs:96`). Both are general-purpose per D1. |