import { describe, it, expect } from 'vitest';
import {
  validateNode,
  validateEdge,
  validateExtractionResult,
} from '../validation.js';
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
} from '../types.js';
import {
  buildNodeId,
  buildEdgeId,
  repositoryNodeId,
  fileNodeId,
  symbolNodeId,
} from '../canonical-id.js';

// ─── Test helpers ────────────────────────────────────────────────

const REPO_ID = 'a1b2c3d4e5f6';
const NOW = '2026-01-15T10:00:00.000Z';
const COMMIT = 'abc123def456';

function makeObservation(overrides?: Partial<Observation>): Observation {
  return {
    extractorName: 'repo-structure',
    extractorVersion: '1.0.0',
    analysisBackend: AnalysisBackend.TreeSitter,
    confidenceTier: ConfidenceTier.Exact,
    confidence: 1.0,
    commitSha: COMMIT,
    timestamp: NOW,
    sourceFile: 'src/main.ts',
    ...overrides,
  };
}

function makeFileNode(overrides?: Partial<GraphNode>): GraphNode {
  return {
    id: fileNodeId(REPO_ID, 'src/main.ts'),
    kind: NodeKind.File,
    properties: { name: 'main.ts', path: 'src/main.ts' },
    staleness: Staleness.Current,
    observations: [makeObservation()],
    createdAt: NOW,
    lastSeenAt: NOW,
    ...overrides,
  };
}

function makeFunctionNode(overrides?: Partial<GraphNode>): GraphNode {
  return {
    id: symbolNodeId(NodeKind.Function, REPO_ID, 'src/main.ts', ['main']),
    kind: NodeKind.Function,
    properties: { name: 'main', qualifiedName: 'main' },
    filePath: 'src/main.ts',
    sourceSpan: { startLine: 1, startColumn: 0, endLine: 10, endColumn: 1 },
    staleness: Staleness.Current,
    observations: [makeObservation()],
    createdAt: NOW,
    lastSeenAt: NOW,
    ...overrides,
  };
}

function makeEdge(overrides?: Partial<GraphEdge>): GraphEdge {
  const sourceId = symbolNodeId(NodeKind.Function, REPO_ID, 'src/a.ts', ['foo']);
  const targetId = symbolNodeId(NodeKind.Function, REPO_ID, 'src/b.ts', ['bar']);
  return {
    id: buildEdgeId(sourceId, targetId, EdgeKind.Calls),
    sourceId,
    targetId,
    kind: EdgeKind.Calls,
    properties: {},
    staleness: Staleness.Current,
    observations: [makeObservation()],
    createdAt: NOW,
    lastSeenAt: NOW,
    ...overrides,
  };
}

// ─── Node validation ─────────────────────────────────────────────

describe('validateNode', () => {
  it('passes for a valid file node', () => {
    const result = validateNode(makeFileNode());
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('passes for a valid symbol node', () => {
    const result = validateNode(makeFunctionNode());
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('fails for invalid canonical node ID', () => {
    const node = makeFileNode({ id: 'garbage-id' });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'id')).toBe(true);
  });

  it('fails for unknown node kind', () => {
    const node = makeFileNode({ kind: 'widget' as NodeKind });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'kind')).toBe(true);
  });

  it('fails for invalid staleness value', () => {
    const node = makeFileNode({ staleness: 'maybe' as Staleness });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'staleness')).toBe(true);
  });

  it('fails for symbol node without qualifiedName', () => {
    const node = makeFunctionNode({
      properties: { name: 'main', qualifiedName: '' },
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'properties.qualifiedName')).toBe(true);
  });

  it('fails for symbol node without name', () => {
    const node = makeFunctionNode({
      properties: { name: '', qualifiedName: 'main' },
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'properties.name')).toBe(true);
  });

  it('fails for node properties missing name', () => {
    const node = makeFileNode({
      properties: { path: 'src/main.ts' } as any,
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'properties.name')).toBe(true);
  });

  it('fails for invalid source span (negative startLine)', () => {
    const node = makeFunctionNode({
      sourceSpan: { startLine: -1, startColumn: 0, endLine: 5, endColumn: 0 },
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'sourceSpan.startLine')).toBe(true);
  });

  it('fails for source span where endLine < startLine', () => {
    const node = makeFunctionNode({
      sourceSpan: { startLine: 10, startColumn: 0, endLine: 5, endColumn: 0 },
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'sourceSpan.endLine')).toBe(true);
  });

  it('fails for source span where same line but endColumn < startColumn', () => {
    const node = makeFunctionNode({
      sourceSpan: { startLine: 10, startColumn: 20, endLine: 10, endColumn: 5 },
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'sourceSpan.endColumn')).toBe(true);
  });

  it('fails for non-deleted node with zero observations', () => {
    const node = makeFileNode({ observations: [] });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'observations')).toBe(true);
  });

  it('passes for deleted node with zero observations', () => {
    const node = makeFileNode({
      staleness: Staleness.Deleted,
      observations: [],
    });
    const result = validateNode(node);
    // Only ID/kind/timestamp validation, observations are optional for deleted
    const obsError = result.errors.find((e) => e.path === 'observations');
    expect(obsError).toBeUndefined();
  });

  it('fails for observation with invalid extractor version', () => {
    const node = makeFileNode({
      observations: [makeObservation({ extractorVersion: 'notSemver' })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('semver'))).toBe(true);
  });

  it('fails for observation with invalid analysisBackend', () => {
    const node = makeFileNode({
      observations: [makeObservation({ analysisBackend: 'magic' as AnalysisBackend })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('analysis backend'))).toBe(true);
  });

  it('fails for observation with invalid confidenceTier', () => {
    const node = makeFileNode({
      observations: [makeObservation({ confidenceTier: 'maybe' as ConfidenceTier })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('confidence tier'))).toBe(true);
  });

  it('fails for observation with empty sourceFile', () => {
    const node = makeFileNode({
      observations: [makeObservation({ sourceFile: '' })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('non-empty'))).toBe(true);
  });

  it('fails for observation with confidence out of range', () => {
    const node = makeFileNode({
      observations: [makeObservation({ confidence: 1.5 })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('0.0 and 1.0'))).toBe(true);
  });

  it('fails for observation with empty commitSha', () => {
    const node = makeFileNode({
      observations: [makeObservation({ commitSha: '' })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('non-empty'))).toBe(true);
  });

  it('fails for observation with invalid timestamp', () => {
    const node = makeFileNode({
      observations: [makeObservation({ timestamp: 'not-a-date' })],
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.message.includes('ISO 8601'))).toBe(true);
  });

  it('fails for invalid createdAt timestamp', () => {
    const node = makeFileNode({ createdAt: 'nope' });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'createdAt')).toBe(true);
  });

  it('fails for invalid lastSeenAt timestamp', () => {
    const node = makeFileNode({ lastSeenAt: '' });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'lastSeenAt')).toBe(true);
  });

  it('accumulates multiple errors at once', () => {
    const node = makeFileNode({
      id: 'bad',
      kind: 'bogus' as NodeKind,
      observations: [],
      createdAt: 'nope',
      lastSeenAt: 'nope',
    });
    const result = validateNode(node);
    expect(result.valid).toBe(false);
    expect(result.errors.length).toBeGreaterThan(3);
  });
});

// ─── Edge validation ─────────────────────────────────────────────

describe('validateEdge', () => {
  it('passes for a valid edge', () => {
    const result = validateEdge(makeEdge());
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('fails for invalid edge ID', () => {
    const edge = makeEdge({ id: 'not-an-edge-id' });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'id')).toBe(true);
  });

  it('fails for invalid sourceId', () => {
    const edge = makeEdge({ sourceId: 'garbage' });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'sourceId')).toBe(true);
  });

  it('fails for invalid targetId', () => {
    const edge = makeEdge({ targetId: 'garbage' });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'targetId')).toBe(true);
  });

  it('fails for unknown edge kind', () => {
    const edge = makeEdge({ kind: 'yolo' as EdgeKind });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'kind')).toBe(true);
  });

  it('fails for non-deleted edge with zero observations', () => {
    const edge = makeEdge({ observations: [] });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'observations')).toBe(true);
  });

  it('passes for deleted edge with zero observations', () => {
    const edge = makeEdge({
      staleness: Staleness.Deleted,
      observations: [],
    });
    const result = validateEdge(edge);
    const obsError = result.errors.find((e) => e.path === 'observations');
    expect(obsError).toBeUndefined();
  });

  it('validates source span on edge', () => {
    const edge = makeEdge({
      sourceSpan: { startLine: -1, startColumn: 0, endLine: 5, endColumn: 0 },
    });
    const result = validateEdge(edge);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'sourceSpan.startLine')).toBe(true);
  });
});

// ─── ExtractionResult validation ────────────────────────────────

describe('validateExtractionResult', () => {
  function makeResult(overrides?: Partial<ExtractionResult>): ExtractionResult {
    const repoNode: GraphNode = {
      id: repositoryNodeId(REPO_ID),
      kind: NodeKind.Repository,
      properties: { name: 'test-repo' },
      staleness: Staleness.Current,
      observations: [makeObservation()],
      createdAt: NOW,
      lastSeenAt: NOW,
    };
    const fileNode = makeFileNode();

    const edge: GraphEdge = {
      id: buildEdgeId(repoNode.id, fileNode.id, EdgeKind.Contains),
      sourceId: repoNode.id,
      targetId: fileNode.id,
      kind: EdgeKind.Contains,
      properties: { ordinal: 0 },
      staleness: Staleness.Current,
      observations: [makeObservation()],
      createdAt: NOW,
      lastSeenAt: NOW,
    };

    return {
      schemaVersion: SCHEMA_VERSION,
      extractionId: 'extract-001',
      scope: {
        extractorName: 'repo-structure',
        includedPaths: ['**/*.ts'],
        ownedNodeKinds: [NodeKind.Repository, NodeKind.File],
        ownedEdgeKinds: [EdgeKind.Contains],
      },
      commitSha: COMMIT,
      startedAt: NOW,
      completedAt: NOW,
      languages: ['typescript'],
      nodes: [repoNode, fileNode],
      edges: [edge],
      processedFiles: ['src/main.ts'],
      ...overrides,
    };
  }

  it('passes for a valid extraction result', () => {
    const result = validateExtractionResult(makeResult());
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('fails for mismatched schema version', () => {
    const result = validateExtractionResult(
      makeResult({ schemaVersion: '0.0.1' as typeof SCHEMA_VERSION }),
    );
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'schemaVersion')).toBe(true);
  });

  it('fails for empty extractionId', () => {
    const result = validateExtractionResult(makeResult({ extractionId: '' }));
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'extractionId')).toBe(true);
  });

  it('fails for empty extractor name in scope', () => {
    const result = validateExtractionResult(
      makeResult({
        scope: {
          extractorName: '',
          includedPaths: ['**/*.ts'],
          ownedNodeKinds: [NodeKind.File],
          ownedEdgeKinds: [],
        },
      }),
    );
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'scope.extractorName')).toBe(true);
  });

  it('fails for empty includedPaths in scope', () => {
    const result = validateExtractionResult(
      makeResult({
        scope: {
          extractorName: 'test',
          includedPaths: [],
          ownedNodeKinds: [NodeKind.File],
          ownedEdgeKinds: [],
        },
      }),
    );
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'scope.includedPaths')).toBe(true);
  });

  it('fails for empty commitSha', () => {
    const result = validateExtractionResult(makeResult({ commitSha: '' }));
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'commitSha')).toBe(true);
  });

  it('fails for invalid timestamps', () => {
    const result = validateExtractionResult(
      makeResult({ startedAt: 'bogus', completedAt: 'bogus' }),
    );
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path === 'startedAt')).toBe(true);
    expect(result.errors.some((e) => e.path === 'completedAt')).toBe(true);
  });

  it('fails when an edge references a node not in the result', () => {
    const orphanEdge: GraphEdge = {
      id: buildEdgeId(
        symbolNodeId(NodeKind.Function, REPO_ID, 'x.ts', ['x']),
        symbolNodeId(NodeKind.Function, REPO_ID, 'y.ts', ['y']),
        EdgeKind.Calls,
      ),
      sourceId: symbolNodeId(NodeKind.Function, REPO_ID, 'x.ts', ['x']),
      targetId: symbolNodeId(NodeKind.Function, REPO_ID, 'y.ts', ['y']),
      kind: EdgeKind.Calls,
      properties: {},
      staleness: Staleness.Current,
      observations: [makeObservation()],
      createdAt: NOW,
      lastSeenAt: NOW,
    };

    const base = makeResult();
    const result = validateExtractionResult({
      ...base,
      edges: [...base.edges, orphanEdge],
    });
    expect(result.valid).toBe(false);
    // Should flag both sourceId and targetId of the orphan edge
    const edgeErrors = result.errors.filter((e) => e.path.startsWith('edges[1]'));
    expect(edgeErrors.length).toBeGreaterThanOrEqual(2);
  });

  it('cascades node-level validation errors into the result', () => {
    const badNode: GraphNode = {
      id: 'bad-id',
      kind: NodeKind.File,
      properties: { name: 'x.ts', path: 'x.ts' },
      staleness: Staleness.Current,
      observations: [],
      createdAt: NOW,
      lastSeenAt: NOW,
    };
    const result = validateExtractionResult(
      makeResult({ nodes: [badNode], edges: [] }),
    );
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.path.startsWith('nodes[0]'))).toBe(true);
  });
});
