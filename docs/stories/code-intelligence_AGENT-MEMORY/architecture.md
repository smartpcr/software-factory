# Architecture — Agent Memory (code-intelligence:AGENT-MEMORY)

---

## 1  System Context

```
┌──────────────────────────────────────────────────────────────────┐
│                        Forge.Api Process                        │
│                                                                  │
│  ┌──────────────┐   ┌────────────────┐   ┌───────────────────┐  │
│  │ Agent Runner  │──▶│ MemoryRetriever│──▶│  Agent Prompt +   │  │
│  │ (StartAsync)  │   │                │   │  Memory Context   │  │
│  └──────┬───────┘   └───────▲────────┘   └───────────────────┘  │
│         │ episode            │ query                              │
│         ▼                    │                                    │
│  ┌──────────────┐   ┌───────┴────────┐   ┌───────────────────┐  │
│  │ EpisodeWriter │   │   RepoGraph    │◀──│  MemoryEvolver    │  │
│  │ (on finish)   │──▶│   (SQLite)     │   │  (IHostedService) │  │
│  └──────────────┘   └───────▲────────┘   └───────┬───────────┘  │
│                              │                    │               │
│                     ┌────────┴───────┐   ┌───────▼───────────┐  │
│                     │ RepoGraph      │   │ WorktreeChange    │  │
│                     │ Builder        │   │ Feed (existing)   │  │
│                     │ (initial scan) │   └───────────────────┘  │
│                     └────────────────┘                           │
└──────────────────────────────────────────────────────────────────┘
```

The memory subsystem introduces five new components that integrate with three
existing Forge services: `AgentRunnerDispatcher`, `WorktreeChangeFeed`, and
`ForgeDbContext`.

---

## 2  Component Catalogue

### 2.1  RepoGraphBuilder

**Responsibility**: Scan a repository on disk and produce the initial graph
(directory nodes, file nodes, symbol nodes, and all structural edges).

**Inputs**: Repo clone root path, optional `.sln` / `.csproj` path for Roslyn.

**Outputs**: Batch of `MemoryNodeRow` + `MemoryEdgeRow` records.

**Strategy**:
1. Walk the directory tree (`Directory.EnumerateFileSystemEntries`), skip
   `.git`, `bin`, `obj`, `node_modules`. Create `directory` and `file` nodes
   with `contains` edges.
2. If a `.sln` or `.csproj` exists and the Roslyn feature flag is on, load
   the workspace via `MSBuildWorkspace`, enumerate `Document` → `SyntaxTree`,
   extract `ClassDeclarationSyntax`, `InterfaceDeclarationSyntax`,
   `MethodDeclarationSyntax`, `PropertyDeclarationSyntax`. Create `type` and
   `member` nodes with `declares` edges from the file node.
3. For call-chain analysis: walk each method body's `InvocationExpressionSyntax`
   nodes, resolve the target symbol via the semantic model, and create `calls`
   edges. Walk base-type lists for `inherits` / `implements` edges.
4. If Roslyn fails or is disabled, fall back to regex-based extraction:
   match `class|interface|struct|enum|record` declarations and `public|internal`
   method signatures. This produces type + member nodes without call-chain edges.

**Concurrency**: Runs as a one-shot `Task` during startup or on explicit
`POST /api/memory/{repoId}/rebuild`. Not reentrant — uses a per-repo
`SemaphoreSlim(1)`.

---

### 2.2  MemoryEvolver

**Responsibility**: Keep the graph up-to-date as the codebase changes.

**Trigger**: Subscribes to `WorktreeChangeFeed` snapshot diffs. On each tick
where `Added > 0 || Modified > 0 || Deleted > 0`:

| Change Kind | Graph Action |
|-------------|--------------|
| File added | Insert `file` node + `contains` edge. If C# and Roslyn enabled, extract symbols. |
| File modified | Re-extract symbols for the file; diff against existing symbol nodes. Add new, remove gone, update changed. |
| File deleted | Soft-delete the `file` node and its child symbol nodes (set `DeletedAt`). |
| File renamed | Update `Path` on the existing node (detected via `git diff --name-status R`). |

**Frequency**: Debounced to at most once per 5 seconds per repo to avoid
thrashing on burst edits.

**Implementation**: `IHostedService` with an internal `Channel<RepoChangeEvent>`.
The `WorktreeChangeFeed` pushes change events; the evolver drains them in a
serial loop.

---

### 2.3  MemoryRetriever

**Responsibility**: Given a retrieval query (task description, file-path hints,
agent-id), return a ranked set of graph nodes with their edges, facts, and
recent episodes, formatted as a markdown context block that fits within the
token budget.

**Algorithm** (multi-signal ranking):

```
Score(node) =
    w₁ · PathOverlap(node.path, query.fileHints)
  + w₂ · KeywordMatch(node.name, query.taskDescription)
  + w₃ · Recency(node.latestEpisode)
  + w₄ · AgentAffinity(node, query.agentId)
  + w₅ · Centrality(node.edgeCount)
```

Default weights: `w₁=0.35, w₂=0.25, w₃=0.15, w₄=0.15, w₅=0.10`.

**Output format**: Markdown block with three sections:

```markdown
## Repository Context (from memory)

### Structure
- src/forge/Forge.Api/Persistence/
  - Entities.cs (98 types)
  - ForgeDbContext.cs (configures all DbSets)

### Related Symbols
- ForgeDbContext.OnModelCreating — configures 40+ entity mappings
- DomainExpertRow — expertise index (Path, Topic, Summary)

### Prior Experience
- Agent "eng-alpha" last touched Entities.cs 2h ago (score: 4.2/5)
- Agent "rev-beta" reviewed PR #42 touching ForgeDbContext (approved)
```

**Token enforcement**: The renderer counts tokens (1 token ≈ 4 chars) and
truncates sections in priority order: episodes first, then edges, then facts.

---

### 2.4  EpisodeWriter

**Responsibility**: After each agent invocation completes, write an episode
record linking the agent, task, files touched, outcome, and quality signal.

**Integration point**: Called from `CrewQueueWorker` (or equivalent) after
the `AgentInvocation.Events` stream terminates, using the same completion
handler that writes `AgentUsageRow`.

**Data captured**:

| Field | Source |
|-------|--------|
| `AgentId` | From `AgentInvocation.AgentId` |
| `TaskId` | From `AgentInvocation.TaskId` |
| `InvocationId` | From `AgentInvocation.InvocationId` |
| `FilesTouched` | JSON array of relative paths from tool-call events (`WriteFileAsync` calls) |
| `QualityScore` | From the evaluator's score on the deliverable, or null if not yet scored |
| `ReviewFeedback` | Summary of PR review comments received (populated asynchronously) |
| `Outcome` | `success` / `failure` / `partial` based on invocation exit status |
| `At` | Timestamp |

Each file path in `FilesTouched` is resolved to a `MemoryNodeRow` id, and a
`memory_episode_nodes` join row is inserted so the episode can be retrieved
via graph traversal.

---

### 2.5  MemoryFactService

**Responsibility**: Manage the append-only fact store. Facts are short textual
assertions attached to graph nodes, produced by:

1. **Agent self-report** — at invocation end, the agent may emit a
   `memory.fact` tool-call with `(nodePath, assertion, confidence)`.
2. **Automated extraction** — the episode writer derives simple facts from
   the episode: "Agent X modified this file", "This file was part of story Y".
3. **Operator annotation** — `POST /api/memory/facts` allows manual facts.

Facts are versioned: a new fact on the same node with the same `factKey`
supersedes the previous one (soft-delete + insert).

---

## 3  Data Model

### 3.1  Tables

```
┌─────────────────────┐       ┌─────────────────────┐
│   memory_nodes      │       │   memory_edges      │
├─────────────────────┤       ├─────────────────────┤
│ Id        TEXT PK   │◀──┐   │ Id        INTEGER PK│
│ RepoId    TEXT FK   │   │   │ FromNodeId TEXT FK ──┼──▶ memory_nodes.Id
│ NodeType  TEXT      │   │   │ ToNodeId   TEXT FK ──┼──▶ memory_nodes.Id
│ Path      TEXT      │   └───│ EdgeType   TEXT      │
│ Name      TEXT      │       │ Weight     REAL      │
│ ParentId  TEXT FK?  │       │ Metadata   TEXT?     │
│ Metadata  TEXT?     │       │ CreatedAt  TEXT      │
│ CreatedAt TEXT      │       └─────────────────────┘
│ UpdatedAt TEXT      │
│ DeletedAt TEXT?     │       ┌─────────────────────┐
└─────────────────────┘       │   memory_facts      │
                               ├─────────────────────┤
┌─────────────────────┐       │ Id        INTEGER PK│
│ memory_episodes     │       │ NodeId    TEXT FK ───┼──▶ memory_nodes.Id
├─────────────────────┤       │ FactKey   TEXT       │
│ Id        INTEGER PK│       │ Assertion TEXT       │
│ RepoId    TEXT      │       │ Confidence REAL      │
│ AgentId   TEXT      │       │ Source    TEXT        │
│ TaskId    TEXT      │       │ CreatedAt TEXT        │
│ InvocationId TEXT   │       │ SupersededAt TEXT?   │
│ Outcome   TEXT      │       └─────────────────────┘
│ QualityScore REAL?  │
│ ReviewSummary TEXT? │       ┌─────────────────────────┐
│ FilesTouched TEXT    │       │ memory_episode_nodes    │
│ At        TEXT      │       ├─────────────────────────┤
└─────────────────────┘       │ EpisodeId  INTEGER FK ──┼──▶ memory_episodes.Id
                               │ NodeId     TEXT FK ─────┼──▶ memory_nodes.Id
                               │ Role       TEXT         │ (touched / read / reviewed)
                               └─────────────────────────┘
```

### 3.2  Node Types

| NodeType | Description | Example `Path` |
|----------|-------------|----------------|
| `directory` | A directory in the repo tree | `src/forge/Forge.Api/Persistence` |
| `file` | A single source file | `src/forge/Forge.Api/Persistence/Entities.cs` |
| `namespace` | A C# namespace | `Forge.Api.Persistence` |
| `type` | A class, struct, interface, enum, record | `Forge.Api.Persistence.EpicRow` |
| `member` | A method, property, field, event | `Forge.Api.Persistence.EpicRow.Id` |

### 3.3  Edge Types

| EdgeType | Semantics | From → To |
|----------|-----------|-----------|
| `contains` | Structural parent-child | directory → file, file → type, type → member |
| `declares` | A file declares a type | file → type |
| `calls` | Method A invokes method B | member → member |
| `inherits` | Type A extends type B | type → type |
| `implements` | Type A implements interface B | type → type |
| `uses` | Type A references type B (field, parameter, local) | type → type |
| `imports` | File A has a `using` for namespace B | file → namespace |

### 3.4  Node ID Scheme

Node IDs are deterministic, content-addressed strings:

```
{repoId}:{nodeType}:{normalizedPath}
```

Examples:
- `repo-1:directory:src/forge/Forge.Api/Persistence`
- `repo-1:file:src/forge/Forge.Api/Persistence/Entities.cs`
- `repo-1:type:Forge.Api.Persistence.EpicRow`
- `repo-1:member:Forge.Api.Persistence.EpicRow.get_Id`

This makes upserts idempotent and rename detection a simple path update.

---

## 4  Interfaces

### 4.1  IRepoGraphBuilder

```csharp
public interface IRepoGraphBuilder
{
    /// <summary>
    /// Full rebuild of the repo graph. Deletes existing nodes for the repo
    /// and re-scans from disk.
    /// </summary>
    Task RebuildAsync(string repoId, string cloneRoot, CancellationToken ct);

    /// <summary>
    /// Incremental update for a set of changed files.
    /// </summary>
    Task UpdateAsync(string repoId, string cloneRoot,
        IReadOnlyList<string> added,
        IReadOnlyList<string> modified,
        IReadOnlyList<string> deleted,
        IReadOnlyList<(string From, string To)> renamed,
        CancellationToken ct);
}
```

### 4.2  IMemoryRetriever

```csharp
public interface IMemoryRetriever
{
    /// <summary>
    /// Retrieve relevant memory context for an agent task.
    /// Returns a markdown-formatted string within the token budget.
    /// </summary>
    Task<MemoryContext> RetrieveAsync(MemoryQuery query, CancellationToken ct);
}

public sealed record MemoryQuery(
    string RepoId,
    string? AgentId = null,
    string? TaskDescription = null,
    IReadOnlyList<string>? FileHints = null,
    int MaxTokens = 4000);

public sealed record MemoryContext(
    string MarkdownBlock,
    int EstimatedTokens,
    IReadOnlyList<string> NodeIdsUsed);
```

### 4.3  IEpisodeWriter

```csharp
public interface IEpisodeWriter
{
    Task WriteAsync(EpisodeRecord episode, CancellationToken ct);
}

public sealed record EpisodeRecord(
    string RepoId,
    string AgentId,
    string TaskId,
    string InvocationId,
    string Outcome,
    double? QualityScore,
    string? ReviewSummary,
    IReadOnlyList<string> FilesTouched);
```

### 4.4  IMemoryFactService

```csharp
public interface IMemoryFactService
{
    Task UpsertAsync(string nodeId, string factKey, string assertion,
        double confidence, string source, CancellationToken ct);

    Task<IReadOnlyList<MemoryFactRow>> GetForNodeAsync(
        string nodeId, CancellationToken ct);
}
```

---

## 5  Sequence Flows

### 5.1  Initial Graph Build (on repo registration)

```
Operator                 POST /api/memory/{repoId}/rebuild
  │
  ▼
MemoryEndpoints ──────▶ RepoGraphBuilder.RebuildAsync(repoId, cloneRoot)
                              │
                              ├─ Walk directory tree → INSERT memory_nodes (dir, file)
                              │                       INSERT memory_edges (contains)
                              │
                              ├─ [if Roslyn enabled]
                              │   Load MSBuildWorkspace
                              │   For each Document:
                              │     Extract types, members → INSERT memory_nodes
                              │     Extract calls, inherits → INSERT memory_edges
                              │
                              └─ Return { nodeCount, edgeCount, elapsed }
```

### 5.2  Agent Invocation with Memory

```
CrewQueueWorker             dequeue task for agent "eng-alpha"
  │
  ├─ IMemoryRetriever.RetrieveAsync(repoId, agentId, taskDescription, fileHints)
  │     │
  │     ├─ Query memory_nodes by path overlap + keyword
  │     ├─ Expand 1-hop edges (calls, inherits)
  │     ├─ Attach recent episodes for agentId
  │     ├─ Attach top-3 facts per node
  │     ├─ Rank, truncate to maxTokens
  │     └─ Return MemoryContext { markdownBlock }
  │
  ├─ Prepend memoryContext.MarkdownBlock to agent prompt
  │
  ├─ IAgentRunner.StartAsync(taskId, agentId, prompt, context)
  │     │
  │     └─ ... agent executes, emits events ...
  │
  └─ On completion:
       IEpisodeWriter.WriteAsync(episode)
         │
         ├─ INSERT memory_episodes
         ├─ For each file in FilesTouched:
         │    Resolve to nodeId
         │    INSERT memory_episode_nodes
         └─ Done
```

### 5.3  Incremental Graph Evolution

```
WorktreeChangeFeed          snapshot diff detected (added: [f1], modified: [f2], deleted: [f3])
  │
  ▼
MemoryEvolver               (IHostedService, subscribed to change feed)
  │
  ├─ Debounce 5s
  │
  ├─ IRepoGraphBuilder.UpdateAsync(repoId, cloneRoot, added, modified, deleted, renamed)
  │     │
  │     ├─ For each added file:
  │     │    INSERT file node + contains edge
  │     │    [if C#] Extract symbols → INSERT type/member nodes + edges
  │     │
  │     ├─ For each modified file:
  │     │    Re-extract symbols
  │     │    Diff with existing → INSERT new, soft-DELETE removed, UPDATE changed
  │     │
  │     ├─ For each deleted file:
  │     │    SET DeletedAt on file node + child nodes
  │     │
  │     └─ For each renamed (from, to):
  │          UPDATE Path on existing node
  │
  └─ Done (next tick will detect further changes)
```

---

## 6  REST API Surface

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/memory/{repoId}/rebuild` | Trigger full graph rebuild |
| `GET` | `/api/memory/{repoId}/stats` | Node/edge/fact/episode counts |
| `GET` | `/api/memory/{repoId}/nodes` | Paginated node list (filter by type, path prefix) |
| `GET` | `/api/memory/{repoId}/nodes/{nodeId}` | Single node with edges, facts, episodes |
| `GET` | `/api/memory/{repoId}/graph` | Subgraph rooted at a given path (depth-limited) |
| `POST` | `/api/memory/{repoId}/query` | Retrieval query (body = `MemoryQuery`) |
| `POST` | `/api/memory/facts` | Upsert a fact |
| `GET` | `/api/memory/{repoId}/episodes` | Paginated episode list (filter by agent, task) |
| `GET` | `/api/memory/{repoId}/preview` | Preview the memory context an agent would receive |

All endpoints require authentication (existing `ForgeAuth` middleware).
Write endpoints require `product-owner` role. Read endpoints open to any
signed-in user.

---

## 7  Integration with Existing Components

| Component | Integration |
|-----------|-------------|
| `ForgeDbContext` | Add `DbSet<MemoryNodeRow>`, `DbSet<MemoryEdgeRow>`, `DbSet<MemoryFactRow>`, `DbSet<MemoryEpisodeRow>`, `DbSet<MemoryEpisodeNodeRow>`. Configure in `OnModelCreating`. |
| `WorktreeChangeFeed` | `MemoryEvolver` registers as a listener. The change feed already provides `Added/Modified/Deleted` file lists per tick. |
| `AgentRunnerDispatcher` | No change. The memory context is prepended to the prompt by the caller (`CrewQueueWorker`), not by the runner itself. |
| `CrewQueueWorker` | Modified to call `IMemoryRetriever.RetrieveAsync` before dispatching. Modified to call `IEpisodeWriter.WriteAsync` after completion. |
| `DomainExpertRow` | Read during the retrieval step as a supplementary signal. No writes — the existing domain-expert indexing is unaffected. |
| `SoulService` | Unchanged. Soul.md remains the identity layer; memory is the experience layer. |

---

## 8  Configuration

```json
{
  "Forge": {
    "Memory": {
      "Enabled": true,
      "RoslynEnabled": true,
      "MaxTokenBudget": 4000,
      "EvolutionDebounceSeconds": 5,
      "MaxNodesPerRepo": 50000,
      "RetrievalWeights": {
        "PathOverlap": 0.35,
        "KeywordMatch": 0.25,
        "Recency": 0.15,
        "AgentAffinity": 0.15,
        "Centrality": 0.10
      }
    }
  }
}
```

Bound to `IOptions<MemoryOptions>` via `Forge:Memory` section.
