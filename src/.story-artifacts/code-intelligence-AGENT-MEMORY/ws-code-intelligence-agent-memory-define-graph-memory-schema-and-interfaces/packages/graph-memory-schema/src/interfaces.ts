/**
 * Service contracts for the four engines that operate on the knowledge graph.
 *
 * These interfaces match the architecture.md §4.1–4.5 contracts exactly.
 * Method names follow TypeScript camelCase convention (the canonical API
 * surface); data-layer field names (nodes, edges, properties) use
 * snake_case to match the JSON-Schema wire format.
 *
 * Downstream workstreams implement these interfaces; this package only
 * publishes the shapes so that all producers and consumers share a single
 * type surface.
 */

import type { GraphNode, GraphEdge, NodeKind, GraphLayer } from './types.js';
import type {
  KnowledgeGraph,
  GraphDiff,
  GraphFact,
  Snapshot,
  TraversalPlan,
  TraversalResult,
  NodeFilter,
  ScoredNode,
  RetrievalQuery,
  RetrievalResult,
  ExpertiseEntry,
  ConsolidationReport,
  WatchOptions,
  GraphValidationIssue,
} from './graph.js';

// ─── GraphStore (§4.2) ──────────────────────────────────────────────────

/**
 * Persistence layer for reading and writing graph facts.
 *
 * Uses tombstone / version_range semantics: nodes and edges are never
 * hard-deleted; instead their version_range.until is set to the commit
 * SHA at which they were removed.
 *
 * Downstream implementations should target **Neo4j** as the primary
 * persistence backend.
 */
export interface GraphStore {
  /** Apply a batch of facts (idempotent via content_hash). */
  applyDiff(diff: GraphDiff, commitSha: string): Promise<Snapshot>;

  /** Traverse from a starting node with a query plan. */
  traverse(start: string, plan: TraversalPlan): Promise<TraversalResult>;

  /** ANN vector search on the Semantic layer. */
  vectorSearch(
    query: number[],
    topK: number,
    filter?: NodeFilter,
  ): Promise<ScoredNode[]>;

  /** Read a node by ID, optionally at a specific snapshot. */
  getNode(id: string, atCommit?: string): Promise<GraphNode | null>;

  /** Find nodes by qualified name prefix (autocomplete). */
  findByName(
    prefix: string,
    kinds?: NodeKind[],
    limit?: number,
  ): Promise<GraphNode[]>;

  /** Get community / cluster summary for a subgraph. */
  getClusterSummary(clusterId: string): Promise<string>;

  /** Materialise the full graph (use with care on large graphs). */
  getFullGraph(): Promise<KnowledgeGraph>;

  /** Validate the entire stored graph and return any issues found. */
  validate(): Promise<GraphValidationIssue[]>;
}

// ─── ExtractionPipeline (§4.1) ──────────────────────────────────────────

export interface ExtractionOptions {
  /** Which languages to parse. */
  languages?: string[];
  /** Enrich with type-resolved edges via LSP. */
  enableLsp?: boolean;
  /** Model ID for semantic embedding vectors. */
  embeddingModel?: string;
  /** Skip files larger than this (bytes). */
  maxFileSize?: number;
  /** Only extract these node kinds; omit for all. */
  nodeKinds?: NodeKind[];
  /** Glob patterns of files to include. */
  includeGlobs?: string[];
  /** Glob patterns of files to exclude. */
  excludeGlobs?: string[];
}

/** Parses source trees into graph facts. */
export interface ExtractionPipeline {
  /**
   * Full extraction for an entire repository.
   * Returns an async iterable of GraphFact (node or edge) objects.
   */
  extractAll(
    repoRoot: string,
    options?: ExtractionOptions,
  ): AsyncIterable<GraphFact>;

  /**
   * Incremental extraction for changed files since a base commit.
   * Returns a GraphDiff: inserts, updates, and tombstones.
   */
  extractIncremental(
    repoRoot: string,
    baseCommit: string,
    headCommit: string,
    changedFiles: string[],
    options?: ExtractionOptions,
  ): Promise<GraphDiff>;

  /** Languages this pipeline can handle. */
  supportedLanguages(): string[];
}

// ─── RetrievalEngine (§4.3) ─────────────────────────────────────────────

/** Assembles context from the knowledge graph for an agent prompt. */
export interface RetrievalEngine {
  /** Top-level retrieval: query → assembled context block. */
  retrieve(query: RetrievalQuery): Promise<RetrievalResult>;

  /** Low-level graph traversal from a specific node. */
  traverseFrom(nodeId: string, plan: TraversalPlan): Promise<TraversalResult>;
}

// ─── EvolutionEngine (§4.4) ─────────────────────────────────────────────

/** Writes agent-learned expertise back into the graph and consolidates it. */
export interface EvolutionEngine {
  /** Agent writes back a learning. Returns the new expertise node ID. */
  recordExpertise(entry: ExpertiseEntry): Promise<string>;

  /** Trigger consolidation pass over a scope (package / module qualified name prefix). */
  consolidate(scope: string): Promise<ConsolidationReport>;

  /**
   * Run decay pass: reduce confidence of expertise nodes whose
   * APPLIES_TO targets have been deleted or substantially rewritten.
   * Returns the count of decayed nodes.
   */
  decayStaleExpertise(headCommit: string): Promise<number>;
}

// ─── IncrementalSync (§4.5) ─────────────────────────────────────────────

/** Coordinates incremental graph updates when the repo changes. */
export interface IncrementalSync {
  /** Start watching a repo for changes (file-save or commit). */
  watch(repoRoot: string, options?: WatchOptions): void;

  /** Stop watching. */
  unwatch(repoRoot: string): void;

  /** Manual trigger: sync to a specific commit. */
  syncToCommit(repoRoot: string, commitSha: string): Promise<Snapshot>;
}
