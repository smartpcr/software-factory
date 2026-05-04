/**
 * Tests that the published dist/ entrypoint exposes the same API as src/.
 *
 * If dist/ is stale (not rebuilt after src/ changes), these tests will fail,
 * catching the exact class of bug that bit iterations 1-7.
 */
import { describe, it, expect } from 'vitest';

// Import from the package dist entrypoint (what downstream consumers see).
import * as dist from '../dist/index.js';
// Import from source for comparison.
import * as src from '../src/index.js';

describe('dist entrypoint parity with src', () => {
  it('exports every named export from src', () => {
    const srcKeys = Object.keys(src).sort();
    const distKeys = Object.keys(dist).sort();
    expect(distKeys).toEqual(srcKeys);
  });

  // ── Constants ──

  it('NODE_KINDS matches', () => {
    expect(dist.NODE_KINDS).toEqual(src.NODE_KINDS);
  });

  it('EDGE_RELATIONS matches', () => {
    expect(dist.EDGE_RELATIONS).toEqual(src.EDGE_RELATIONS);
  });

  it('GRAPH_LAYERS matches', () => {
    expect(dist.GRAPH_LAYERS).toEqual(src.GRAPH_LAYERS);
  });

  it('STRUCTURAL_NODE_KINDS matches', () => {
    expect(dist.STRUCTURAL_NODE_KINDS).toEqual(src.STRUCTURAL_NODE_KINDS);
  });

  it('CODE_NODE_KINDS matches', () => {
    expect(dist.CODE_NODE_KINDS).toEqual(src.CODE_NODE_KINDS);
  });

  it('SEMANTIC_NODE_KINDS matches', () => {
    expect(dist.SEMANTIC_NODE_KINDS).toEqual(src.SEMANTIC_NODE_KINDS);
  });

  it('EXPERTISE_NODE_KINDS matches', () => {
    expect(dist.EXPERTISE_NODE_KINDS).toEqual(src.EXPERTISE_NODE_KINDS);
  });

  it('STRUCTURAL_EDGE_RELATIONS matches', () => {
    expect(dist.STRUCTURAL_EDGE_RELATIONS).toEqual(src.STRUCTURAL_EDGE_RELATIONS);
  });

  it('CALL_CHAIN_EDGE_RELATIONS matches', () => {
    expect(dist.CALL_CHAIN_EDGE_RELATIONS).toEqual(src.CALL_CHAIN_EDGE_RELATIONS);
  });

  it('SEMANTIC_EDGE_RELATIONS matches', () => {
    expect(dist.SEMANTIC_EDGE_RELATIONS).toEqual(src.SEMANTIC_EDGE_RELATIONS);
  });

  it('EXPERTISE_EDGE_RELATIONS matches', () => {
    expect(dist.EXPERTISE_EDGE_RELATIONS).toEqual(src.EXPERTISE_EDGE_RELATIONS);
  });

  it('REFERENCE_EDGE_RELATIONS matches', () => {
    expect(dist.REFERENCE_EDGE_RELATIONS).toEqual(src.REFERENCE_EDGE_RELATIONS);
  });

  // ── Identity helpers ──

  it('makeCanonicalId produces same output', () => {
    const id = dist.makeCanonicalId('repo', 'ts', 'src/a.ts#Foo', 'abc');
    expect(id).toBe(src.makeCanonicalId('repo', 'ts', 'src/a.ts#Foo', 'abc'));
  });

  it('parseCanonicalId round-trips through dist', () => {
    const id = 'myrepo:typescript:src/a.ts#Bar:deadbeef';
    expect(dist.parseCanonicalId(id)).toEqual(src.parseCanonicalId(id));
  });

  it('makeNodeId produces same output', () => {
    expect(dist.makeNodeId('file', 'src/x.ts')).toBe(src.makeNodeId('file', 'src/x.ts'));
  });

  it('parseNodeId produces same output', () => {
    expect(dist.parseNodeId('class:foo')).toEqual(src.parseNodeId('class:foo'));
  });

  it('buildQualifiedName produces same output', () => {
    expect(dist.buildQualifiedName('f.ts', ['A'], 'b')).toBe(
      src.buildQualifiedName('f.ts', ['A'], 'b'),
    );
  });

  it('buildFileScopePath produces same output', () => {
    expect(dist.buildFileScopePath('repo', 'src/a')).toBe(
      src.buildFileScopePath('repo', 'src/a'),
    );
  });

  // ── Constants functions ──

  it('getNodeLayer matches for every kind', () => {
    for (const k of src.NODE_KINDS) {
      expect(dist.getNodeLayer(k)).toBe(src.getNodeLayer(k));
    }
  });

  it('getRelationLayer matches for every relation', () => {
    for (const r of src.EDGE_RELATIONS) {
      expect(dist.getRelationLayer(r)).toBe(src.getRelationLayer(r));
    }
  });

  // ── Schema ──

  it('graphMemorySchema is structurally identical', () => {
    expect(dist.graphMemorySchema).toEqual(src.graphMemorySchema);
  });

  it('graphNodeSubSchema is structurally identical', () => {
    expect(dist.graphNodeSubSchema).toEqual(src.graphNodeSubSchema);
  });

  it('graphEdgeSubSchema is structurally identical', () => {
    expect(dist.graphEdgeSubSchema).toEqual(src.graphEdgeSubSchema);
  });

  // ── Validators ──

  it('dist validateNode accepts a valid node', () => {
    const node = {
      id: 'repo:myrepo',
      kind: 'repo',
      qualified_name: 'myrepo',
      layer: ['structural'],
      version_range: { since: 'abc123' },
      last_touched: '2026-01-01T00:00:00Z',
      properties: { root_path: '/x', default_branch: 'main' },
    };
    expect(dist.validateNode(node)).toEqual([]);
  });

  it('dist validateEdge accepts a valid edge', () => {
    const edge = {
      id: 'e1',
      source: 'a',
      target: 'b',
      relation: 'contains',
      layer: 'structural',
      weight: 1.0,
      version_range: { since: 'abc' },
      stale: false,
    };
    expect(dist.validateEdge(edge)).toEqual([]);
  });

  it('dist validateKnowledgeGraph validates a full graph', () => {
    const graph = {
      nodes: [{
        id: 'repo:x',
        kind: 'repo',
        qualified_name: 'x',
        layer: ['structural'],
        version_range: { since: 'abc' },
        last_touched: '2026-01-01T00:00:00Z',
        properties: { root_path: '/x', default_branch: 'main' },
      }],
      edges: [],
    };
    expect(dist.validateKnowledgeGraph(graph)).toEqual([]);
  });

  // ── Type guards ──

  it('isNodeKind matches src', () => {
    expect(dist.isNodeKind('repo')).toBe(src.isNodeKind('repo'));
    expect(dist.isNodeKind('banana')).toBe(src.isNodeKind('banana'));
  });

  it('isEdgeRelation matches src', () => {
    expect(dist.isEdgeRelation('calls')).toBe(src.isEdgeRelation('calls'));
    expect(dist.isEdgeRelation('nope')).toBe(src.isEdgeRelation('nope'));
  });

  it('isGraphLayer matches src', () => {
    expect(dist.isGraphLayer('structural')).toBe(src.isGraphLayer('structural'));
    expect(dist.isGraphLayer('bad')).toBe(src.isGraphLayer('bad'));
  });

  it('isGraphNode matches src', () => {
    const node = {
      id: 'repo:x', kind: 'repo', qualified_name: 'x',
      layer: ['structural'], version_range: { since: 'abc' },
      last_touched: '2026-01-01T00:00:00Z',
      properties: { root_path: '/x', default_branch: 'main' },
    };
    expect(dist.isGraphNode(node)).toBe(src.isGraphNode(node));
    expect(dist.isGraphNode(null)).toBe(src.isGraphNode(null));
  });

  it('isGraphEdge matches src', () => {
    const edge = {
      id: 'e1', source: 'a', target: 'b', relation: 'contains',
      layer: 'structural', weight: 1.0, version_range: { since: 'abc' }, stale: false,
    };
    expect(dist.isGraphEdge(edge)).toBe(src.isGraphEdge(edge));
    expect(dist.isGraphEdge(null)).toBe(src.isGraphEdge(null));
  });

  it('isKnowledgeGraph matches src', () => {
    expect(dist.isKnowledgeGraph({ nodes: [], edges: [] }))
      .toBe(src.isKnowledgeGraph({ nodes: [], edges: [] }));
    expect(dist.isKnowledgeGraph(null)).toBe(src.isKnowledgeGraph(null));
  });

  // ── Edge endpoint constraints ──

  it('VALID_EDGE_ENDPOINTS is a Map with matching keys', () => {
    expect(dist.VALID_EDGE_ENDPOINTS).toBeInstanceOf(Map);
    const distKeys = [...dist.VALID_EDGE_ENDPOINTS.keys()].sort();
    const srcKeys = [...src.VALID_EDGE_ENDPOINTS.keys()].sort();
    expect(distKeys).toEqual(srcKeys);
  });
});
