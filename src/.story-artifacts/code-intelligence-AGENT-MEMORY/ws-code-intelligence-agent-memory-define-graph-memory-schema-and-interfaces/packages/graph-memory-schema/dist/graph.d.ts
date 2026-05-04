/**
 * Top-level graph container types, snapshots, diffs, and query structures.
 *
 * Wire-format / data-model types (KnowledgeGraph, Snapshot, GraphStats,
 * GraphDiff, GraphFact) use snake_case to match the canonical JSON-Schema.
 *
 * TS-only API contracts (TraversalPlan, NodeFilter, RetrievalQuery,
 * RetrievalResult, ExpertiseEntry, ConsolidationReport) use camelCase
 * to follow TypeScript convention and match architecture.md §4 contracts.
 */
import type { GraphNode, GraphEdge, NodeKind, EdgeRelation, GraphLayer, Citation } from './types.js';
/** A complete knowledge graph: a set of nodes and edges with optional snapshot. */
export interface KnowledgeGraph {
    nodes: GraphNode[];
    edges: GraphEdge[];
    snapshot?: Snapshot;
}
/** A point-in-time bookmark for the graph, keyed by commit SHA. */
export interface Snapshot {
    id: string;
    commit_sha: string;
    parent_sha?: string;
    created_at: string;
    stats: GraphStats;
}
export interface GraphStats {
    node_count: number;
    edge_count: number;
    expertise_node_count: number;
}
/** A set of changes to apply to a graph. */
export interface GraphDiff {
    inserts: GraphFact[];
    updates: GraphFact[];
    tombstones: string[];
}
/** A single fact is either a node or an edge. */
export type GraphFact = {
    type: 'node';
    value: GraphNode;
} | {
    type: 'edge';
    value: GraphEdge;
};
export interface TraversalPlan {
    direction: 'outbound' | 'inbound' | 'both';
    edgeTypes: EdgeRelation[];
    maxDepth: number;
    filters?: NodeFilter;
    limit?: number;
}
export interface NodeFilter {
    kinds?: NodeKind[];
    layers?: GraphLayer[];
    qualifiedNamePrefix?: string;
    modifiedSince?: string;
}
export interface TraversalResult {
    nodes: GraphNode[];
    edges: GraphEdge[];
    paths: string[][];
}
/** A node returned from vector search, with its similarity score. */
export interface ScoredNode {
    node: GraphNode;
    score: number;
}
export type RetrievalMode = 'STRUCTURAL_DRILL' | 'CALL_TRACE' | 'SEMANTIC_SEARCH' | 'EXPERTISE_RECALL' | 'HYBRID';
export interface RetrievalQuery {
    text: string;
    scope?: string;
    mode?: RetrievalMode;
    tokenBudget: number;
    includeExpertise: boolean;
    atCommit?: string;
}
export interface RetrievalResult {
    contextBlock: string;
    tokenCount: number;
    citations: Citation[];
    resolvedMode: RetrievalMode;
}
export interface ExpertiseEntry {
    kind: 'pattern' | 'pitfall' | 'preference' | 'insight';
    title: string;
    body: string;
    appliesTo: string[];
    scope: 'exact' | 'subtree';
    evidence: Citation[];
    sourceAgent: string;
}
export interface ConsolidationReport {
    merged: number;
    superseded: number;
    retracted: number;
    newTopics: string[];
}
export interface WatchOptions {
    /** Debounce interval for file-save events (ms). Default: 1000. */
    debounceMs?: number;
    /** Also run Evolution Engine decay on each sync. Default: true. */
    autoDecay?: boolean;
}
export interface GraphValidationIssue {
    severity: 'error' | 'warning';
    path: string;
    message: string;
}
//# sourceMappingURL=graph.d.ts.map