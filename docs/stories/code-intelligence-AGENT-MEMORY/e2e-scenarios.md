# Agent Memory — End-to-End Scenarios

> Story: **code-intelligence:AGENT-MEMORY**
>
> Build graph-based agent memory on top-down repo structure and call-chain
> context so that agents can learn from prior interactions and construct
> richer, more accurate context over time.
>
> Reference taxonomy: [Awesome-GraphMemory](https://github.com/DEEP-PolyU/Awesome-GraphMemory)
> — Memory Extraction → Storage → Retrieval → Evolution.

---

## Feature 1: Repository Structure Graph Construction

Extracts a hierarchical knowledge graph from a repository's top-down
directory / module / package / file / symbol structure.

### Scenario 1.1 — Build repo-structure graph for a typical project

```gherkin
Feature: Repository Structure Graph Construction

  Scenario: Ingest a multi-language repository and produce a structure graph
    Given a repository with the following layout:
      | path                          | language   |
      | src/auth/login.ts             | TypeScript |
      | src/auth/token.ts             | TypeScript |
      | src/api/routes.py             | Python     |
      | lib/utils/hash.go             | Go         |
    When the memory extraction pipeline processes the repository root
    Then a directed acyclic graph is produced with nodes:
      | node_id          | kind       | parent          |
      | /                | repo       |                 |
      | /src             | directory  | /               |
      | /src/auth        | module     | /src            |
      | /src/auth/login  | file       | /src/auth       |
      | /src/auth/token  | file       | /src/auth       |
      | /src/api         | module     | /src            |
      | /src/api/routes  | file       | /src/api        |
      | /lib/utils       | module     | /               |
      | /lib/utils/hash  | file       | /lib/utils      |
    And each file node contains extracted symbol children (functions, classes, exports)
    And the graph includes a metadata envelope with:
      | field              | value          |
      | repo_commit_sha    | <HEAD SHA>     |
      | extraction_time    | <ISO 8601>     |
      | languages_detected | TypeScript, Python, Go |
```

### Scenario 1.2 — Symbol-level extraction within files

```gherkin
  Scenario: Extract symbols from a source file
    Given a TypeScript file "src/auth/login.ts" containing:
      """
      export class AuthService {
        login(user: string, pass: string): Token { ... }
        private validate(user: string): boolean { ... }
      }
      export function createSession(token: Token): Session { ... }
      """
    When the extraction pipeline processes the file
    Then the graph contains symbol nodes:
      | symbol_id                        | kind     | visibility |
      | /src/auth/login::AuthService     | class    | export     |
      | /src/auth/login::AuthService.login    | method   | public     |
      | /src/auth/login::AuthService.validate | method   | private    |
      | /src/auth/login::createSession   | function | export     |
    And each symbol node stores its line range, signature, and doc-comment if present
```

### Scenario 1.3 — Edge: repository with no parseable source files

```gherkin
  Scenario: Repository contains only non-code files
    Given a repository containing only:
      | path           |
      | README.md      |
      | LICENSE        |
      | .gitignore     |
      | data/input.csv |
    When the extraction pipeline processes the repository
    Then a structure graph is produced containing directory nodes only
    And no symbol nodes are created
    And a diagnostic is emitted:
      | severity | message                                      |
      | warning  | No parseable source files detected in repository |
```

### Scenario 1.4 — Edge: circular module dependencies

```gherkin
  Scenario: Modules with circular import dependencies
    Given file "a.py" imports from "b.py"
    And file "b.py" imports from "a.py"
    When the extraction pipeline processes both files
    Then both files appear in the structure graph
    And a bidirectional dependency edge is recorded between the modules
    And a diagnostic is emitted:
      | severity | message                                  |
      | info     | Circular dependency detected: a.py ↔ b.py |
```

### Scenario 1.5 — Edge: monorepo with multiple ecosystems

```gherkin
  Scenario: Monorepo with independent sub-projects
    Given a repository with:
      | path                        | ecosystem |
      | frontend/package.json       | npm       |
      | backend/requirements.txt    | pip       |
      | infra/go.mod                | go        |
    When the extraction pipeline processes the repository root
    Then separate sub-graphs are created per ecosystem root
    And a top-level repo node links to each ecosystem sub-graph
    And cross-ecosystem edges are not inferred unless explicit imports exist
```

### Scenario 1.6 — Edge: partial parse failure produces partial graph

```gherkin
  Scenario: Some source files fail to parse
    Given a repository with 100 source files
    And 3 files contain syntax errors that prevent AST construction
    When the extraction pipeline processes the repository
    Then a structure graph is produced containing the 97 successfully parsed files
    And the 3 failed files appear as file nodes with a "parse_failed" status
    And a diagnostic report lists each failure with file path, line, and error message
    And the graph metadata includes:
      | field             | value |
      | files_attempted   | 100   |
      | files_succeeded   | 97    |
      | files_failed      | 3     |
```

---

## Feature 2: Call-Chain Context Extraction

Builds a call graph capturing caller → callee relationships within and
across files so that agents can trace execution context.

### Scenario 2.1 — Single entry-point call graph

```gherkin
Feature: Call-Chain Context Extraction

  Scenario: Build call graph from a single entry-point function
    Given function "handleRequest" in "src/api/routes.py" calls:
      | callee                  | location            |
      | authenticate            | src/auth/login.ts   |
      | fetchUserProfile        | src/user/profile.ts |
      | sendResponse            | src/api/response.ts |
    When the call-chain extractor processes "handleRequest" as entry point
    Then the call graph contains directed edges:
      | from            | to                | edge_type      |
      | handleRequest   | authenticate      | direct_call    |
      | handleRequest   | fetchUserProfile  | direct_call    |
      | handleRequest   | sendResponse      | direct_call    |
    And each edge records the call site (file, line, column)
```

### Scenario 2.2 — Cross-file transitive call chain

```gherkin
  Scenario: Resolve transitive call chains across files
    Given "main.ts" calls "processOrder" in "order.ts"
    And "processOrder" calls "validatePayment" in "payment.ts"
    And "validatePayment" calls "chargeCard" in "billing.ts"
    When the call-chain extractor resolves the full graph from "main.ts"
    Then the transitive call chain is:
      | depth | caller          | callee          |
      | 0     | main            | processOrder    |
      | 1     | processOrder    | validatePayment |
      | 2     | validatePayment | chargeCard      |
    And each node carries its file path, signature, and summary
```

### Scenario 2.3 — Edge: dynamic dispatch / polymorphic calls

```gherkin
  Scenario: Polymorphic method call produces multiple candidate edges
    Given interface "Storage" is implemented by "DiskStorage" and "S3Storage"
    And function "backup" calls "storage.write()" where storage is typed as "Storage"
    When the call-chain extractor processes "backup"
    Then the call graph contains ambiguous edges:
      | from   | to                 | edge_type   | confidence |
      | backup | DiskStorage.write  | polymorphic | 0.5        |
      | backup | S3Storage.write    | polymorphic | 0.5        |
    And the edges are annotated with "requires_runtime_resolution"
```

### Scenario 2.4 — Edge: external dependency boundary nodes

```gherkin
  Scenario: Calls into third-party libraries create boundary nodes
    Given function "encrypt" calls "crypto.createCipheriv" from Node.js stdlib
    And function "encrypt" calls "aws.KMS.encrypt" from "@aws-sdk/client-kms"
    When the call-chain extractor processes "encrypt"
    Then boundary nodes are created:
      | node_id                       | kind     | source   |
      | ext::crypto.createCipheriv    | boundary | stdlib   |
      | ext::aws.KMS.encrypt          | boundary | npm:@aws-sdk/client-kms |
    And no further graph expansion occurs beyond boundary nodes
```

### Scenario 2.5 — Edge: recursive call chains

```gherkin
  Scenario: Recursive function does not cause infinite graph expansion
    Given function "traverse" calls itself recursively
    And "traverse" also calls "visit"
    When the call-chain extractor processes "traverse"
    Then the graph contains a self-referencing edge:
      | from     | to       | edge_type |
      | traverse | traverse | recursive |
      | traverse | visit    | direct_call |
    And the extractor terminates without stack overflow or infinite loop
```

### Scenario 2.6 — Edge: symbol ambiguity (duplicate names, re-exports)

```gherkin
  Scenario: Two modules define the same symbol name
    Given "src/utils/format.ts" exports function "format"
    And "src/logging/format.ts" exports function "format"
    And "src/api/handler.ts" imports "format" from "src/utils/format.ts"
    When the call-chain extractor resolves calls in "handler.ts"
    Then the edge targets "src/utils/format.ts::format" specifically
    And "src/logging/format.ts::format" is not incorrectly linked
    And the graph records fully-qualified symbol identifiers to prevent ambiguity
```

### Scenario 2.7 — Edge: cross-service / RPC boundaries

```gherkin
  Scenario: Call chain crosses a service boundary via HTTP/RPC
    Given function "placeOrder" in "order-service/handler.ts" makes an HTTP call to
      "POST /api/payments" served by "payment-service/controller.ts::createPayment"
    When the call-chain extractor processes "placeOrder"
    Then a cross-service edge is created:
      | from       | to             | edge_type    | protocol |
      | placeOrder | createPayment  | service_call | HTTP     |
    And the edge is annotated with the API route and HTTP method
    And the target is resolved only if the payment-service is within the same repo
```

---

## Feature 3: Knowledge Graph Storage

Persists the extracted memory graph with hierarchical summaries and
temporal versioning.

### Scenario 3.1 — Persist graph to storage backend

```gherkin
Feature: Knowledge Graph Storage

  Scenario: Store the extracted graph after initial ingestion
    Given a fully extracted structure + call-chain graph for commit "abc123"
    When the storage layer persists the graph
    Then the graph is retrievable by repository ID and commit SHA
    And the stored representation includes:
      | component         | count |
      | structure_nodes   | 342   |
      | symbol_nodes      | 1287  |
      | call_edges        | 2910  |
      | hierarchy_edges   | 342   |
    And a storage receipt is returned with graph_id and byte size
```

### Scenario 3.2 — Hierarchical memory summaries

```gherkin
  Scenario: Generate multi-level summaries for the knowledge graph
    Given a stored graph for repository "my-app"
    When hierarchical summarization is triggered
    Then summaries are generated at each level:
      | level        | example_node     | summary_contains                |
      | repo         | /                | "TypeScript web app with auth"  |
      | module       | /src/auth        | "Authentication and session mgmt" |
      | file         | /src/auth/login  | "Login flow with JWT tokens"    |
      | symbol       | AuthService      | "Handles user authentication"   |
    And each summary is stored as a property on the corresponding node
    And summaries are bounded to a maximum token length per level:
      | level  | max_tokens |
      | repo   | 500        |
      | module | 200        |
      | file   | 100        |
      | symbol | 50         |
```

### Scenario 3.3 — Temporal versioning across commits

```gherkin
  Scenario: Store graph snapshots per commit for temporal queries
    Given graphs have been stored for commits:
      | commit  | timestamp           |
      | abc123  | 2026-04-01T10:00:00 |
      | def456  | 2026-04-15T14:30:00 |
      | ghi789  | 2026-05-01T09:00:00 |
    When a temporal query requests the graph state at "2026-04-20"
    Then the graph for commit "def456" is returned (most recent before date)
    And the response includes the temporal context:
      | field          | value               |
      | resolved_commit| def456              |
      | exact_match    | false               |
      | query_date     | 2026-04-20          |
```

### Scenario 3.4 — Edge: concurrent writes from multiple agents

```gherkin
  Scenario: Two agents attempt to write overlapping graph regions
    Given agent-A is storing an updated call graph for module "/src/auth"
    And agent-B is simultaneously storing an updated call graph for module "/src/auth"
    When both writes are submitted concurrently
    Then one write succeeds and the other receives a conflict error
    And the conflict error includes the conflicting node IDs and a retry hint
    And no partial or corrupted graph state is persisted
```

### Scenario 3.5 — Edge: storage quota exceeded

```gherkin
  Scenario: Graph storage exceeds configured quota
    Given the storage backend has a 500 MB quota for this repository
    And the current stored graph occupies 490 MB
    And a new graph version requires 20 MB additional storage
    When the storage layer attempts to persist the new version
    Then the write is rejected with a quota-exceeded error
    And a suggestion is returned to run memory consolidation or increase quota
    And the previous graph version remains intact and queryable
```

### Scenario 3.6 — Edge: sensitive content exclusion

```gherkin
  Scenario: Secrets and credentials are excluded from the graph
    Given a repository contains:
      | path                | content_type      |
      | .env                | environment vars  |
      | config/secrets.yaml | API keys          |
      | src/app.ts          | application code  |
    And a ".memoryignore" or ".gitignore" pattern excludes ".env" and "secrets.yaml"
    When the extraction pipeline processes the repository
    Then no nodes are created for ".env" or "config/secrets.yaml"
    And no symbol nodes contain secret values (API keys, tokens, passwords)
    And string literals matching secret patterns are redacted in stored summaries
    And the extraction report confirms:
      | field           | value |
      | files_excluded  | 2     |
      | secrets_redacted| true  |
```

---

## Feature 4: Context Retrieval from Memory

Queries the knowledge graph to return ranked, token-budgeted context
packs for agent consumption.

### Scenario 4.1 — Query by symbol name returns subgraph with call context

```gherkin
Feature: Context Retrieval from Memory

  Scenario: Retrieve context for a known symbol
    Given a stored graph containing symbol "AuthService.login" with:
      | relationship  | target                  |
      | calls         | TokenStore.create       |
      | calls         | UserRepo.findByEmail    |
      | called_by     | LoginController.handle  |
    When the retrieval engine receives query: symbol="AuthService.login", depth=1
    Then the result includes:
      | node                       | included | reason       |
      | AuthService.login          | yes      | query target |
      | TokenStore.create          | yes      | 1-hop callee |
      | UserRepo.findByEmail       | yes      | 1-hop callee |
      | LoginController.handle     | yes      | 1-hop caller |
    And each node carries its source snippet, file path, and summary
```

### Scenario 4.2 — Natural-language intent query returns ranked results

```gherkin
  Scenario: Query by natural-language intent
    Given a stored graph with summaries for 1,287 symbol nodes
    When the retrieval engine receives query: intent="How does user authentication work?"
    Then the result is a ranked list of nodes:
      | rank | node                    | relevance_score |
      | 1    | AuthService.login       | 0.95            |
      | 2    | AuthService.validate    | 0.88            |
      | 3    | TokenStore.create       | 0.82            |
    And the ranking is based on summary embedding similarity
    And at most top-K results are returned (default K=10)
    And each result includes a confidence score between 0.0 and 1.0
```

### Scenario 4.3 — Multi-hop transitive traversal

```gherkin
  Scenario: Retrieve transitive callers of a function
    Given function "chargeCard" is called by "validatePayment"
    And "validatePayment" is called by "processOrder"
    And "processOrder" is called by "handleCheckout"
    When the retrieval engine receives query: symbol="chargeCard", direction="callers", depth=3
    Then the result contains the full call chain:
      | depth | node             |
      | 0     | chargeCard       |
      | 1     | validatePayment  |
      | 2     | processOrder     |
      | 3     | handleCheckout   |
```

### Scenario 4.4 — Token-budgeted context assembly

```gherkin
  Scenario: Assemble a context pack within a token budget
    Given 25 relevant nodes are retrieved for query "authentication flow"
    And the full context of all 25 nodes would consume 12,000 tokens
    And the agent's token budget for context is 4,000 tokens
    When the context assembler builds the context pack
    Then it includes nodes in priority order:
      | priority | criteria                          |
      | 1        | direct query matches              |
      | 2        | 1-hop call-chain neighbors        |
      | 3        | module-level summaries            |
      | 4        | repo-level summary                |
    And the total token count of the assembled pack is ≤ 4,000
    And truncated nodes are replaced with their summary (not omitted entirely)
    And the pack includes a provenance header listing included node IDs and confidence scores
```

### Scenario 4.5 — Edge: query returns no matches

```gherkin
  Scenario: No relevant nodes found for a query
    Given a stored graph for a Python backend project
    When the retrieval engine receives query: intent="iOS SwiftUI view layout"
    Then the result set is empty
    And the response includes:
      | field          | value                                    |
      | matches        | 0                                        |
      | suggestion     | "No relevant code found in this repository" |
      | closest_topics | ["API routes", "database models"]        |
```

### Scenario 4.6 — Edge: result explosion (too many matches)

```gherkin
  Scenario: Query matches an excessively large portion of the graph
    Given a query for symbol "logger" which is called by 500+ functions
    When the retrieval engine processes the query
    Then results are capped at the configured maximum (default: 50 nodes)
    And the response includes:
      | field           | value                                |
      | total_matches   | 523                                  |
      | returned        | 50                                   |
      | truncated       | true                                 |
      | truncation_hint | "Narrow query with module scope or caller filter" |
```

### Scenario 4.7 — Edge: stale memory references deleted code

```gherkin
  Scenario: Retrieved memory references code that has been deleted
    Given the stored graph was built at commit "abc123"
    And function "legacyAuth" existed at "abc123" but was deleted in "def456"
    And the current working tree is at commit "def456"
    When the retrieval engine returns "legacyAuth" as a result
    Then the node is annotated with:
      | field          | value                              |
      | staleness      | deleted                            |
      | last_seen_at   | abc123                             |
      | confidence     | 0.2 (reduced from original score)  |
    And stale nodes are ranked below non-stale nodes in the result set
```

### Scenario 4.8 — Edge: provenance and confidence tracking

```gherkin
  Scenario: Each retrieved node includes provenance metadata
    Given the retrieval engine returns 5 nodes for a query
    Then each node in the response includes:
      | field              | description                                       |
      | source_commit      | commit SHA where the node was last extracted        |
      | extraction_method  | "ast_parse" or "llm_inferred" or "heuristic"       |
      | confidence         | 0.0–1.0 score based on extraction reliability       |
      | last_validated     | timestamp of last validation against source         |
    And nodes with confidence < 0.3 carry a "low_confidence" warning
```

---

## Feature 5: Memory Evolution and Learning

Updates the knowledge graph incrementally as the codebase changes and
incorporates agent task-outcome feedback to strengthen useful pathways.

### Scenario 5.1 — Incremental update after a commit

```gherkin
Feature: Memory Evolution and Learning

  Scenario: Update graph incrementally after a small commit
    Given a stored graph at commit "abc123" with 1,287 symbol nodes
    And commit "def456" modifies 3 files and adds 1 new file
    When the incremental update pipeline processes the diff between "abc123" and "def456"
    Then only the 4 affected files are re-extracted
    And the graph is updated in-place:
      | operation       | count |
      | nodes_added     | 12    |
      | nodes_modified  | 8     |
      | nodes_removed   | 2     |
      | edges_updated   | 15    |
    And the graph's commit SHA is advanced to "def456"
    And unaffected regions of the graph are unchanged
```

### Scenario 5.2 — Memory consolidation

```gherkin
  Scenario: Consolidate redundant and low-value nodes
    Given the graph has accumulated 50 snapshots over 3 months
    And 200 nodes reference symbols that were created and deleted within that period
    And 30 pairs of nodes are near-duplicates (e.g., renamed but semantically identical)
    When memory consolidation runs
    Then transient nodes (created and deleted within window) are archived
    And near-duplicate node pairs are merged, preserving the most recent identity
    And frequently-accessed edges have their weight increased
    And the consolidated graph is smaller:
      | metric          | before | after |
      | total_nodes     | 5,000  | 4,750 |
      | total_edges     | 12,000 | 11,400|
    And an audit log records every merge and archive operation
```

### Scenario 5.3 — Agent learns from task outcomes (feedback loop)

```gherkin
  Scenario: Successful task outcome reinforces memory pathways
    Given an agent completed a coding task using context from nodes:
      | node_id               | was_useful |
      | AuthService.login     | true       |
      | TokenStore.create     | true       |
      | Logger.info           | false      |
    When the agent reports task success with per-node usefulness feedback
    Then edge weights are updated:
      | edge                                    | weight_change |
      | AuthService.login → TokenStore.create   | +0.1          |
      | query("auth") → AuthService.login       | +0.1          |
      | query("auth") → Logger.info             | -0.05         |
    And future queries for similar intents will rank AuthService.login higher
    And Logger.info will be deprioritized for auth-related queries
```

### Scenario 5.4 — Agent avoids prior mistakes on retry

```gherkin
  Scenario: Prior failure is stored and explicitly avoided on retry
    Given an agent previously attempted task "fix login timeout" and failed
    And the failure was attributed to using deprecated method "OldAuth.connect"
    And this failure record is stored in the memory graph as a negative exemplar
    When the agent receives a similar task "fix authentication timeout"
    Then the retrieval engine returns "OldAuth.connect" with:
      | field            | value                                |
      | prior_outcome    | failure                              |
      | failure_reason   | "deprecated method, use NewAuth.connect" |
      | recommendation   | avoid                                |
    And the agent's context pack includes "NewAuth.connect" instead
```

### Scenario 5.5 — Edge: large refactor invalidates significant graph portion

```gherkin
  Scenario: Major refactor invalidates a large portion of the graph
    Given a stored graph with 5,000 nodes
    And a refactor commit renames the "src/auth" module to "src/identity"
    And moves 40 files and renames 200 symbols
    When the incremental update pipeline processes the refactor diff
    Then the pipeline detects a large-scale structural change (>10% of nodes affected)
    And it performs a targeted re-extraction of the affected subtree
    And rename lineage is preserved:
      | old_id                   | new_id                       | lineage_type |
      | /src/auth/login::login   | /src/identity/login::login   | renamed      |
    And historical queries referencing old paths resolve to new paths via lineage
```

### Scenario 5.6 — Edge: branch divergence

```gherkin
  Scenario: Memory built on one branch is queried from a different branch
    Given the memory graph was built from branch "main" at commit "abc123"
    And the agent is working on branch "feature/new-auth" which diverged 20 commits ago
    When the agent queries memory for "AuthService"
    Then the retrieval engine detects branch divergence
    And the response includes:
      | field              | value                                        |
      | branch_source      | main                                         |
      | branch_current     | feature/new-auth                             |
      | divergence_commits | 20                                           |
      | staleness_risk     | medium                                       |
    And results from files modified on the feature branch are flagged as potentially stale
```

### Scenario 5.7 — Edge: rename/move preserves temporal identity

```gherkin
  Scenario: File rename preserves memory continuity
    Given file "src/helpers/auth.ts" is renamed to "src/services/authentication.ts"
    And the function signatures inside are unchanged
    When the incremental update processes the rename
    Then the old file node is marked as "moved"
    And a lineage edge links old path to new path
    And all prior retrieval history (access counts, usefulness scores) transfers to the new node
    And queries using the old path transparently resolve to the new path
```

---

## Feature 6: Agent Context Construction (End-to-End)

Validates the full pipeline from task intake → memory retrieval →
context assembly → agent output, proving the memory system makes agents
measurably more effective.

### Scenario 6.1 — Agent uses memory to produce contextually-rich output

```gherkin
Feature: Agent Context Construction

  Scenario: Agent retrieves memory and uses it for a coding task
    Given a stored memory graph for repository "my-app"
    And the agent receives task: "Add rate limiting to the login endpoint"
    When the agent queries memory with intent derived from the task
    Then the context pack returned includes:
      | node                       | relevance |
      | LoginController.handle     | high      |
      | AuthService.login          | high      |
      | RateLimiter (if exists)    | high      |
      | /src/api (module summary)  | medium    |
    And the context pack total token count is within the configured budget
    And the agent uses this context to produce a code change
    And the code change references the correct file paths and function signatures
```

### Scenario 6.2 — Second attempt improves via accumulated memory

```gherkin
  Scenario: Agent's second attempt at a similar task leverages prior memory
    Given the agent previously completed task "add caching to user profile endpoint"
    And the memory graph recorded which nodes were useful and the task succeeded
    When the agent receives a new task "add caching to order history endpoint"
    Then the retrieval engine identifies structural similarity to the prior task
    And the context pack includes:
      | source              | content                                 |
      | prior_task_memory   | successful caching pattern from profile  |
      | current_code        | order history endpoint symbols           |
    And the agent's output reuses the validated caching pattern
    And measurable improvement is observed:
      | metric                    | without_memory | with_memory |
      | relevant_files_found      | 3 of 5         | 5 of 5      |
      | correct_import_statements | 2 of 4         | 4 of 4      |
```

### Scenario 6.3 — Edge: cold start with no prior memory

```gherkin
  Scenario: Agent operates on a repository with no prior memory
    Given repository "new-project" has never been ingested
    And the agent receives task: "Fix the broken unit test in auth module"
    When the agent attempts to query memory
    Then the memory system returns:
      | field       | value                                      |
      | status      | cold_start                                 |
      | suggestion  | "Triggering initial repository ingestion"  |
    And the system initiates background graph extraction
    And the agent falls back to direct file-system exploration for the current task
    And once ingestion completes, subsequent queries return graph-based results
```

### Scenario 6.4 — Edge: memory transfer from structurally similar repo

```gherkin
  Scenario: Agent leverages memory from a structurally similar repository
    Given repository "service-A" has rich memory built over 6 months
    And repository "service-B" is a new repo using the same framework and patterns
    And service-B has been ingested but has minimal task history
    When the agent working on service-B queries for "authentication middleware"
    Then the retrieval engine identifies structural similarity with service-A
    And the response includes cross-repo suggestions:
      | source_repo | node                         | transferability |
      | service-A   | AuthMiddleware.handle        | high            |
      | service-A   | middleware/auth.ts (pattern) | high            |
    And cross-repo results are clearly labeled and ranked below local results
    And the agent can opt in to use the transferred pattern
```

---

## Feature 7: Performance and Scale

Validates that memory operations remain bounded and responsive at
production-scale repository sizes.

### Scenario 7.1 — Large repository initial ingestion

```gherkin
Feature: Performance and Scale

  Scenario: Ingest a large repository within acceptable time bounds
    Given a repository with:
      | metric          | value   |
      | total_files     | 50,000  |
      | source_files    | 35,000  |
      | total_symbols   | 200,000 |
      | lines_of_code   | 5M      |
    When the extraction pipeline processes the repository
    Then the ingestion completes within the configured timeout (e.g., 30 minutes)
    And progress is reported incrementally (files processed / total)
    And the resulting graph is queryable immediately upon completion
```

### Scenario 7.2 — Incremental update on large repository

```gherkin
  Scenario: Small commit triggers fast incremental update on a large repo
    Given a stored graph for a 50,000-file repository
    And a new commit modifies 5 files
    When the incremental update pipeline processes the commit
    Then the update completes in under 30 seconds
    And only the 5 modified files and their direct dependents are re-processed
    And the graph is consistent after the update
```

### Scenario 7.3 — Retrieval latency on large graph

```gherkin
  Scenario: Retrieval remains fast on a large knowledge graph
    Given a stored graph with 200,000 nodes and 500,000 edges
    When a symbol query with depth=2 is executed
    Then the query returns results in under 2 seconds (p95)
    And the result set is bounded to the configured maximum (default: 50 nodes)
    When a natural-language intent query is executed
    Then the query returns ranked results in under 5 seconds (p95)
```

### Scenario 7.4 — Concurrent agent access

```gherkin
  Scenario: Multiple agents query the same graph concurrently
    Given a stored graph for repository "shared-service"
    And 10 agents simultaneously submit retrieval queries
    When all queries are processed
    Then all 10 queries return correct results
    And no query exceeds 2x the single-agent latency threshold
    And no data corruption or inconsistency occurs in the graph
```

---

## Appendix: Scenario Traceability Matrix

| Feature | Scenario | Story Aspect Covered | Memory Taxonomy Phase |
|---------|----------|---------------------|-----------------------|
| 1.1 | Repo structure graph | top-down repo structure | Extraction |
| 1.2 | Symbol-level extraction | repo structure, call-chain prep | Extraction |
| 1.3 | No parseable files | edge: degenerate repo | Extraction |
| 1.4 | Circular dependencies | edge: complex repos | Extraction |
| 1.5 | Monorepo ecosystems | edge: real-world scale | Extraction |
| 1.6 | Partial parse failure | edge: robustness | Extraction |
| 2.1 | Single entry-point call graph | call-chain context | Extraction |
| 2.2 | Transitive call chain | call-chain context | Extraction |
| 2.3 | Polymorphic dispatch | edge: ambiguous calls | Extraction |
| 2.4 | External dependencies | edge: boundary nodes | Extraction |
| 2.5 | Recursive calls | edge: infinite loops | Extraction |
| 2.6 | Symbol ambiguity | edge: duplicate names | Extraction |
| 2.7 | Cross-service RPC | edge: service boundaries | Extraction |
| 3.1 | Persist graph | graph storage | Storage |
| 3.2 | Hierarchical summaries | expertise / top-down structure | Storage |
| 3.3 | Temporal versioning | memory evolution | Storage |
| 3.4 | Concurrent writes | edge: multi-agent | Storage |
| 3.5 | Quota exceeded | edge: resource limits | Storage |
| 3.6 | Sensitive content | edge: security | Storage |
| 4.1 | Symbol query | context construction | Retrieval |
| 4.2 | NL intent query | agent gets smarter | Retrieval |
| 4.3 | Multi-hop traversal | call-chain context | Retrieval |
| 4.4 | Token-budgeted assembly | context construction | Retrieval |
| 4.5 | No matches | edge: empty results | Retrieval |
| 4.6 | Result explosion | edge: over-broad query | Retrieval |
| 4.7 | Stale memory | edge: deleted code | Retrieval |
| 4.8 | Provenance tracking | confidence / trust | Retrieval |
| 5.1 | Incremental update | agent learns, memory evolves | Evolution |
| 5.2 | Consolidation | memory evolution | Evolution |
| 5.3 | Feedback loop | agent learns / gets smarter | Evolution |
| 5.4 | Avoid prior mistakes | agent learns / gets smarter | Evolution |
| 5.5 | Large refactor | edge: structural upheaval | Evolution |
| 5.6 | Branch divergence | edge: multi-branch | Evolution |
| 5.7 | Rename lineage | edge: temporal identity | Evolution |
| 6.1 | Context-rich output | construct context, agent smarter | End-to-End |
| 6.2 | Improved second attempt | agent learns / gets smarter | End-to-End |
| 6.3 | Cold start | edge: no prior memory | End-to-End |
| 6.4 | Cross-repo transfer | edge: transfer learning | End-to-End |
| 7.1 | Large repo ingestion | scale: extraction | Performance |
| 7.2 | Incremental on large repo | scale: evolution | Performance |
| 7.3 | Retrieval latency | scale: retrieval | Performance |
| 7.4 | Concurrent access | scale: multi-agent | Performance |
