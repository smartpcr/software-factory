# Agent Memory — End-to-End Scenarios

> Story: `code-intelligence:AGENT-MEMORY` · 10 SP
>
> Scope: graph-based agent memory over top-down repo structure and
> call-chain context, enabling agents to learn and grow smarter over time.

---

## Feature: Repository Structure Graph Construction

Build a hierarchical knowledge graph that mirrors the top-down repository
structure — from workspace root through modules, packages, files, classes,
and functions — so the agent has a structural backbone for memory.

### Scenario: Build graph from a single-language repository

```gherkin
Given a repository rooted at "/workspace/acme-api"
  And the repository contains 42 source files across 6 packages
When the agent triggers a full structure-graph build
Then a directed acyclic graph is produced
  And every source file appears as a node with metadata (path, language, size, last-modified)
  And every package appears as a parent node linked to its child files via "contains" edges
  And the root node represents the repository itself
  And the graph contains exactly 42 file-nodes and 6 package-nodes plus 1 root-node
```

### Scenario: Build graph from a polyglot repository

```gherkin
Given a repository containing TypeScript files under "src/", Python files under "scripts/", and Go files under "services/"
When the agent triggers a full structure-graph build
Then each language subtree is parsed with the correct language-aware parser
  And cross-language boundary nodes (e.g., an RPC proto shared by Go and Python) are created once and linked into both subtrees
  And the graph records the primary language on each file node
```

### Scenario: Incremental graph update after a commit

```gherkin
Given an existing structure graph built at commit abc1234
  And a new commit def5678 adds 2 files, modifies 3 files, and deletes 1 file
When the agent receives a change-set notification for def5678
Then only the 6 affected nodes and their edges are recalculated
  And the deleted file's node is marked as "removed" (soft-delete) with a tombstone timestamp
  And the graph's root metadata records the new head commit def5678
  And the total wall-clock time is less than 10% of a full rebuild
```

### Scenario: Handle a repository with no parseable source files

```gherkin
Given a repository that contains only binary assets and markdown documentation
When the agent triggers a full structure-graph build
Then the graph is created with directory-level nodes only
  And a warning event "no-parseable-sources" is emitted
  And the agent does not fail or produce an empty graph
```

### Scenario: Handle deeply nested directory trees

```gherkin
Given a repository with a directory depth of 25 levels
When the agent triggers a full structure-graph build
Then every level is represented in the graph without stack overflow or recursion-depth errors
  And the graph's maximum path length equals 25
```

---

## Feature: Call-Chain Context Extraction

Extract intra- and inter-file call relationships so the agent understands
how functions invoke each other, forming a call graph overlaid on the
structure graph.

### Scenario: Extract direct function calls within a single file

```gherkin
Given a Python file "order_service.py" containing functions create_order(), validate_inventory(), and charge_payment()
  And create_order() calls validate_inventory() then charge_payment()
When the agent extracts call-chain context for "order_service.py"
Then the call graph contains edges:
  | caller          | callee             |
  | create_order    | validate_inventory |
  | create_order    | charge_payment     |
  And each edge records the call site's line number and column offset
```

### Scenario: Extract cross-file call chains

```gherkin
Given "api/handler.py" imports and calls "services/billing.py::invoice()"
  And "services/billing.py::invoice()" calls "db/models.py::save_invoice()"
When the agent extracts the transitive call chain starting from "api/handler.py::handle_request()"
Then the resulting chain is:
  handle_request → invoice → save_invoice
  And each hop carries the source file path and symbol name
```

### Scenario: Detect and label recursive and circular call chains

```gherkin
Given function A() calls B(), B() calls C(), and C() calls A()
When the agent extracts call-chain context for the containing module
Then the cycle A → B → C → A is detected
  And every edge participating in the cycle is annotated with "cycle: true"
  And the agent emits a "cycle-detected" event with the ordered list of participants
  And graph traversal terminates without infinite looping
```

### Scenario: Handle dynamic dispatch and virtual calls

```gherkin
Given an interface "PaymentProcessor" with implementations "StripeProcessor" and "PayPalProcessor"
  And "checkout()" calls "processor.charge()" via interface dispatch
When the agent extracts call-chain context
Then the call graph records a polymorphic edge from checkout() to PaymentProcessor.charge()
  And subsidiary edges link to StripeProcessor.charge() and PayPalProcessor.charge()
  And each subsidiary edge is annotated with "dispatch: dynamic"
```

### Scenario: Handle unresolved external dependencies

```gherkin
Given "analytics.py" calls "pandas.DataFrame.groupby()" from an external library
  And the external library source is not present in the repository
When the agent extracts call-chain context
Then the call graph creates a stub node for "pandas.DataFrame.groupby"
  And the stub node is typed as "external-dependency"
  And no error is raised for the missing source
```

---

## Feature: Memory Storage — Knowledge Graph Persistence

Store the combined structure + call-chain graph in a durable, versioned
knowledge graph, supporting temporal queries and hierarchical memory
(episodic, semantic, procedural layers as described in the graph-memory
taxonomy).

### Scenario: Persist graph to durable storage on first build

```gherkin
Given the agent has completed a full structure-graph build and call-chain extraction
  And no prior graph exists in storage
When the agent persists the graph
Then the storage backend contains all nodes and edges
  And every node carries a creation timestamp equal to the build time
  And a version record "v1" is created linking to commit SHA and build metadata
```

### Scenario: Query historical graph state at a prior version

```gherkin
Given the graph has been updated through versions v1, v2, and v3
When a consumer requests the graph state "as-of v1"
Then the returned graph contains only nodes and edges that existed at v1
  And nodes added in v2 or v3 are absent
  And nodes deleted between v1 and v3 are present
```

### Scenario: Hierarchical memory layers are maintained

```gherkin
Given the agent processes a repository over 5 sessions
When the memory is queried for its layers
Then three distinct layers exist:
  | layer      | description                                                  |
  | episodic   | per-session interaction records (queries asked, files touched)|
  | semantic   | consolidated facts (module purposes, key abstractions)       |
  | procedural | learned patterns (common fix strategies, refactoring recipes)|
  And each layer's nodes link back to source evidence in the structure graph
```

### Scenario: Enforce storage size limits

```gherkin
Given the agent memory storage quota is set to 500 MB
  And the current graph occupies 480 MB
When a new extraction would add 50 MB of nodes
Then the agent triggers a compaction cycle before persisting
  And the compaction merges low-value episodic memories older than 30 days
  And the post-compaction graph plus new data fits within the 500 MB quota
  And no semantic or procedural memory is lost during compaction
```

---

## Feature: Context Retrieval for Agent Tasks

When an agent needs context to answer a question, fix a bug, or plan a
refactor, the memory system retrieves relevant subgraphs efficiently.

### Scenario: Retrieve context for a symbol lookup

```gherkin
Given the agent memory contains the full graph for "acme-api"
When the agent queries "What does the function calculate_tax() do and who calls it?"
Then the retrieval returns:
  | node               | relationship     |
  | calculate_tax      | definition       |
  | order_total        | calls            |
  | invoice_generator  | calls            |
  And the returned subgraph includes the function's docstring, parameters, and return type
  And retrieval completes in under 200 ms for a graph of 10,000 nodes
```

### Scenario: Retrieve context scoped to a pull-request diff

```gherkin
Given a pull request modifies files A.py, B.py, and C.py
When the agent retrieves memory scoped to the PR diff
Then the subgraph includes:
  - nodes for all symbols defined in A.py, B.py, and C.py
  - all direct callers and callees of modified symbols (1-hop neighborhood)
  - any semantic memory entries tagged to these files
  And symbols in unmodified files outside the 1-hop radius are excluded
```

### Scenario: Retrieve context with recency bias

```gherkin
Given the agent has interacted with module "auth" 12 times in the last 7 days
  And the agent has interacted with module "logging" once 90 days ago
When the agent queries for context related to "authentication flow"
Then results from "auth" are ranked higher than results from "logging"
  And the ranking formula weights recency at 40%, frequency at 30%, and semantic relevance at 30%
```

### Scenario: Retrieve context when memory is empty

```gherkin
Given the agent memory graph has not been initialized for this repository
When the agent queries "Explain the payment module"
Then the system returns an empty result set
  And emits an event "memory-not-initialized"
  And suggests the action "Run a full structure-graph build first"
  And does not hallucinate or fabricate context
```

### Scenario: Retrieve context across repository boundaries

```gherkin
Given the agent has memory graphs for repositories "frontend-app" and "backend-api"
  And "frontend-app" makes HTTP calls to endpoints defined in "backend-api"
When the agent queries "What backend endpoints does the checkout page use?"
Then the retrieval traverses the cross-repo link edges
  And returns endpoint nodes from "backend-api" connected to the checkout component in "frontend-app"
```

---

## Feature: Agent Learning — Expertise Accumulation

The agent accumulates expertise over repeated interactions: remembering
past decisions, consolidating patterns, and surfacing learned insights
without being explicitly re-taught.

### Scenario: Learn from a successful bug fix

```gherkin
Given the agent assisted in fixing a null-pointer bug in "user_service.py"
  And the fix involved adding a guard clause before calling "profile.load()"
When the same pattern (unguarded nullable call to profile.load()) appears in "admin_service.py"
Then the agent proactively suggests "Add a null-check before profile.load() — this pattern caused bug #1234"
  And the suggestion references the prior fix's commit SHA and timestamp
```

### Scenario: Consolidate repeated observations into semantic memory

```gherkin
Given the agent has observed across 5 sessions that module "payments" always validates input via "schema.validate()" before processing
When the agent is asked "How should I add a new payment method?"
Then the response includes the learned pattern: "All payment methods must pass schema.validate() before processing"
  And the pattern is stored as a semantic memory node with confidence score ≥ 0.8
  And the node links to the 5 episodic memory entries that evidenced it
```

### Scenario: Decay stale expertise when codebase changes

```gherkin
Given the agent has a procedural memory "Use ORM method save() for persistence"
  And a recent refactor replaced all save() calls with a repository-pattern "repo.persist()"
When the agent next assists with a persistence task
Then the stale procedural memory is demoted (confidence reduced below 0.3)
  And a new procedural memory "Use repo.persist() for persistence" is created with confidence ≥ 0.7
  And the agent does not suggest the deprecated save() pattern
```

### Scenario: Resist learning from a single anomalous event

```gherkin
Given the agent observed a one-time hotfix that bypassed the standard review process
When the agent is asked about the team's deployment workflow
Then the hotfix event is not promoted to semantic or procedural memory
  And the episodic record exists but is annotated "single-occurrence, low-generalizability"
  And the agent's response describes the standard process, not the exception
```

### Scenario: Agent demonstrates improved performance over time

```gherkin
Given the agent has processed 50 sessions against repository "acme-api"
When the agent's retrieval accuracy is measured on a held-out set of 20 context-lookup queries
Then the accuracy at session 50 is at least 15% higher than at session 1
  And the mean retrieval latency at session 50 is no worse than at session 1
```

---

## Feature: Memory Evolution — Graph Maintenance and Self-Healing

The memory graph must evolve as the codebase evolves: merging duplicate
nodes, pruning orphans, and re-indexing after large-scale refactors.

### Scenario: Merge duplicate nodes after a file rename

```gherkin
Given "utils/helpers.py" is renamed to "core/helpers.py"
  And the old node "utils/helpers.py" and new node "core/helpers.py" share identical content hashes
When the agent runs a graph-evolution cycle
Then the two nodes are merged into a single node at "core/helpers.py"
  And all edges formerly pointing to "utils/helpers.py" are redirected
  And a provenance record captures the rename event with old and new paths
```

### Scenario: Prune orphan nodes after bulk deletion

```gherkin
Given 30 source files in the "legacy/" directory have been deleted from the repository
When the agent runs a graph-evolution cycle
Then all 30 file nodes and their descendant symbol nodes are marked as pruned
  And edges to pruned nodes are removed from the active graph
  And a pruning summary event lists all affected paths
  And the pruned nodes remain accessible in the temporal history for rollback
```

### Scenario: Re-index after a major framework migration

```gherkin
Given the repository migrated from Express.js to Fastify
  And 80% of route-handler files changed their export signatures
When the agent detects that the change-set exceeds the "major-refactor" threshold (>60% of nodes modified)
Then the agent triggers a full graph rebuild instead of incremental updates
  And procedural memories referencing Express-specific patterns are flagged for review
  And a "migration-detected" event is emitted with before/after framework metadata
```

### Scenario: Self-heal corrupted graph edges

```gherkin
Given a storage fault has left 5 edges pointing to non-existent target nodes
When the agent runs its periodic integrity check
Then the 5 dangling edges are identified and quarantined
  And a repair attempt re-resolves each edge's target by symbol name
  And successfully re-linked edges are restored; unresolvable edges are deleted with an audit log entry
  And the integrity check completes without crashing the memory service
```

---

## Feature: Concurrency, Consistency, and Failure Handling

The memory system operates in a multi-agent, potentially multi-repo
environment. It must handle concurrent access, partial failures, and
conflicting writes.

### Scenario: Concurrent reads during a graph write

```gherkin
Given agent-A is writing a graph update for commit xyz789
  And agent-B issues a read query at the same time
When agent-B's read executes
Then agent-B receives a consistent snapshot from the last committed version
  And agent-B does not see partially-written nodes from agent-A's in-progress update
  And agent-A's write is not blocked or slowed by agent-B's read
```

### Scenario: Concurrent writes from two agents

```gherkin
Given agent-A and agent-B both attempt to update the graph for the same file simultaneously
When both writes are submitted
Then one write wins via optimistic concurrency (version vector comparison)
  And the losing agent receives a conflict error with the winning version
  And the losing agent can retry with a merged changeset
  And no data is silently lost
```

### Scenario: Recover from a crashed extraction mid-flight

```gherkin
Given the agent is extracting call-chain context and has processed 200 of 400 files
  And the process crashes due to an out-of-memory error on file 201
When the agent restarts
Then it detects the incomplete extraction via a checkpoint record
  And resumes from file 201 without re-processing files 1–200
  And the final graph is identical to what a full uninterrupted run would produce
```

### Scenario: Storage backend is temporarily unavailable

```gherkin
Given the graph storage backend is unreachable for 30 seconds
When the agent attempts to persist a graph update during the outage
Then the update is queued in a local write-ahead log
  And the agent continues serving reads from the last-known-good in-memory cache
  And when the backend recovers, the queued update is flushed within 10 seconds
  And no duplicate nodes are created from the retry
```

### Scenario: Handle graph exceeding maximum supported size

```gherkin
Given a monorepo with 500,000 source files
  And the resulting graph would contain over 5 million nodes
When the agent attempts a full build
Then the build partitions the graph into sub-graphs by top-level module
  And each sub-graph is built and stored independently
  And cross-partition edges are stored in a separate federation index
  And queries transparently span partitions via the federation layer
```

---

## Feature: Security and Access Control

Memory may contain sensitive context. The system must enforce access
boundaries and prevent leakage across tenants or repositories.

### Scenario: Enforce tenant isolation in a multi-tenant deployment

```gherkin
Given tenant-A owns repository "alpha" and tenant-B owns repository "beta"
When tenant-A queries the memory system
Then only nodes and edges from repository "alpha" are returned
  And no nodes, edges, or metadata from repository "beta" are visible
  And audit logs record the tenant-scoped query
```

### Scenario: Redact secrets accidentally committed to the graph

```gherkin
Given a source file containing an API key "sk-live-abc123" was processed into the graph
  And the secret is embedded in a node's raw-content field
When the secret scanner runs during the next evolution cycle
Then the node's raw-content field is redacted to "sk-live-***"
  And an alert "secret-in-memory" is emitted with the node ID and file path
  And the un-redacted value is not recoverable from the graph's temporal history
```

### Scenario: Respect .gitignore and exclusion patterns

```gherkin
Given the repository's .gitignore excludes "secrets/", "*.env", and "node_modules/"
When the agent triggers a structure-graph build
Then no nodes are created for files matching the exclusion patterns
  And the build log records how many files were skipped and why
```

---

## Feature: Observability and Diagnostics

Operators need visibility into memory health, build status, and retrieval
quality to run the system in production.

### Scenario: Emit metrics after every graph build

```gherkin
Given the agent completes a structure-graph build
Then the following metrics are emitted:
  | metric                     | type      |
  | graph.build.duration_ms    | histogram |
  | graph.nodes.total          | gauge     |
  | graph.edges.total          | gauge     |
  | graph.build.files_skipped  | counter   |
  | graph.build.errors         | counter   |
  And each metric carries labels for repository, commit SHA, and build type (full/incremental)
```

### Scenario: Emit retrieval quality feedback loop

```gherkin
Given the agent retrieves a context subgraph for a developer query
  And the developer rates the result as "not helpful"
When the feedback is recorded
Then an event "retrieval-feedback-negative" is emitted with the query fingerprint and subgraph ID
  And the episodic memory for this interaction is annotated with the negative signal
  And the retrieval ranking model schedules a weight adjustment for the next training cycle
```

### Scenario: Health check reports graph staleness

```gherkin
Given the last graph update was 48 hours ago
  And the repository has received 12 commits since the last update
When the health-check endpoint is called
Then it returns status "degraded"
  And the response body includes:
  | field               | value          |
  | last_update_age_hrs | 48             |
  | pending_commits     | 12             |
  | recommendation      | "trigger incremental update" |
```

---

_End of scenarios. 16 features, 35 scenarios covering happy paths, edge
cases, concurrency, security, observability, and learning/evolution._
