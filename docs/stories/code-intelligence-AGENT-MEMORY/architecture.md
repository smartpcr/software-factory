# Agent Memory — Architecture

## 1 Problem Statement

Coding agents today operate without persistent memory. Each session starts
cold: the agent re-discovers repository structure, re-traces call chains, and
re-learns domain patterns from scratch. This wastes tokens, increases latency,
and caps the agent's effective expertise at what fits in a single context
window.

**Agent Memory** solves this by building a persistent, graph-structured
knowledge layer on top of two first-class data sources:

| Source | What it captures |
|---|---|
| **Top-down repo structure** | Package → module → file → symbol hierarchy; ownership, visibility, layering rules. |
| **Call-chain context** | Function-level call graphs, data-flow edges, cross-module dependencies. |

The graph accumulates *expertise*—learned heuristics, failure patterns,
refactoring strategies—so agents get measurably smarter across sessions.

### Design influences

This architecture draws on the taxonomy established by the *Graph-based Agent
Memory* survey (Yang et al., arXiv 2602.05665), specifically:

- **Hierarchical memory storage** (HiAgent, G-Memory) — multi-level graphs
  that mirror cognitive short-/long-term separation.
- **Temporal knowledge graphs** (Zep, Graphiti) — edges carry validity
  intervals so stale knowledge self-expires.
- **RL-based memory evolution** (MAGMA, Mem-α) — reinforcement signals from
  agent task outcomes drive graph consolidation and pruning.
- **Multi-hop graph retrieval** (GraphRAG, SimGRAG) — traversal-based context
  assembly rather than flat vector search.

---

## 2 Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Agent Runtime (caller)                       │
│   ┌──────────┐   ┌──────────────┐   ┌────────────────────────────┐ │
│   │  Planner  │   │  Tool Router  │   │  Context Assembler (§2.4) │ │
│   └────┬─────┘   └──────┬───────┘   └────────────┬───────────────┘ │
│        │                │                         │                 │
│        ▼                ▼                         ▼                 │
│  ┌───────────────────────────────────────────────────────────┐      │
│  │                   Memory Gateway API                      │      │
│  │            ingest · query · feedback · admin               │      │
│  └──────┬──────────────┬──────────────┬──────────────┬──────┘      │
└─────────┼──────────────┼──────────────┼──────────────┼─────────────┘
          │              │              │              │
   ┌──────▼──────┐ ┌─────▼──────┐ ┌────▼─────┐ ┌─────▼──────┐
   │  Extraction │ │  Storage   │ │ Retrieval│ │ Evolution  │
   │  Pipeline   │ │  Engine    │ │ Engine   │ │ Engine     │
   │   (§3.1)    │ │   (§3.2)   │ │  (§3.3)  │ │  (§3.4)    │
   └──────┬──────┘ └─────┬──────┘ └────┬─────┘ └─────┬──────┘
          │              │              │              │
          ▼              ▼              ▼              ▼
   ┌──────────────────────────────────────────────────────────┐
   │                  Unified Graph Store                      │
   │  ┌──────────────┬──────────────┬───────────────────────┐ │
   │  │  Structural  │  Behavioral  │  Semantic / Expertise │ │
   │  │    Layer     │    Layer     │       Layer            │ │
   │  └──────────────┴──────────────┴───────────────────────┘ │
   │  ┌──────────────────────────────────────────────────────┐ │
   │  │            Temporal Version Index                     │ │
   │  └──────────────────────────────────────────────────────┘ │
   │  ┌──────────────────────────────────────────────────────┐ │
   │  │            Embedding Vector Index                     │ │
   │  └──────────────────────────────────────────────────────┘ │
   └──────────────────────────────────────────────────────────┘
```

The system decomposes into **four processing engines** that implement the
memory lifecycle (Extract → Store → Retrieve → Evolve), one **gateway API**
that the agent runtime calls, and one **unified graph store** that holds the
three-layer knowledge graph plus auxiliary indexes.

---

## 3 Components and Responsibilities

### 3.1 Extraction Pipeline

Transforms raw repository artifacts into graph-ready facts.

| Sub-component | Input | Output | Notes |
|---|---|---|---|
| **StructureAnalyzer** | File tree, `package.json` / `pyproject.toml` / build manifests | `Module`, `File`, `Package` nodes + `CONTAINS` edges | Runs on clone/pull; incremental via diff. |
| **SymbolExtractor** | Source files | `Symbol` nodes (functions, classes, interfaces, constants) + `DEFINES` edges to `File` | Uses Tree-sitter grammars — language-agnostic. |
| **CallGraphBuilder** | AST of each symbol | `CALLS`, `IMPORTS`, `IMPLEMENTS` edges between `Symbol` nodes | Intra-repo static analysis; marks `DYNAMIC` edges when resolution is ambiguous. |
| **DataFlowTracer** | AST + type information | `READS`, `WRITES`, `RETURNS_TYPE` edges | Optional; requires type-checker output (e.g., TSC diagnostics). |
| **SessionTraceRecorder** | Agent tool-use logs, LLM completions | `Session`, `Action`, `Observation` nodes + `PERFORMED`, `OBSERVED` edges | Captures what the agent *did*, not just what the code *is*. |

**Key design rule:** Extraction is *append-only* into a staging area. The
Storage Engine decides when to merge staged facts into the live graph
(§3.2 Merge Protocol).

### 3.2 Storage Engine

Owns the Unified Graph Store and exposes transactional write operations.

| Sub-component | Responsibility |
|---|---|
| **GraphWriter** | Atomic upsert of nodes and edges. Maintains referential integrity. Deduplicates by canonical key (see §4 Data Model). |
| **TemporalVersioner** | Stamps every node/edge with `[valid_from, valid_to)` intervals derived from commit SHAs. Queries can pin to a point-in-time. |
| **EmbeddingIndexer** | On node create/update, enqueues an embedding job. Stores vectors in an ANN index (HNSW) keyed by node ID. |
| **MergeProtocol** | Compares staged facts against the live graph. For each fact: (a) if new → insert; (b) if changed → close old interval, open new; (c) if deleted → close interval, keep tombstone. Runs per-commit or per-session. |

**Storage layout:** The graph is logically partitioned into three *layers*
(see §4) but physically lives in a single labeled-property graph database.
Each node carries a `layer` label so queries can target one or more layers.

### 3.3 Retrieval Engine

Assembles context for the agent by combining graph traversal with vector
similarity.

| Sub-component | Responsibility |
|---|---|
| **StructuralLocator** | Given a file path or symbol name, returns the node and its k-hop neighborhood in the structural layer. Respects hierarchy: asking about a module returns its files and their top-level symbols. |
| **CallChainWalker** | Given a symbol node, performs bounded BFS/DFS along `CALLS`/`IMPORTS` edges. Returns upstream callers, downstream callees, or full chains up to configurable depth. |
| **SemanticSearcher** | Embedding-based ANN search over the semantic layer. Returns expertise nodes (patterns, heuristics, failure memories) ranked by cosine similarity to the query. |
| **ContextRanker** | Takes the union of candidates from the three sub-components above. Scores each candidate on (recency × relevance × connectivity). Applies a token budget and returns a ranked, pruned subgraph serializable to markdown or JSON. |
| **TemporalFilter** | Pre-filter applied to any retrieval path. Constrains results to nodes/edges valid at a given commit SHA or timestamp. Defaults to HEAD. |

**Retrieval strategy:** The engine implements *multi-source, multi-hop*
retrieval (per GraphRAG / SimGRAG findings). A single query fans out to
all three layers in parallel, results are merged by the ContextRanker, and
the final context window respects a caller-specified token budget.

### 3.4 Evolution Engine

Makes the graph smarter over time by processing feedback signals.

| Sub-component | Responsibility |
|---|---|
| **FeedbackIngestor** | Receives explicit signals (agent marked a suggestion as helpful/unhelpful) and implicit signals (task succeeded/failed, test pass/fail after agent edit). Writes `Feedback` nodes linked to the `Session` that produced them. |
| **RelevanceDecayer** | Periodic job. Reduces the `confidence` score of nodes/edges that have not been accessed or reinforced within a configurable TTL window. Nodes below a threshold are marked `DORMANT`. |
| **ConsolidationWorker** | Merges clusters of semantically similar expertise nodes into higher-level generalizations. E.g., three specific "null-check failure" memories consolidate into one "defensive-coding pattern for nullable returns" expertise node. Uses LLM summarization. |
| **PruningWorker** | Deletes `DORMANT` nodes whose confidence is below a hard floor and that have zero inbound edges from non-dormant nodes. Runs on a schedule (e.g., daily). Tombstones are retained for audit. |
| **ExpertiseSynthesizer** | After a successful agent session, extracts generalizable lessons (heuristics, anti-patterns, preferred APIs) and writes them as new `Expertise` nodes in the semantic layer. This is the primary mechanism by which agents "get smarter." |

**Evolution loop (inspired by MAGMA / Reflexion):**

```
Agent Session completes
        │
        ▼
  FeedbackIngestor records outcome
        │
        ▼
  ExpertiseSynthesizer extracts lessons  ──►  new Expertise nodes
        │
        ▼
  ConsolidationWorker merges duplicates
        │
        ▼
  RelevanceDecayer adjusts scores
        │
        ▼
  PruningWorker removes dead weight
```

### 3.5 Memory Gateway API

Single entry-point for the agent runtime. Stateless HTTP/gRPC service.

| Endpoint | Verb | Purpose |
|---|---|---|
| `/ingest/commit` | POST | Trigger extraction for a commit range. |
| `/ingest/session` | POST | Submit an agent session trace. |
| `/query/context` | POST | Retrieve assembled context for a task. |
| `/query/symbol` | GET | Look up a single symbol and its neighborhood. |
| `/query/callchain` | GET | Trace call chain from a symbol. |
| `/feedback` | POST | Submit outcome signal for a session. |
| `/admin/stats` | GET | Graph size, layer distribution, staleness metrics. |
| `/admin/evolve` | POST | Trigger on-demand consolidation/pruning cycle. |

### 3.6 Context Assembler (agent-side)

Lives inside the agent runtime (not in the memory service). Translates
the memory service's graph-structured response into a prompt-ready
context block.

| Responsibility | Detail |
|---|---|
| **Format negotiation** | Converts subgraph to markdown (for chat models) or structured JSON (for tool-use models). |
| **Token budgeting** | Caller specifies max tokens; assembler truncates least-relevant nodes first. |
| **Priority injection** | Expertise nodes flagged `high_confidence` are placed at the top of the context block so the model sees them first. |
| **Delta mode** | For follow-up queries within a session, returns only nodes not already in the conversation history. |

---

## 4 Data Model

### 4.1 Node Types

All nodes share a common base:

```
BaseNode {
  id            : UUID           -- globally unique
  canonical_key : string         -- dedup key (e.g., "repo:path:symbol")
  layer         : enum(STRUCTURAL | BEHAVIORAL | SEMANTIC)
  valid_from    : string         -- commit SHA or ISO timestamp
  valid_to      : string | null  -- null = currently valid
  confidence    : float [0,1]    -- 1.0 = verified fact; decays over time
  embedding     : float[768]     -- populated by EmbeddingIndexer
  created_at    : datetime
  updated_at    : datetime
}
```

#### Structural Layer

| Node | Key Fields | Canonical Key Pattern |
|---|---|---|
| **Repository** | `name`, `remote_url`, `default_branch` | `repo:{remote_url}` |
| **Package** | `name`, `version`, `manager` (npm/pip/maven/…) | `repo:{repo_id}:pkg:{name}` |
| **Module** | `path` (relative dir), `language` | `repo:{repo_id}:mod:{path}` |
| **File** | `path`, `language`, `size_bytes`, `hash` | `repo:{repo_id}:file:{path}` |
| **Symbol** | `name`, `kind` (function/class/interface/const/type), `signature`, `visibility` (public/private/internal), `line_start`, `line_end`, `docstring` | `repo:{repo_id}:file:{path}:sym:{name}:{kind}` |

#### Behavioral Layer

| Node | Key Fields | Canonical Key Pattern |
|---|---|---|
| **CallSite** | `caller_symbol_id`, `callee_symbol_id`, `call_type` (static/dynamic/virtual), `line` | `repo:{repo_id}:cs:{caller}→{callee}:{line}` |
| **DataFlow** | `source_symbol_id`, `sink_symbol_id`, `flow_type` (read/write/return) | `repo:{repo_id}:df:{source}→{sink}:{type}` |
| **Dependency** | `from_module_id`, `to_module_id`, `kind` (import/re-export/lazy) | `repo:{repo_id}:dep:{from}→{to}` |

#### Semantic Layer

| Node | Key Fields | Canonical Key Pattern |
|---|---|---|
| **Expertise** | `title`, `description`, `category` (pattern/anti-pattern/heuristic/api-preference), `source_session_ids[]`, `example_snippet` | `repo:{repo_id}:exp:{hash(title)}` |
| **Concept** | `name`, `description`, `related_symbols[]` | `repo:{repo_id}:concept:{name}` |
| **FailureMemory** | `error_signature`, `root_cause`, `resolution`, `recurrence_count` | `repo:{repo_id}:fail:{hash(error_signature)}` |
| **Session** | `agent_id`, `task_description`, `outcome` (success/partial/failure), `duration_ms`, `token_usage` | `session:{uuid}` |
| **Action** | `tool_name`, `input_summary`, `output_summary`, `symbol_ids_touched[]` | `session:{session_id}:action:{seq}` |
| **Feedback** | `session_id`, `signal` (positive/negative/neutral), `detail` | `session:{session_id}:fb:{seq}` |

### 4.2 Edge Types

| Edge | Source → Target | Layer | Key Properties |
|---|---|---|---|
| `CONTAINS` | Repository→Package, Package→Module, Module→File, File→Symbol | Structural | `order` (positional index) |
| `DEFINES` | File → Symbol | Structural | `line_start`, `line_end` |
| `CALLS` | Symbol → Symbol | Behavioral | `call_type`, `line`, `is_conditional` |
| `IMPORTS` | Module → Module | Behavioral | `kind` (static/dynamic/re-export) |
| `IMPLEMENTS` | Symbol → Symbol | Behavioral | (class implements interface) |
| `EXTENDS` | Symbol → Symbol | Behavioral | (class extends class) |
| `READS` / `WRITES` | Symbol → Symbol | Behavioral | `flow_type` |
| `RETURNS_TYPE` | Symbol → Symbol | Behavioral | `type_name` |
| `RELATES_TO` | Concept → Symbol | Semantic | `relevance_score` |
| `LEARNED_FROM` | Expertise → Session | Semantic | `confidence_delta` |
| `RESOLVES` | Expertise → FailureMemory | Semantic | |
| `SUPERSEDES` | Expertise → Expertise | Semantic | (consolidation link) |
| `PERFORMED` | Session → Action | Semantic | `seq` |
| `TOUCHED` | Action → Symbol | Cross-layer | |

### 4.3 Temporal Versioning

Every node and edge carries `[valid_from, valid_to)`. Queries that do not
specify a version pin default to `valid_to IS NULL` (current HEAD).

**Version granularity:** Structural and behavioral facts are versioned by
commit SHA. Semantic facts (expertise, failure memories) are versioned by
session timestamp, since they are not tied to a single commit.

**Tombstoning:** When a symbol is deleted in a commit, its node's `valid_to`
is set to that commit SHA. The node remains in the graph for historical
queries and for expertise nodes that reference it.

---

## 5 Interfaces Between Components

### 5.1 Extraction → Storage

```
interface StagedFact {
  operation : "UPSERT" | "DELETE"
  node?     : BaseNode            // present for UPSERT
  edge?     : Edge                // present for UPSERT
  source    : "commit" | "session"
  source_ref: string              // commit SHA or session ID
}

ExtractionPipeline.flush() → StagedFact[]
StorageEngine.merge(facts: StagedFact[], source_ref: string) → MergeReport
```

`MergeReport` contains counts: `{ inserted, updated, closed, conflicts }`.
Conflicts (two concurrent sources updating the same canonical key) are
resolved by last-writer-wins within the same source type, or commit-wins
over session for cross-type conflicts.

### 5.2 Agent Runtime → Memory Gateway

```
// Context retrieval — primary interface
POST /query/context
{
  repo_id       : string
  task          : string             // natural-language task description
  entry_points  : string[]           // file paths or symbol names (optional)
  commit_sha    : string             // version pin (optional, default HEAD)
  include_layers: ["STRUCTURAL","BEHAVIORAL","SEMANTIC"]  // default all
  max_tokens    : int                // token budget for response
  session_id    : string             // to enable delta mode
}

Response {
  subgraph : {
    nodes  : BaseNode[]
    edges  : Edge[]
  }
  expertise : Expertise[]           // top-ranked expertise nodes, separate for priority
  token_estimate : int
  staleness_warning : string | null // if graph is >N commits behind HEAD
}
```

### 5.3 Agent Runtime → Feedback

```
POST /feedback
{
  session_id : string
  outcome    : "success" | "partial" | "failure"
  signals    : [
    { node_id: string, signal: "positive" | "negative", detail?: string }
  ]
}
```

### 5.4 Evolution Engine Internal Contracts

```
FeedbackIngestor.process(feedback: Feedback) → void
  // writes Feedback node, links to Session

ExpertiseSynthesizer.synthesize(session: Session) → Expertise[]
  // LLM call: given session trace, extract generalizable lessons

ConsolidationWorker.consolidate(cluster: Expertise[]) → Expertise
  // LLM call: merge N similar expertise nodes into one generalized node
  // creates SUPERSEDES edges from new node to old nodes

RelevanceDecayer.decay(ttl: Duration) → int
  // returns count of nodes whose confidence was reduced

PruningWorker.prune(floor: float) → int
  // returns count of nodes tombstoned
```

---

## 6 End-to-End Sequence Flows

### 6.1 Repository Onboarding (Cold Start)

```
Operator                ExtractionPipeline        StorageEngine        Graph Store
  │                           │                        │                    │
  │  POST /ingest/commit      │                        │                    │
  │  {repo, commit_range:     │                        │                    │
  │   "initial..HEAD"}        │                        │                    │
  │──────────────────────────►│                        │                    │
  │                           │  clone/checkout         │                    │
  │                           │  StructureAnalyzer      │                    │
  │                           │  SymbolExtractor        │                    │
  │                           │  CallGraphBuilder       │                    │
  │                           │  DataFlowTracer         │                    │
  │                           │                        │                    │
  │                           │  flush() → StagedFact[] │                    │
  │                           │───────────────────────►│                    │
  │                           │                        │  merge(facts)       │
  │                           │                        │───────────────────►│
  │                           │                        │  upsert nodes/edges│
  │                           │                        │◄───────────────────│
  │                           │                        │                    │
  │                           │                        │  EmbeddingIndexer   │
  │                           │                        │  enqueue all nodes  │
  │                           │                        │───────────────────►│
  │                           │                        │                    │
  │  202 Accepted             │                        │                    │
  │◄──────────────────────────│                        │                    │
  │  (async; poll /admin/stats for completion)          │                    │
```

**Expected volume:** A 100K-LOC TypeScript repo produces ~5,000 Symbol nodes,
~15,000 CALLS edges, ~500 Module nodes. Onboarding completes in <5 minutes.

### 6.2 Incremental Update (New Commits)

```
CI/CD webhook             Gateway           ExtractionPipeline     StorageEngine
  │                          │                      │                    │
  │  POST /ingest/commit     │                      │                    │
  │  {repo, commit_range:    │                      │                    │
  │   "abc123..def456"}      │                      │                    │
  │─────────────────────────►│                      │                    │
  │                          │  diff --name-status   │                    │
  │                          │─────────────────────►│                    │
  │                          │                      │  parse only changed │
  │                          │                      │  files              │
  │                          │                      │                    │
  │                          │                      │  flush() → facts    │
  │                          │                      │──────────────────►│
  │                          │                      │                    │
  │                          │                      │  MergeProtocol:     │
  │                          │                      │  close old intervals│
  │                          │                      │  open new intervals │
  │                          │                      │◄──────────────────│
  │  202 Accepted            │                      │                    │
  │◄─────────────────────────│                      │                    │
```

**Key invariant:** After merge, querying at `commit_sha=abc123` returns the
old graph; querying at `commit_sha=def456` returns the new graph. Both are
valid simultaneously.

### 6.3 Agent Task Execution (Warm Query)

```
Agent                  ContextAssembler        Gateway         RetrievalEngine      Graph Store
  │                          │                    │                   │                  │
  │  "Fix null-ref in        │                    │                   │                  │
  │   UserService.getProfile"│                    │                   │                  │
  │─────────────────────────►│                    │                   │                  │
  │                          │  POST /query/context                   │                  │
  │                          │  {task, entry:      │                   │                  │
  │                          │   ["UserService",   │                   │                  │
  │                          │    "getProfile"],   │                   │                  │
  │                          │   max_tokens: 8000} │                   │                  │
  │                          │───────────────────►│                   │                  │
  │                          │                    │                   │                  │
  │                          │                    │  StructuralLocator │                  │
  │                          │                    │  → File, Module    │                  │
  │                          │                    │──────────────────►│                  │
  │                          │                    │                   │  nodes + edges    │
  │                          │                    │                   │◄─────────────────│
  │                          │                    │                   │                  │
  │                          │                    │  CallChainWalker   │                  │
  │                          │                    │  → callers/callees │                  │
  │                          │                    │  (depth=3)         │                  │
  │                          │                    │──────────────────►│                  │
  │                          │                    │                   │  call subgraph    │
  │                          │                    │                   │◄─────────────────│
  │                          │                    │                   │                  │
  │                          │                    │  SemanticSearcher  │                  │
  │                          │                    │  → "null-ref fix"  │                  │
  │                          │                    │  expertise match   │                  │
  │                          │                    │──────────────────►│                  │
  │                          │                    │                   │  Expertise nodes  │
  │                          │                    │                   │◄─────────────────│
  │                          │                    │                   │                  │
  │                          │                    │  ContextRanker     │                  │
  │                          │                    │  merge + score +   │                  │
  │                          │                    │  budget trim       │                  │
  │                          │                    │                   │                  │
  │                          │  Response {subgraph, expertise}        │                  │
  │                          │◄──────────────────│                   │                  │
  │                          │                    │                   │                  │
  │                          │  Format to markdown │                   │                  │
  │                          │  Apply token budget │                   │                  │
  │                          │  Inject expertise   │                   │                  │
  │  context_block (markdown) │                    │                   │                  │
  │◄─────────────────────────│                    │                   │                  │
  │                          │                    │                   │                  │
  │  (agent uses context to  │                    │                   │                  │
  │   complete the task)     │                    │                   │                  │
```

### 6.4 Post-Session Learning (Evolution Loop)

```
Agent              Gateway        FeedbackIngestor   ExpertiseSynth.   ConsolidationWkr   PruningWkr
  │                   │                  │                 │                  │                │
  │  POST /feedback   │                  │                 │                  │                │
  │  {session, outcome│                  │                 │                  │                │
  │   signals[…]}     │                  │                 │                  │                │
  │──────────────────►│                  │                 │                  │                │
  │                   │  process()       │                 │                  │                │
  │                   │─────────────────►│                 │                  │                │
  │                   │                  │  write Feedback  │                  │                │
  │                   │                  │  nodes + edges   │                  │                │
  │                   │                  │                 │                  │                │
  │                   │                  │  trigger synth   │                  │                │
  │                   │                  │────────────────►│                  │                │
  │                   │                  │                 │  LLM: extract    │                │
  │                   │                  │                 │  lessons from    │                │
  │                   │                  │                 │  session trace   │                │
  │                   │                  │                 │                  │                │
  │                   │                  │                 │  write Expertise │                │
  │                   │                  │                 │  nodes           │                │
  │                   │                  │                 │                  │                │
  │                   │                  │                 │  trigger consol. │                │
  │                   │                  │                 │─────────────────►│                │
  │                   │                  │                 │                  │  cluster similar│
  │                   │                  │                 │                  │  expertise;     │
  │                   │                  │                 │                  │  LLM: merge     │
  │                   │                  │                 │                  │  into general   │
  │                   │                  │                 │                  │  node           │
  │                   │                  │                 │                  │                │
  │                   │                  │                 │                  │  trigger prune  │
  │                   │                  │                 │                  │───────────────►│
  │                   │                  │                 │                  │                │
  │                   │                  │                 │                  │  decay + prune  │
  │                   │                  │                 │                  │  dormant nodes  │
  │  200 OK           │                  │                 │                  │                │
  │◄──────────────────│                  │                 │                  │                │
```

### 6.5 Cross-Session Expertise Recall

This flow shows how expertise learned in Session A benefits Session B,
which is the core "agent gets smarter" scenario.

```
Session A (past)                              Session B (current)
─────────────────                             ─────────────────
Agent fixes a null-ref in                     Agent encounters similar
UserService.getProfile by                     null-ref in OrderService.
adding a guard clause.                        submit.
       │                                             │
       ▼                                             │
FeedbackIngestor:                                    │
  outcome = success                                  │
       │                                             │
       ▼                                             │
ExpertiseSynthesizer:                                │
  "When service methods return                       │
   nullable entities from the DB,                    │
   callers must null-check before                    │
   accessing nested properties.                      │
   Pattern: guard clause with                        │
   early return."                                    │
       │                                             │
       ▼                                             │
Expertise node written to                            │
semantic layer, confidence=0.7                       │
                                                     │
                                              POST /query/context
                                              task="fix null-ref in
                                                OrderService.submit"
                                                     │
                                                     ▼
                                              SemanticSearcher finds
                                              expertise node by embedding
                                              similarity (null-ref, guard)
                                                     │
                                                     ▼
                                              ContextRanker includes it
                                              in the assembled context
                                                     │
                                                     ▼
                                              Agent sees: "Previously
                                              learned: guard clause
                                              pattern for nullable DB
                                              returns" → applies same
                                              fix, faster, first try.
                                                     │
                                                     ▼
                                              POST /feedback
                                              outcome=success,
                                              signal=positive on
                                              expertise node
                                                     │
                                                     ▼
                                              Expertise confidence
                                              bumped to 0.85
```

---

## 7 Key Design Decisions

### 7.1 Why a Graph, Not a Vector Store

| Concern | Vector store | Graph store |
|---|---|---|
| **Structural queries** ("what calls `getProfile`?") | Requires brute-force search or pre-computed lists | Direct edge traversal, O(degree) |
| **Multi-hop reasoning** ("trace all callers of all callers of X") | Not natively supported | BFS/DFS with depth control |
| **Temporal slicing** ("show me the call graph at commit abc123") | Must maintain N copies of embeddings | Single graph with interval predicates |
| **Explainability** | Opaque similarity scores | Named edges with provenance |

Embeddings are still used — as an *index* on graph nodes, not as the primary
storage. This gives us the best of both worlds: semantic fuzzy matching for
expertise recall, plus structural precision for call-chain and hierarchy
queries.

### 7.2 Why Three Layers

Separating structural, behavioral, and semantic concerns enables:

1. **Independent refresh cadences.** Structural/behavioral layers update on
   every commit. Semantic layer updates on session completion — a much slower
   cadence.
2. **Layer-specific retrieval strategies.** Structural → deterministic
   traversal. Behavioral → bounded graph search. Semantic → embedding ANN +
   confidence filtering.
3. **Access-control boundaries.** A team might share the structural layer
   across agents but keep the semantic layer per-agent (personal expertise vs.
   shared codebase facts).

### 7.3 Temporal Versioning vs. Snapshots

We use **interval-based temporal versioning** (inspired by Zep / Graphiti)
rather than full graph snapshots because:

- Storage is O(changes), not O(graph × versions).
- Point-in-time queries are a single predicate filter, not a snapshot lookup.
- Tombstoned nodes remain reachable for expertise nodes that reference them
  (a deleted function can still teach the agent something).

### 7.4 Confidence Decay Model

Each node's `confidence` score follows:

```
confidence(t) = base_confidence × decay_factor ^ (t - last_reinforced_at)
```

Where `decay_factor` is configurable per layer (default: 0.95/day for
semantic, 1.0 for structural/behavioral since those are anchored to code).

Positive feedback resets `last_reinforced_at` and may increase
`base_confidence` (capped at 1.0). Negative feedback applies an immediate
multiplicative penalty (default: ×0.5).

### 7.5 Token Budget Discipline

The ContextRanker enforces a strict token budget to prevent context window
blow-up. Algorithm:

1. Sort all candidate nodes by `score = recency × relevance × confidence`.
2. Greedily add nodes from highest to lowest score.
3. For each node, estimate tokens from `len(serialized_node) / 4`.
4. Stop when adding the next node would exceed the budget.
5. If expertise nodes haven't been included, evict the lowest-scored
   non-expertise node to make room (expertise gets priority).

### 7.6 Consolidation Guard Rails

Unsupervised LLM-based consolidation is risky (hallucinated generalizations).
Guard rails:

- Consolidation only fires when a cluster has ≥3 similar expertise nodes with
  mean confidence ≥0.6.
- The merged node inherits the **minimum** confidence of its constituents, not
  the average.
- Original nodes are marked `SUPERSEDED`, not deleted — rollback is trivial.
- A human-reviewable audit log captures every consolidation event with
  before/after content.

---

## 8 Deployment Topology

```
┌────────────────────────────────┐
│         Agent Runtime          │
│  (any host; calls Gateway API) │
└──────────────┬─────────────────┘
               │ HTTPS / gRPC
               ▼
┌────────────────────────────────┐
│       Memory Gateway API       │
│  (stateless; horizontally      │
│   scalable behind LB)          │
└──────┬───────────┬─────────────┘
       │           │
       ▼           ▼
┌─────────┐  ┌───────────────┐
│ Graph DB│  │ Vector Index   │
│ (Neo4j /│  │ (pgvector /    │
│  Age /  │  │  Qdrant /      │
│  Memgr.)│  │  Weaviate)     │
└─────────┘  └───────────────┘
       │
       ▼
┌─────────────────────────────┐
│   Async Worker Pool          │
│  (Extraction, Evolution,     │
│   Embedding jobs)            │
│  Backed by task queue        │
│  (e.g., Celery / Temporal)   │
└─────────────────────────────┘
```

**Minimum viable deployment:** Single-node SQLite + HNSW in-process
(for local dev). Scales to managed Neo4j + Qdrant + Temporal for
production multi-repo deployments.

---

## 9 Observability Contract

| Signal | SLI | Target |
|---|---|---|
| Extraction latency | p95 time from commit push to graph merge complete | < 30 s for incremental, < 5 min for onboarding |
| Query latency | p95 time from `/query/context` request to response | < 500 ms |
| Graph freshness | Commits behind HEAD at query time | ≤ 1 commit (async pipeline) |
| Expertise recall precision | % of surfaced expertise nodes rated positive by agent feedback | ≥ 70% |
| Consolidation audit rate | % of consolidation events reviewed within 7 days | 100% |
| Pruning safety | Nodes pruned that were later re-requested within 30 days | < 1% |

Each SLI has a corresponding Prometheus metric, Grafana dashboard panel,
and PagerDuty alert with a runbook. The runbook is the deliverable.
