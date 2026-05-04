# Agent Memory — Design Document

## Overview

AgentMemory implements graph-based agent memory for constructing contextual
knowledge from top-down repository structure and call-chain context. It enables
agents to learn and get smarter over time by recording interactions, distilling
expertise, and building a queryable memory graph.

The design draws from recent graph-based agent memory research:

| System | Key Idea | Applied Here |
|--------|----------|--------------|
| **G-Memory** (arXiv:2506.07398) | 3-tier hierarchy: Insight → Query → Interaction | `MemoryTier` enum, tier promotion |
| **MAGMA** (arXiv:2601.03236, ACL'26) | Multi-graph with orthogonal semantic/temporal/causal/entity views | Multi-signal candidate scoring in `ContextBuilder` |
| **Zep/Graphiti** (arXiv:2501.13956) | Temporally-aware knowledge graph | Freshness tracking, half-life decay |
| **Mem0** (arXiv:2504.19413) | Scalable graph consolidation with evolution | `MemoryEvolver` consolidation + decay |
| **HyperGraphRAG** (arXiv:2503.21322) | N-ary relation modeling | `CallChain` (ordered n-ary sequences) |
| **ExpeL** (AAAI'24) | Insight distillation from experience | Episode → expertise consolidation |

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    Context Builder                        │
│  (MAGMA multi-signal: candidates → ranking → assembly)   │
├────────────┬──────────────┬──────────────┬───────────────┤
│  Episodes  │  Expertise   │  Call Chains │  Code Graph   │
│  (episodic │  (semantic   │  (n-ary      │  (structural  │
│   memory)  │   memory)    │   relational)│   context)    │
├────────────┴──────────────┴──────────────┴───────────────┤
│                    Memory Store                           │
│  IAgentMemoryStore + ITieredExpertiseStore + ICallChain  │
├────────────┬──────────────┬──────────────────────────────┤
│  Memory    │    Memory    │        Memory                │
│  Evolver   │    Edges     │        Graph                 │
│ (consol.   │ (directed    │  (episodes ↔ expertise       │
│  decay,    │  weighted    │   ↔ code nodes ↔ files)      │
│  promote)  │  links)      │                              │
└────────────┴──────────────┴──────────────────────────────┘
```

## Memory Lifecycle

### 1. Extraction (Episode Recording)

Agents use `IEpisodeRecorder` to record interactions. Each episode captures:
- **What**: task description, type (BugFix, Feature, Investigation, etc.)
- **Where**: touched files and symbols with explicit `TouchType` (Read, Modified, Created, Deleted, Tested)
- **How**: observations and evidence collected during the task
- **Outcome**: success, failure, partial, abandoned

Episodes are persisted immediately in InProgress state for crash safety.

### 2. Storage (Memory Graph)

Memory is stored as a directed graph with three node types:
- **Episodes** — raw interaction records
- **Expertise** — consolidated knowledge with confidence scores
- **Code nodes** — file paths and symbol IDs from the AST hypergraph

Edges (`MemoryEdge`) encode relationships:
- `EpisodeTouchedFile/Symbol` — what an episode interacted with
- `EpisodeProducedExpertise` — provenance from episode to expertise
- `ExpertiseAboutFile/Symbol` — what expertise describes
- `CoChangedWith` — files frequently modified together
- `PromotedTo` — expertise distilled to a higher tier
- `ContainedIn` — hierarchical containment (method→type→namespace→project)
- `CallsMethod` — call relationships between methods

### 3. Retrieval (Context Building)

`ContextBuilder` implements a staged retrieval pipeline:

1. **Candidate generation** — gather episodes, expertise, call chains, and graph neighbors matching the request
2. **Ranking** — score all candidates using multiple signals:
   - File/symbol overlap with request (0.3–0.4 weight)
   - Confidence/outcome quality (0.2–0.3 weight)
   - Recency with 90-day decay (0.1–0.2 weight)
   - Observation count and source type for call chains
3. **Assembly** — build `ContextWindow` with top-N episodes, expertise, call chains, and freshness warnings

### 4. Evolution (Consolidation, Decay, Promotion)

#### Consolidation (episodes → expertise)
When N+ successful episodes touch the same file/symbol:
- Create `FileExpertise`/`TypeExpertise` records
- Aggregate evidence, count episodes, compute confidence
- Derive `FailurePattern` records from repeated failures

#### Decay (confidence half-life)
Expertise confidence decays exponentially:
```
newConfidence = confidence × 0.5^(daysSinceValidation / halfLifeDays)
```
Access-based reinforcement slows decay for frequently-used knowledge.
Records below minimum confidence are garbage-collected.

#### Tier Promotion (G-Memory hierarchy)
The 3-tier hierarchy maps to repo structure:

| Tier | Scope | Example |
|------|-------|---------|
| **Interaction** | File/method-level | "Parser.cs uses regex for tokenization" |
| **Query** | Namespace/project-level | "The EtwIngest project follows BDD testing patterns" |
| **Insight** | Architecture-level | "Cross-cutting DI registration convention" |

Promotion creates **new** higher-tier records (does not mutate originals)
linked via `PromotedTo` edges. This preserves lower-tier provenance.

Promotion rules:
- File expertise → group by directory → NamespaceExpertise at Query tier
- Type/method expertise → group by namespace → NamespaceExpertise at Query tier
- Query-tier records → group by broader scope → ArchitecturalKnowledge at Insight tier

#### Co-Change Detection
Files frequently modified together across episodes form co-change groups.

**Invariant**: Only `Modified`/`Created`/`Deleted` touches count — reads are excluded.

Detection uses **minimum count + Jaccard similarity**:
```
Jaccard(A, B) = |episodes(A) ∩ episodes(B)| / |episodes(A) ∪ episodes(B)|
```
Both thresholds must be met (default: count ≥ 3, Jaccard ≥ 0.3).

Connected components of strong co-change pairs form `CoChangeGroup` expertise.

## Call Chain Context

Call chains capture ordered n-ary method call sequences:
```
MethodA → MethodB → MethodC
```

Unlike binary edges, they model flow paths as atomic units. Each chain tracks:
- **Source**: Observed (agent saw it) vs. Inferred (static analysis)
- **Confidence**: Reinforced by observation count, decayed by time
- **Provenance**: Which episodes discovered it

Observed chains score higher than inferred ones in context building,
since they represent validated runtime behavior.

## Dependency Injection

```csharp
services.AddAgentMemory(); // in-memory store (testing/single-session)
services.AddAgentMemory<MyPersistentStore>(); // custom store
```

Store = singleton (shared state). Services = transient (stateless).

## Key Invariants

1. **Promotion creates new records** — lower-tier records are preserved
2. **Co-change uses modification events** — reads are excluded
3. **Call chains are ranked** — not blindly appended to context
4. **Evidence is list-based** — duplicate categories are preserved, not overwritten
5. **Confidence never exceeds 1.0** — all reinforcement is capped
6. **Touch types flow through edges** — `EpisodeBuilder` preserves actual touch type
