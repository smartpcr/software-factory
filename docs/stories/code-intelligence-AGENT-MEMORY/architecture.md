# Agent Memory — Architecture

## 1. Problem Statement

Coding agents operate statelessly. Every session begins cold: the agent
re-discovers repository structure, call chains, coding patterns, and
domain conventions from scratch. Prior successes and failures are
forgotten. The cost is measured in wasted tokens, repeated mistakes, and
an inability to accumulate expertise across tasks.

**Agent Memory** is a graph-based memory system that builds a persistent
knowledge graph on top of repository structure and call-chain context.
It records agent interactions as episodes, consolidates them into
durable expertise, and retrieves relevant context subgraphs so that
agents can learn and get smarter over time.

### Design Principles

| # | Principle | Rationale |
|---|-----------|-----------|
| 1 | **Evidence over assertion** | Expertise is promoted only when backed by sufficient episodes with measurable outcomes. |
| 2 | **Version-aware memory** | Every fact is scoped to a code state; stale knowledge is surfaced, not silently served. |
| 3 | **Probabilistic edges** | Static analysis is fallible. Edges carry confidence and provenance so consumers can weight them. |
| 4 | **Repo-local by default** | Memory is isolated per repository. Only abstracted, de-identified patterns cross repo boundaries. |
| 5 | **Token-budget-first retrieval** | The context assembler never returns more than the caller can consume. |
| 6 | **Measure before trusting** | An evaluation harness measures whether memory actually improves agent outcomes. |

---

## 2. Components and Responsibilities

### 2.1 Repo Structure Indexer

Parses a repository into a hierarchical structural graph.

| Responsibility | Detail |
|----------------|--------|
| File-tree walk | Enumerate files, directories, and packages from the repository root. |
| AST parsing | Use tree-sitter grammars (with language-specific extensions) to extract symbols: classes, functions, interfaces, variables, type aliases. |
| Metadata extraction | Capture visibility, docstrings, type signatures, line ranges, file checksums. |
| Incremental re-index | On file change events (or git diff), re-parse only affected files and update the graph. |
| Stable identity | Assign symbol IDs from `(repo, file_path, qualified_name, kind, language)` tuples so renames and moves can be detected. |

**Outputs:** `Repository`, `Package`, `Module`, `Symbol` nodes and
`CONTAINS` edges written to the Graph Store.

### 2.2 Call-Chain Analyzer

Builds cross-cutting edges on top of the structural graph.

| Responsibility | Detail |
|----------------|--------|
| Reference extraction | Walk the AST of each symbol body to find identifiers that reference other symbols. |
| Call resolution | Resolve call targets. Prefer compiler/LSP index when available; fall back to heuristic tree-sitter name matching. |
| Import tracking | Record module-to-module import dependencies. |
| Inheritance / implementation | Detect class hierarchy and interface implementation edges. |
| Edge annotation | Every edge carries `confidence` (0.0–1.0), `analysis_backend` (`lsp` / `tree-sitter` / `heuristic`), and `resolution_kind` (`declared` / `resolved` / `possible`). |

**Outputs:** `CALLS`, `REFERENCES`, `IMPORTS`, `INHERITS`, `IMPLEMENTS`
edges written to the Graph Store.

> **Scope control (v1):** Data-flow analysis (`DATA_FLOWS_TO`) is
> deferred. V1 ships containment, imports, references, calls, and
> inheritance — the edges with the highest confidence-to-cost ratio.

### 2.3 Episode Recorder

Captures each agent task session as a first-class graph entity.

| Responsibility | Detail |
|----------------|--------|
| Session capture | Record the full agent interaction: task description, hypothesis/plan, attempted actions, tools invoked, files read/written, errors encountered. |
| Outcome classification | Tag the episode as `success`, `failure`, `partial`, or `abandoned`, with supporting evidence (test results, CI status, human acceptance signal). |
| Graph linkage | Create `TOUCHED_IN` edges from the episode to every `Symbol` and `Module` the agent interacted with, scoped to the commit at interaction time. |
| Trace management | Store full interaction traces temporarily (configurable retention, default 30 days). Generate a durable normalized summary for long-term use. |

**Outputs:** `Episode` nodes, `TOUCHED_IN` edges, and associated
`EpisodeTrace` records written to the Graph Store.

### 2.4 Memory Consolidator

Converts raw episodes into durable, evidence-backed expertise.

The consolidator is a single service but operates as a four-stage
pipeline:

```
┌──────────────┐    ┌────────────────┐    ┌─────────────────┐    ┌─────────────────────┐
│ Normalize    │───▶│ Mine Patterns  │───▶│ Promote / Merge │───▶│ Decay / Revalidate  │
│ episodes     │    │                │    │ expertise       │    │ / Archive           │
└──────────────┘    └────────────────┘    └─────────────────┘    └─────────────────────┘
```

| Stage | Responsibility |
|-------|----------------|
| **Normalize** | Deduplicate, canonicalize symbol references, align episodes to current HEAD. |
| **Mine** | Group episodes by code area and task type. Identify recurring strategies, including anti-patterns (negative expertise). Require a minimum evidence threshold (default: 3 corroborating episodes) before proposing a pattern. |
| **Promote** | Create or update `Expertise` nodes. Record `support_count`, `opposition_count`, `success_rate`, `min_code_version`, `last_verified_at`. Link via `DERIVED_FROM` (to episodes) and `APPLIES_TO` (to code nodes). |
| **Decay / Revalidate** | Reduce confidence of expertise not reinforced within a configurable window. Archive expertise whose target code has been deleted or substantially refactored. Revalidate expertise when underlying code changes significantly. |

**Outputs:** `Expertise` nodes, `DERIVED_FROM` edges, `APPLIES_TO`
edges.

### 2.5 Context Assembler

Given a task, retrieves the most relevant subgraph and packages it for
the agent's context window.

Retrieval is a three-phase pipeline:

```
┌──────────────────┐    ┌──────────────┐    ┌──────────────────┐
│ 1. Candidate     │───▶│ 2. Rerank    │───▶│ 3. Package       │
│    Generation    │    │              │    │    & Serialize    │
└──────────────────┘    └──────────────┘    └──────────────────┘
```

| Phase | Detail |
|-------|--------|
| **Candidate generation** | Multiple channels fire in parallel: (a) keyword/symbol match against the structural graph, (b) semantic embedding similarity against episode summaries and expertise, (c) structural neighborhood expansion (1–2 hops along call/import edges), (d) recency-weighted episode lookup. |
| **Reranking** | Score candidates on a weighted combination of structural proximity, semantic relevance, temporal recency, episode frequency, expertise confidence, and freshness relative to HEAD. Deduplicate overlapping subgraphs. |
| **Package & serialize** | Prune the ranked subgraph to fit the caller's token budget. Serialize as structured markdown or JSON. Include provenance metadata (confidence, source episode IDs, code version) so the agent can weight the information. |

**Outputs:** A serialized context payload (structured markdown or JSON)
returned to the agent runtime.

### 2.6 Graph Store

Persistent storage layer for all nodes, edges, and metadata.

| Responsibility | Detail |
|----------------|--------|
| Node/edge CRUD | Standard create, read, update, delete for all node and edge types. |
| Graph traversal | Multi-hop neighborhood queries, shortest-path, subgraph extraction. |
| Temporal scoping | Edges and nodes carry `valid_from_commit` / `valid_to_commit` fields. Queries can request the graph "as of" a given commit. |
| Full-text index | Inverted index on symbol names, docstrings, episode summaries, and expertise descriptions for keyword search. |
| Vector index | Embedding index on episode summaries and expertise descriptions for semantic similarity search. |
| Retention management | Automated archival of expired episode traces; compaction of stale graph snapshots. |

**V1 implementation:** SQLite with adjacency tables (nodes table, edges
table, full-text via FTS5, vector index via `sqlite-vec` or an
in-process HNSW library). This avoids operational overhead. The schema
is designed so migration to a dedicated graph database (Neo4j, etc.) is
straightforward if traversal performance demands it.

### 2.7 Evaluation Harness

Measures whether memory improves agent outcomes.

| Metric | Description |
|--------|-------------|
| Retrieval precision | Fraction of retrieved context nodes that the agent actually used. |
| Task success delta | Success rate with memory vs. without (A/B on held-out tasks). |
| Stale-memory error rate | Fraction of retrieved expertise that references code no longer at HEAD. |
| Latency overhead | Wall-clock time added by context assembly. |
| Token efficiency | Ratio of useful context tokens to total budget consumed. |

---

## 3. Data Model

### 3.1 Node Types

#### Repository

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | Stable identifier (e.g., `github.com/org/repo`). |
| `name` | `TEXT` | Human-readable repository name. |
| `url` | `TEXT` | Clone URL. |
| `default_branch` | `TEXT` | Main branch name. |
| `last_indexed_commit` | `TEXT` | SHA of the most recently indexed commit. |

#### Package

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | `(repo_id, package_path)` composite. |
| `name` | `TEXT` | Package/directory name. |
| `path` | `TEXT` | Relative path from repo root. |
| `repo_id` | `TEXT FK` | Owning repository. |

#### Module

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | `(repo_id, file_path)` composite. |
| `name` | `TEXT` | File name. |
| `file_path` | `TEXT` | Relative path from repo root. |
| `language` | `TEXT` | Detected language. |
| `checksum` | `TEXT` | Content hash for change detection. |
| `line_count` | `INT` | Total lines. |

#### Symbol

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | `(repo_id, file_path, qualified_name, kind)` composite. |
| `name` | `TEXT` | Unqualified symbol name. |
| `qualified_name` | `TEXT` | Fully qualified name within the module. |
| `kind` | `TEXT` | One of: `class`, `function`, `method`, `interface`, `variable`, `type_alias`, `enum`, `constant`. |
| `signature` | `TEXT` | Type signature or parameter list. |
| `visibility` | `TEXT` | `public`, `private`, `protected`, `internal`. |
| `docstring` | `TEXT` | Extracted documentation. |
| `start_line` | `INT` | Start line in source file. |
| `end_line` | `INT` | End line in source file. |
| `module_id` | `TEXT FK` | Containing module. |

#### Episode

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | UUID. |
| `repo_id` | `TEXT FK` | Repository context. |
| `commit_sha` | `TEXT` | Commit at time of interaction. |
| `agent_id` | `TEXT` | Identifier of the agent instance. |
| `task_description` | `TEXT` | What the agent was asked to do. |
| `hypothesis` | `TEXT` | Agent's plan or hypothesis before acting. |
| `actions_taken` | `TEXT (JSON)` | Ordered list of actions with parameters and results. |
| `errors_encountered` | `TEXT (JSON)` | Errors, stack traces, failure signals. |
| `outcome` | `TEXT` | `success` / `failure` / `partial` / `abandoned`. |
| `outcome_evidence` | `TEXT` | Test results, CI status, human feedback. |
| `summary` | `TEXT` | LLM-generated normalized summary (durable). |
| `started_at` | `TIMESTAMP` | Session start. |
| `ended_at` | `TIMESTAMP` | Session end. |
| `trace_expires_at` | `TIMESTAMP` | When the full trace is eligible for archival. |

#### Expertise

| Field | Type | Description |
|-------|------|-------------|
| `id` | `TEXT PK` | UUID. |
| `repo_id` | `TEXT FK NULL` | `NULL` for cross-repo transferable expertise. |
| `area_path` | `TEXT` | Code area this expertise applies to (e.g., `src/auth`). |
| `pattern_type` | `TEXT` | `strategy` / `anti_pattern` / `convention` / `prerequisite`. |
| `description` | `TEXT` | Natural-language description of the pattern or rule. |
| `preconditions` | `TEXT` | When this expertise applies. |
| `confidence` | `REAL` | 0.0–1.0, decayed over time. |
| `support_count` | `INT` | Number of corroborating episodes. |
| `opposition_count` | `INT` | Number of contradicting episodes. |
| `success_rate` | `REAL` | Outcome success rate among supporting episodes. |
| `min_evidence` | `INT` | Minimum episodes required before this was promoted (default 3). |
| `derived_from_commit` | `TEXT` | Earliest commit SHA in the evidence base. |
| `last_verified_commit` | `TEXT` | Most recent commit where this expertise was confirmed. |
| `last_reinforced_at` | `TIMESTAMP` | Last time a new episode reinforced this. |
| `created_at` | `TIMESTAMP` | Creation time. |

### 3.2 Edge Types

| Edge | Source → Target | Key Fields | Description |
|------|-----------------|------------|-------------|
| `CONTAINS` | Package → Module, Module → Symbol | — | Hierarchical containment. |
| `CALLS` | Symbol → Symbol | `confidence`, `analysis_backend`, `resolution_kind` | Function/method call. |
| `REFERENCES` | Symbol → Symbol | `confidence`, `analysis_backend` | Name reference (read/use without call). |
| `IMPORTS` | Module → Module | `import_path`, `is_reexport` | Module-level import dependency. |
| `INHERITS` | Symbol → Symbol | — | Class inheritance. |
| `IMPLEMENTS` | Symbol → Symbol | — | Interface implementation. |
| `TOUCHED_IN` | Episode → Symbol | `interaction_type` (`read` / `write` / `debug`) | Which symbols an episode involved. |
| `TOUCHED_MODULE` | Episode → Module | `interaction_type` | Which modules an episode involved. |
| `DERIVED_FROM` | Expertise → Episode | — | Provenance link. |
| `APPLIES_TO` | Expertise → Symbol / Module / Package | — | Where expertise is relevant. |

All edges carry `valid_from_commit` and `valid_to_commit` for temporal
scoping. Code-structural edges (`CALLS`, `REFERENCES`, `IMPORTS`,
`INHERITS`, `IMPLEMENTS`) additionally carry `confidence` (0.0–1.0) and
`analysis_backend` (`lsp` / `tree-sitter` / `heuristic`).

### 3.3 Memory Isolation Model

```
┌─────────────────────────────────────────────────────┐
│                   Global Memory                     │
│  Transferable expertise only (repo_id = NULL).      │
│  No repo-specific identifiers. Abstracted patterns. │
│  Example: "When fixing a race condition in a        │
│  producer-consumer queue, check the lock ordering." │
├──────────────┬──────────────┬───────────────────────┤
│  Repo A      │  Repo B      │  Repo C              │
│  Local graph │  Local graph │  Local graph          │
│  + episodes  │  + episodes  │  + episodes           │
│  + expertise │  + expertise │  + expertise           │
└──────────────┴──────────────┴───────────────────────┘
```

Cross-repo expertise is **promoted** from repo-local expertise only when:
1. The pattern is language-general or tooling-general (not tied to specific APIs).
2. It has been observed in ≥ 2 distinct repositories.
3. All repo-specific identifiers (file paths, symbol names, internal APIs) are stripped.

---

## 4. Interfaces Between Components

### 4.1 Internal Interfaces

```
┌───────────────────┐          ┌───────────────────┐
│  Repo Structure   │──write──▶│                   │
│  Indexer          │          │                   │
├───────────────────┤          │                   │
│  Call-Chain       │──write──▶│   Graph Store     │
│  Analyzer         │          │                   │
├───────────────────┤          │                   │
│  Episode          │──write──▶│                   │
│  Recorder         │          │                   │
├───────────────────┤          │                   │
│  Memory           │──r/w────▶│                   │
│  Consolidator     │          │                   │
├───────────────────┤          │                   │
│  Context          │──read───▶│                   │
│  Assembler        │          │                   │
└───────────────────┘          └───────────────────┘
```

#### Graph Store Write API

```
interface GraphWriter {
  upsertNode(type: NodeType, data: Record<string, any>): NodeId
  upsertEdge(type: EdgeType, source: NodeId, target: NodeId, attrs: Record<string, any>): EdgeId
  deleteNode(id: NodeId): void
  deleteEdge(id: EdgeId): void
  bulkUpsert(operations: UpsertOp[]): void
  setValidityRange(edgeId: EdgeId, fromCommit: string, toCommit: string | null): void
}
```

#### Graph Store Read API

```
interface GraphReader {
  getNode(id: NodeId): Node | null
  getNeighbors(id: NodeId, edgeTypes: EdgeType[], direction: "in" | "out" | "both", hops: number): Subgraph
  findNodes(query: NodeQuery): Node[]          // structured field match
  searchText(text: string, nodeTypes: NodeType[]): ScoredNode[]   // FTS
  searchSemantic(embedding: float[], nodeTypes: NodeType[], topK: number): ScoredNode[]  // vector
  getSubgraphAsOf(nodeIds: NodeId[], commit: string): Subgraph    // temporal query
}
```

#### Context Assembler Output Interface

```
interface ContextPayload {
  structural_context: {
    modules: ModuleSummary[]
    symbols: SymbolDetail[]
    call_chains: CallChain[]
  }
  expertise: {
    strategies: ExpertiseEntry[]
    anti_patterns: ExpertiseEntry[]
    conventions: ExpertiseEntry[]
  }
  related_episodes: {
    similar_tasks: EpisodeSummary[]
    recent_in_area: EpisodeSummary[]
  }
  metadata: {
    repo_id: string
    head_commit: string
    token_budget_used: number
    token_budget_total: number
    retrieval_latency_ms: number
  }
}
```

### 4.2 External Interfaces

| Interface | Consumer | Protocol | Description |
|-----------|----------|----------|-------------|
| Agent Runtime → Episode Recorder | Agent orchestrator | Function call / event | Push episode data on task start, action, and completion. |
| Agent Runtime → Context Assembler | Agent orchestrator | Function call | Request assembled context given a task description and token budget. |
| Repository Events → Repo Structure Indexer | Git hook / file watcher / CI webhook | Webhook / event | Trigger re-indexing on push, merge, or file change. |
| Memory Consolidator (scheduler) | Cron / task queue | Timer | Periodic consolidation (configurable interval, default 1 hour). |
| Evaluation Harness → Graph Store | Offline analysis | Read-only queries | Compute metrics over stored episodes and expertise. |

---

## 5. End-to-End Sequence Flows

### 5.1 Flow: Repository Ingestion (First Index)

```
User                Indexer              Analyzer             Graph Store
 │                    │                    │                      │
 │─ configure repo ──▶│                    │                      │
 │                    │─ clone/access ────▶│                      │
 │                    │─ walk file tree ──▶│                      │
 │                    │                    │                      │
 │                    │ for each file:     │                      │
 │                    │─ tree-sitter parse │                      │
 │                    │─ extract symbols ──│                      │
 │                    │─ bulkUpsert ───────│─────────────────────▶│
 │                    │  (Repo, Pkg,       │                      │
 │                    │   Module, Symbol,  │                      │
 │                    │   CONTAINS edges)  │                      │
 │                    │                    │                      │
 │                    │─ hand off ────────▶│                      │
 │                    │                    │─ resolve calls ─────▶│
 │                    │                    │─ resolve imports ───▶│
 │                    │                    │─ resolve inheritance▶│
 │                    │                    │─ bulkUpsert ────────▶│
 │                    │                    │  (CALLS, IMPORTS,    │
 │                    │                    │   INHERITS, etc.     │
 │                    │                    │   with confidence)   │
 │                    │                    │                      │
 │                    │─ set last_indexed_commit ────────────────▶│
 │◀── indexing complete──────────────────────────────────────────│
```

### 5.2 Flow: Incremental Re-Index

```
Git Push Event       Indexer              Analyzer             Graph Store
 │                    │                    │                      │
 │─ webhook ─────────▶│                    │                      │
 │                    │─ git diff old..new │                      │
 │                    │                    │                      │
 │                    │ for each changed file:                    │
 │                    │─ re-parse AST ────▶│                      │
 │                    │─ diff old vs new symbols                  │
 │                    │─ upsert changed ──│─────────────────────▶│
 │                    │─ delete removed ──│─────────────────────▶│
 │                    │─ set valid_to on stale edges ───────────▶│
 │                    │                    │                      │
 │                    │─ hand off ────────▶│                      │
 │                    │                    │─ re-resolve edges ──▶│
 │                    │                    │─ upsert new edges ──▶│
 │                    │                    │  (with valid_from =  │
 │                    │                    │   new commit)        │
 │                    │─ update last_indexed_commit ─────────────▶│
```

### 5.3 Flow: Agent Task with Memory-Augmented Context

```
Agent Runtime        Context Assembler    Graph Store          Agent LLM
 │                    │                    │                      │
 │─ requestContext ──▶│                    │                      │
 │  (task_desc,       │                    │                      │
 │   token_budget,    │                    │                      │
 │   repo_id)         │                    │                      │
 │                    │                    │                      │
 │                    │── PHASE 1: Candidate Generation ──       │
 │                    │─ searchText ──────▶│                      │
 │                    │─ searchSemantic ──▶│                      │
 │                    │─ findExpertise ───▶│                      │
 │                    │─ findEpisodes ────▶│                      │
 │                    │◀─ candidate sets ──│                      │
 │                    │                    │                      │
 │                    │── PHASE 2: Expand & Rerank ──            │
 │                    │─ getNeighbors ────▶│  (1-2 hop expand)   │
 │                    │◀─ subgraphs ───────│                      │
 │                    │─ score(structural, │                      │
 │                    │   semantic, recency│                      │
 │                    │   frequency,       │                      │
 │                    │   confidence,      │                      │
 │                    │   freshness)       │                      │
 │                    │─ deduplicate       │                      │
 │                    │                    │                      │
 │                    │── PHASE 3: Package ──                    │
 │                    │─ prune to budget   │                      │
 │                    │─ serialize         │                      │
 │                    │                    │                      │
 │◀─ ContextPayload ─│                    │                      │
 │                    │                    │                      │
 │─ inject context ───────────────────────────────────────────── ▶│
 │─ send task + context ─────────────────────────────────────── ▶│
 │◀─ agent response ────────────────────────────────────────────│
```

### 5.4 Flow: Episode Recording

```
Agent Runtime        Episode Recorder     Graph Store
 │                    │                    │
 │─ onTaskStart ─────▶│                    │
 │  (task_desc,       │                    │
 │   agent_id,        │─ create Episode ──▶│
 │   repo_id,         │  (status=active)   │
 │   commit_sha)      │                    │
 │                    │                    │
 │─ onAction ────────▶│                    │
 │  (action, params,  │─ append to         │
 │   result, files)   │  actions_taken     │
 │                    │─ upsert TOUCHED_IN▶│
 │                    │                    │
 │─ onError ─────────▶│                    │
 │  (error, trace)    │─ append to         │
 │                    │  errors_encountered│
 │                    │                    │
 │─ onTaskEnd ───────▶│                    │
 │  (outcome,         │─ finalize Episode ▶│
 │   evidence)        │  (outcome, summary,│
 │                    │   ended_at,        │
 │                    │   trace_expires_at)│
 │                    │                    │
```

### 5.5 Flow: Memory Consolidation

```
Scheduler            Consolidator         Graph Store          LLM (summarizer)
 │                    │                    │                      │
 │─ trigger ─────────▶│                    │                      │
 │                    │                    │                      │
 │                    │── Stage 1: Normalize ──                  │
 │                    │─ query recent ────▶│                      │
 │                    │  unconsolidated    │                      │
 │                    │  episodes          │                      │
 │                    │◀─ episodes ────────│                      │
 │                    │─ canonicalize      │                      │
 │                    │  symbol refs       │                      │
 │                    │─ align to HEAD     │                      │
 │                    │                    │                      │
 │                    │── Stage 2: Mine ──                       │
 │                    │─ group by area +   │                      │
 │                    │  task type         │                      │
 │                    │─ identify clusters │                      │
 │                    │  with ≥ min_evidence                      │
 │                    │  (default 3)       │                      │
 │                    │                    │                      │
 │                    │── Stage 3: Promote ──                    │
 │                    │  for each cluster: │                      │
 │                    │─ summarize ────────│──────────────────── ▶│
 │                    │◀─ pattern desc ────│──────────────────── │
 │                    │─ check existing    │                      │
 │                    │  expertise ───────▶│                      │
 │                    │◀─ matches ─────────│                      │
 │                    │─ upsert Expertise ▶│                      │
 │                    │  (update counts,   │                      │
 │                    │   confidence,      │                      │
 │                    │   success_rate)    │                      │
 │                    │─ upsert DERIVED_FROM, APPLIES_TO ──────▶│
 │                    │                    │                      │
 │                    │── Stage 4: Decay ──                      │
 │                    │─ query stale ─────▶│                      │
 │                    │  expertise         │                      │
 │                    │─ reduce confidence │                      │
 │                    │  for unreinforced  │                      │
 │                    │─ archive expired ─▶│                      │
 │                    │─ revalidate against│                      │
 │                    │  current HEAD ────▶│                      │
```

---

## 6. Cross-Cutting Concerns

### 6.1 Temporal Versioning

Every structural node and edge is scoped to a commit range:

- `valid_from_commit`: the commit SHA where this fact was first observed.
- `valid_to_commit`: the commit SHA where this fact was invalidated
  (`NULL` means still valid at HEAD).

Queries default to HEAD but can request any historical commit. The
Context Assembler always checks freshness: if expertise was last
verified against a commit more than N commits behind HEAD (configurable,
default 50), it is flagged as `potentially_stale` in the payload.

### 6.2 Retention and Lifecycle

| Entity | Retention Policy |
|--------|-----------------|
| Episode traces (full) | 30 days (configurable), then archived to cold storage. |
| Episode summaries | Permanent (compact, low cost). |
| Structural graph snapshots | Retained for the last N commits (default 100). Older snapshots compacted. |
| Expertise nodes | Permanent while `confidence > 0.1`. Below threshold → archived. |
| Stale edges (`valid_to_commit` set) | Pruned after 90 days unless referenced by active expertise. |

### 6.3 Negative Memory (Anti-Patterns)

Expertise nodes with `pattern_type = 'anti_pattern'` encode lessons
from failures:

- "Do not use raw SQL string concatenation in `src/db/` — use the query
  builder (3 failures recorded)."
- "Avoid mocking the entire HTTP client in `tests/integration/` — use
  the test server fixture instead."

Anti-patterns are surfaced in the context payload alongside positive
strategies, clearly labeled so the agent can avoid known pitfalls.

### 6.4 Analysis Backend Strategy

The Call-Chain Analyzer supports multiple backends per language:

| Priority | Backend | Trade-offs |
|----------|---------|------------|
| 1 (preferred) | **LSP / compiler index** | High accuracy, requires a running language server. |
| 2 | **Tree-sitter + scope resolution** | Good for containment and local calls; struggles with dynamic dispatch, DI, reflection. |
| 3 | **Heuristic name matching** | Low confidence; useful for dynamic languages as a fallback. |

Edges record which backend produced them. The Context Assembler can
filter or weight by backend when precision matters.

### 6.5 Error Handling

| Failure Mode | Mitigation |
|-------------|------------|
| Tree-sitter parse failure | Log warning, skip file, continue indexing. File is marked as `parse_failed` in the module node. |
| LSP server crash | Fall back to tree-sitter backend. Retry LSP on next indexing cycle. |
| Consolidator LLM timeout | Retry with exponential backoff. After 3 failures, defer the batch to the next cycle. |
| Graph Store write conflict | Last-writer-wins for structural nodes (idempotent upsert). Episodes are append-only. |
| Stale expertise served | Flagged in context payload. Agent warned via `potentially_stale` marker. |

---

## 7. Technology Choices (V1)

| Concern | Choice | Rationale |
|---------|--------|-----------|
| Graph storage | SQLite + adjacency tables | Zero-ops, embeddable, sufficient for single-repo scale. Migration path to Neo4j exists. |
| Full-text search | SQLite FTS5 | Built-in, fast, supports ranking. |
| Vector search | `sqlite-vec` or in-process HNSW | Avoids external service dependency. |
| AST parsing | tree-sitter | Multi-language, incremental, battle-tested. |
| LSP integration | Language-specific LSP servers (tsserver, rust-analyzer, pylsp) | High-accuracy call resolution for supported languages. |
| Embedding model | Sentence-transformer (local) or API-based | Configurable; local for latency, API for quality. |
| Summarization LLM | Configurable (local or API) | Used only in the consolidator, not on the hot path. |
| Scheduling | OS-level cron or in-process timer | Consolidation is not latency-sensitive. |

---

## 8. Future Considerations (Out of Scope for V1)

| Area | Description |
|------|-------------|
| Data-flow analysis | Fine-grained `DATA_FLOWS_TO` edges for selected languages. |
| Multi-agent shared memory | Collaborative memory where multiple agents contribute to and read from the same graph with access control. |
| Active learning | Agent explicitly requests the consolidator to form expertise about a specific area. |
| Graph neural network retrieval | GNN-based relevance scoring over the knowledge graph instead of heuristic reranking. |
| Memory export/import | Portable memory snapshots for transferring expertise between environments. |
