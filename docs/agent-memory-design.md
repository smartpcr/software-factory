# Agent Memory — Graph-based Architecture Design

> **Status**: Accepted
> **Date**: 2026-05-01
> **Authors**: Agent-assisted design
> **Related Research**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665)

## Context

Agents working on codebases start each session without accumulated knowledge. They re-discover
repo structure, re-trace call chains, and re-learn conventions every time. The Graph-based Agent
Memory survey (arXiv:2602.05665) identifies four lifecycle phases—extraction, storage, retrieval,
evolution—and shows that graph structures outperform flat/vector-only approaches for relational
and hierarchical reasoning.

This repository already has a rich code intelligence pipeline:
- `Ast.Core` — hypergraph models (HypergraphNode, Hyperedge, HypergraphData)
- `Ast.GraphDb` — Neo4j-backed graph store (IGraphStore)
- `Ast.VectorDb` — Qdrant-backed vector store (IVectorStore)
- `CodeIntelligence` — code analysis, detection, scoring

The gap: no persistent agent memory layer that records what agents learn and uses it to improve
future interactions.

## Decision

Build an `AgentMemory` library that overlays agent-specific knowledge (episodes, expertise) onto
the existing code graph. The design draws from three key systems:

| System | Key idea adopted |
|--------|-----------------|
| **MAGMA** (arXiv:2601.03236) | Multi-graph with orthogonal views (semantic, temporal, causal, entity) |
| **Zep/Graphiti** (arXiv:2501.13956) | Temporal knowledge graph with dynamic synthesis |
| **Mem0** (arXiv:2504.19413) | Scalable memory with graph consolidation, 90% latency reduction |

### Architecture — Three Memory Types

Agent memory does **not** duplicate the structural/relational graph already in Ast.Core. Instead
it adds two new memory types that reference the existing code graph:

```
┌─────────────────────────────────────────────────────────┐
│                    Code Graph (existing)                 │
│  HypergraphNode ──Hyperedge──▶ HypergraphNode           │
│  (classes, methods, properties, call chains, DI, etc.)  │
└──────────────┬──────────────────────────┬────────────────┘
               │ references               │ references
     ┌─────────▼─────────┐     ┌──────────▼──────────┐
     │  Episodic Memory   │     │  Semantic Memory     │
     │  (Episodes)        │────▶│  (ExpertiseRecords)  │
     │                    │     │                      │
     │  • What happened   │     │  • Patterns learned  │
     │  • Files touched   │     │  • Area expertise    │
     │  • Problems found  │     │  • Co-change groups  │
     │  • Solutions used   │     │  • Conventions       │
     │  • Outcome          │     │  • Confidence scores │
     └────────────────────┘     └──────────────────────┘
```

### Why not reuse HypergraphNode for memory?

1. HypergraphNode requires `Language`, `FilePath`, `LineStart`—memory records are not code.
2. Neo4jGraphStore persists nodes as `:CodeNode` and drops `Metadata`.
3. Memory needs fields (outcome, confidence, evidence, timestamps) that are the core data,
   not incidental metadata.
4. Separate models enable independent evolution without coupling agent logic to code analysis.

### Why a separate MemoryEdgeType?

`HyperedgeType` has ~40 code-extraction values. Adding `AgentVisited`, `LearnedPattern`,
`DerivedFrom` would couple memory semantics to code-analysis semantics and break existing
query APIs (`GetCallersAsync`, `AnalyzeImpactAsync`).

## Models

### Episode

Records a single agent interaction with the codebase:

```csharp
class Episode
{
    string Id
    DateTime Timestamp
    string AgentId
    string TaskDescription
    EpisodeType Type           // Investigation, BugFix, Refactor, Feature, Review
    EpisodeOutcome Outcome     // Success, Failed, Partial, Abandoned
    string Summary
    List<string> TouchedFiles
    List<string> TouchedSymbolIds  // references to HypergraphNode.Id
    List<string> Tags
    string? CommitSha
    string? Branch
    Dictionary<string, string> Observations   // key insights
    Dictionary<string, string> Evidence        // supporting data
    TimeSpan Duration
}
```

### ExpertiseRecord

Consolidated knowledge derived from episodes:

```csharp
class ExpertiseRecord
{
    string Id
    string Subject            // file path, type name, project, or pattern name
    ExpertiseCategory Category // FileExpertise, TypeExpertise, PatternKnowledge, Convention
    double Confidence         // 0.0 to 1.0
    int EpisodeCount          // how many episodes contributed
    List<string> DerivedFromEpisodeIds
    List<string> PositiveEvidence
    List<string> NegativeEvidence
    string Summary
    DateTime FirstObserved
    DateTime LastValidated
    string? CommitSha         // last commit when this was validated
    int AccessCount           // reinforcement counter
    Dictionary<string, string> Properties
}
```

### MemoryEdge

Links between memory records and code nodes:

```csharp
enum MemoryEdgeType
{
    EpisodeTouchedFile,      // episode → file node
    EpisodeTouchedSymbol,    // episode → code symbol
    EpisodeProducedExpertise,// episode → expertise
    ExpertiseAboutSymbol,    // expertise → code symbol
    ExpertiseAboutFile,      // expertise → file
    CoChangedWith,           // symbol ↔ symbol (derived from episodes or git)
    RelatedEpisode,          // episode → episode (temporal/causal link)
    SupersededBy             // expertise → newer expertise
}
```

### ContextWindow

The assembled context for an agent task:

```csharp
class ContextWindow
{
    string TaskDescription
    List<StructuralContext> Structure    // repo/project/namespace hierarchy
    List<RelationalContext> Relations    // relevant call chains and dependencies
    List<Episode> RelevantEpisodes      // past interactions with these code areas
    List<ExpertiseRecord> Expertise     // accumulated knowledge
    double RelevanceScore
    Dictionary<string, double> SymbolFamiliarity  // per-symbol expertise scores
}
```

## Key Interfaces

```csharp
interface IAgentMemoryStore
{
    // Episodes
    Task RecordEpisodeAsync(Episode episode, CancellationToken ct)
    Task<Episode?> GetEpisodeAsync(string id, CancellationToken ct)
    Task<IReadOnlyList<Episode>> GetEpisodesAsync(EpisodeFilter filter, CancellationToken ct)

    // Expertise
    Task UpsertExpertiseAsync(ExpertiseRecord record, CancellationToken ct)
    Task<ExpertiseRecord?> GetExpertiseAsync(string id, CancellationToken ct)
    Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseForSubjectAsync(string subject, CancellationToken ct)

    // Memory edges
    Task AddMemoryEdgeAsync(MemoryEdge edge, CancellationToken ct)
    Task<IReadOnlyList<MemoryEdge>> GetEdgesForNodeAsync(string nodeId, CancellationToken ct)
}

interface IEpisodeRecorder
{
    EpisodeBuilder StartEpisode(string agentId, string taskDescription, EpisodeType type)
}

interface IContextBuilder
{
    Task<ContextWindow> BuildContextAsync(ContextRequest request, CancellationToken ct)
}

interface IMemoryEvolver
{
    Task ConsolidateAsync(ConsolidationOptions options, CancellationToken ct)
    Task DecayAsync(DecayOptions options, CancellationToken ct)
}
```

## Retrieval Strategy

Following MAGMA's multi-graph traversal approach, context retrieval uses a staged pipeline:

### Stage 1: Candidate Generation
- **Exact matches**: file paths, symbol names from the task description
- **Graph neighbors**: walk call-chain edges from matched symbols (depth 1-2)
- **Semantic search**: embed task description, search vector DB for similar code
- **Recent episodes**: episodes touching the same files/symbols

### Stage 2: Reranking
Score each candidate using weighted features:
- Task/symbol match (exact name match)
- Graph distance (hops from primary symbols)
- Semantic similarity (cosine distance)
- Recency (episodes within last N days)
- Episode outcome (successful episodes weighted higher)
- Expertise confidence score
- Code freshness (commits since last validation)

### Stage 3: Assembly
Combine top-ranked candidates into a ContextWindow, respecting token budget.

## Memory Evolution

### Consolidation (Episodes → Expertise)
When 3+ episodes touch the same symbol/file with successful outcomes, consolidate into
an ExpertiseRecord. Evidence from individual episodes is aggregated.

### Decay
- Time-based: reduce confidence by 10% per month of inactivity
- Code-churn: reduce confidence when CommitSha diverges (file was modified)
- Access-based: reinforce confidence when expertise is retrieved and validated

### Validation
When expertise is retrieved for a new task, compare `CommitSha` against current HEAD.
If the referenced code has changed, mark expertise as stale (reduce confidence).

## Consequences

### Positive
- Agents accumulate knowledge across sessions
- Context retrieval provides targeted, relevant information
- Expertise records capture institutional knowledge
- Separate models don't couple memory to code analysis

### Negative
- Additional storage requirements for episodes and expertise
- Consolidation logic adds complexity
- Stale expertise can mislead if decay/validation is misconfigured

### Risks
- Over-reliance on agent-derived co-change vs. git-based co-change
  → Mitigation: seed co-change from git log in future iteration
- Memory bloat from excessive episode recording
  → Mitigation: consolidation reduces episode volume; configurable retention

## Research Strategy

This design was informed by the Graph-based Agent Memory survey (arXiv:2602.05665, DEEP-PolyU)
and 25+ related papers. Below are the key systems evaluated and what we adopted or rejected.

### Adopted

| System | Paper | What we adopted |
|--------|-------|-----------------|
| **MAGMA** | arXiv:2601.03236 | Multi-signal retrieval with orthogonal views (semantic/temporal/causal). Our ContextBuilder uses staged pipeline: candidate generation → multi-signal reranking → assembly. |
| **Graphiti/Zep** | arXiv:2501.13956 | Temporal validity via CommitSha on expertise records. Staleness detection by comparing commit SHAs. |
| **ExpeL** | AAAI'24, arXiv:2308.10144 | Experience distillation without fine-tuning. Our MemoryEvolver consolidates 3+ successful episodes into ExpertiseRecords with evidence aggregation. |
| **G-Memory** | arXiv:2506.07398 | Three-tier hierarchy: raw episodes (interaction) → expertise records (query) → pattern knowledge (insight). Maps to our Episode → ExpertiseRecord → PatternKnowledge category. |
| **xMemory** | arXiv:2602.02007 | Decouple-aggregate retrieval to avoid redundant similar results. Our reranking avoids pure cosine similarity in favor of multi-signal scoring. |
| **Mem0** | arXiv:2504.19413 | Scalable memory with graph consolidation. Our consolidation diversity gates (require multiple agents/sessions) prevent false expertise from loops. |
| **Reflexion** | NeurIPS'23 | Verbal reflection pattern. Our failure-pattern expertise derives insights from repeated failures. |

### Deferred (future iterations)

| System | Paper | Why deferred |
|--------|-------|-------------|
| **GraphRAG** | arXiv:2404.16130 | Community-based hierarchy with LLM-generated summaries. Excellent for top-down codebase comprehension but requires expensive LLM indexing. Will add when LLM budget is available. |
| **Trainable Graph Memory** | arXiv:2511.07800 | RL-optimized edge weights. Requires reward signal infrastructure (did the agent's fix work?). Deferred to Phase 3. |
| **MemGPT** | arXiv:2310.08560 | OS-inspired memory paging. Our current token budgets don't require paging, but the pattern is documented for future scaling. |
| **HyperGraphRAG** | arXiv:2503.21322 | N-ary relationships via hyperedges. Already supported by Ast.Core's existing HypergraphNode model. Will integrate when ContextBuilder connects to graph store. |

### Key research findings applied

1. **Iterative retrieval outperforms single-shot** (arXiv:2412.15266) — our staged pipeline
   implements multi-round candidate expansion before reranking.
2. **Half-life decay** instead of linear decay — more natural memory fading curve,
   configurable per deployment.
3. **Diversity gates for consolidation** — prevents single-agent loops from creating
   false expertise (critique-driven refinement of the ExpeL threshold).
4. **Evidence as list, not map** — avoids silent data loss when multiple observations
   share the same category key (critique-driven model refinement).
5. **Touch-type differentiation** — read vs. modify vs. test gives higher signal
   for retrieval weighting (critique-driven model refinement).
