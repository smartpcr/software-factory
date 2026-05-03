# Agent Memory — Implementation Plan

> **Work Item**: Agent Memory (WI-1035)
> **Status**: Planning (iteration 3)
> **Research Basis**: [Graph-based Agent Memory Survey](https://arxiv.org/abs/2602.05665) (DEEP-PolyU)

## Problem Statement

Agents start every session blank — re-discovering repo structure, re-tracing call chains, and re-learning conventions. The `AgentMemory` library already provides the domain model (Episode, ExpertiseRecord, CallChain, MemoryEdge), two storage backends (in-memory and file-system), a staged ContextBuilder, and a MemoryEvolver.

**Iteration 2 gaps identified by evaluator:**
1. **Tier model not enforced** — `ExpertiseRecord.Tier` always defaults to `MemoryTier.Interaction` regardless of category. `TierMapping.DefaultTierFor()` and `IsValidCombination()` exist but are never called on creation or upsert. Records for `FailurePattern`, `PatternKnowledge`, `NamespaceExpertise`, and `CoChangeGroup` can be stored at incorrect tiers.
2. **All stores accept invalid combinations** — `UpsertExpertiseAsync` in both `InMemoryAgentMemoryStore` and `FileSystemAgentMemoryStore` blindly persists whatever tier is on the record.
3. **No migration/normalization for persisted records** — When enforcement begins, existing file-system records with wrong tiers would silently remain incorrect.
4. **Concrete bug** — `ConsolidateFailurePatternAsync` creates `FailurePattern` expertise at `Interaction` tier (the class default) even though `TierMapping` maps `FailurePattern` → `Query`.
5. **Mutable reference bypass** — `ExpertiseRecord.Tier` and `.Category` are mutable. Callers can fetch a record, mutate `Tier` to an invalid value, and the in-memory store's reference is already corrupted before any validation runs.

**Remaining work:** robust symbol identity, tier enforcement as a centralized policy with immutability guarantees, orchestrator integration, persistent graph/vector backends, knowledge extraction from repo structure and call chains, a feedback loop with observability, and CLI/MCP surfaces.

### Key Constraints

- **Persistence boundary**: `Ast.GraphDb.IGraphStore` stores nodes as `:CodeNode` — memory needs separate labels and domain-specific queries. Memory nodes reference code nodes via cross-label relationships.
- **Symbol identity**: Identifiers must be canonical and stable across sessions, handling renames, worktrees, and branch drift. Each symbol carries a validity window (`FirstSeenCommit`, `LastSeenCommit`).
- **Provenance**: Records originate from git history (ground truth), static analysis (inferred), and agent episodes (observed) — each weighted differently in retrieval.
- **Cross-backend invariant**: Tier enforcement must be a cross-cutting concern applied by every store implementation (in-memory, file-system, and future Neo4j/Qdrant). The policy is defined once; backends call it uniformly.

## Architectural Approach

The design adopts a **hybrid hierarchical graph** from the survey taxonomy:

1. **MAGMA multi-graph** (arXiv:2601.03236) — orthogonal semantic, temporal, and structural views; policy-guided traversal for query-adaptive retrieval and multi-signal reranking in ContextBuilder.
2. **G-Memory 3-tier hierarchy** (arXiv:2506.07398) — Interaction → Query → Insight tiers with centralized tier enforcement policy and promotion pipeline.
3. **Zep/Graphiti temporal KG** (arXiv:2501.13956) — temporal validity via `CommitSha`; git-seeded co-change grounds temporal edges.
4. **Mem0 consolidation** (arXiv:2504.19413) — diversity-gated episode aggregation preventing false expertise.
5. **ExpeL distillation** (AAAI'24) — failure patterns from repeated failures; verbal reflection without fine-tuning.

| Decision | Rationale |
|----------|-----------|
| **Centralized `TierPolicy` normalize-and-validate** | Store-side enforcement as source of truth; model defaults are convenience only. All stores, seeders, and evolver paths call the same policy. Exhaustive category mapping fails fast for unknown categories. `Normalize()` corrects **any** invalid tier (not just `Interaction` default), computing `DefaultTierFor(category)` and promoting to it when the current tier is lower. |
| **Immutable tier/category on `ExpertiseRecord`** | `Category` becomes `init`-only. `Tier` becomes `init`-only on the record; only `TierPolicy.Normalize()` returns a corrected copy. Stores return defensive copies to prevent caller mutation of shared state. This closes the mutable-reference bypass. |
| **Upsert validates against persisted state** | `Validate()` compares incoming tier against any existing record's tier for the same ID; rejects demotions (new tier < existing tier) unless the record has been invalidated. This prevents accidental downgrades. |
| **Migration step for legacy records** | All persisted backends auto-repair on load via `TierPolicy.Normalize()`. Tests cover upgrade from iteration-2 data. Counter exposed in `MemoryStatistics`. |
| **Promotion transforms both category and tier** | `FileExpertise → NamespaceExpertise` on Interaction→Query; `NamespaceExpertise → ArchitecturalKnowledge` on Query→Insight. Same-category promotion is disallowed to keep tier semantics clean. |
| Early vertical slice with eval harness | Validate record → retrieve → measure loop before investing in Neo4j/Qdrant |
| Separate `IMemoryGraphStore` | Code-graph store lacks memory CRUD, drops metadata, hardcodes `:CodeNode` |
| Canonical identity with validity windows | Handles renames, partial classes, branch drift across sessions |
| Provenance on all records | Retrieval weights `git-seeded` > `inferred` > `observed` differently |

---

## Phase 1 — Tier Enforcement & Domain Hardening

**Primary goal**: Close every evaluator finding from iteration 2. Deliver centralized tier enforcement with immutability, store-level validation, migration for persisted records, and tests proving the FailurePattern bug is fixed.

### Stage 1.1 — TierPolicy, Immutability & Store Enforcement

- **Step 1.1.1**: Create centralized `TierPolicy` static class — `Normalize(ExpertiseRecord)` returns a copy with tier corrected to `TierMapping.DefaultTierFor(category)` when the current tier is **lower** than the category's default (not just when tier == `Interaction`). `Validate(ExpertiseRecord, ExpertiseRecord? existing)` throws `InvalidOperationException` when: (a) tier < `DefaultTierFor(category)` after normalization, (b) tier < existing record's tier (demotion), (c) category is not a defined enum value. Exhaustive switch with no wildcard fallback. Make `ExpertiseRecord.Category` `init`-only. Make `ExpertiseRecord.Tier` `init`-only; tier changes produce new record copies via `TierPolicy.WithTier(record, tier)`. Tests: each `ExpertiseCategory` → correct default tier; demotion is rejected; explicit valid higher tier is accepted; omitted tier is auto-assigned; undefined category is rejected.
  `expectedFileChanges`: 6

- **Step 1.1.2**: Enforce `TierPolicy` in all store upsert paths — Update `InMemoryAgentMemoryStore.UpsertExpertiseAsync()` and `FileSystemAgentMemoryStore.UpsertExpertiseAsync()` to call `TierPolicy.Normalize` then `TierPolicy.Validate(record, existingRecord)` before persisting. Stores return defensive copies from read methods (`GetExpertiseAsync`, `GetExpertiseForSubjectAsync`, `GetExpertiseByCategoryAsync`, `GetExpertiseByTierAsync`) to prevent caller mutation of internal state. Tests: round-trip upsert/read returns correct tier; mutation of returned record does not affect store; demotion upsert throws.
  `expectedFileChanges`: 6

- **Step 1.1.3**: Fix all `ExpertiseRecord` creation sites in `MemoryEvolver` — Update `ConsolidateFailurePatternAsync` to set `Category = FailurePattern` and rely on `TierPolicy.Normalize` (applied at store) to assign `Query` tier. Update `ConsolidateFileExpertiseAsync`, `ConsolidateSymbolExpertiseAsync`, `CreatePromotedExpertiseAsync`, and `DetectCoChangesAsync` to create records with `init`-only category/tier pattern. Verify co-change group creation (currently sets `Query` explicitly) remains correct. Tests: ConsolidateFailurePattern produces record at Query tier; promoted records have correct target category and tier; co-change groups are at Query tier.
  `expectedFileChanges`: 4

### Stage 1.2 — Migration, Configuration & Baseline Tests

- **Step 1.2.1**: Legacy record migration and statistics — `FileSystemAgentMemoryStore` auto-repair on load: iterate all persisted expertise records, apply `TierPolicy.Normalize`, persist corrected records. Add `AutoNormalizedCount` and `InvalidTierRejectedCount` counters to `MemoryStatistics`. Add `TierDistributionByCategory` (dict of category → dict of tier → count) to `MemoryStatistics`. Tests: load a fixture file with iteration-2 data (FailurePattern at Interaction), verify records are repaired and counters incremented.
  `expectedFileChanges`: 5

- **Step 1.2.2**: Feature flags and memory configuration — `AgentMemoryOptions` with `EnableNeo4j`, `EnableQdrant`, `FallbackToFileSystem`, `StoragePath`. Wire into `ServiceCollectionExtensions`. `IMemoryStoreHealthCheck` interface. Tests for store selection and fallback behavior.
  `expectedFileChanges`: 5

- **Step 1.2.3**: Baseline evaluation harness — `IMemoryMetrics` with context hit rate, retrieval latency, invalid-tier-encountered, auto-normalized-records counters. Structured logging for hit/miss events. 3 golden replay scenarios as test fixtures verifying context assembly against known expectations (including tier-ordered retrieval: Insight → Query → Interaction). Tier distribution by category in metrics output.
  `expectedFileChanges`: 7

---

## Phase 2 — Identity, Orchestrator Integration & Context Injection

Wire agent memory into the agent lifecycle: canonical identity, automatic episode recording, and context injection before task dispatch.

### Stage 2.1 — Canonical Symbol Identity

- **Step 2.1.1**: Symbol identity resolver — `SymbolIdentity` static class with `NormalizePath(repoRoot, absolutePath)` and `CanonicalSymbolId(namespace, typeName, memberName)`. Validity window fields (`FirstSeenCommit`, `LastSeenCommit`). Tests for drive letters, worktrees, renames, case sensitivity. Update `EpisodeRecorder` to use canonical paths.
  `expectedFileChanges`: 7

### Stage 2.2 — Orchestrator Episode Recording

- **Step 2.2.1**: Episode recording middleware — Hook `IEpisodeRecorder` into `CopilotAgentOrchestrator`. Auto-start/complete episodes on task lifecycle. Track touched files from tool calls (view, edit, create). Classify `TouchType` from tool name (`view` → `Read`, `edit` → `Modified`, `create` → `Created`). Persist `TouchType` on `MemoryEdge` via `EpisodeBuilder.CreateEdgesAsync()`.
  `expectedFileChanges`: 8

- **Step 2.2.2**: Symbol resolution for touched files — Resolve touched files to `HypergraphNode` IDs via `IGraphStoreAdapter`. Populate `TouchedSymbolIds`. Graceful fallback for file-only episodes when graph unavailable.
  `expectedFileChanges`: 5

### Stage 2.3 — Context Injection & Provenance

- **Step 2.3.1**: Retrieval contract and context injection — Define retrieval contract (token budget, max items per source, confidence threshold, low-confidence behavior). ContextBuilder uses `ITieredExpertiseStore` to query by tier: Insight records first (architectural context), then Query-tier (namespace/pattern), then Interaction-tier (file-level). Add `IContextBuilder.BuildContextAsync()` call before task dispatch. Format `ContextWindow` as markdown sections with per-source attribution.
  `expectedFileChanges`: 8

- **Step 2.3.2**: Provenance model — Add `Provenance` enum (`GitSeeded`, `Inferred`, `Observed`) to `MemoryEdge`, `CallChain`, `ExpertiseRecord`. Source-of-truth rules per entity type. Retrieval weights ground-truth provenance higher. Tests verify provenance is set on all creation paths.
  `expectedFileChanges`: 6

---

## Phase 3 — Persistent Graph Backend & Code Graph Seeding

Connect to Neo4j for durable graph memory. Seed the initial knowledge graph from top-down repo structure, call chains, and git history. All store implementations call `TierPolicy.Normalize` + `TierPolicy.Validate` on upsert (cross-backend invariant).

### Stage 3.1 — Neo4j Memory Graph Store

- **Step 3.1.1**: Schema DDL and migration — Cypher for labels (`:MemoryEpisode`, `:MemoryExpertise`, `:MemoryCallChain`), relationships (`:TOUCHED_FILE`, `:TOUCHED_SYMBOL`, `:PRODUCED_EXPERTISE`, `:CO_CHANGED_WITH`), indices, constraints. Idempotent `MemorySchemaMigration.ApplyAsync()`. Include tier property on `:MemoryExpertise` nodes.
  `expectedFileChanges`: 7

- **Step 3.1.2**: Episode and expertise CRUD — `Neo4jMemoryStore` implementing `IAgentMemoryStore` and `ITieredExpertiseStore`. Cypher for CRUD operations. `UpsertExpertiseAsync` calls `TierPolicy.Normalize` + `Validate`. Defensive copies on read. Integration tests with Testcontainers.
  `expectedFileChanges`: 12

- **Step 3.1.3**: Edges, call chains, and statistics — `AddMemoryEdgeAsync` with `TouchType` property on relationship, `GetOutgoingEdgesAsync`/`GetIncomingEdgesAsync` via Cypher traversals. `ICallChainStore` with ordered list serialization. `GetStatisticsAsync` with tier distribution by category. Integration tests.
  `expectedFileChanges`: 10

- **Step 3.1.4**: DI registration and health checks — `services.AddNeo4jAgentMemory(connectionString)`. Health checks, configuration binding, docker-compose entry.
  `expectedFileChanges`: 5

### Stage 3.2 — Structural Memory Seeder

- **Step 3.2.1**: Solution and project hierarchy — Walk `HypergraphData.Nodes` grouped by project. Create `ProjectExpertise` records (auto-assigned Query tier via `TierPolicy`). `ContainedIn` edges for solution → project → namespace. Include folder/module boundaries and assembly references. Invalidation: detect removed/added projects.
  `expectedFileChanges`: 8

- **Step 3.2.2**: Namespace, type, and API surface seeding — `NamespaceExpertise` (Query tier), `TypeExpertise` (Interaction tier), `MethodExpertise` (Interaction tier) for public/internal methods. Detect entry points and API endpoints from `HyperedgeType.ApiEndpoint`, `EntryPoint`. `ContainedIn` edges namespace → type. Invalidation: detect renamed/moved types, signature changes.
  `expectedFileChanges`: 10

### Stage 3.3 — Call Chain & Co-Change Extraction

- **Step 3.3.1**: Static call chain extractor — Walk `MethodInvocation` edges depth-first from entry points. Build `CallChain` with `CallChainSource.Inferred`. Max depth 8, cycle detection. Separate static invocation edges from DI resolution edges. Follow `ServiceRegistration` edges for DI chains. Create `CallChainPattern` expertise (Query tier). Confidence-scored, partial graphs.
  `expectedFileChanges`: 10

- **Step 3.3.2**: Git co-change seeder — Parse `git log --name-only`. Compute Jaccard similarity. Gates: commit size ≤50 files, file-type filters, repeated co-change count ≥3. `CoChangedWith` edges with `Provenance.GitSeeded`. Incremental via last-processed commit SHA watermark. Idempotent replay.
  `expectedFileChanges`: 8

---

## Phase 4 — Feedback, Evolution & Observability

Close the feedback loop and schedule memory evolution. Promotion pipeline creates records at correct target tier with mandatory category transformation (FileExpertise→NamespaceExpertise on Interaction→Query, NamespaceExpertise→ArchitecturalKnowledge on Query→Insight). All created records pass through `TierPolicy`.

### Stage 4.1 — Feedback Loop

- **Step 4.1.1**: Outcome-based reinforcement — Reinforce expertise confidence on `Success`, record negative evidence on `Failed`. Update `LastValidated`, `CommitSha`, `AccessCount`. Tests verify confidence changes and evidence accumulation.
  `expectedFileChanges`: 5

- **Step 4.1.2**: Staleness detection and invalidation — Compare `CommitSha` against HEAD. Detect renamed/moved symbols via `SymbolIdentity` validity window. Stale expertise gets freshness warnings. Auto-decay on confirmed invalidation.
  `expectedFileChanges`: 5

### Stage 4.2 — Memory Evolution Scheduling

- **Step 4.2.1**: Evolver checkpointing — High-water mark for last-processed episode, last-decay-run, last-promotion-run. Skip already-processed episodes. Tests verify no reprocessing.
  `expectedFileChanges`: 6

- **Step 4.2.2**: Background hosted service — `IHostedService` running consolidation, decay, promotion on configurable intervals. Lease/lock for concurrency. Log evolution statistics including tier distribution changes.
  `expectedFileChanges`: 5

### Stage 4.3 — Vector Integration & Extended Observability

- **Step 4.3.1**: Vector memory adapter — `IVectorMemoryAdapter` with `IndexEpisodeAsync`, `IndexExpertiseAsync`, `SearchSimilarAsync`. Qdrant collections: `agent_memory_episodes`, `agent_memory_expertise`. Semantic search as candidate generation step in ContextBuilder. Graceful fallback when unavailable.
  `expectedFileChanges`: 10

- **Step 4.3.2**: Full retrieval metrics and health dashboard — Extend Phase 1 metrics with stale-memory rate, tier distribution by category, per-provenance hit rates, context token budget usage, auto-normalized record count, invalid-tier rejection count. `memory health` command: hit rate, stale rate, tier distribution, promotion count by category pair, top expertise. A/B feature flag for with-memory vs without-memory.
  `expectedFileChanges`: 8

---

## Phase 5 — CLI & MCP Surfaces

Expose memory operations to humans (CLI) and agents (MCP tools).

### Stage 5.1 — MCP Memory Tools

- **Step 5.1.1**: MCP query tools — `memory_search` (find expertise by tier/category/subject), `memory_context` (build context window), `memory_stats` (statistics with tier distribution). Register as MCP tool handlers. Tests with mock store.
  `expectedFileChanges`: 10

- **Step 5.1.2**: MCP record and evolve tools — `memory_record_observation`, `memory_consolidate`, `memory_decay`. Tests with mock store.
  `expectedFileChanges`: 6

### Stage 5.2 — CLI Memory Commands

- **Step 5.2.1**: CLI inspect commands — `memory stats`, `memory list-episodes`, `memory list-expertise`, `memory show <id>`. Table-formatted output with confidence bars, tier indicators, and category→tier validation status via Spectre.Console.
  `expectedFileChanges`: 10

- **Step 5.2.2**: CLI management commands — `memory consolidate`, `memory decay`, `memory seed`, `memory reindex`, `memory clear`. Progress bars.
  `expectedFileChanges`: 7

---

## Out of Scope

- **GraphRAG community detection** (arXiv:2404.16130) — Deferred until Phase 4.3 metrics show simple graph + vector retrieval is insufficient.
- **Full temporal knowledge graph** (arXiv:2501.13956) — `ValidFrom`/`ValidTo` windows on edges. Current `CommitSha` + `LastValidated` suffices.
- **LLM-generated community summaries** — Requires LLM API budget not yet established.
- **RL-based memory optimization** (arXiv:2511.07800) — Trainable edge weights need reward signal infrastructure.
- **Multi-agent collaborative memory** (arXiv:2505.18279) — Beyond current single-agent architecture.
- **MemGPT-style memory paging** (arXiv:2310.08560) — Context windows fit within limits.
- **Fine-tuning or model adaptation** — Memory is retrieved at inference time.
- **Academic dataset benchmarking** (LoCoMo, LongMemEval) — Not actionable for the product.
- **Reusing `IGraphStore` for memory** — Lacks memory CRUD, drops metadata, hardcodes `:CodeNode`.
- **Same-category tier promotion** — Promotion must transform category to maintain semantic clarity (e.g., `FileExpertise` cannot be promoted to `Insight` without becoming `ArchitecturalKnowledge`).
- **Composite multi-backend store** — Outbox-style dispatch to Neo4j + Qdrant. Deferred until both backends exist and consistency rules are validated.