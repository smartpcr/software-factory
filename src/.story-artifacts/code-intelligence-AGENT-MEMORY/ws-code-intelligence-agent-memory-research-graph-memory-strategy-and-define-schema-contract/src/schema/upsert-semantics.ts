/**
 * Upsert and delete semantics for the agent memory knowledge graph.
 *
 * Defines the merge rules that the ingestion pipeline applies when
 * an extractor emits nodes/edges that may already exist in the store.
 *
 * ## Key Principles
 *
 * 1. **Extraction-scoped operations**: Deletions are only valid within
 *    the declared scope of a completed extraction. A repo-structure
 *    extractor cannot delete nodes owned by the call-chain extractor.
 *
 * 2. **Multi-extractor evidence**: Multiple extractors can observe the
 *    same entity. Observations are accumulated, not overwritten.
 *    Confidence is derived from the highest-confidence observation.
 *
 * 3. **Immutable identity**: Canonical IDs and creation timestamps are
 *    never modified by upsert operations.
 *
 * 4. **Soft delete first**: Entities are marked as deleted (soft delete)
 *    before being physically removed after a configurable retention
 *    period. This allows recovery from incorrect deletions.
 */

import {
  type GraphNode,
  type GraphEdge,
  type Observation,
  type ExtractionResult,
  type ExtractionScope,
  Staleness,
  type NodeKind,
  type EdgeKind,
} from './types.js';

// ─── Field merge rules ───────────────────────────────────────────

/**
 * Classifies fields by their merge behavior during upsert.
 *
 * - immutable: never changes after creation (id, kind, createdAt)
 * - latest_wins: replaced by the most recent extraction (properties, sourceSpan, filePath)
 * - accumulate: new values are appended (observations)
 * - derived: computed from accumulated data (staleness, lastSeenAt, effective confidence)
 */
export const FieldMergeRule = {
  Immutable: 'immutable',
  LatestWins: 'latest_wins',
  Accumulate: 'accumulate',
  Derived: 'derived',
} as const;

export type FieldMergeRule = (typeof FieldMergeRule)[keyof typeof FieldMergeRule];

/**
 * Documents which merge rule applies to each node field.
 */
export const NODE_FIELD_MERGE_RULES: Record<string, FieldMergeRule> = {
  id: FieldMergeRule.Immutable,
  kind: FieldMergeRule.Immutable,
  createdAt: FieldMergeRule.Immutable,
  properties: FieldMergeRule.LatestWins,
  sourceSpan: FieldMergeRule.LatestWins,
  filePath: FieldMergeRule.LatestWins,
  observations: FieldMergeRule.Accumulate,
  staleness: FieldMergeRule.Derived,
  lastSeenAt: FieldMergeRule.Derived,
};

/**
 * Documents which merge rule applies to each edge field.
 */
export const EDGE_FIELD_MERGE_RULES: Record<string, FieldMergeRule> = {
  id: FieldMergeRule.Immutable,
  sourceId: FieldMergeRule.Immutable,
  targetId: FieldMergeRule.Immutable,
  kind: FieldMergeRule.Immutable,
  createdAt: FieldMergeRule.Immutable,
  sourceSpan: FieldMergeRule.Immutable, // part of edge identity hash
  properties: FieldMergeRule.LatestWins,
  observations: FieldMergeRule.Accumulate,
  staleness: FieldMergeRule.Derived,
  lastSeenAt: FieldMergeRule.Derived,
};

// ─── Observation merging ─────────────────────────────────────────

/**
 * Merge a new observation into an existing observation list.
 *
 * If an observation from the same extractor+version already exists,
 * it is replaced (the extractor re-ran). Otherwise, the new
 * observation is appended.
 */
export function mergeObservations(
  existing: readonly Observation[],
  incoming: Observation,
): Observation[] {
  const merged = existing.filter(
    (obs) =>
      !(
        obs.extractorName === incoming.extractorName &&
        obs.extractorVersion === incoming.extractorVersion
      ),
  );
  merged.push(incoming);
  return merged;
}

/**
 * Merge all observations from an incoming entity into an existing
 * observation list.
 */
export function mergeAllObservations(
  existing: readonly Observation[],
  incoming: readonly Observation[],
): Observation[] {
  let result = [...existing];
  for (const obs of incoming) {
    result = mergeObservations(result, obs);
  }
  return result;
}

/**
 * Derive the effective confidence from accumulated observations.
 * Uses the maximum confidence across all observations.
 */
export function deriveConfidence(observations: readonly Observation[]): number {
  if (observations.length === 0) return 0;
  return Math.max(...observations.map((o) => o.confidence));
}

/**
 * Derive the latest timestamp from accumulated observations.
 */
export function deriveLastSeenAt(observations: readonly Observation[]): string {
  if (observations.length === 0) return new Date().toISOString();
  return observations.reduce((latest, obs) =>
    obs.timestamp > latest ? obs.timestamp : latest,
    observations[0]!.timestamp,
  );
}

// ─── Upsert conflict detection ───────────────────────────────────

/**
 * Error thrown when an upsert detects mismatched identity fields
 * between existing and incoming entities. This indicates a
 * programming error — the caller matched the wrong entities.
 */
export class UpsertConflictError extends Error {
  constructor(
    public readonly field: string,
    public readonly existingValue: string,
    public readonly incomingValue: string,
  ) {
    super(
      `Upsert conflict on ${field}: existing="${existingValue}" vs incoming="${incomingValue}"`,
    );
    this.name = 'UpsertConflictError';
  }
}

// ─── Node upsert ─────────────────────────────────────────────────

/**
 * Apply upsert semantics to merge an incoming node with an existing node.
 *
 * Rules:
 * - id, kind, createdAt are immutable (taken from existing)
 * - properties, sourceSpan, filePath are replaced by incoming (latest wins)
 * - observations are accumulated (same extractor+version replaces)
 * - staleness is set to 'current' on upsert
 * - lastSeenAt is derived from observations
 *
 * If `existing` is null, the incoming node is returned as-is.
 *
 * @throws {UpsertConflictError} when identity fields (id, kind) mismatch
 */
export function upsertNode(
  existing: GraphNode | null,
  incoming: GraphNode,
): GraphNode {
  if (existing === null) {
    return {
      ...incoming,
      staleness: Staleness.Current,
    };
  }

  // Guard: identity fields must match
  if (existing.id !== incoming.id) {
    throw new UpsertConflictError('id', existing.id, incoming.id);
  }
  if (existing.kind !== incoming.kind) {
    throw new UpsertConflictError('kind', existing.kind, incoming.kind);
  }

  const observations = mergeAllObservations(existing.observations, incoming.observations);

  return {
    id: existing.id,
    kind: existing.kind,
    properties: incoming.properties,
    sourceSpan: incoming.sourceSpan,
    filePath: incoming.filePath,
    staleness: Staleness.Current,
    observations,
    createdAt: existing.createdAt,
    lastSeenAt: deriveLastSeenAt(observations),
  };
}

// ─── Edge upsert ─────────────────────────────────────────────────

/**
 * Apply upsert semantics to merge an incoming edge with an existing edge.
 *
 * Same rules as node upsert: immutable identity, latest-wins properties,
 * accumulated observations. Edge `sourceSpan` is immutable because it
 * participates in edge identity (included in the edge ID hash).
 *
 * @throws {UpsertConflictError} when identity fields mismatch
 */
export function upsertEdge(
  existing: GraphEdge | null,
  incoming: GraphEdge,
): GraphEdge {
  if (existing === null) {
    return {
      ...incoming,
      staleness: Staleness.Current,
    };
  }

  // Guard: identity fields must match
  if (existing.id !== incoming.id) {
    throw new UpsertConflictError('id', existing.id, incoming.id);
  }
  if (existing.sourceId !== incoming.sourceId) {
    throw new UpsertConflictError('sourceId', existing.sourceId, incoming.sourceId);
  }
  if (existing.targetId !== incoming.targetId) {
    throw new UpsertConflictError('targetId', existing.targetId, incoming.targetId);
  }
  if (existing.kind !== incoming.kind) {
    throw new UpsertConflictError('kind', existing.kind, incoming.kind);
  }

  const observations = mergeAllObservations(existing.observations, incoming.observations);

  return {
    id: existing.id,
    sourceId: existing.sourceId,
    targetId: existing.targetId,
    kind: existing.kind,
    properties: incoming.properties,
    sourceSpan: existing.sourceSpan, // immutable: part of edge identity
    staleness: Staleness.Current,
    observations,
    createdAt: existing.createdAt,
    lastSeenAt: deriveLastSeenAt(observations),
  };
}

// ─── Scope-based staleness ───────────────────────────────────────

/**
 * Determine whether a node falls within an extraction scope.
 * A node is "in scope" if its kind is owned by the extractor AND its
 * file path matches the scope's included paths (and not excluded paths).
 */
export function isNodeInScope(
  nodeKind: NodeKind,
  nodeFilePath: string | undefined,
  scope: ExtractionScope,
): boolean {
  // Must be an owned kind
  if (!scope.ownedNodeKinds.includes(nodeKind)) return false;

  // If node has no file path, it's in scope if its kind is owned
  if (!nodeFilePath) return true;

  // Check included paths (simple prefix/glob matching)
  const included = scope.includedPaths.some((pattern) =>
    matchesGlobPattern(nodeFilePath, pattern),
  );
  if (!included) return false;

  // Check excluded paths
  if (scope.excludedPaths) {
    const excluded = scope.excludedPaths.some((pattern) =>
      matchesGlobPattern(nodeFilePath, pattern),
    );
    if (excluded) return false;
  }

  return true;
}

/**
 * Determine whether an edge falls within an extraction scope.
 *
 * An edge is "in scope" if:
 * 1. Its kind is owned by the extractor
 * 2. At least one observation from the current extractor has a sourceFile
 *    that matches the scope's included paths (and not excluded paths)
 *
 * This prevents one extractor from marking stale edges observed by another
 * extractor, and ensures path-awareness for scope-bounded staleness.
 */
export function isEdgeInScope(
  edge: GraphEdge,
  scope: ExtractionScope,
): boolean {
  if (!scope.ownedEdgeKinds.includes(edge.kind)) return false;

  // Find observations from the current extractor
  const extractorObs = edge.observations.filter(
    (obs) => obs.extractorName === scope.extractorName,
  );

  // If this extractor never observed this edge, it's not in scope
  if (extractorObs.length === 0) return false;

  // Check that at least one observation's sourceFile falls in scope paths
  return extractorObs.some((obs) => {
    const included = scope.includedPaths.some((pattern) =>
      matchesGlobPattern(obs.sourceFile, pattern),
    );
    if (!included) return false;

    if (scope.excludedPaths) {
      const excluded = scope.excludedPaths.some((pattern) =>
        matchesGlobPattern(obs.sourceFile, pattern),
      );
      if (excluded) return false;
    }

    return true;
  });
}

/**
 * Simplified glob pattern matching.
 * Supports `*` (any characters within a segment), `**` (any path depth),
 * and `?` (single non-slash character).
 */
export function matchesGlobPattern(path: string, pattern: string): boolean {
  const normalizedPath = path.replace(/\\/g, '/');
  const normalizedPattern = pattern.replace(/\\/g, '/');

  // Replace glob tokens with unique placeholders, then escape regex
  // specials, then expand placeholders to their regex equivalents.
  // This avoids glob-to-regex artifacts (e.g., the `?` quantifier in
  // `(.*/)?` being consumed by the glob `?` handler).
  const regexStr = normalizedPattern
    .replace(/\*\*\//g, '\0DS\0')             // **/ (dir-star)
    .replace(/\*\*/g, '\0GS\0')              // standalone **
    .replace(/\*/g, '\0S\0')                  // single *
    .replace(/\?/g, '\0Q\0')                  // single ?
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')    // escape regex specials
    .replace(/\0DS\0/g, '(.*/)?')             // **/ → optional dir prefix
    .replace(/\0GS\0/g, '.*')                 // ** → match everything
    .replace(/\0S\0/g, '[^/]*')               // * → within one segment
    .replace(/\0Q\0/g, '[^/]');               // ? → single non-slash char

  const regex = new RegExp(`^${regexStr}$`);
  return regex.test(normalizedPath);
}

/**
 * Given a completed extraction result and a list of existing entities,
 * determine which existing entities should be marked stale.
 *
 * An entity is marked stale if ALL of the following hold:
 * 1. It falls within the extraction scope (kind + path)
 * 2. It was not observed in the current extraction result
 * 3. Its file was actually processed in this extraction run
 *    (prevents false staleness from partial/incremental runs)
 * 4. It has NO observations from extractors other than the current one
 *    (multi-extractor protection: other extractors still support the entity)
 *
 * Returns the IDs of entities that should be marked stale.
 *
 * ### Multi-extractor protection
 *
 * If a node was observed by both extractor A and extractor B, running
 * extractor A alone and not seeing the node will NOT mark it stale,
 * because extractor B's observation still supports it. Only when ALL
 * supporting extractors have run and none observed the node should it
 * be marked stale. In practice, each extractor run only stales entities
 * that it alone supports.
 *
 * **Tradeoff:** If an extractor is decommissioned, its old observations
 * can pin entities as "supported" indefinitely. Pipelines should
 * periodically purge observations from retired extractors.
 */
export function computeStaleNodeIds(
  existingNodes: readonly GraphNode[],
  result: ExtractionResult,
): string[] {
  const observedIds = new Set(result.nodes.map((n) => n.id));
  const processedFilesSet = new Set(result.processedFiles);

  return existingNodes
    .filter((node) => {
      // 1. Must be within the extraction scope
      if (!isNodeInScope(node.kind, node.filePath, result.scope)) return false;

      // 2. Must not have been observed in this run
      if (observedIds.has(node.id)) return false;

      // 3. For file-backed nodes, only stale if their file was actually processed
      if (node.filePath && !processedFilesSet.has(node.filePath)) return false;

      // 4. Multi-extractor protection: skip if other extractors still support it
      const hasOtherExtractorObs = node.observations.some(
        (obs) => obs.extractorName !== result.scope.extractorName,
      );
      if (hasOtherExtractorObs) return false;

      return true;
    })
    .map((n) => n.id);
}

/**
 * Compute which edges should be marked stale after an extraction.
 *
 * Same rules as node staleness:
 * 1. Edge is in scope (kind + source file path from observations)
 * 2. Edge was not re-observed in this extraction
 * 3. Edge's source files were actually processed
 * 4. No observations from other extractors still support the edge
 */
export function computeStaleEdgeIds(
  existingEdges: readonly GraphEdge[],
  result: ExtractionResult,
): string[] {
  const observedIds = new Set(result.edges.map((e) => e.id));
  const processedFilesSet = new Set(result.processedFiles);

  return existingEdges
    .filter((edge) => {
      // 1. Must be in scope (kind + path-aware via observations)
      if (!isEdgeInScope(edge, result.scope)) return false;

      // 2. Must not have been observed in this run
      if (observedIds.has(edge.id)) return false;

      // 3. Only stale if the edge's source files were actually processed
      const currentExtractorObs = edge.observations.filter(
        (obs) => obs.extractorName === result.scope.extractorName,
      );
      const edgeInProcessedFile = currentExtractorObs.some(
        (obs) => processedFilesSet.has(obs.sourceFile),
      );
      if (!edgeInProcessedFile) return false;

      // 4. Multi-extractor protection
      const hasOtherExtractorObs = edge.observations.some(
        (obs) => obs.extractorName !== result.scope.extractorName,
      );
      if (hasOtherExtractorObs) return false;

      return true;
    })
    .map((e) => e.id);
}

/**
 * Apply soft-delete to a node. Sets staleness to 'deleted' while
 * preserving all other data for potential recovery.
 */
export function softDeleteNode(node: GraphNode): GraphNode {
  return {
    ...node,
    staleness: Staleness.Deleted,
    lastSeenAt: new Date().toISOString(),
  };
}

/**
 * Apply soft-delete to an edge.
 */
export function softDeleteEdge(edge: GraphEdge): GraphEdge {
  return {
    ...edge,
    staleness: Staleness.Deleted,
    lastSeenAt: new Date().toISOString(),
  };
}

/**
 * Mark a node as stale (not deleted, but not confirmed current).
 */
export function markNodeStale(node: GraphNode): GraphNode {
  return {
    ...node,
    staleness: Staleness.Stale,
  };
}

/**
 * Mark an edge as stale.
 */
export function markEdgeStale(edge: GraphEdge): GraphEdge {
  return {
    ...edge,
    staleness: Staleness.Stale,
  };
}
