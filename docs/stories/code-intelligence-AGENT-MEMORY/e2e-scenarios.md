# Agent Memory — End-to-End Scenarios

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> Build graph-based agent memory on top-down repo structure and call-chain
> context so that agents can learn from prior interactions and construct
> richer, more accurate context over time.
>
> **Companion documents** (written in parallel — referenced by name, not duplicated):
> - [`architecture.md`](architecture.md) — canonical authority for vocabulary, data model, edge taxonomy, scoring weights.
> - [`tech-spec.md`](tech-spec.md) — problem statement, constraints, risks, success criteria.
> - [`implementation-plan.md`](implementation-plan.md) — phased work items and deliverables.
>
> When this document and `architecture.md` disagree on naming or thresholds,
> `architecture.md` governs. This document owns Gherkin acceptance scenarios.
>
> **Canonical vocabulary quick-reference** (from `architecture.md` §2):
> - **Layers:** `structural`, `call_chain`, `semantic`, `expertise`
> - **Code-derived edges:** `CONTAINS`, `DEPENDS_ON`, `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`
> - **Memory-only edges:** `SIMILAR_TO`, `BELONGS_TO_CLUSTER`, `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM`
> - **Retired names (do NOT use):** `CALLED_BY` → reverse traversal of `CALLS`; `IMPORTS` → `DEPENDS_ON`; `INHERITS` → `EXTENDS`; `ExpertiseNote` → `ExpertiseEntry`; `IMemoryStore` → `IMemoryGraphStore`
> - **Node ID format:** `"{filePath}::{qualifiedName}"` for symbols, `"{relativePath}"` for files, `"/"` for repo root, `"expertise:{kind}:{hash8}"` for expertise nodes
> - **Scoring weights:** structural 0.30, semantic 0.25, centrality 0.20, recency 0.15, expertise 0.10
> - **Token budget:** default 8,000; ceiling 32,000
> - **Temporal model:** `commit_index` integers (not timestamps); `VersionRange(Since, SinceIndex, Until?, UntilIndex?)`
>
> **Fixture repository:** All scenarios use the actual `src/ast/` subtree of this
> repository as the fixture, unless explicitly noted. MVP languages are **C#** and
> **PowerShell** (via existing `Ast.Csharp` and `Ast.PowerShell` parsers).

---

## Feature 1: Structural Layer — Repository Structure Graph Construction

Extracts the top-down containment hierarchy from the repository into the
Structural layer: Repo → Workspace → Package → Module → Class → Function,
connected by `CONTAINS` and `DEPENDS_ON` edges (per `architecture.md` §2.1,
§2.2). The `Workspace` level is present only in monorepo layouts; in
single-workspace repos like this one it is absent and the Repo root contains
Packages directly.

```gherkin
Feature: Structural Layer — Repository Structure Graph Construction

  Background:
    Given the repository root is "src/ast/"
    And the extraction pipeline is configured with languages ["C#", "PowerShell"]
    And HEAD commit "abc1234" has been registered with commit_index 1

  Scenario: 1.1 — Ingest src/ast/ and produce structural layer graph
    When the extraction pipeline processes the repository root
    Then the graph contains a Repo root node with Id "/" and NodeType "Repo"
    # Workspace nodes are absent: this repo has no monorepo layout with separate
    # workspace directories (e.g., /packages/frontend/, /packages/backend/).
    # Packages are directly under the Repo root. (architecture.md §4.2:
    # "Monorepo child; absent in single-package repos" — and here, packages
    # share a flat layout without an intermediate Workspace level.)
    And no Workspace nodes exist in the graph
    And Package nodes exist for each non-test .csproj, including:
      | node_id                   | NodeType | Metadata.MemoryLayer |
      | /src/ast/Ast.Core         | Package  | structural           |
      | /src/ast/Ast.Csharp       | Package  | structural           |
      | /src/ast/Ast.PowerShell   | Package  | structural           |
      | /src/ast/Ast.Embedding    | Package  | structural           |
      | /src/ast/Ast.GraphDb      | Package  | structural           |
      | /src/ast/Ast.Query        | Package  | structural           |
      | /src/ast/Ast.VectorDb     | Package  | structural           |
      | /src/ast/Ast.Lsp          | Package  | structural           |
      | /src/ast/Ast.Cli          | Package  | structural           |
      | /src/ast/CodeIntelligence | Package  | structural           |
    And each Package node is connected to "/" via a CONTAINS edge:
      | edge SemanticRelation | Roles.parent | Roles.child             |
      | CONTAINS              | /            | /src/ast/Ast.Core       |
      | CONTAINS              | /            | /src/ast/Ast.Csharp     |
    And every node carries VersionRange with SinceIndex = 1 and UntilIndex = null
    And every edge carries Metadata["SemanticRelation"] matching its canonical name

  Scenario: 1.2 — Symbol-level extraction from real C# source files
    Given the file "src/ast/Ast.Core/HypergraphModels.cs" exists in the repository
    When the extraction pipeline processes the file
    Then the graph contains symbol nodes:
      | node_id                                              | NodeType  | Language |
      | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode | Class     | C#       |
      | /src/ast/Ast.Core/HypergraphModels.cs::Hyperedge      | Class     | C#       |
      | /src/ast/Ast.Core/HypergraphModels.cs::HyperedgeType  | Class     | C#       |
      | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText | Function | C# |
      | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEnhancedEmbeddingText | Function | C# |
    And the HypergraphNode class node is connected to its method nodes via CONTAINS edges with Roles:
      | Roles.parent                                           | Roles.child                                                                    |
      | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode  | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText         |
      | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode  | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEnhancedEmbeddingText |
    And the Module node "/src/ast/Ast.Core/HypergraphModels.cs" CONTAINS the class nodes
    And each symbol node has LineStart and LineEnd set to non-zero values

  Scenario: 1.3 — DEPENDS_ON edges from C# using directives
    Given "src/ast/Ast.Csharp/CSharpHyperedgeExtractor.cs" contains:
      """
      using Ast.Core;
      using Microsoft.CodeAnalysis;
      using Microsoft.CodeAnalysis.CSharp;
      """
    When the extraction pipeline processes the file
    Then a DEPENDS_ON edge is created for the in-repo import:
      | SemanticRelation | Roles.importer                              | Roles.imported         |
      | DEPENDS_ON       | /src/ast/Ast.Csharp/CSharpHyperedgeExtractor.cs | /src/ast/Ast.Core  |
    # External imports produce External boundary stub nodes (architecture.md §4.2, §4.4)
    # DEPENDS_ON from a using directive creates a package-level External stub node
    And an External boundary node is created:
      | node_id                                  | NodeType | Metadata.MemoryLayer |
      | ext:Microsoft.CodeAnalysis:*             | External | call_chain           |
    And the External node has no ContentHash, no VersionRange, and no Embedding
    And a DEPENDS_ON edge connects the module to the External boundary node

  Scenario: 1.4 — C# source extraction in Ast.PowerShell package
    Given the file "src/ast/Ast.PowerShell/PowerShellAstParser.cs" exists
    And it contains class PowerShellAstParser with methods ParseFile, ParseCode, ParseRepository
    When the extraction pipeline processes the file as C# source
    Then the graph contains a Module node "/src/ast/Ast.PowerShell/PowerShellAstParser.cs"
    And symbol nodes are created:
      | node_id                                                                       | NodeType |
      | /src/ast/Ast.PowerShell/PowerShellAstParser.cs::PowerShellAstParser            | Class    |
      | /src/ast/Ast.PowerShell/PowerShellAstParser.cs::PowerShellAstParser.ParseFile  | Function |
      | /src/ast/Ast.PowerShell/PowerShellAstParser.cs::PowerShellAstParser.ParseCode  | Function |
      | /src/ast/Ast.PowerShell/PowerShellAstParser.cs::PowerShellAstParser.ParseRepository | Function |
    And the Module node is linked to Package "/src/ast/Ast.PowerShell" via CONTAINS
    # External dependency System.Management.Automation produces an External stub node
    And an External boundary node "ext:System.Management.Automation:*" is created
    And the External node has NodeType "External" and no embedding

  Scenario: 1.5 — Monorepo layout produces Workspace nodes
    Given a monorepo with this directory structure:
      | path                        | marker_file      |
      | /packages/frontend          | package.json     |
      | /packages/backend           | backend.csproj   |
    When the extraction pipeline processes the repository root
    Then the graph contains Workspace nodes:
      | node_id              | NodeType  | Metadata.MemoryLayer |
      | /packages/frontend   | Workspace | structural           |
      | /packages/backend    | Workspace | structural           |
    And the Repo root "/" contains each Workspace via CONTAINS edges
    And each Workspace contains its Package nodes via CONTAINS edges
    # Hierarchy: Repo → Workspace → Package → Module → Class → Function

  Scenario: 1.6 — Repository with no parseable source files
    Given a test directory containing only:
      | path           |
      | README.md      |
      | LICENSE        |
    When the extraction pipeline processes the directory
    Then the graph contains only the Repo root node "/"
    And no Workspace, Package, Module, or Class nodes are created
    And a diagnostic is emitted with severity "warning" and message containing
      "No parseable source files detected"

  Scenario: 1.7 — Partial parse failure produces partial graph
    Given a repository with 20 C# source files
    And 2 files contain syntax errors that prevent Roslyn AST construction
    When the extraction pipeline processes the repository
    Then the graph contains Module nodes for all 20 files
    And the 2 failed files have Metadata["ParseStatus"] = "failed"
    And the 18 successful files have Class and Function children
    And the Snapshot.Stats reports:
      | field         | value |
      | Nodes         | >= 20 |
    And a diagnostic summary reports 2 files with ParseStatus "failed"

  Scenario: 1.8 — HyperedgeType enum values map to CONTAINS edges
    Given the extraction pipeline encounters a ClassDefinition HyperedgeType
    When the edge is stored in the structural layer
    Then the edge's Metadata["SemanticRelation"] is set to "CONTAINS"
    And the edge's Metadata["OriginalEdgeType"] is set to "ClassDefinition"
    And the edge's EdgeType is the original HyperedgeType (not MemoryRelation)
    # Code-derived edges preserve their original HyperedgeType (architecture.md §4.6)
```

---

## Feature 2: Call-Chain Layer — Call Graph Extraction

Builds the Call-Chain layer with `CALLS`, `EXTENDS`, `IMPLEMENTS`, and
`REFERENCES` edges between symbol nodes (per `architecture.md` §2.2).
All edges carry `Metadata["SemanticRelation"]` as the primary filter
and `Metadata["Confidence"]` (1.0 for direct calls, 0.5–0.7 for dynamic dispatch).

```gherkin
Feature: Call-Chain Layer — Call Graph Extraction

  Background:
    Given the repository root is "src/ast/"
    And the structural layer graph has been built at commit_index 1

  Scenario: 2.1 — Direct method invocations produce CALLS edges
    Given "src/ast/Ast.Embedding/CodeEmbeddingService.cs" contains method
      GenerateNodeEmbeddingsAsync which calls IEmbeddingGenerator.GenerateBatchEmbeddingsAsync
    When the call-chain analyzer processes the file
    Then a CALLS edge is created:
      | SemanticRelation | Roles.caller | Roles.callee |
      | CALLS | /src/ast/Ast.Embedding/CodeEmbeddingService.cs::CodeEmbeddingService.GenerateNodeEmbeddingsAsync | /src/ast/Ast.Embedding/IEmbeddingGenerator.cs::IEmbeddingGenerator.GenerateBatchEmbeddingsAsync |
    And the edge Metadata["Confidence"] = 1.0
    And the edge Metadata["MemoryLayer"] = "call_chain"
    And the edge Metadata["OriginalEdgeType"] = "MethodInvocation"

  Scenario: 2.2 — Transitive call chain across files
    Given this call chain exists in the src/ast/ codebase:
      | caller_file                                    | caller_method                           | callee_method                                      |
      | src/ast/Ast.Core/RepositoryProcessor.cs        | RepositoryProcessor.Process             | CSharpHyperedgeExtractor.Extract (via CSharpExtractor delegate) |
      | src/ast/Ast.Csharp/CSharpHyperedgeExtractor.cs | CSharpHyperedgeExtractor.Extract        | CSharpHyperedgeExtractor.ExtractMethodDefinition   |
    When the retrieval engine resolves the call chain from RepositoryProcessor.Process with depth 2
    Then the TraversalResult.Paths contains at least one path of length 2
    And each intermediate node carries its FilePath and Metadata["QualifiedName"]

  Scenario: 2.3 — Polymorphic call produces CALLS edges with reduced confidence
    Given interface IGraphStore in "src/ast/Ast.GraphDb/IGraphStore.cs"
    And class Neo4jGraphStore in "src/ast/Ast.GraphDb/Neo4jGraphStore.cs" implements IGraphStore
    And a method calls graphStore.CreateNodeAsync() where graphStore is typed as IGraphStore
    When the call-chain analyzer processes the calling method
    Then CALLS edges are created to each known implementor:
      | SemanticRelation | Roles.callee                                                     | Confidence |
      | CALLS            | /src/ast/Ast.GraphDb/Neo4jGraphStore.cs::Neo4jGraphStore.CreateNodeAsync | 0.5       |
    And each edge has Metadata["Confidence"] between 0.5 and 0.7
    And Metadata["IsPolymorphic"] = true on each edge

  Scenario: 2.4 — EXTENDS and IMPLEMENTS edges from class hierarchy
    Given class Neo4jGraphStore in "src/ast/Ast.GraphDb/Neo4jGraphStore.cs"
      implements interface IGraphStore in "src/ast/Ast.GraphDb/IGraphStore.cs"
    When the call-chain analyzer processes "src/ast/Ast.GraphDb/"
    Then an IMPLEMENTS edge is created:
      | SemanticRelation | Roles.implementor | Roles.interface |
      | IMPLEMENTS | /src/ast/Ast.GraphDb/Neo4jGraphStore.cs::Neo4jGraphStore | /src/ast/Ast.GraphDb/IGraphStore.cs::IGraphStore |
    And the edge uses HyperedgeType.Implements (code-derived, not MemoryRelation)
    And Metadata["SemanticRelation"] = "IMPLEMENTS"

  Scenario: 2.5 — Cross-language call edges (C# ↔ PowerShell)
    Given a PowerShell test fixture file "test-fixtures/invoke-csharp.ps1" contains:
      """
      $processor = [Ast.Core.RepositoryProcessor]::new($repoPath)
      $processor.Process()
      """
    # Note: scripts/dev.ps1 in this repo is a UI dev launcher; this scenario
    # uses a synthetic fixture to exercise the CrossLanguageMethodCall path.
    And the C# class RepositoryProcessor exists at
      "src/ast/Ast.Core/RepositoryProcessor.cs" with method Process
    When the extraction pipeline processes both files
    Then a CrossLanguageMethodCall edge is created:
      | SemanticRelation | Roles.caller                                                  | Roles.callee                                                          |
      | CALLS            | /test-fixtures/invoke-csharp.ps1::<ScriptBlock>.Process       | /src/ast/Ast.Core/RepositoryProcessor.cs::RepositoryProcessor.Process |
    And the caller node has Language = "PowerShell" and the callee has Language = "C#"
    And the edge has Metadata["OriginalEdgeType"] = "CrossLanguageMethodCall"
    And the edge's IsCrossLanguage property returns true
    # Per Hyperedge.IsCrossLanguage: Languages.Distinct().Count() > 1
    And the edge's Languages list contains both "PowerShell" and "C#"

  Scenario: 2.6 — Recursive call does not cause infinite expansion
    Given a function calls itself recursively
    When the call-chain analyzer processes the function
    Then exactly one self-referencing CALLS edge is created
      with the same node ID in both Roles.caller and Roles.callee
    And the analyzer terminates without stack overflow

  Scenario: 2.7 — External dependency calls create External boundary nodes
    Given CodeEmbeddingService calls methods from Microsoft.Extensions.Logging.ILogger
    When the call-chain analyzer processes the file
    Then an External boundary node is created:
      | node_id                                                   | NodeType | Metadata.MemoryLayer |
      | ext:Microsoft.Extensions.Logging:ILogger.LogInformation   | External | call_chain           |
    And a CALLS edge connects the caller to the External node:
      | SemanticRelation | Roles.caller                                                           | Roles.callee                                                    |
      | CALLS            | /src/ast/Ast.Embedding/CodeEmbeddingService.cs::CodeEmbeddingService.GenerateNodeEmbeddingsAsync | ext:Microsoft.Extensions.Logging:ILogger.LogInformation |
    # External boundary nodes are stubs: no embedding, no VersionRange (architecture.md §4.4)
    And the External node has no ContentHash and no VersionRange
    And the External node is never extracted or embedded
```

---

## Feature 3: Semantic Layer — Embedding & Similarity

Computes embeddings for code nodes via the existing `Ast.Embedding.IEmbeddingGenerator`
interface and creates `SIMILAR_TO` edges when cosine similarity exceeds
`SimilarityThreshold` (0.85, per `architecture.md` §2.4). Memory-only edges
use `HyperedgeType.MemoryRelation` with the true relation in `Metadata["SemanticRelation"]`.

```gherkin
Feature: Semantic Layer — Embedding and Similarity

  Background:
    Given the structural and call-chain layers are populated for "src/ast/"
    And the embedding model is "nomic-embed-text" (per architecture.md §4.7)

  Scenario: 3.1 — Code nodes receive embeddings at extraction time
    When the semantic indexer processes all Function and Class nodes
    Then every code node has Metadata["Embedding"] set to a non-null float[]
    And Metadata["EmbeddingModel"] = "nomic-embed-text" on each node
    And the embedding text is generated via HypergraphNode.GetEmbeddingText()
      or GetEnhancedEmbeddingText() when LSP enrichment is available

  Scenario: 3.2 — SIMILAR_TO edges created above threshold
    Given two Function nodes with cosine similarity 0.91 (above SimilarityThreshold 0.85)
    When the semantic indexer computes pairwise similarity
    Then a SIMILAR_TO edge is created:
      | EdgeType       | SemanticRelation | Metadata.CosineSim | Metadata.MemoryLayer |
      | MemoryRelation | SIMILAR_TO       | 0.91               | semantic             |
    And the edge has exactly 2 NodeIds with Roles "node_a" and "node_b"

  Scenario: 3.3 — Below-threshold pairs do NOT create edges
    Given two Function nodes with cosine similarity 0.72 (below SimilarityThreshold 0.85)
    When the semantic indexer computes pairwise similarity
    Then no SIMILAR_TO edge is created between them

  Scenario: 3.4 — Vector search returns ranked results
    Given the vector index has been built for all code nodes
    When the agent calls VectorSearchAsync with query "extract hypergraph from C# code" and topK = 5
    Then the result contains at most 5 ScoredNode entries
    And the top result is likely a node from "src/ast/Ast.Csharp/CSharpHyperedgeExtractor.cs"
    # Its embedding text contains "extract" and "hyperedge" terms
    And each ScoredNode has a Score between 0.0 and 1.0

  Scenario: 3.5 — Embedding model version tracked per node
    Given a node was embedded with model "nomic-embed-text"
    When the embedding model is upgraded to a different model
    Then re-embedding sets Metadata["EmbeddingModel"] to the new model name
    And retrieval filters to current model version to avoid comparing
      vectors from different embedding spaces
```

---

## Feature 4: Expertise Layer — Agent Learning & Write-Back

Agents record expertise (Pattern, Pitfall, Preference, Insight) as typed
Expertise-layer nodes linked to code targets via `APPLIES_TO` edges.
The Evolution Engine manages write-back, consolidation, and decay
(per `architecture.md` §3.5 and §5.4).

```gherkin
Feature: Expertise Layer — Agent Learning and Write-Back

  Background:
    Given the memory graph is populated for "src/ast/" at commit_index 5
    And the agent session ID is "copilot-session-42"

  Scenario: 4.1 — Record a Pitfall expertise entry
    When the agent calls IEvolutionEngine.RecordExpertiseAsync with:
      | field       | value                                                                   |
      | Kind        | pitfall                                                                 |
      | Title       | Null-deref in HypergraphNode.Metadata access                            |
      | Body        | Accessing Metadata keys without null-check causes NRE when key missing  |
      | AppliesTo   | ["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"]               |
      | Scope       | exact                                                                   |
      | SourceAgent | copilot-session-42                                                      |
    Then a new expertise node is created with:
      | field    | value                            |
      | Id       | expertise:pitfall:<hash8>        |
      | NodeType | Pitfall                          |
      | Metadata.MemoryLayer | expertise              |
      | Metadata.Confidence  | 1.0                    |
      | Metadata.Title       | Null-deref in HypergraphNode.Metadata access |
      | Metadata.SourceAgent | copilot-session-42     |
    And an APPLIES_TO edge is created:
      | EdgeType       | SemanticRelation | Roles.expertise              | Roles.target                                           |
      | MemoryRelation | APPLIES_TO       | expertise:pitfall:<hash8>    | /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode  |
    And the expertise node's embedding is computed from "pitfall: Null-deref in HypergraphNode.Metadata access\n<body>"
    And an audit_log entry is written with operation "Create" and entity_type "Pitfall"

  Scenario: 4.2 — Record a Pattern expertise entry
    When the agent calls RecordExpertiseAsync with Kind = "pattern" and:
      | Title     | Composition-over-inheritance wrapper pattern                          |
      | Body      | MemoryNode wraps HypergraphNode via Inner property, not inheritance   |
      | AppliesTo | ["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"]             |
    Then a Pattern node is created with Metadata.ExpertiseKind = "pattern"
    And Metadata.Confidence = 1.0

  Scenario: 4.3 — Record a Preference expertise entry
    When the agent records Kind = "preference":
      | Title     | Always use this. qualifier for instance members         |
      | Body      | Per .editorconfig, all C# instance members use this.    |
      | AppliesTo | ["/"]                                                    |
      | Scope     | subtree                                                 |
    Then a Preference node is created with APPLIES_TO scope = "subtree"
    And the APPLIES_TO edge targets the repo root "/"

  Scenario: 4.4 — Record an Insight expertise entry
    When the agent records Kind = "insight":
      | Title     | CodeIntelligence analyzers are independent and composable              |
      | Body      | Each of the 27 analyzers in CodeIntelligence/Analyzers/ can run independently |
      | AppliesTo | ["/src/ast/CodeIntelligence"]                                         |
    Then an Insight node is created linked to the CodeIntelligence package

  Scenario: 4.5 — Duplicate expertise triggers DERIVED_FROM edge
    Given an existing Pitfall node "expertise:pitfall:a1a11111" with Title
      "Metadata access may throw KeyNotFoundException"
    And it applies to "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"
    When the agent records a new Pitfall with Title
      "Null-deref in HypergraphNode.Metadata access"
    And the cosine similarity between old and new expertise > SimilarityThreshold (0.85)
    Then a DERIVED_FROM edge is created:
      | SemanticRelation | Roles.derived                | Roles.source                 |
      | DERIVED_FROM     | expertise:pitfall:<new_hash> | expertise:pitfall:a1a11111   |
    And a consolidation is scheduled

  Scenario: 4.6 — Expertise with invalid AppliesTo target is rejected
    When the agent calls RecordExpertiseAsync with AppliesTo = ["/nonexistent/path::Foo"]
    Then the call fails with a validation error indicating the target node does not exist
    And no expertise node is created
    And no audit_log entry is written

  Scenario: 4.7 — Expertise with ext: extension metadata key
    When the agent records expertise with Metadata key "ext:team" = "payments"
    Then the key is stored in the expertise node's Metadata
    And a validation warning is surfaced for the non-registry "ext:" key
    And the expertise node is still created successfully
```

---

## Feature 5: Temporal Versioning — Point-in-Time Queries

Every node and edge carries a `VersionRange` with commit SHA + monotonic
`commit_index`. Tombstoned entities have `Until` set but remain queryable
for history (per `architecture.md` §4.3).

```gherkin
Feature: Temporal Versioning — Point-in-Time Queries

  Background:
    Given the following commits have been registered:
      | commit_sha | commit_index | parent_sha |
      | aaa1111    | 1            | null       |
      | bbb2222    | 2            | aaa1111    |
      | ccc3333    | 3            | bbb2222    |
    And the graph was built at commit_index 1

  Scenario: 5.1 — New nodes carry correct VersionRange
    When a Class node is created during extraction at commit "aaa1111"
    Then the node's VersionRange is:
      | Since   | SinceIndex | Until | UntilIndex |
      | aaa1111 | 1          | null  | null       |

  Scenario: 5.2 — Deleted symbol is tombstoned, not removed
    Given a Function node "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.OldMethod"
      exists with SinceIndex = 1
    When the method is deleted in commit "bbb2222" (commit_index 2)
    Then the node's VersionRange.Until = "bbb2222" and UntilIndex = 2
    And the node is NOT physically deleted from the store
    And GetNodeAtCommitAsync(nodeId, "aaa1111") returns the node
    And GetNodeAtCommitAsync(nodeId, "ccc3333") returns null

  Scenario: 5.3 — Point-in-time query filters by commit_index range
    Given 3 nodes exist:
      | node_id | SinceIndex | UntilIndex |
      | A       | 1          | null       |
      | B       | 1          | 2          |
      | C       | 2          | null       |
    When querying at commit_index 2
    Then the result includes nodes A and C
    And excludes node B (tombstoned at index 2: UntilIndex > target required)

  Scenario: 5.4 — Edges tombstoned when source node is tombstoned
    Given a CALLS edge from node X to node Y, both created at SinceIndex 1
    When node X is tombstoned at commit_index 3
    Then the CALLS edge also receives UntilIndex = 3
    And future queries at commit_index >= 3 do not see the edge

  Scenario: 5.5 — Snapshot created after each sync
    When IncrementalSync.SyncToCommitAsync processes commit "ccc3333"
    Then a Snapshot record is created with:
      | CommitSha | CommitIndex | Stats.Nodes | Stats.Edges |
      | ccc3333   | 3           | > 0         | > 0         |
```

---

## Feature 6: Hybrid Retrieval — Context Construction

The Retrieval Engine accepts a natural-language query, plans retrieval mode(s),
traverses the graph, ranks results using the 5-signal model, and assembles a
token-budgeted context block (per `architecture.md` §3.3–3.4).

```gherkin
Feature: Hybrid Retrieval — Context Construction

  Background:
    Given the full memory graph is populated for "src/ast/" at commit_index 5
    And expertise nodes have been recorded for Ast.Core and Ast.Csharp packages
    And the embedding model is "nomic-embed-text"

  Scenario: 6.1 — Hybrid retrieval with default token budget
    When the agent calls IMemoryRetrievalEngine.RetrieveAsync with:
      | field          | value                                              |
      | Text           | How does C# code extraction work in the AST stack? |
      | TokenBudget    | 8000                                               |
      | IncludeExpertise | true                                             |
    Then the RetrievalResult contains:
      | field         | assertion                                |
      | ContextBlock  | contains "## Structure"                  |
      | ContextBlock  | contains "## Call Graph"                 |
      | ContextBlock  | contains "## Related Code"               |
      | ContextBlock  | contains "## Known Expertise"            |
      | TokenCount    | <= 8000                                  |
      | ResolvedMode  | Hybrid                                   |
      | Citations     | length > 0                               |
    And citations reference nodes from Ast.Csharp and Ast.Core packages

  Scenario: 6.2 — StructuralDrill retrieval mode
    When the agent calls RetrieveAsync with Mode = StructuralDrill and
      Scope = "/src/ast/Ast.Core"
    Then the ContextBlock lists the containment hierarchy of Ast.Core:
      Package → Module files → Class → Function
    And only CONTAINS and DEPENDS_ON edges are traversed
    And the ResolvedMode = StructuralDrill

  Scenario: 6.3 — CallTrace retrieval mode
    When the agent calls RetrieveAsync with Mode = CallTrace and
      Text = "RepositoryProcessor.Process"
    Then the ContextBlock includes callers and callees of RepositoryProcessor.Process
    And the traversal follows CALLS edges up to MaxDepth 3
    And the ResolvedMode = CallTrace

  Scenario: 6.4 — ExpertiseRecall retrieval mode
    Given a Pitfall expertise node applies to HypergraphNode
    When the agent calls RetrieveAsync with Mode = ExpertiseRecall and
      Scope = "/src/ast/Ast.Core"
    Then the ContextBlock's "## Known Expertise" section contains the Pitfall
    And the expertise entry shows its Confidence value
    And the ResolvedMode = ExpertiseRecall

  Scenario: 6.5 — Token budget enforced with hierarchy compression
    When the agent calls RetrieveAsync with TokenBudget = 500
    Then the RetrievalResult.TokenCount <= 500
    And parent summaries replace child details (HiAgent pattern)
    And the ContextBlock still has the four standard sections

  Scenario: 6.6 — Token budget above ceiling is clamped
    When the agent calls RetrieveAsync with TokenBudget = 50000
    Then the effective budget is clamped to TokenBudgetCeiling = 32000
    And the RetrievalResult.TokenCount <= 32000

  Scenario: 6.7 — Scoring weights applied during ranking
    When the ranker computes the fused score for a candidate node
    Then the score formula is:
      score = 0.30 × structural_proximity
            + 0.25 × semantic_similarity
            + 0.20 × centrality
            + 0.15 × recency
            + 0.10 × expertise_signal
    And each signal is normalized to [0, 1]
    And structural_proximity is inverse BFS distance from seed
    And centrality is normalized PageRank from CALLS fan-in
    And recency is 1.0 − (HEAD_idx − node_sinceIdx) / max_distance
    And expertise_signal is max Confidence of APPLIES_TO edges targeting the node

  Scenario: 6.8 — Deduplication: same code span from multiple paths emitted once
    Given a node is reachable via both CALLS and SIMILAR_TO paths
    When the context assembler formats the result
    Then the node's code snippet appears exactly once in the ContextBlock
    And the citation list includes both paths

  Scenario: 6.9 — Query with AtCommit returns historical state
    When the agent calls RetrieveAsync with AtCommit = "aaa1111"
    Then the retrieval engine uses commit_index corresponding to "aaa1111"
    And only nodes/edges with SinceIndex <= that index and (UntilIndex is null OR UntilIndex > that index) are included
```

---

## Feature 7: Incremental Sync — Keeping the Graph Current

On commit or file-save, the Incremental Sync component re-extracts only
changed files and propagates invalidation to downstream edges
(per `architecture.md` §3.6). Performance target: < 2 s for single-file
change on repos ≤ 500K LOC (per `tech-spec.md` HC2).

```gherkin
Feature: Incremental Sync — Keeping the Graph Current

  Background:
    Given the graph is populated at commit_index 5 for "src/ast/"

  Scenario: 7.1 — Single-file change triggers incremental re-extraction
    Given the developer modifies "src/ast/Ast.Core/RepositoryProcessor.cs"
    And commits the change as commit "ddd4444" (commit_index 6)
    When IncrementalSync.SyncToCommitAsync processes "ddd4444"
    Then only RepositoryProcessor.cs is re-extracted (not the full repo)
    And modified nodes get new VersionRange.Since = "ddd4444", SinceIndex = 6
    And the old versions of those nodes get UntilIndex = 6
    And a new Snapshot is created with CommitIndex = 6
    And the sync completes in < 2 seconds

  Scenario: 7.2 — Method signature change marks downstream CALLS edges as Stale
    Given method RepositoryProcessor.Process has signature change in commit "ddd4444"
    And 3 CALLS edges target RepositoryProcessor.Process
    When incremental sync processes the commit
    Then all 3 downstream CALLS edges have Metadata["Stale"] = true
    And the edges remain in the graph (not tombstoned) pending re-confirmation

  Scenario: 7.3 — File deletion tombstones all contained nodes
    Given file "src/ast/Ast.Core/OldHelper.cs" with 2 Class nodes and 5 Function nodes
    When the file is deleted in commit "eee5555" (commit_index 7)
    Then the Module node and all 7 contained symbol nodes receive UntilIndex = 7
    And all CONTAINS edges from the Module receive UntilIndex = 7
    And all CALLS edges referencing any of those 7 nodes receive UntilIndex = 7

  Scenario: 7.4 — ContentHash detects meaningful vs. whitespace-only changes
    Given a file's ContentHash changes due to adding a blank line
    When incremental sync computes the diff
    Then the Module node's ContentHash is updated
    But symbol nodes whose own ContentHash did not change are NOT re-extracted
    And expertise targeting unchanged symbols receives MinorDecayFactor (0.9)
      instead of StaleDecayFactor (0.5)

  Scenario: 7.5 — Debounce prevents rapid-fire re-extraction
    Given IncrementalSyncDebounceMs = 1000 (per architecture.md §2.4)
    When 5 file-save events occur within 500ms
    Then only 1 extraction run is triggered after the debounce window
    And all 5 changed files are processed in a single batch

  Scenario: 7.6 — Diff affecting > 50 files triggers bounded reconcile
    Given a commit renames a namespace affecting 60 files
    When incremental sync processes the commit
    Then a bounded reconcile pass runs on the affected subtrees
    And rename tracking updates node IDs to reflect new paths
    And old-path nodes are tombstoned with UntilIndex = current commit_index
```

---

## Feature 8: Evolution Engine — Consolidation & Decay

Post-session consolidation merges overlapping expertise, decays stale
entries, and generates community summaries (per `architecture.md` §3.5).

```gherkin
Feature: Evolution Engine — Consolidation and Decay

  Background:
    Given the graph at commit_index 10 with multiple expertise nodes

  Scenario: 8.1 — Consolidation merges overlapping expertise via SUPERSEDES
    Given 3 Pitfall nodes about "null handling in Metadata access":
      | Id                         | Confidence | SourceAgent          |
      | expertise:pitfall:aa11bb22 | 0.8        | copilot-session-10   |
      | expertise:pitfall:cc33dd44 | 0.9        | copilot-session-15   |
      | expertise:pitfall:ee55ff66 | 1.0        | copilot-session-20   |
    When IEvolutionEngine.ConsolidateAsync runs for scope "/src/ast/Ast.Core"
    Then 1 merged expertise node is produced
    And SUPERSEDES edges are created:
      | SemanticRelation | Roles.newer                   | Roles.older                   |
      | SUPERSEDES       | expertise:pitfall:<merged>    | expertise:pitfall:aa11bb22    |
      | SUPERSEDES       | expertise:pitfall:<merged>    | expertise:pitfall:cc33dd44    |
      | SUPERSEDES       | expertise:pitfall:<merged>    | expertise:pitfall:ee55ff66    |
    And the ConsolidationReport shows:
      | field       | value |
      | Merged      | 3     |
      | Superseded  | 3     |

  Scenario: 8.2 — Time-based decay with centrality boost
    Given an expertise node with initial Confidence = 1.0 created 1095 days ago
    And the target code node has normalized_pagerank = 0.6
    When decay is computed during retrieval
    Then the decayed confidence is approximately:
      1.0 × 2^(−1095 / 1095) × (1.0 + 0.5 × 0.6) = 0.5 × 1.3 = 0.65
    And the expertise node remains active (0.65 > ArchiveConfidenceFloor 0.1)

  Scenario: 8.3 — Event-based decay on major code change
    Given an expertise node with Confidence = 0.8
    And its APPLIES_TO target's ContentHash changes (major change, not whitespace)
    When DecayStaleExpertiseAsync runs
    Then the expertise Confidence becomes 0.8 × StaleDecayFactor (0.5) = 0.4
    And the expertise node remains active (0.4 > ArchiveConfidenceFloor 0.1)

  Scenario: 8.4 — Event-based decay on minor change
    Given an expertise node with Confidence = 0.3
    And its target has a whitespace-only ContentHash change
    When DecayStaleExpertiseAsync runs
    Then the expertise Confidence becomes 0.3 × MinorDecayFactor (0.9) = 0.27
    And the expertise node remains active (0.27 > ArchiveConfidenceFloor 0.1)

  Scenario: 8.5 — Expertise below ArchiveConfidenceFloor is soft-deleted
    Given an expertise node with Confidence = 0.08 (below ArchiveConfidenceFloor 0.1)
    When DecayStaleExpertiseAsync runs
    Then the expertise node is soft-deleted (VersionRange.Until set to current commit)
    And it no longer appears in default ExpertiseRecall results
    But GetNodeAtCommitAsync with a prior commit still returns it

  Scenario: 8.6 — Community summarization produces Topic nodes
    When ConsolidateAsync runs community detection (Louvain) on the structural layer
    Then SemanticCluster nodes are created for each detected community
    And Topic nodes are created with LLM-generated summary_prose
    And BELONGS_TO_CLUSTER edges link member nodes to their cluster:
      | EdgeType       | SemanticRelation      | Metadata.MemoryLayer |
      | MemoryRelation | BELONGS_TO_CLUSTER    | semantic             |
```

---

## Feature 9: Cross-Session Learning — Memory Compounds Over Time

Verifies the end-to-end lifecycle where expertise accumulates across
agent sessions, decays with code changes, and surfaces in future retrievals
(per `architecture.md` §6.5).

```gherkin
Feature: Cross-Session Learning — Memory Compounds Over Time

  Scenario: 9.1 — Full lifecycle: onboard → learn → sync → recall
    # Session 1: Onboard and discover
    Given a fresh repository with the src/ast/ subtree
    When IncrementalSync onboards the repo at commit_index 1
    Then the graph has Structural and Call-Chain layers populated
    And 0 expertise nodes exist

    # Session 2: Agent learns a pattern
    When agent "copilot-session-A" completes a task in Ast.Core
    And records a Pattern: "Composition wrappers for backward compatibility"
      applying to "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"
    Then 1 expertise node exists with Confidence = 1.0

    # Session 3: Another agent retrieves and learns more
    When agent "copilot-session-B" queries for context about HypergraphNode
    Then the Hybrid retrieval includes the Pattern in "## Known Expertise"
    When "copilot-session-B" records an Insight: "GetEnhancedEmbeddingText includes source code up to 2000 chars"
      applying to "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEnhancedEmbeddingText"
    Then 2 expertise nodes exist

    # Code change triggers decay
    When the developer refactors HypergraphNode in commit "fff6666" (commit_index 10)
    And incremental sync processes the commit
    Then the Pattern's Confidence is decayed by StaleDecayFactor (0.5) to 0.5
    And the Insight is decayed similarly because its target changed

    # Session 4: Next agent sees decayed but still-active expertise
    When agent "copilot-session-C" queries for HypergraphNode context
    Then both expertise entries appear with their decayed Confidence values
    And the Context Assembler injects "⚠ stale — target code changed" marker

  Scenario: 9.2 — Cross-branch expertise visibility
    Given agent "session-feature" records a Pitfall on branch "feature/refactor"
      applying to "/src/ast/Ast.Core/RepositoryProcessor.cs::RepositoryProcessor.Process"
    When agent "session-main" queries on branch "main"
    Then the Pitfall is visible (expertise is globally visible per tech-spec D3)
    And if the target's ContentHash differs between branches,
      the Context Assembler marks the expertise as "⚠ stale — target code changed"

  Scenario: 9.3 — 10 sessions produce >= 5 retrievable expertise nodes
    Given 10 agent sessions each record at least 1 expertise entry
      across different code targets in src/ast/
    When the Evolution Engine has run consolidation after session 10
    Then at least 5 expertise nodes with Confidence > 0.7 exist
    And ExpertiseRecall retrieval surfaces them ranked by Confidence
    # Success criterion from tech-spec §10: "After 10 agent sessions, ≥ 5 expertise nodes with confidence > 0.7"
```

---

## Feature 10: MCP Tool Interface — External Agent Integration

Agents interact with the memory graph via 5 MCP tools: `memory_query`,
`memory_call_chain`, `memory_find_similar`, `memory_annotate`, `memory_status`
(per `architecture.md` §5.6).

```gherkin
Feature: MCP Tool Interface — External Agent Integration

  Background:
    Given the memory graph is populated for "src/ast/"
    And the MCP server is running with 5 registered tools

  Scenario: 10.1 — memory_query returns token-budgeted context
    When an MCP client sends:
      """json
      {
        "name": "memory_query",
        "arguments": {
          "query": "How does hypergraph extraction work?",
          "scope": "src/ast/Ast.Csharp",
          "tokenBudget": 4000,
          "includeExpertise": true
        }
      }
      """
    Then the MCP response has content[0].type = "text"
    And content[0].text contains "## Structure" and "## Call Graph"
    And metadata.tokenCount <= 4000
    And metadata.resolvedMode = "hybrid"

  Scenario: 10.2 — memory_call_chain traces callers/callees
    When an MCP client sends:
      """json
      {
        "name": "memory_call_chain",
        "arguments": {
          "symbol": "/src/ast/Ast.Embedding/CodeEmbeddingService.cs::CodeEmbeddingService.GenerateNodeEmbeddingsAsync",
          "direction": "outbound",
          "depth": 2
        }
      }
      """
    Then the response lists callees at depth 1 and depth 2
    And each callee entry includes its node ID and qualified name

  Scenario: 10.3 — memory_find_similar returns ranked similar nodes
    When an MCP client sends:
      """json
      {
        "name": "memory_find_similar",
        "arguments": {
          "query": "parse C# abstract syntax tree",
          "topK": 5,
          "kinds": ["Function", "Class"]
        }
      }
      """
    Then the response contains at most 5 entries
    And each entry has a similarity score between 0.0 and 1.0

  Scenario: 10.4 — memory_annotate writes expertise and returns ID
    When an MCP client sends:
      """json
      {
        "name": "memory_annotate",
        "arguments": {
          "kind": "pitfall",
          "title": "GetEnhancedEmbeddingText may include secrets in SourceCode metadata",
          "body": "When LSP enrichment is enabled, up to 2000 chars of source code appear in embedding input. This may leak secrets if not filtered.",
          "appliesTo": ["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEnhancedEmbeddingText"],
          "scope": "exact"
        }
      }
      """
    Then the response metadata.expertiseId matches pattern "expertise:pitfall:<hash8>"
    And the expertise node is retrievable via ExpertiseRecall

  Scenario: 10.5 — memory_status returns graph statistics
    When an MCP client sends:
      """json
      {
        "name": "memory_status",
        "arguments": {
          "repoRoot": "src/ast/",
          "syncToHead": false
        }
      }
      """
    Then the response includes node count, edge count, expertise count,
      last sync commit SHA, and commit_index

  Scenario: 10.6 — memory_query with tokenBudget exceeding ceiling
    When an MCP client sends memory_query with tokenBudget = 50000
    Then the effective budget is clamped to 32000
    And metadata.tokenCount <= 32000

  Scenario: 10.7 — memory_annotate with missing required field
    When an MCP client sends memory_annotate without "kind" field
    Then the MCP response is an error with a validation message
    And no expertise node is created
```

---

## Feature 11: Backward Compatibility — Existing Tests Unbroken

The memory layer extends `Ast.Core` via composition wrappers and `Metadata`
dictionaries. No changes to `HypergraphNode` or `Hyperedge` class signatures.
One additive `HyperedgeType.MemoryRelation` enum value. Existing tests must
pass unmodified (per `tech-spec.md` HC8, success criterion in §10).

```gherkin
Feature: Backward Compatibility — Existing Tests Unbroken

  Scenario: 11.1 — All existing Ast.Core tests pass after adding MemoryRelation
    Given the HyperedgeType enum has a new value MemoryRelation appended
    When dotnet test runs on Ast.Core.UnitTests
    Then all tests pass with 0 failures
    And no test references or depends on specific HyperedgeType enum count

  Scenario: 11.2 — MemoryNode composition wrapper does not alter HypergraphNode
    Given MemoryNode wraps HypergraphNode via an Inner property (composition)
    When existing code creates a HypergraphNode with standard properties
    Then the HypergraphNode behaves identically with or without Ast.Memory referenced
    And MemoryNode.Inner returns the original HypergraphNode unmodified
    And MemoryNode provides typed accessors for Metadata["MemoryLayer"],
      Metadata["VersionRange"], and Metadata["ContentHash"]

  Scenario: 11.3 — Existing IGraphStore implementations accept memory edges
    Given Neo4jGraphStore implements IGraphStore.CreateEdgeAsync
    When a Hyperedge with EdgeType = MemoryRelation is passed to CreateEdgeAsync
    Then the edge is stored successfully (MemoryRelation is a valid HyperedgeType)
    And existing IGraphStore methods work unchanged for non-memory edges

  Scenario: 11.4 — Full test suite regression check
    When dotnet test runs on all existing test projects:
      | project                          |
      | Ast.Core.UnitTests               |
      | Ast.Csharp.UnitTests             |
      | Ast.PowerShell.UnitTests         |
      | Ast.Embedding.UnitTests          |
      | Ast.GraphDb.UnitTests            |
      | Ast.Query.UnitTests              |
      | Ast.VectorDb.UnitTests           |
      | Ast.Lsp.UnitTests                |
      | CodeIntelligence.UnitTests       |
    Then all tests pass with 0 failures
    And the total test count matches or exceeds the pre-memory-layer baseline
```

---

## Feature 12: Performance & Storage — Non-Functional Requirements

Validates latency SLOs, storage budgets, and concurrency behavior
(per `tech-spec.md` HC1–HC5 and success criteria §10).

```gherkin
Feature: Performance and Storage — Non-Functional Requirements

  Scenario: 12.1 — Context construction latency p95 < 500ms
    Given a graph with 50,000 nodes (SQLite backend, single machine)
    When 100 Hybrid retrieval queries are executed
    Then the p95 latency is below 500 milliseconds
    And no retrieval query makes external network calls (per tech-spec HC5)

  Scenario: 12.2 — Incremental sync latency < 2s for single file
    Given a repository with 500K LOC
    When a single file is changed and sync runs
    Then the sync completes in under 2 seconds (per tech-spec HC2)

  Scenario: 12.3 — Active graph storage < 500 MB for 100K files
    Given a repository with 100,000 source files fully extracted
    And 30 days of simulated annotation workload
    Then the active graph (excluding cold archive) fits in < 500 MB

  Scenario: 12.4 — SQLite WAL mode handles concurrent reads
    Given 2 agents reading from the same SQLite graph simultaneously
    When both execute RetrieveAsync at the same time
    Then both queries succeed without deadlock or corruption
    And writes are serialized via in-process mutex (not optimistic concurrency)

  Scenario: 12.5 — Retrieval uses pre-computed embeddings only
    When a retrieval query executes
    Then no calls to IEmbeddingGenerator occur for code-node embeddings during retrieval
    # Embeddings are pre-computed at extraction and write-back time (tech-spec HC5)
    # The query text itself is embedded via one local Ollama call to compare against stored vectors

  Scenario: 12.6 — Audit trail completeness
    Given a sequence of operations: onboard, sync, record expertise, consolidate
    When the audit_log table is queried
    Then all 4 operations appear in chronological order
    And each entry has session_id, timestamp, operation type, and detail JSON
    # Per tech-spec HC7
```

---

## Feature 13: Graph Schema Integrity — Edge and Metadata Validation

Validates that stored graph data conforms to the canonical schema:
correct `SemanticRelation` values, proper hyperedge shape, and
curated metadata keys (per `architecture.md` §2.2, §4.5, §4.6).

```gherkin
Feature: Graph Schema Integrity — Edge and Metadata Validation

  Scenario: 13.1 — All edges carry SemanticRelation metadata
    Given the graph has been populated for "src/ast/"
    When all edges are enumerated
    Then every edge has Metadata["SemanticRelation"] set to one of:
      CONTAINS, DEPENDS_ON, CALLS, EXTENDS, IMPLEMENTS, REFERENCES,
      SIMILAR_TO, BELONGS_TO_CLUSTER, APPLIES_TO, SUPERSEDES, DERIVED_FROM

  Scenario: 13.2 — Hyperedge shape: binary relationships have exactly 2 NodeIds
    Given a CALLS edge between two function nodes
    Then the edge has exactly 2 entries in NodeIds
    And Roles maps each NodeId to its semantic role ("caller", "callee")
    # Per architecture.md §4.6: hyperedge with 2 NodeIds for binary relationships

  Scenario: 13.3 — Memory-only edges use HyperedgeType.MemoryRelation
    Given a SIMILAR_TO edge in the semantic layer
    Then the edge's EdgeType = MemoryRelation
    And Metadata["SemanticRelation"] = "SIMILAR_TO"
    And Metadata["MemoryLayer"] = "semantic"

  Scenario: 13.4 — Code-derived edges preserve original HyperedgeType
    Given a CALLS edge derived from a MethodInvocation
    Then the edge's EdgeType = MethodInvocation (not MemoryRelation)
    And Metadata["SemanticRelation"] = "CALLS"
    And Metadata["OriginalEdgeType"] = "MethodInvocation"

  Scenario: 13.5 — AnnotationKeyRegistry validates well-known keys
    Given a node with Metadata keys:
      | key          | valid |
      | MemoryLayer  | yes   |
      | ContentHash  | yes   |
      | ext:custom   | yes (with warning) |
      | badkey       | no    |
    When the node is validated against AnnotationKeyRegistry
    Then "badkey" raises a validation error
    And "ext:custom" is stored but surfaced with a warning
    And "MemoryLayer" and "ContentHash" pass validation

  Scenario: 13.6 — Node ID format follows canonical convention
    Given nodes extracted from "src/ast/Ast.Core/HypergraphModels.cs"
    Then the Module node ID is "/src/ast/Ast.Core/HypergraphModels.cs"
    And the Class node ID is "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"
    And the Function node ID is "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText"
    And the repo root node ID is "/"
    # Per architecture.md §4.4
```