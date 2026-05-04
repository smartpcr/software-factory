# Tech Spec — Agent Memory (code-intelligence:AGENT-MEMORY)

| Field         | Value |
|---------------|-------|
| Story         | code-intelligence:AGENT-MEMORY |
| Points        | 10 |
| Author        | Yuan Shi Tianzun (Architect) |
| Status        | Draft |

---

## 1  Problem Statement

Forge agents today are stateless across invocations. Every time an agent starts
a task — architect planning a story, engineer implementing a workstream, reviewer
commenting on a PR — the system prompt is assembled from scratch by reading the
soul.md file, possibly augmented with the `DomainExpertRow` index. There is no
durable memory of:

* **Which files the agent has touched before**, how confidently it navigated
  them, and what it learned.
* **How the repository is structured** beyond what fits in the current prompt
  window — directory hierarchy, module boundaries, ownership.
* **Call-chain relationships** between symbols — which functions call which,
  which types implement which interfaces, where cross-cutting concerns live.
* **Past outcomes** — whether a previous attempt succeeded, what review comments
  it received, which patterns the evaluator rewarded or penalised.

The result: agents repeatedly re-discover the same context, waste tokens on
orientation, and cannot transfer expertise across tasks within the same codebase.

### 1.1  Desired End-State

An agent receiving a new task should be able to consult a **persistent,
graph-structured memory** that encodes:

1. The repository's top-down structure (directory → file → symbol).
2. Call-chain / dependency edges between symbols.
3. Per-agent episodic records (what was done, the outcome, quality signal).
4. Consolidated semantic facts ("this module handles auth", "this file is
   the entry point for build validation").

The memory should evolve incrementally as the codebase changes (commits,
worktree ticks) and as agents complete work (invocations, PR reviews), so
each subsequent invocation starts with a richer, more accurate context.

---

## 2  Scope

### 2.1  In Scope

| # | Capability |
|---|------------|
| S1 | **Repo Structure Graph** — parse a repo's directory tree and file list into a hierarchical graph stored in SQLite. Nodes: `directory`, `file`. Edges: `contains`. |
| S2 | **Symbol Extraction** — for C# repos, extract top-level symbols (namespaces, classes, interfaces, methods, properties) via Roslyn workspace analysis. Nodes: `namespace`, `type`, `member`. Edges: `declares`, `contains`. |
| S3 | **Call-Chain Edges** — static analysis to extract `calls`, `implements`, `inherits`, `uses` edges between symbol nodes. |
| S4 | **Memory Fact Store** — append-only fact records attached to graph nodes. Facts are short textual assertions with a confidence score, source (agent-id, invocation-id), and timestamp. |
| S5 | **Episode Log** — record each agent invocation outcome (task-id, files-touched, token-usage, quality-score, review-feedback-summary) as an episode linked to graph nodes. |
| S6 | **Memory Retrieval API** — given a task description and/or a set of file paths, return the top-k relevant graph nodes, their edges, attached facts, and recent episodes. Used by `IAgentRunner.StartAsync` to enrich the system prompt. |
| S7 | **Memory Evolution Service** — background `IHostedService` that reacts to `WorktreeChangeFeed` ticks and invocation-completion events to incrementally update the graph (add new files, remove deleted ones, re-index changed symbols). |
| S8 | **Persistence** — all graph data stored in the existing Forge SQLite database via EF Core. New tables added via a standard `PhaseNNMigration`. |
| S9 | **REST Endpoints** — CRUD + query endpoints under `/api/memory` for the UI to inspect the graph (e.g. visualise repo structure, view agent expertise). |

### 2.2  Non-Goals

| # | Exclusion | Rationale |
|---|-----------|-----------|
| N1 | Embedding-based vector search | SQLite has no native vector index. The retrieval strategy uses graph traversal + keyword matching. A future phase may add a sidecar vector store. |
| N2 | Cross-repository memory | Each repo's graph is independent. Multi-repo expertise transfer is a separate story. |
| N3 | Runtime call-graph tracing (dynamic analysis) | Only static analysis in this phase. |
| N4 | Language support beyond C# | Roslyn analysis covers the Forge codebase. Other languages deferred. |
| N5 | LLM-generated summaries at index time | Facts from agent interactions are already LLM-produced. Automated summarisation of source files is out of scope (too expensive at index time). |
| N6 | Fine-tuning or RL-based memory optimisation | The graph is populated via deterministic extraction + append-only agent episodes. RL-based memory construction (Mem-α, Memory-R1) is research-phase only. |

---

## 3  Constraints

| # | Constraint |
|---|------------|
| C1 | **SQLite single-writer** — the Forge database is SQLite. All graph writes must go through the existing `ForgeDbContext` serialisation. Batch writes should be wrapped in a single transaction. |
| C2 | **Windows-only** — Roslyn workspace analysis must use MSBuild locator on Windows. The `RuntimeIdentifier` is `win-x64`. |
| C3 | **Startup latency** — initial graph build for a ~500-file repo must complete in under 30 seconds. Incremental updates (per commit) must complete in under 5 seconds. |
| C4 | **Token budget** — the memory context injected into an agent prompt must not exceed 4 000 tokens (roughly 3 000 words). Retrieval must rank and truncate. |
| C5 | **No new NuGet packages** beyond `Microsoft.CodeAnalysis.CSharp.Workspaces` (already transitively available via the SDK). |
| C6 | **Backward-compatible schema** — new tables only; no alterations to existing tables. The migration must be safe to run on an existing `forge.db`. |

---

## 4  Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|------------|--------|------------|
| R1 | Roslyn workspace load fails on large solutions (OOM, missing SDK) | Medium | High | Provide a fallback `regex`-based symbol extractor that parses `class`/`interface`/`method` declarations without loading the full compilation. Gate Roslyn behind a feature flag. |
| R2 | Graph grows too large for SQLite query performance | Low | Medium | Index on `(repo_id, node_type)` and `(from_node_id)` / `(to_node_id)`. Partition by repo. Prune stale nodes on each evolution tick. |
| R3 | Incremental evolution misses renames (git mv) | Medium | Low | Use `git diff --name-status` (already parsed by `WorktreeChangeFeed`) to detect renames and update node paths rather than delete + re-create. |
| R4 | Memory context retrieved is irrelevant, bloating the prompt | Medium | High | Rank retrieval results by: (a) path overlap with task file hints, (b) recency of episodes, (c) agent-specific affinity. Provide a `/api/memory/preview` endpoint so the operator can inspect what an agent would see. |
| R5 | Concurrent graph evolution + agent retrieval causes stale reads | Low | Low | Reads use snapshot isolation (SQLite WAL mode, already configured). Writes are serialised via `ForgeDbContext`. |

---

## 5  Dependencies

| Dependency | Direction | Notes |
|------------|-----------|-------|
| `ForgeDbContext` / EF Core | Internal | New `DbSet<>` registrations + migration. |
| `WorktreeChangeFeed` | Internal | Provides the commit-level change signal that triggers graph evolution. |
| `IAgentRunner` / `AgentRunnerDispatcher` | Internal | Consumes memory context at invocation start; writes episode log on completion. |
| `DomainExpertRow` | Internal | Existing expertise index. The new graph subsumes and extends it; the legacy table remains read-compatible. |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | NuGet | Roslyn APIs for symbol extraction and call-chain analysis. |

---

## 6  Success Criteria

| # | Criterion | Measurement |
|---|-----------|-------------|
| SC1 | Repo structure graph for `Forge.Api` (≈100 files) built in < 10 s on first run. | Timed in integration test. |
| SC2 | Symbol extraction produces ≥ 90 % of public types and methods for `Forge.Api`. | Compared against `dotnet list` + manual spot-check. |
| SC3 | Call-chain edges cover direct method calls within the same solution. | Spot-check 10 known call sites. |
| SC4 | Memory retrieval for a task targeting `Persistence/Entities.cs` returns the file node, its containing directory, the `ForgeDbContext` type, and at least 3 related symbols within top-10 results. | Automated test. |
| SC5 | After an agent invocation completes, an episode row exists linking the agent, task, files touched, and quality score. | Automated test. |
| SC6 | Graph evolution correctly adds a new file and removes a deleted file after a simulated commit. | Automated test. |
| SC7 | Injected memory context stays within the 4 000-token budget. | Assertion in retrieval service. |