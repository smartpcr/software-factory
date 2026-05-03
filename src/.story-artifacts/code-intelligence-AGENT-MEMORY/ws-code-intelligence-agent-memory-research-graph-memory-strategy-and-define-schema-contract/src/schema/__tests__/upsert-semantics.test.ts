import { describe, it, expect } from 'vitest';
import {
  mergeObservations,
  mergeAllObservations,
  deriveConfidence,
  deriveLastSeenAt,
  upsertNode,
  upsertEdge,
  isNodeInScope,
  isEdgeInScope,
  matchesGlobPattern,
  computeStaleNodeIds,
  computeStaleEdgeIds,
  softDeleteNode,
  softDeleteEdge,
  markNodeStale,
  markEdgeStale,
  FieldMergeRule,
  NODE_FIELD_MERGE_RULES,
  EDGE_FIELD_MERGE_RULES,
} from '../upsert-semantics.js';
import {
  NodeKind,
  EdgeKind,
  Staleness,
  ConfidenceTier,
  AnalysisBackend,
  SCHEMA_VERSION,
  type GraphNode,
  type GraphEdge,
  type Observation,
  type ExtractionResult,
  type ExtractionScope,
} from '../types.js';
import {
  fileNodeId,
  symbolNodeId,
  repositoryNodeId,
  buildEdgeId,
} from '../canonical-id.js';

// ─── Test helpers ────────────────────────────────────────────────

const REPO_ID = 'a1b2c3d4e5f6';
const T1 = '2026-01-15T10:00:00.000Z';
const T2 = '2026-01-15T11:00:00.000Z';
const T3 = '2026-01-15T12:00:00.000Z';

function obs(overrides?: Partial<Observation>): Observation {
  return {
    extractorName: 'repo-structure',
    extractorVersion: '1.0.0',
    analysisBackend: AnalysisBackend.TreeSitter,
    confidenceTier: ConfidenceTier.Exact,
    confidence: 1.0,
    commitSha: 'abc123',
    timestamp: T1,
    sourceFile: 'src/main.ts',
    ...overrides,
  };
}

function makeNode(overrides?: Partial<GraphNode>): GraphNode {
  return {
    id: fileNodeId(REPO_ID, 'src/main.ts'),
    kind: NodeKind.File,
    properties: { name: 'main.ts', path: 'src/main.ts' },
    staleness: Staleness.Current,
    observations: [obs()],
    createdAt: T1,
    lastSeenAt: T1,
    ...overrides,
  };
}

function makeEdge(overrides?: Partial<GraphEdge>): GraphEdge {
  const src = symbolNodeId(NodeKind.Function, REPO_ID, 'src/a.ts', ['foo']);
  const tgt = symbolNodeId(NodeKind.Function, REPO_ID, 'src/b.ts', ['bar']);
  return {
    id: buildEdgeId(src, tgt, EdgeKind.Calls),
    sourceId: src,
    targetId: tgt,
    kind: EdgeKind.Calls,
    properties: {},
    staleness: Staleness.Current,
    observations: [obs()],
    createdAt: T1,
    lastSeenAt: T1,
    ...overrides,
  };
}

// ─── Observation merging ─────────────────────────────────────────

describe('mergeObservations', () => {
  it('appends a new observation from a different extractor', () => {
    const existing = [obs()];
    const incoming = obs({ extractorName: 'call-chain', extractorVersion: '2.0.0' });
    const result = mergeObservations(existing, incoming);
    expect(result).toHaveLength(2);
    expect(result[1]!.extractorName).toBe('call-chain');
  });

  it('replaces an observation with matching extractor+version', () => {
    const existing = [obs({ confidence: 0.5 })];
    const incoming = obs({ confidence: 0.9 });
    const result = mergeObservations(existing, incoming);
    expect(result).toHaveLength(1);
    expect(result[0]!.confidence).toBe(0.9);
  });

  it('appends when same extractor but different version', () => {
    const existing = [obs({ extractorVersion: '1.0.0' })];
    const incoming = obs({ extractorVersion: '2.0.0' });
    const result = mergeObservations(existing, incoming);
    expect(result).toHaveLength(2);
  });

  it('handles empty existing list', () => {
    const result = mergeObservations([], obs());
    expect(result).toHaveLength(1);
  });
});

describe('mergeAllObservations', () => {
  it('merges multiple incoming observations', () => {
    const existing = [obs()];
    const incoming = [
      obs({ extractorName: 'call-chain', extractorVersion: '1.0.0' }),
      obs({ extractorName: 'type-resolver', extractorVersion: '1.0.0' }),
    ];
    const result = mergeAllObservations(existing, incoming);
    expect(result).toHaveLength(3);
  });

  it('replaces observations with the same extractor+version among incoming', () => {
    const existing = [obs({ confidence: 0.5 })];
    const incoming = [obs({ confidence: 0.9 })];
    const result = mergeAllObservations(existing, incoming);
    expect(result).toHaveLength(1);
    expect(result[0]!.confidence).toBe(0.9);
  });
});

// ─── Derived fields ──────────────────────────────────────────────

describe('deriveConfidence', () => {
  it('returns the maximum confidence across observations', () => {
    const observations = [
      obs({ confidence: 0.3 }),
      obs({ confidence: 0.9, extractorName: 'b', extractorVersion: '1.0.0' }),
      obs({ confidence: 0.7, extractorName: 'c', extractorVersion: '1.0.0' }),
    ];
    expect(deriveConfidence(observations)).toBe(0.9);
  });

  it('returns 0 for empty observations', () => {
    expect(deriveConfidence([])).toBe(0);
  });

  it('returns 1.0 for a single exact observation', () => {
    expect(deriveConfidence([obs({ confidence: 1.0 })])).toBe(1.0);
  });
});

describe('deriveLastSeenAt', () => {
  it('returns the latest timestamp', () => {
    const observations = [
      obs({ timestamp: T1 }),
      obs({ timestamp: T3, extractorName: 'b', extractorVersion: '1.0.0' }),
      obs({ timestamp: T2, extractorName: 'c', extractorVersion: '1.0.0' }),
    ];
    expect(deriveLastSeenAt(observations)).toBe(T3);
  });

  it('returns current time for empty list', () => {
    const result = deriveLastSeenAt([]);
    // Should be a valid ISO timestamp
    expect(() => new Date(result)).not.toThrow();
  });
});

// ─── Node upsert ─────────────────────────────────────────────────

describe('upsertNode', () => {
  it('returns incoming node as-is (with staleness current) when existing is null', () => {
    const incoming = makeNode({ staleness: Staleness.Stale });
    const result = upsertNode(null, incoming);
    expect(result.staleness).toBe(Staleness.Current);
    expect(result.id).toBe(incoming.id);
    expect(result.kind).toBe(incoming.kind);
  });

  it('preserves immutable fields from existing (id, kind, createdAt)', () => {
    const existing = makeNode({ createdAt: T1 });
    const incoming = makeNode({
      id: 'something-else', // should be ignored
      createdAt: T2,
      properties: { name: 'updated.ts', path: 'src/updated.ts' },
      observations: [obs({ timestamp: T2 })],
    });
    const result = upsertNode(existing, incoming);
    expect(result.id).toBe(existing.id);
    expect(result.kind).toBe(existing.kind);
    expect(result.createdAt).toBe(T1);
  });

  it('uses latest-wins for properties', () => {
    const existing = makeNode({ properties: { name: 'old.ts', path: 'old.ts' } });
    const incoming = makeNode({ properties: { name: 'new.ts', path: 'new.ts' } });
    const result = upsertNode(existing, incoming);
    expect((result.properties as { name: string }).name).toBe('new.ts');
  });

  it('uses latest-wins for sourceSpan', () => {
    const existing = makeNode({
      sourceSpan: { startLine: 1, startColumn: 0, endLine: 10, endColumn: 0 },
    });
    const incoming = makeNode({
      sourceSpan: { startLine: 5, startColumn: 0, endLine: 20, endColumn: 0 },
      observations: [obs({ timestamp: T2 })],
    });
    const result = upsertNode(existing, incoming);
    expect(result.sourceSpan?.startLine).toBe(5);
  });

  it('accumulates observations from different extractors', () => {
    const existing = makeNode({ observations: [obs()] });
    const incoming = makeNode({
      observations: [obs({ extractorName: 'call-chain', extractorVersion: '1.0.0', timestamp: T2 })],
    });
    const result = upsertNode(existing, incoming);
    expect(result.observations).toHaveLength(2);
  });

  it('sets staleness to current on upsert', () => {
    const existing = makeNode({ staleness: Staleness.Stale });
    const incoming = makeNode({ observations: [obs({ timestamp: T2 })] });
    const result = upsertNode(existing, incoming);
    expect(result.staleness).toBe(Staleness.Current);
  });

  it('derives lastSeenAt from observations', () => {
    const existing = makeNode({ observations: [obs({ timestamp: T1 })] });
    const incoming = makeNode({
      observations: [obs({ extractorName: 'b', extractorVersion: '1.0.0', timestamp: T3 })],
    });
    const result = upsertNode(existing, incoming);
    expect(result.lastSeenAt).toBe(T3);
  });
});

// ─── Edge upsert ─────────────────────────────────────────────────

describe('upsertEdge', () => {
  it('returns incoming edge with staleness current when existing is null', () => {
    const incoming = makeEdge({ staleness: Staleness.Stale });
    const result = upsertEdge(null, incoming);
    expect(result.staleness).toBe(Staleness.Current);
  });

  it('preserves immutable fields (id, sourceId, targetId, kind, createdAt)', () => {
    const existing = makeEdge({ createdAt: T1 });
    const incoming = makeEdge({
      createdAt: T2,
      observations: [obs({ timestamp: T2 })],
    });
    const result = upsertEdge(existing, incoming);
    expect(result.id).toBe(existing.id);
    expect(result.sourceId).toBe(existing.sourceId);
    expect(result.targetId).toBe(existing.targetId);
    expect(result.kind).toBe(existing.kind);
    expect(result.createdAt).toBe(T1);
  });

  it('uses latest-wins for properties', () => {
    const existing = makeEdge({ properties: { isDynamic: false } });
    const incoming = makeEdge({
      properties: { isDynamic: true },
      observations: [obs({ timestamp: T2 })],
    });
    const result = upsertEdge(existing, incoming);
    expect((result.properties as { isDynamic: boolean }).isDynamic).toBe(true);
  });

  it('accumulates observations', () => {
    const existing = makeEdge({ observations: [obs()] });
    const incoming = makeEdge({
      observations: [obs({ extractorName: 'lsp-extractor', extractorVersion: '1.0.0', timestamp: T2 })],
    });
    const result = upsertEdge(existing, incoming);
    expect(result.observations).toHaveLength(2);
  });
});

// ─── Merge rule declarations ─────────────────────────────────────

describe('field merge rules', () => {
  it('node immutable fields are declared', () => {
    expect(NODE_FIELD_MERGE_RULES.id).toBe(FieldMergeRule.Immutable);
    expect(NODE_FIELD_MERGE_RULES.kind).toBe(FieldMergeRule.Immutable);
    expect(NODE_FIELD_MERGE_RULES.createdAt).toBe(FieldMergeRule.Immutable);
  });

  it('node latest-wins fields are declared', () => {
    expect(NODE_FIELD_MERGE_RULES.properties).toBe(FieldMergeRule.LatestWins);
    expect(NODE_FIELD_MERGE_RULES.sourceSpan).toBe(FieldMergeRule.LatestWins);
  });

  it('node accumulate fields are declared', () => {
    expect(NODE_FIELD_MERGE_RULES.observations).toBe(FieldMergeRule.Accumulate);
  });

  it('node derived fields are declared', () => {
    expect(NODE_FIELD_MERGE_RULES.staleness).toBe(FieldMergeRule.Derived);
    expect(NODE_FIELD_MERGE_RULES.lastSeenAt).toBe(FieldMergeRule.Derived);
  });

  it('edge merge rules mirror node rules for shared fields', () => {
    expect(EDGE_FIELD_MERGE_RULES.id).toBe(FieldMergeRule.Immutable);
    expect(EDGE_FIELD_MERGE_RULES.properties).toBe(FieldMergeRule.LatestWins);
    expect(EDGE_FIELD_MERGE_RULES.observations).toBe(FieldMergeRule.Accumulate);
  });
});

// ─── Scope matching ──────────────────────────────────────────────

describe('matchesGlobPattern', () => {
  it('matches exact path', () => {
    expect(matchesGlobPattern('src/main.ts', 'src/main.ts')).toBe(true);
  });

  it('matches wildcard within segment', () => {
    expect(matchesGlobPattern('src/main.ts', 'src/*.ts')).toBe(true);
    expect(matchesGlobPattern('src/main.js', 'src/*.ts')).toBe(false);
  });

  it('matches ** across segments', () => {
    expect(matchesGlobPattern('src/deep/nested/file.ts', '**/*.ts')).toBe(true);
    expect(matchesGlobPattern('file.ts', '**/*.ts')).toBe(true);
  });

  it('matches ? for single character', () => {
    expect(matchesGlobPattern('src/a.ts', 'src/?.ts')).toBe(true);
    expect(matchesGlobPattern('src/ab.ts', 'src/?.ts')).toBe(false);
  });

  it('normalizes backslashes', () => {
    expect(matchesGlobPattern('src\\auth\\main.ts', 'src/**/*.ts')).toBe(true);
  });

  it('does not match unrelated path', () => {
    expect(matchesGlobPattern('lib/other.ts', 'src/**/*.ts')).toBe(false);
  });
});

describe('isNodeInScope', () => {
  const scope: ExtractionScope = {
    extractorName: 'repo-structure',
    includedPaths: ['src/**/*.ts'],
    excludedPaths: ['src/**/*.test.ts'],
    ownedNodeKinds: [NodeKind.File, NodeKind.Directory],
    ownedEdgeKinds: [EdgeKind.Contains],
  };

  it('returns true for owned kind matching included path', () => {
    expect(isNodeInScope(NodeKind.File, 'src/main.ts', scope)).toBe(true);
  });

  it('returns false for non-owned kind', () => {
    expect(isNodeInScope(NodeKind.Function, 'src/main.ts', scope)).toBe(false);
  });

  it('returns false for path not matching included patterns', () => {
    expect(isNodeInScope(NodeKind.File, 'lib/other.ts', scope)).toBe(false);
  });

  it('returns false for path matching excluded pattern', () => {
    expect(isNodeInScope(NodeKind.File, 'src/main.test.ts', scope)).toBe(false);
  });

  it('returns true for node with no file path if kind is owned', () => {
    expect(isNodeInScope(NodeKind.Directory, undefined, scope)).toBe(true);
  });
});

describe('isEdgeInScope', () => {
  const scope: ExtractionScope = {
    extractorName: 'repo-structure',
    includedPaths: ['**/*'],
    ownedNodeKinds: [NodeKind.File],
    ownedEdgeKinds: [EdgeKind.Contains],
  };

  it('returns true for owned edge kind', () => {
    expect(isEdgeInScope(EdgeKind.Contains, scope)).toBe(true);
  });

  it('returns false for non-owned edge kind', () => {
    expect(isEdgeInScope(EdgeKind.Calls, scope)).toBe(false);
  });
});

// ─── Staleness computation ───────────────────────────────────────

describe('computeStaleNodeIds', () => {
  const scope: ExtractionScope = {
    extractorName: 'repo-structure',
    includedPaths: ['src/**/*.ts'],
    ownedNodeKinds: [NodeKind.File],
    ownedEdgeKinds: [EdgeKind.Contains],
  };

  it('returns IDs of nodes in scope that were not observed', () => {
    const existing = [
      makeNode({ id: fileNodeId(REPO_ID, 'src/a.ts'), filePath: 'src/a.ts' }),
      makeNode({ id: fileNodeId(REPO_ID, 'src/b.ts'), filePath: 'src/b.ts' }),
    ];
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-1',
      scope,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [existing[0]!], // only a.ts observed
      edges: [],
      processedFiles: ['src/a.ts'],
    };
    const staleIds = computeStaleNodeIds(existing, result);
    expect(staleIds).toEqual([fileNodeId(REPO_ID, 'src/b.ts')]);
  });

  it('does not return nodes outside the extraction scope', () => {
    const existing = [
      makeNode({
        id: symbolNodeId(NodeKind.Function, REPO_ID, 'src/a.ts', ['fn']),
        kind: NodeKind.Function,
        filePath: 'src/a.ts',
      }),
    ];
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-1',
      scope,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [],
      edges: [],
      processedFiles: ['src/a.ts'],
    };
    // Function kind is not owned by this scope
    expect(computeStaleNodeIds(existing, result)).toHaveLength(0);
  });

  it('returns empty array when all nodes are observed', () => {
    const node = makeNode({
      id: fileNodeId(REPO_ID, 'src/a.ts'),
      filePath: 'src/a.ts',
    });
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-1',
      scope,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [node],
      edges: [],
      processedFiles: ['src/a.ts'],
    };
    expect(computeStaleNodeIds([node], result)).toHaveLength(0);
  });
});

describe('computeStaleEdgeIds', () => {
  const scope: ExtractionScope = {
    extractorName: 'repo-structure',
    includedPaths: ['**/*'],
    ownedNodeKinds: [NodeKind.File],
    ownedEdgeKinds: [EdgeKind.Contains],
  };

  it('returns IDs of edges in scope that were not observed', () => {
    const existing = [
      makeEdge({ kind: EdgeKind.Contains }),
    ];
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-1',
      scope,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [],
      edges: [], // no edges observed
      processedFiles: [],
    };
    expect(computeStaleEdgeIds(existing, result)).toEqual([existing[0]!.id]);
  });

  it('does not return edges outside scope', () => {
    const existing = [makeEdge({ kind: EdgeKind.Calls })]; // Calls is not owned
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-1',
      scope,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [],
      edges: [],
      processedFiles: [],
    };
    expect(computeStaleEdgeIds(existing, result)).toHaveLength(0);
  });
});

// ─── Soft delete & mark stale ────────────────────────────────────

describe('softDeleteNode', () => {
  it('sets staleness to deleted and updates lastSeenAt', () => {
    const node = makeNode();
    const deleted = softDeleteNode(node);
    expect(deleted.staleness).toBe(Staleness.Deleted);
    expect(deleted.id).toBe(node.id);
    expect(deleted.kind).toBe(node.kind);
    // lastSeenAt should be a recent ISO timestamp
    expect(() => new Date(deleted.lastSeenAt)).not.toThrow();
  });

  it('preserves all other fields', () => {
    const node = makeNode();
    const deleted = softDeleteNode(node);
    expect(deleted.properties).toEqual(node.properties);
    expect(deleted.observations).toEqual(node.observations);
    expect(deleted.createdAt).toBe(node.createdAt);
  });
});

describe('softDeleteEdge', () => {
  it('sets staleness to deleted', () => {
    const edge = makeEdge();
    const deleted = softDeleteEdge(edge);
    expect(deleted.staleness).toBe(Staleness.Deleted);
    expect(deleted.sourceId).toBe(edge.sourceId);
    expect(deleted.targetId).toBe(edge.targetId);
  });
});

describe('markNodeStale', () => {
  it('sets staleness to stale without modifying other fields', () => {
    const node = makeNode({ staleness: Staleness.Current });
    const stale = markNodeStale(node);
    expect(stale.staleness).toBe(Staleness.Stale);
    expect(stale.id).toBe(node.id);
    expect(stale.properties).toEqual(node.properties);
    expect(stale.lastSeenAt).toBe(node.lastSeenAt);
  });
});

describe('markEdgeStale', () => {
  it('sets staleness to stale', () => {
    const edge = makeEdge({ staleness: Staleness.Current });
    const stale = markEdgeStale(edge);
    expect(stale.staleness).toBe(Staleness.Stale);
    expect(stale.id).toBe(edge.id);
  });
});

// ─── Multi-extractor scenarios ───────────────────────────────────

describe('multi-extractor staleness', () => {
  it('extractor A scope does not mark nodes owned by extractor B as stale', () => {
    const scopeA: ExtractionScope = {
      extractorName: 'repo-structure',
      includedPaths: ['src/**/*.ts'],
      ownedNodeKinds: [NodeKind.File, NodeKind.Directory],
      ownedEdgeKinds: [EdgeKind.Contains],
    };

    // A function node observed by extractor B
    const fnNode = makeNode({
      id: symbolNodeId(NodeKind.Function, REPO_ID, 'src/main.ts', ['main']),
      kind: NodeKind.Function,
      filePath: 'src/main.ts',
      observations: [obs({
        extractorName: 'call-chain',
        extractorVersion: '1.0.0',
      })],
    });

    // Extractor A runs and finds no Function nodes (it doesn't own them)
    const result: ExtractionResult = {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'run-a',
      scope: scopeA,
      commitSha: 'abc123',
      startedAt: T1,
      completedAt: T2,
      languages: ['typescript'],
      nodes: [],
      edges: [],
      processedFiles: ['src/main.ts'],
    };

    // Function node should NOT be marked stale — it's outside scope A
    const staleIds = computeStaleNodeIds([fnNode], result);
    expect(staleIds).toHaveLength(0);
  });

  it('upsert preserves observations from both extractors', () => {
    const obsA = obs({ extractorName: 'repo-structure', extractorVersion: '1.0.0', timestamp: T1 });
    const obsB = obs({ extractorName: 'call-chain', extractorVersion: '1.0.0', timestamp: T2 });

    const existing = makeNode({ observations: [obsA] });
    const incoming = makeNode({ observations: [obsB] });

    const result = upsertNode(existing, incoming);
    expect(result.observations).toHaveLength(2);
    expect(result.observations.map(o => o.extractorName)).toContain('repo-structure');
    expect(result.observations.map(o => o.extractorName)).toContain('call-chain');
  });
});
