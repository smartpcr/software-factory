# Implementation Plan — Agent Memory (code-intelligence:AGENT-MEMORY)

---

## Overview

This plan decomposes the agent memory feature into six phases. Each phase is
independently testable and deployable. The phases are ordered so that each
builds on the previous one's persistence layer and can be validated in
isolation before the next begins.

**Total estimated phases**: 6
**Dependency chain**: Phase 1 → Phase 2 → Phase 3 (parallel with Phase 4) → Phase 5 → Phase 6

---

## Phase 1 — Schema & Persistence Layer

**Goal**: Add the five new tables to the Forge SQLite database and register
them in `ForgeDbContext`. No business logic — just the storage foundation.

### Tasks

| # | Task | Files |
|---|------|-------|
| 1.1 | Define entity classes: `MemoryNodeRow`, `MemoryEdgeRow`, `MemoryFactRow`, `MemoryEpisodeRow`, `MemoryEpisodeNodeRow` | `Persistence/Entities.cs` (append) |
| 1.2 | Register `DbSet<>` for each new entity in `ForgeDbContext` | `Persistence/ForgeDbContext.cs` |
| 1.3 | Configure table names, primary keys, indexes, and foreign keys in `OnModelCreating` | `Persistence/ForgeDbContext.cs` |
| 1.4 | Create `Phase34Migration.cs` (or next available phase number) with `CREATE TABLE` + `CREATE INDEX` DDL | `Persistence/Phase34Migration.cs` (new) |
| 1.5 | Add `MemoryOptions` configuration class bound to `Forge:Memory` | `Services/MemoryOptions.cs` (new) |
| 1.6 | Wire `MemoryOptions` in `Program.cs` DI registration | `Program.cs` |

### Entity Definitions

```csharp
public class MemoryNodeRow
{
    public string Id { get; set; } = string.Empty;        // {repoId}:{type}:{path}
    public string RepoId { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;  // directory, file, namespace, type, member
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? Metadata { get; set; }                  // JSON blob (modifiers, return type, etc.)
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public class MemoryEdgeRow
{
    public long Id { get; set; }
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string EdgeType { get; set; } = string.Empty;  // contains, declares, calls, inherits, implements, uses, imports
    public double Weight { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MemoryFactRow
{
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string FactKey { get; set; } = string.Empty;
    public string Assertion { get; set; } = string.Empty;
    public double Confidence { get; set; } = 1.0;
    public string Source { get; set; } = string.Empty;     // agent-id or "system"
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SupersededAt { get; set; }
}

public class MemoryEpisodeRow
{
    public long Id { get; set; }
    public string RepoId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string InvocationId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;   // success, failure, partial
    public double? QualityScore { get; set; }
    public string? ReviewSummary { get; set; }
    public string FilesTouched { get; set; } = "[]";       // JSON array
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class MemoryEpisodeNodeRow
{
    public long EpisodeId { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string Role { get; set; } = "touched";          // touched, read, reviewed
}
```

### Indexes

- `memory_nodes`: `(RepoId, NodeType)`, `(RepoId, Path)`, `(ParentId)`
- `memory_edges`: `(FromNodeId)`, `(ToNodeId)`, `(FromNodeId, EdgeType)`
- `memory_facts`: `(NodeId, FactKey)` — unique where `SupersededAt IS NULL`
- `memory_episodes`: `(RepoId, AgentId, At)`, `(TaskId)`
- `memory_episode_nodes`: composite PK `(EpisodeId, NodeId)`

### Acceptance

- [ ] `dotnet build` succeeds.
- [ ] Migration runs cleanly on a fresh `forge.db`.
- [ ] Migration runs cleanly on an existing `forge.db` (backward-compatible).
- [ ] `ForgeDbContext` can CRUD all five entity types in a unit test.

---

## Phase 2 — Repo Graph Builder (Structure + Symbols)

**Goal**: Implement `IRepoGraphBuilder` with directory-tree scanning and
Roslyn-based symbol extraction.

### Tasks

| # | Task | Files |
|---|------|-------|
| 2.1 | Define `IRepoGraphBuilder` interface | `Services/Memory/IRepoGraphBuilder.cs` (new) |
| 2.2 | Implement `RepoGraphBuilder` — directory tree walk | `Services/Memory/RepoGraphBuilder.cs` (new) |
| 2.3 | Implement Roslyn symbol extraction (behind `MemoryOptions.RoslynEnabled`) | `Services/Memory/RoslynSymbolExtractor.cs` (new) |
| 2.4 | Implement regex-based fallback extractor | `Services/Memory/RegexSymbolExtractor.cs` (new) |
| 2.5 | Implement call-chain edge extraction (Roslyn semantic model) | `Services/Memory/CallChainAnalyzer.cs` (new) |
| 2.6 | Register `IRepoGraphBuilder` in DI | `Program.cs` |
| 2.7 | Add `POST /api/memory/{repoId}/rebuild` endpoint | `Endpoints/MemoryEndpoints.cs` (new) |
| 2.8 | Add `GET /api/memory/{repoId}/stats` endpoint | `Endpoints/MemoryEndpoints.cs` |
| 2.9 | Write integration test: build graph for `Forge.Api`, assert node/edge counts | `Forge.Api.Tests` |

### Implementation Notes

- `RoslynSymbolExtractor` uses `Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace`
  to open the solution. Each `Document` is compiled to get `SemanticModel`.
  Syntax walkers extract declarations; the semantic model resolves call targets.
- The builder wraps all inserts in a single EF `SaveChangesAsync` call per
  file batch (100 files per batch) to stay within SQLite transaction limits.
- Node IDs follow the `{repoId}:{nodeType}:{normalizedPath}` scheme, making
  upserts idempotent via `INSERT OR REPLACE`.
- The regex fallback uses patterns:
  ```
  (public|internal|private|protected)?\s*(static\s+)?(partial\s+)?(class|interface|struct|record|enum)\s+(\w+)
  (public|internal)\s+\S+\s+(\w+)\s*\(
  ```

### Acceptance

- [ ] `POST /api/memory/{repoId}/rebuild` for the Forge.Api repo completes in < 30 s.
- [ ] Directory and file nodes match the actual file count (±5 %).
- [ ] Type nodes for `EpicRow`, `ForgeDbContext`, `AgentRunnerDispatcher` exist.
- [ ] `calls` edge exists from `CrewQueueWorker` to `AgentRunnerDispatcher.StartAsync`.
- [ ] When Roslyn is disabled, the regex fallback still produces type nodes.

---

## Phase 3 — Memory Retrieval

**Goal**: Implement `IMemoryRetriever` — the read path that agents use to get
context from the graph.

### Tasks

| # | Task | Files |
|---|------|-------|
| 3.1 | Define `IMemoryRetriever`, `MemoryQuery`, `MemoryContext` | `Services/Memory/IMemoryRetriever.cs` (new) |
| 3.2 | Implement `MemoryRetriever` with multi-signal ranking | `Services/Memory/MemoryRetriever.cs` (new) |
| 3.3 | Implement token-budget-aware markdown renderer | `Services/Memory/MemoryContextRenderer.cs` (new) |
| 3.4 | Add `POST /api/memory/{repoId}/query` endpoint | `Endpoints/MemoryEndpoints.cs` |
| 3.5 | Add `GET /api/memory/{repoId}/preview` endpoint (agent-id + task-description → rendered context) | `Endpoints/MemoryEndpoints.cs` |
| 3.6 | Register `IMemoryRetriever` in DI | `Program.cs` |
| 3.7 | Write unit tests for ranking algorithm | `Forge.Api.Tests` |
| 3.8 | Write integration test: query with file hints → verify expected nodes in top-10 | `Forge.Api.Tests` |

### Ranking Details

The retriever executes in three phases:

1. **Candidate selection** — query `memory_nodes` where:
   - `RepoId` matches AND `DeletedAt IS NULL`
   - Path starts with any of the file-hint prefixes (directory containment), OR
   - Name contains any keyword from the task description (split on whitespace, stopwords removed).
   - Limit to 200 candidates.

2. **Expansion** — for each candidate, load 1-hop outbound edges (`calls`,
   `inherits`, `implements`). Add the target nodes to the candidate set if
   not already present. Cap at 500 total.

3. **Scoring** — apply the multi-signal formula. Sort descending. Take top-k
   (default k=20).

4. **Rendering** — group nodes into Structure / Symbols / Episodes sections.
   Attach facts (top-3 per node, highest confidence). Attach recent episodes
   (last 5 for the requesting agent). Count tokens; truncate.

### Acceptance

- [ ] Query with `fileHints: ["src/forge/Forge.Api/Persistence/Entities.cs"]`
  returns `Entities.cs` file node, `ForgeDbContext` type, and at least 3
  entity types in top-10.
- [ ] Rendered markdown is ≤ 4 000 tokens.
- [ ] Query with no hints returns a meaningful repo-level overview.
- [ ] Agent-affinity boost is measurable: an agent with prior episodes on a
  file ranks that file higher than an agent without.

---

## Phase 4 — Episode Writer & Fact Service

**Goal**: Implement the write path — recording agent interactions and managing
the fact store. Runs in parallel with Phase 3.

### Tasks

| # | Task | Files |
|---|------|-------|
| 4.1 | Define `IEpisodeWriter`, `EpisodeRecord` | `Services/Memory/IEpisodeWriter.cs` (new) |
| 4.2 | Implement `EpisodeWriter` | `Services/Memory/EpisodeWriter.cs` (new) |
| 4.3 | Define `IMemoryFactService` | `Services/Memory/IMemoryFactService.cs` (new) |
| 4.4 | Implement `MemoryFactService` (upsert with supersede semantics) | `Services/Memory/MemoryFactService.cs` (new) |
| 4.5 | Integrate `EpisodeWriter` into `CrewQueueWorker` completion handler | `Services/CrewQueueWorker.cs` |
| 4.6 | Add `POST /api/memory/facts` endpoint | `Endpoints/MemoryEndpoints.cs` |
| 4.7 | Add `GET /api/memory/{repoId}/episodes` endpoint | `Endpoints/MemoryEndpoints.cs` |
| 4.8 | Register `IEpisodeWriter`, `IMemoryFactService` in DI | `Program.cs` |
| 4.9 | Write unit test: episode write + read-back | `Forge.Api.Tests` |
| 4.10 | Write unit test: fact upsert + supersede | `Forge.Api.Tests` |

### Fact Supersede Logic

```sql
-- On upsert of (NodeId, FactKey):
UPDATE memory_facts
   SET SupersededAt = CURRENT_TIMESTAMP
 WHERE NodeId = @nodeId AND FactKey = @factKey AND SupersededAt IS NULL;

INSERT INTO memory_facts (NodeId, FactKey, Assertion, Confidence, Source, CreatedAt)
VALUES (@nodeId, @factKey, @assertion, @confidence, @source, CURRENT_TIMESTAMP);
```

### Acceptance

- [ ] After a simulated invocation, an episode row exists with correct fields.
- [ ] `memory_episode_nodes` join rows link the episode to the correct file nodes.
- [ ] Upserting a fact with the same key supersedes the old one (`SupersededAt` set).
- [ ] `GET /api/memory/{repoId}/episodes?agentId=X` returns only that agent's episodes.

---

## Phase 5 — Memory Evolution Service

**Goal**: Implement the `MemoryEvolver` hosted service that keeps the graph
in sync with codebase changes.

### Tasks

| # | Task | Files |
|---|------|-------|
| 5.1 | Implement `MemoryEvolver` as `IHostedService` | `Services/Memory/MemoryEvolver.cs` (new) |
| 5.2 | Subscribe to `WorktreeChangeFeed` snapshot diffs | `Services/Memory/MemoryEvolver.cs` |
| 5.3 | Implement debounce logic (5 s per repo) | `Services/Memory/MemoryEvolver.cs` |
| 5.4 | Call `IRepoGraphBuilder.UpdateAsync` on each debounced change batch | `Services/Memory/MemoryEvolver.cs` |
| 5.5 | Implement `IRepoGraphBuilder.UpdateAsync` — incremental add/modify/delete/rename | `Services/Memory/RepoGraphBuilder.cs` |
| 5.6 | Register `MemoryEvolver` as hosted service in DI | `Program.cs` |
| 5.7 | Add integration test: simulate file add → verify new node appears | `Forge.Api.Tests` |
| 5.8 | Add integration test: simulate file delete → verify node soft-deleted | `Forge.Api.Tests` |
| 5.9 | Add integration test: simulate file rename → verify path updated | `Forge.Api.Tests` |

### WorktreeChangeFeed Integration

The `MemoryEvolver` does **not** modify `WorktreeChangeFeed`. Instead, it
registers a callback by subscribing to the change feed's `Snapshot` output.
Implementation approach:

```csharp
// In MemoryEvolver.StartAsync:
this.changeFeed.OnSnapshotChanged += this.HandleSnapshotChanged;

// Or: poll changeFeed.GetLatest(worktreePath) on a timer,
// comparing with the last-seen snapshot.
```

Since `WorktreeChangeFeed` currently has no event/callback mechanism, Phase 5
adds a lightweight `Action<string, Snapshot>? OnSnapshotChanged` event to
`WorktreeChangeFeed`. The change is additive (no breaking change to existing
consumers).

### Acceptance

- [ ] After a file is created in the worktree, the evolver creates a
  corresponding `file` node within 10 s.
- [ ] After a file is deleted, the node's `DeletedAt` is set.
- [ ] After a file is renamed, the node's `Path` is updated (same `Id`).
- [ ] Modified C# files have their symbol nodes refreshed.
- [ ] Debounce works: 10 rapid file changes produce at most 2 evolution cycles.

---

## Phase 6 — Agent Prompt Integration & Wiring

**Goal**: Wire everything together so agents actually consume memory context
during invocations.

### Tasks

| # | Task | Files |
|---|------|-------|
| 6.1 | Modify `CrewQueueWorker` to call `IMemoryRetriever` before dispatching | `Services/CrewQueueWorker.cs` |
| 6.2 | Prepend `MemoryContext.MarkdownBlock` to the agent prompt (after soul.md, before task description) | `Services/CrewQueueWorker.cs` |
| 6.3 | Add `memory.fact` tool registration in `AgentToolHost` | `Agents/AgentToolHost.cs` |
| 6.4 | Handle `memory.fact` tool calls: delegate to `IMemoryFactService.UpsertAsync` | `Agents/AgentToolHost.cs` |
| 6.5 | Add `GET /api/memory/{repoId}/nodes` and `GET /api/memory/{repoId}/nodes/{nodeId}` endpoints | `Endpoints/MemoryEndpoints.cs` |
| 6.6 | Add `GET /api/memory/{repoId}/graph` endpoint (subgraph query) | `Endpoints/MemoryEndpoints.cs` |
| 6.7 | Trigger initial graph build when a repo is first registered (`POST /api/repos`) | `Endpoints/ReposEndpoints.cs` (modify) |
| 6.8 | Feature-gate all memory integration behind `MemoryOptions.Enabled` | All modified files |
| 6.9 | Write end-to-end integration test: full lifecycle (build → retrieve → invoke → episode) | `Forge.Api.Tests` |
| 6.10 | Update `appsettings.json` with default `Forge:Memory` section | `appsettings.json` |

### Prompt Assembly Order

```
1. Soul.md body (agent identity + system prompt)
2. ── Memory Context ──  ← NEW
   ## Repository Context (from memory)
   ### Structure
   ...
   ### Related Symbols
   ...
   ### Prior Experience
   ...
3. ── Task Description ──
   {task description from work item}
```

### Acceptance

- [ ] With `Memory.Enabled = true`, an agent invocation's prompt contains
  the `## Repository Context (from memory)` section.
- [ ] With `Memory.Enabled = false`, prompts are unchanged from baseline.
- [ ] The `memory.fact` tool call from an agent invocation creates a fact row.
- [ ] Full lifecycle test passes: repo registered → graph built → task
  dispatched → memory context in prompt → invocation completes → episode
  written → subsequent retrieval reflects the episode.

---

## File Inventory (New Files)

| File | Phase |
|------|-------|
| `Services/Memory/IRepoGraphBuilder.cs` | 2 |
| `Services/Memory/RepoGraphBuilder.cs` | 2 |
| `Services/Memory/RoslynSymbolExtractor.cs` | 2 |
| `Services/Memory/RegexSymbolExtractor.cs` | 2 |
| `Services/Memory/CallChainAnalyzer.cs` | 2 |
| `Services/Memory/IMemoryRetriever.cs` | 3 |
| `Services/Memory/MemoryRetriever.cs` | 3 |
| `Services/Memory/MemoryContextRenderer.cs` | 3 |
| `Services/Memory/IEpisodeWriter.cs` | 4 |
| `Services/Memory/EpisodeWriter.cs` | 4 |
| `Services/Memory/IMemoryFactService.cs` | 4 |
| `Services/Memory/MemoryFactService.cs` | 4 |
| `Services/Memory/MemoryEvolver.cs` | 5 |
| `Services/Memory/MemoryOptions.cs` | 1 |
| `Endpoints/MemoryEndpoints.cs` | 2–6 |
| `Persistence/Phase34Migration.cs` | 1 |

## Files Modified

| File | Phase | Change |
|------|-------|--------|
| `Persistence/Entities.cs` | 1 | Append 5 entity classes |
| `Persistence/ForgeDbContext.cs` | 1 | Add DbSets + OnModelCreating config |
| `Program.cs` | 1, 6 | DI registrations, hosted service |
| `Services/CrewQueueWorker.cs` | 4, 6 | Episode write + memory retrieval |
| `Agents/AgentToolHost.cs` | 6 | `memory.fact` tool |
| `Services/WorktreeChangeFeed.cs` | 5 | Add `OnSnapshotChanged` event |
| `appsettings.json` | 6 | Default Memory config section |
