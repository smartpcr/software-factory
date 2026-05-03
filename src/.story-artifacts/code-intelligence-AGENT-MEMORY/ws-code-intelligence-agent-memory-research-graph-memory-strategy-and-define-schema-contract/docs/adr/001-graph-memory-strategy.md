# ADR-001: Graph Memory Strategy for Agent Code Intelligence

**Status:** Accepted  
**Date:** 2026-05-02  
**Deciders:** Code Intelligence team  
**Context:** Agent Memory knowledge graph for repository structure and call-chain context

---

## Context

We are building an agent memory system that learns repository structure and
call-chain relationships so that coding agents can retrieve precise, contextual
information when assisting developers. The memory must be:

1. **Incremental** — updated file-by-file, not rebuilt from scratch.
2. **Multi-extractor** — different analysis backends (tree-sitter, LSP,
   manifest parsers) contribute observations to the same graph.
3. **Provenance-rich** — every fact carries its source, confidence, and
   extraction timestamp so stale data can be detected and evicted.
4. **Top-down navigable** — agents traverse from repository → package →
   directory → file → symbol, mirroring how developers reason about code.

This ADR surveys graph-based agent memory approaches from the literature and
open-source ecosystem, then records the design decisions for our canonical
graph schema.

---

## Options Evaluated

### 1. Zep (Graph-based Conversation Memory)

**Approach:** Zep models conversation memory as a temporal knowledge graph
where entities are extracted from dialogue turns and relationships are
inferred via LLM-based entity/relation extraction. Nodes represent entities
mentioned in conversation; edges represent relationships stated or implied.

**Strengths:**
- Temporal awareness — each edge carries a timestamp enabling decay/recency.
- Entity resolution via embedding similarity merges duplicate references.
- Battle-tested in production chat applications.

**Weaknesses for our use case:**
- Designed for conversational entities (people, topics), not code symbols.
- No concept of file-system containment hierarchy or qualified symbol names.
- Relies on LLM extraction per turn — too expensive for bulk code analysis.
- No multi-extractor provenance; single pipeline writes all edges.

**Verdict:** Temporal provenance model is useful. Entity schema is not
applicable to code structures.

---

### 2. Mem0 (Hybrid Vector + Graph Memory)

**Approach:** Mem0 stores memories as both vector embeddings (for semantic
retrieval) and graph triples (for structured lookup). A memory is a
(subject, predicate, object) triple with metadata. The graph is used for
traversal queries while vectors handle fuzzy recall.

**Strengths:**
- Hybrid retrieval combines structured graph walks with vector similarity.
- Simple triple model makes ingestion straightforward.
- Supports memory updates (upsert) and contradiction detection.

**Weaknesses for our use case:**
- Flat triple store — no hierarchical containment or path-based IDs.
- Predicates are free-text strings, not a controlled vocabulary.
- No concept of extraction scope or multi-extractor confidence.
- Contradiction detection assumes natural-language semantics, not code.

**Verdict:** Hybrid retrieval is a pattern we may adopt later. The flat
triple model lacks the structural hierarchy code navigation requires.

---

### 3. MAGMA (Multi-Agent Graph Memory Architecture)

**Approach:** MAGMA (2024) proposes a shared graph memory across multiple
agents. Each agent reads/writes to a common knowledge graph. Nodes are
typed entities; edges carry provenance metadata indicating which agent
produced the assertion and with what confidence.

**Strengths:**
- **Multi-agent provenance** — first-class support for multiple writers,
  each with their own confidence tier. Directly maps to our multi-extractor
  model.
- **Typed nodes and edges** — enum-based kind discriminators prevent
  schema drift.
- **Upsert semantics** — accumulate-then-derive model where observations
  are appended and effective confidence is derived from the set.
- **Scope-bounded deletions** — an agent can only delete facts it "owns,"
  preventing one extractor from clobbering another's output.

**Weaknesses for our use case:**
- Research prototype; no production-ready implementation.
- Node identity is UUID-based, not deterministic from content.
- Graph schema is domain-agnostic; we need code-specific node kinds.

**Verdict:** Strongest architectural match. We adopt the multi-agent
provenance and scope-bounded deletion model, customizing the schema for
code intelligence.

---

### 4. HiAgent (Hierarchical Agent Memory)

**Approach:** HiAgent (2025) structures agent memory as a hierarchy of
abstraction levels: raw observations → summaries → higher-order concepts.
Lower levels are periodically consolidated into upper levels, similar to
human memory consolidation.

**Strengths:**
- **Hierarchical structure** mirrors the repository → file → symbol nesting
  we need.
- **Consolidation** model provides a natural mechanism for pruning stale
  low-level details while retaining high-level structure.
- Supports both episodic (per-session) and semantic (cross-session) memory.

**Weaknesses for our use case:**
- Designed for LLM-generated summaries, not structured code metadata.
- Consolidation assumes natural-language compression; code symbols need
  exact fidelity, not summarization.
- No multi-extractor provenance.

**Verdict:** Hierarchical organization principle is adopted for our
repository → package → directory → file → symbol node hierarchy.

---

### 5. AriGraph (Autonomous Graph Memory with Persistent Knowledge)

**Approach:** AriGraph (2024) builds a persistent knowledge graph where an
autonomous agent incrementally adds nodes and edges as it explores an
environment. The graph supports both spatial reasoning (environment layout)
and semantic reasoning (object properties, rules).

**Strengths:**
- **Incremental construction** — graph grows as the agent explores, which
  maps directly to incremental extraction of repository contents.
- **Persistent across sessions** — graph survives agent restarts.
- **Spatial + semantic layers** — structural containment (spatial) and
  code relationships (semantic) map well to our structural vs. call-chain
  edge distinction.

**Weaknesses for our use case:**
- Nodes are identified by display name; no canonical ID scheme.
- No provenance metadata — facts have no source attribution.
- Single-writer model; no multi-extractor support.

**Verdict:** Incremental construction and dual-layer (structural +
semantic) graph design are adopted. We add canonical IDs and provenance.

---

## Decision

We adopt a **typed, multi-extractor knowledge graph** with the following
design principles drawn from the evaluated approaches:

| Principle | Source |
|---|---|
| Multi-extractor provenance with per-observation confidence | MAGMA |
| Scope-bounded deletions | MAGMA |
| Hierarchical node structure (repo → pkg → dir → file → symbol) | HiAgent, AriGraph |
| Structural + call-chain edge layers | AriGraph |
| Temporal metadata (createdAt, lastSeenAt) on every entity | Zep |
| Incremental upsert with accumulate-then-derive semantics | MAGMA |
| Deterministic canonical IDs (not UUIDs) for idempotent upserts | Novel |

### Graph Schema Contract

The full schema is implemented in the `@agent-memory/graph-schema` package.
Key design decisions:

#### Node Kinds (13 types)
- **Structural:** `repository`, `package`, `directory`, `file`
- **Symbol:** `class`, `function`, `method`, `interface`, `type_alias`,
  `enum`, `variable`, `constant`, `namespace`

#### Edge Kinds (11 types)
- **Structural:** `contains`, `imports`, `exports`, `re_exports`, `depends_on`
- **Call-chain:** `calls`, `extends`, `implements`, `references`, `type_ref`,
  `instantiates`

#### Canonical ID Format
Node IDs follow the grammar: `{kind}:{repo_id}/{path}#{symbol_chain}`
- `repo_id` is a 12-char hex hash of the normalized clone URL (FNV-1a).
- Path segments are `/`-separated; symbol chains use `#` as separator.
- Reserved characters are percent-encoded.
- Edge IDs are `edge:{hash16}` where the hash is FNV-1a of
  `(sourceId, targetId, kind [, sourceSpan])`.

This produces deterministic, human-readable IDs that survive repository
renames and enable idempotent upserts.

#### Provenance Model
Every node and edge carries an `observations` array. Each `Observation`
records:
- `extractorName` + `extractorVersion` — which analysis pipeline
- `analysisBackend` — `lsp`, `tree_sitter`, `ast_parse`, `heuristic`,
  `manifest_parse`
- `confidenceTier` — `exact`, `heuristic`, `unresolved`
- `confidence` — numeric 0.0–1.0
- `commitSha` — git state at extraction time
- `timestamp` — ISO 8601

When the same extractor+version re-runs, its prior observation is replaced.
Different extractors accumulate independently.

#### Upsert Semantics
Fields follow four merge rules:
| Rule | Fields |
|---|---|
| **Immutable** | `id`, `kind`, `createdAt`, `sourceId`, `targetId` |
| **Latest-wins** | `properties`, `sourceSpan`, `filePath` |
| **Accumulate** | `observations` |
| **Derived** | `staleness`, `lastSeenAt` |

#### Deletion Semantics
1. Each extraction declares a `scope` (owned node kinds + edge kinds + path
   globs).
2. After extraction completes, entities within scope that were **not**
   observed are marked `stale`.
3. A separate process promotes `stale` → `deleted` (soft delete) after a
   configurable retention period.
4. `deleted` entities are excluded from retrieval but physically retained
   for recovery.

---

## Consequences

### Positive
- **Idempotent ingestion** — re-running an extractor produces the same graph
  state, enabling safe retries and incremental updates.
- **Multi-extractor composability** — tree-sitter, LSP, and manifest parsers
  can all contribute to the same graph without overwriting each other.
- **Auditable provenance** — every fact can be traced to its source extractor,
  commit, and timestamp.
- **Safe deletion** — scope-bounded staleness prevents cross-extractor
  clobbering; soft delete enables recovery.

### Negative
- **Schema rigidity** — adding a new node or edge kind requires a schema
  version bump. Mitigated by using a controlled enum rather than free-text.
- **Storage overhead** — the observations array grows with the number of
  extractors. Mitigated by replacing same-extractor+version observations on
  re-run.
- **ID coupling** — canonical IDs encode the repo URL hash, so moving a
  repo to a different remote requires re-extraction. Acceptable since
  re-extraction is expected to be fast and incremental.

---

## References

- Zep — https://www.getzep.com/
- Mem0 — https://github.com/mem0ai/mem0
- MAGMA — Multi-Agent Graph Memory Architecture (2024)
- HiAgent — Hierarchical Agent Memory (2025)
- AriGraph — https://github.com/DEEP-PolyU/Awesome-GraphMemory
