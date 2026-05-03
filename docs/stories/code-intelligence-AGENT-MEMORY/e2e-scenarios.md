# Agent Memory — End-to-End Scenarios

> Story: `code-intelligence:AGENT-MEMORY`
>
> System-under-test: Graph-based agent memory that builds expertise from
> top-down repository structure and call-chain context, enabling agents to
> learn and deliver increasingly accurate responses over time.

---

## Feature: Repository Structure Graph Construction

Agents must ingest a repository's top-down directory/module hierarchy and
produce a navigable graph where nodes represent structural units (repo →
package → module → class → function) and edges capture containment.

### Scenario: Build hierarchy graph from a single-language repo

```gherkin
Given a TypeScript repository with the layout
  | path                        | kind     |
  | src/                        | package  |
  | src/auth/                   | module   |
  | src/auth/login.ts           | file     |
  | src/auth/login.ts::Login    | class    |
  | src/auth/login.ts::Login.exec | method |
When the memory extraction pipeline processes the repo root
Then a directed acyclic graph is persisted with nodes for each structural unit
And each node carries metadata { language, path, depth, signature }
And containment edges connect repo → package → module → file → class → method
And the graph root node equals the repository identifier
```

### Scenario: Build hierarchy graph from a polyglot repo

```gherkin
Given a repository containing both Python and Go source trees
  | path               | language |
  | services/api/main.go    | Go       |
  | services/api/handler.go | Go       |
  | ml/train.py             | Python   |
  | ml/model.py             | Python   |
When the memory extraction pipeline processes the repo root
Then each language subtree produces its own structural subgraph
And both subgraphs share a single repo root node
And language-specific parse rules are applied per subtree
```

### Scenario: Incremental re-index after file changes

```gherkin
Given a repository whose graph was previously built at commit abc123
And a new commit def456 modifies src/auth/login.ts and adds src/auth/mfa.ts
When an incremental extraction is triggered for the diff abc123..def456
Then only the affected subgraph rooted at src/auth/ is rebuilt
And the node for src/auth/mfa.ts is inserted with correct containment edges
And unchanged subtrees retain their original node identifiers and timestamps
And the graph version advances to def456
```

### Scenario: Handle empty or binary-only repository

```gherkin
Given a repository that contains only binary assets (images, compiled objects)
When the memory extraction pipeline processes the repo root
Then a repo root node is created with zero structural children
And an informational event is emitted: "no parseable source files found"
And the agent gracefully falls back to file-listing context only
```

---

## Feature: Call-Chain Extraction and Graph Linking

The system must analyse source code to extract function/method call
relationships and store them as directed edges in the memory graph, layered
on top of the structural hierarchy.

### Scenario: Extract direct call edges within a module

```gherkin
Given module src/orders/checkout.ts containing
  | caller                    | callee                       |
  | Checkout.process          | PaymentGateway.charge        |
  | Checkout.process          | InventoryService.reserve     |
  | PaymentGateway.charge     | Logger.info                  |
When call-chain extraction runs on src/orders/checkout.ts
Then directed "calls" edges are created for each caller → callee pair
And each edge carries metadata { callsite_line, is_async, arity }
And callee nodes are resolved to their structural graph counterparts
```

### Scenario: Extract cross-module call edges

```gherkin
Given module A at src/auth/login.ts calling validateToken from src/crypto/jwt.ts
When call-chain extraction runs on the full repository
Then a "calls" edge links Login.exec → Jwt.validateToken across modules
And the edge is annotated with { cross_module: true, source_module, target_module }
```

### Scenario: Handle unresolved external dependencies

```gherkin
Given a function src/api/handler.ts::fetchData that calls axios.get
And axios is a third-party npm package not present in the repo source
When call-chain extraction encounters the axios.get call
Then a stub node is created with kind = "external_dependency" and package = "axios"
And a "calls" edge links fetchData → axios.get(stub)
And the stub node is flagged as unresolvable for deeper traversal
```

### Scenario: Detect and represent recursive and cyclic call chains

```gherkin
Given function A calls function B and function B calls function A
When call-chain extraction processes both files
Then both directed edges A → B and B → A are stored
And a "cycle" annotation is attached to the strongly-connected component {A, B}
And graph traversal APIs expose a max_depth parameter to prevent infinite walks
```

### Scenario: Extract call chains involving higher-order functions

```gherkin
Given a function applyMiddleware(handler: Handler) that invokes handler()
And a callsite applyMiddleware(authCheck) in src/server.ts
When call-chain extraction runs
Then an edge applyMiddleware → authCheck is created with { indirect: true }
And the edge metadata records the parameter position through which the call flows
```

---

## Feature: Graph-Based Memory Storage

Extracted structural and call-chain information must be persisted as a
knowledge graph that supports versioning, temporal queries, and
hierarchical retrieval — aligning with the KG + hierarchical storage
patterns from the GraphMemory taxonomy.

### Scenario: Persist graph with schema-versioned storage

```gherkin
Given the extraction pipeline produces a graph with 1,200 nodes and 3,400 edges
When the graph is committed to storage
Then all nodes and edges are written atomically within a single transaction
And a schema_version field is set on the graph metadata
And the storage layer supports both node-by-ID lookup and subgraph traversal
```

### Scenario: Temporal versioning across commits

```gherkin
Given graph snapshots exist for commits c1, c2, and c3
When the agent queries "what did function Checkout.process call at commit c1?"
Then the retrieval layer returns the call edges as they existed at c1
And does not include edges added in c2 or c3
```

### Scenario: Hierarchical memory tiers for large repositories

```gherkin
Given a monorepo with 50,000 source files
When the graph is stored
Then nodes are organized into tiers: repo → workspace → package → module → symbol
And retrieval supports tier-bounded queries (e.g., "all symbols in package X")
And summary nodes are pre-computed at the package level for fast top-down navigation
```

### Scenario: Deduplication of shared utility nodes

```gherkin
Given utility function logError is imported and called from 40 different modules
When the graph is stored
Then exactly one node exists for logError
And 40 inbound "calls" edges reference that single node
And the node carries a hotness score reflecting its fan-in count
```

---

## Feature: Context Retrieval for Agent Reasoning

When an agent needs context to answer a question or perform a task, it
retrieves subgraphs from memory using similarity-based, graph-traversal,
and rule-based strategies.

### Scenario: Retrieve local call neighbourhood for a function

```gherkin
Given the agent is asked "what does Checkout.process do?"
When the retrieval layer receives the query
Then it locates the Checkout.process node in the graph
And returns a 2-hop neighbourhood: direct callees and their callees
And the returned subgraph is serialized into a context window of ≤ 4,000 tokens
And each node in the context includes its docstring and signature
```

### Scenario: Retrieve top-down structural context for navigation

```gherkin
Given the agent is asked "give me an overview of the auth module"
When the retrieval layer processes the query
Then it returns the subgraph rooted at src/auth/ down to symbol depth
And symbols are ranked by fan-in (most-called first)
And the response includes module-level summary text from the pre-computed tier
```

### Scenario: Similarity-based retrieval for fuzzy queries

```gherkin
Given the agent is asked "where is payment handled?"
And no node is named exactly "payment"
When similarity retrieval runs against node embeddings
Then the top-k nodes with highest cosine similarity to "payment" are returned
And the results include PaymentGateway, Checkout.process, and BillingService
And each result includes a relevance score ∈ [0, 1]
```

### Scenario: Context budget enforcement

```gherkin
Given the retrieved subgraph would exceed the agent's context window (128k tokens)
When the context serializer prepares the payload
Then it prunes low-relevance leaf nodes first (lowest fan-in, deepest depth)
And retains all nodes on the shortest path from the query node to the root
And the final payload fits within the declared token budget
And a truncation warning is included in the response metadata
```

### Scenario: Retrieve context across multiple related queries in one session

```gherkin
Given the agent has already retrieved context for "auth flow"
And the agent now asks "how does the auth flow interact with billing?"
When the retrieval layer processes the follow-up query
Then it merges the previously retrieved auth subgraph with the billing subgraph
And cross-module edges between auth and billing are included
And duplicate nodes are not repeated in the merged context
```

---

## Feature: Memory Evolution and Agent Learning

The memory graph must evolve over time — both through structural updates
(new commits) and through experiential learning (agent interactions),
enabling the agent to become progressively more effective.

### Scenario: Record successful resolution as episodic memory

```gherkin
Given the agent successfully resolves a bug in Checkout.process
And the resolution involved modifying PaymentGateway.charge and adding a retry
When the task completes with a positive outcome signal
Then an episodic memory node is created with { task_type, affected_nodes, resolution_summary }
And the episodic node is linked to Checkout.process and PaymentGateway.charge
And the episodic node carries a timestamp and a confidence score
```

### Scenario: Reinforce frequently useful subgraphs

```gherkin
Given the agent has retrieved the auth → jwt → crypto subgraph 15 times in 30 days
And 13 of those retrievals led to successful task completions
When the reinforcement loop runs
Then the relevance weight of edges in that subgraph is increased
And future queries touching "auth" will rank jwt and crypto nodes higher
And the weight adjustment is bounded to prevent runaway amplification
```

### Scenario: Decay stale episodic memories

```gherkin
Given an episodic memory from 90 days ago referencing a function that has since been deleted
When the memory evolution sweep runs
Then the episodic node's confidence score is decayed by the configured half-life factor
And if the referenced structural node no longer exists, the episodic node is marked "orphaned"
And orphaned episodic nodes are excluded from retrieval unless explicitly requested
```

### Scenario: Consolidate repeated patterns into procedural memory

```gherkin
Given the agent has resolved 5 similar "null-check missing" bugs in different services
When the consolidation process analyses episodic memory clusters
Then a procedural memory node is created: "null-check pattern"
And it links to the common code pattern and recommended fix template
And future similar bugs trigger retrieval of this procedural node before code search
```

### Scenario: Learn from failed tasks

```gherkin
Given the agent attempted to refactor DataService but introduced a regression
And the regression was caught by CI and the agent's change was reverted
When the failure is recorded
Then an episodic memory node is created with { outcome: "failure", root_cause, reverted_commit }
And the failure node is linked to the affected structural nodes
And future tasks touching DataService will surface the failure memory as a caution
```

---

## Feature: Edge Cases — Repository and Graph Integrity

### Scenario: Repository with deeply nested directory structure (> 20 levels)

```gherkin
Given a repository with a directory nesting depth of 25 levels
When the structure extraction pipeline runs
Then all 25 levels are represented in the graph
And the configurable max_depth parameter (default 30) is not exceeded
And performance remains within SLA (< 60 seconds for 10,000 files)
```

### Scenario: Repository with symlinks and circular directory references

```gherkin
Given a repository where lib/utils is a symlink pointing to ../shared/utils
And shared/utils contains a symlink back to lib/
When the structure extraction pipeline runs
Then symlinks are resolved to their canonical paths
And circular symlink chains are detected and broken after one resolution
And a warning event is emitted listing the circular symlink paths
```

### Scenario: Extremely large file with thousands of functions

```gherkin
Given a generated file api/routes.ts containing 2,500 route handler functions
When call-chain extraction processes the file
Then all 2,500 functions are represented as nodes
And extraction completes within the per-file timeout (default 30 seconds)
And if the timeout is exceeded, partial results are stored and the file is flagged for retry
```

### Scenario: Concurrent graph writes from parallel extraction workers

```gherkin
Given two extraction workers process packages A and B simultaneously
When both workers attempt to commit their subgraphs to storage
Then optimistic concurrency control prevents lost updates
And if a conflict is detected, the later writer retries with a merged base
And the final graph contains complete subgraphs for both A and B
```

### Scenario: Corrupt or unparseable source file

```gherkin
Given a file src/broken.ts has a syntax error that prevents AST parsing
When the extraction pipeline encounters the file
Then an error event is logged with { file, error_type, line_number }
And the file node is created in the structural graph with status = "parse_error"
And remaining files in the same module continue to be processed without interruption
```

### Scenario: Graph storage reaches capacity limits

```gherkin
Given the graph storage backend has a configured maximum of 5,000,000 nodes
And the current graph contains 4,999,000 nodes
And a new extraction would add 50,000 nodes
When the extraction pipeline attempts to commit
Then the system emits a capacity warning before the write
And the operator is notified via the alerting channel
And the write proceeds if soft-limit mode is enabled, or is rejected if hard-limit mode is enabled
And the rejection includes a recommendation to archive older graph versions
```

---

## Feature: Edge Cases — Retrieval Robustness

### Scenario: Query for a symbol that exists in multiple branches

```gherkin
Given function processOrder exists in both main and feature/v2 branches
And the graphs for both branches are stored
When the agent queries "processOrder" without specifying a branch
Then the retrieval layer defaults to the repository's default branch (main)
And the response includes a note: "also found in branches: feature/v2"
```

### Scenario: Query with zero matching nodes

```gherkin
Given the agent asks "explain the Kubernetes deployment pipeline"
And no node in the graph matches "Kubernetes" or "deployment pipeline"
When similarity retrieval returns zero results above the relevance threshold
Then the agent receives an empty context payload with reason = "no_matching_nodes"
And the agent falls back to general knowledge without hallucinating repo-specific details
```

### Scenario: Retrieve context when the graph is mid-update

```gherkin
Given a graph re-index is in progress for commit def456
And the agent queries for Checkout.process during the re-index
When the retrieval layer processes the query
Then it serves results from the last fully committed snapshot (abc123)
And the response metadata includes { snapshot: "abc123", update_in_progress: true }
And no partial or inconsistent subgraph is ever returned
```

### Scenario: Excessive fan-out on a hub node

```gherkin
Given node Logger.info has 800 inbound call edges
When the agent queries "what calls Logger.info?"
Then the retrieval layer applies pagination: returns the first 50 callers sorted by recency
And the response includes { total_callers: 800, page: 1, page_size: 50 }
And the agent can request subsequent pages if needed
```

---

## Feature: End-to-End Learning Loop

These scenarios validate the full cycle: ingest → store → retrieve → act →
learn — confirming the agent genuinely improves over repeated interactions.

### Scenario: Agent answers a structural question more accurately after learning

```gherkin
Given the agent was asked "what modules depend on AuthService?" on day 1
And it returned 3 out of 5 actual dependents (60% recall)
And the missing 2 dependents were identified during code review and fed back
When the agent is asked the same question on day 8
Then the memory graph has been updated with the corrected dependency edges
And the agent returns all 5 dependents (100% recall)
And the retrieval path includes the episodic memory of the prior correction
```

### Scenario: Agent reuses procedural memory to accelerate a fix

```gherkin
Given procedural memory exists for "add null-check before database call"
And the agent encounters a new NullReferenceException in OrderService.save
When the agent retrieves context for the error
Then the procedural memory node is included in the context
And the agent proposes a fix matching the learned pattern within 1 retrieval round
And the fix is applied without requiring additional call-chain exploration
```

### Scenario: Agent avoids a previously failed approach

```gherkin
Given episodic memory records a failed refactor of DataService using strategy "inline all helpers"
And the failure was caused by exceeding the function length lint rule
When the agent is asked to refactor DataService again
Then the failure episodic node is surfaced in the context
And the agent explicitly avoids the "inline all helpers" strategy
And the agent's proposed approach references the prior failure as justification
```

### Scenario: Multi-session continuity — agent retains expertise across restarts

```gherkin
Given the agent built expertise over 20 sessions analysing the payments module
And the agent process is restarted (new session)
When the agent is asked "summarize the payments architecture"
Then it retrieves the persisted graph and episodic memories from prior sessions
And the response quality matches or exceeds that of the 20th session
And no re-indexing of the repository is required for the response
```

### Scenario: Cold start — first interaction with an unknown repository

```gherkin
Given the agent has never seen repository github.com/acme/widget
When the agent is asked "explain the architecture of this repo"
Then the extraction pipeline runs a full structural and call-chain analysis
And a progress indicator reports extraction status to the user
And upon completion, the agent delivers a top-down architectural summary
And the built graph is persisted for all subsequent queries
And total cold-start time for a 10,000-file repo is under 5 minutes
```

---

## Feature: Cross-Repository Memory and Context Linking

When a repository depends on other indexed repositories, the memory system
must resolve external-symbol stubs across graph boundaries so agents receive
complete call-chain context.

### Scenario: Resolve cross-repo call edges during retrieval

```gherkin
Given graphs exist for both "acme/backend" and "acme/shared-lib"
And "acme/backend" has external-symbol stubs referencing functions in "acme/shared-lib"
When the agent queries for the 2-hop call neighbourhood of "auth.authenticate"
    with cross-repo linking enabled
Then stubs are resolved to concrete nodes in "acme/shared-lib"
And the returned subgraph spans both repositories
And each node carries a "source_repository" attribute identifying its origin
```

### Scenario: Cross-repo linking respects per-repository permissions

```gherkin
Given agent "agent-A" has read access to "acme/backend"
And agent "agent-A" does not have access to "acme/secret-lib"
And "acme/backend" graph contains external-symbol stubs referencing "acme/secret-lib"
When agent-A queries with cross-repo linking enabled
Then stubs referencing "acme/secret-lib" remain unresolved
And no content from "acme/secret-lib" is included in the response
And the response metadata notes "1 cross-repo reference withheld: insufficient access"
```

### Scenario: Cross-repo stub where target repository is not yet indexed

```gherkin
Given "acme/backend" references "acme/payments" via import
And "acme/payments" has never been extracted into the graph store
When the agent queries with cross-repo linking enabled
Then the stub node for "acme/payments" symbols remains with kind = "external_dependency"
And the response metadata includes a suggestion: "index acme/payments for deeper context"
And the rest of the subgraph is returned without error
```

---

## Feature: Security, Access Control, and Data Integrity

Graph memory must enforce repository-level access controls, validate data
integrity, and maintain an audit trail for all agent interactions.

### Scenario: Agent can only query graphs for repositories it has access to

```gherkin
Given agent "agent-A" has read access to "acme/backend"
And agent "agent-A" does not have access to "acme/secret-ops"
When agent-A queries for context from "acme/secret-ops"
Then the retrieval service returns an access-denied error
    with reason "insufficient-repository-access"
And the request is recorded in the audit log with agent ID, repository, and denial reason
```

### Scenario: Graph data integrity validation

```gherkin
Given a graph has been committed for "acme/backend" at commit abc123
When the integrity checker runs
Then every "calls" edge references existing source and target nodes
And every child edge's target node references back to its parent
And no orphan nodes exist (every node is reachable from a root)
And the checker reports status "integrity-ok"
```

### Scenario: Tampered graph data is detected

```gherkin
Given a node's metadata has been externally modified outside the extraction pipeline
When the integrity checker runs
Then the checker detects the checksum mismatch on the tampered node
And flags the node with status "integrity-violation"
And an alert is raised to the operator
And the node is excluded from retrieval results until re-extracted
```

### Scenario: Agent identity is validated when recording outcome signals

```gherkin
Given agent "agent-A" attempts to report an outcome signal with agent ID "agent-B"
When the evolution service receives the signal
Then the service validates the caller's authenticated identity
And rejects the signal with error "agent-id-mismatch"
And records the attempted identity mismatch in the security audit log
```

### Scenario: Audit trail for all graph mutations

```gherkin
Given the graph for "acme/backend" undergoes extraction, consolidation, and
    compaction operations over a 7-day period
When an operator queries the audit log for "acme/backend"
Then every mutation (node create, update, tombstone, merge, purge) is listed
And each entry includes timestamp, operation type, actor (pipeline or agent), and affected node IDs
And the audit log is append-only and tamper-evident

```

---

## Feature: Resilience and Operational Edge Cases

### Scenario: Network partition between agent and memory service

```gherkin
Given the agent's connection to the retrieval service is interrupted
When the agent attempts to query graph memory
Then the agent receives a timeout error within the configured SLA (default 10 seconds)
And the agent falls back to local cache if a prior retrieval result is cached
And when connectivity is restored the agent's deferred outcome signals are flushed
    to the evolution service in order
```

### Scenario: Memory service recovers from crash mid-extraction

```gherkin
Given an extraction pipeline is processing commit def456
And the service crashes after committing 60% of the subgraph nodes
When the service restarts
Then the incomplete transaction is rolled back automatically
And the graph remains at the last fully committed version (abc123)
And the extraction for def456 is retried from scratch
And an incident event is recorded with crash diagnostics
```

### Scenario: Conflicting outcome signals from different agents

```gherkin
Given agent-A reports a positive-outcome for the subgraph around "billing.charge"
And agent-B reports a negative-outcome for the same subgraph within the same time window
When the expertise scoring job runs
Then both signals are recorded independently with their agent IDs
And the expertise score reflects the net effect of both signals
And a "conflicting-signals" annotation is attached for operator review
```

### Scenario: Graph compaction reclaims space from tombstoned nodes

```gherkin
Given 50 successive incremental extractions have accumulated tombstoned nodes
When a compaction job runs
Then all tombstoned nodes older than the retention window are permanently removed
And the remaining graph is structurally identical to a fresh full extraction at HEAD
And storage size decreases measurably
And the compaction event is logged with before/after node counts and bytes reclaimed
```
