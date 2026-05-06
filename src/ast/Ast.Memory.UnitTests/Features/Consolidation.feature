@consolidation
Feature: ConsolidationService
  LLM-powered merge of semantically similar expertise entries.
  Uses cosine similarity of embeddings to find candidates and Ollama /api/generate
  to produce merged Title+Body text. Creates SUPERSEDES and DERIVED_FROM edges.

  Scenario: Consolidation merge
    Given two expertise entries targeting the same node with cosine similarity 0.90
    When ConsolidateAsync runs
    Then a new merged entry exists with SUPERSEDES edges to both originals

  Scenario: Below-threshold skip
    Given two expertise entries with cosine similarity 0.70
    When ConsolidateAsync runs
    Then no merge occurs and both entries remain independent

  Scenario: Consolidation lock
    Given ConsolidateAsync is running
    When a second ConsolidateAsync is called concurrently
    Then the second call waits for the first to complete

  Scenario: Cross-target skip
    Given two expertise entries targeting different nodes with cosine similarity 0.95
    When ConsolidateAsync runs
    Then no merge occurs and both entries remain independent

  Scenario: LLM failure graceful skip
    Given two expertise entries targeting the same node with LLM failure configured
    When ConsolidateAsync runs
    Then no merge occurs and both entries remain independent

  Scenario: Cancellation propagation
    Given two expertise entries targeting the same node with cosine similarity 0.90
    When ConsolidateAsync is cancelled during execution
    Then the operation is cancelled with OperationCanceledException

  Scenario: Edge persistence with concrete store
    Given two expertise entries targeting the same node with cosine similarity 0.90 using concrete store
    When ConsolidateAsync runs against concrete store
    Then SUPERSEDES and DERIVED_FROM edges are persisted in the concrete graph store

  Scenario: Qdrant UUID vector ID mapping
    Given two expertise entries with Qdrant-style UUID vector IDs and cosine similarity 0.90
    When ConsolidateAsync runs with UUID vector IDs
    Then the service resolves candidates via payload-based lookup and merges successfully
