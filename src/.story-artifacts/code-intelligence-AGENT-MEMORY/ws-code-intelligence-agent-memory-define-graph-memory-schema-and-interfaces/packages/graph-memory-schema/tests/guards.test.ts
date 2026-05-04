/**
 * Tests for runtime type guards (guards.ts).
 */
import { describe, it, expect } from 'vitest';
import {
  isNodeKind,
  isEdgeRelation,
  isGraphLayer,
  isGraphNode,
  isGraphEdge,
  isKnowledgeGraph,
} from '../src/guards.js';

describe('isNodeKind', () => {
  it('accepts all valid node kinds', () => {
    const kinds = [
      'repo', 'directory', 'package', 'module', 'file',
      'class', 'interface', 'enum', 'type_alias', 'function', 'method', 'variable',
      'semantic_cluster', 'topic', 'pattern', 'pitfall', 'preference', 'insight',
    ];
    for (const k of kinds) {
      expect(isNodeKind(k)).toBe(true);
    }
  });

  it('rejects invalid strings', () => {
    expect(isNodeKind('banana')).toBe(false);
    expect(isNodeKind('')).toBe(false);
  });

  it('rejects non-strings', () => {
    expect(isNodeKind(42)).toBe(false);
    expect(isNodeKind(null)).toBe(false);
    expect(isNodeKind(undefined)).toBe(false);
  });
});

describe('isEdgeRelation', () => {
  it('accepts valid relations', () => {
    expect(isEdgeRelation('contains')).toBe(true);
    expect(isEdgeRelation('calls')).toBe(true);
    expect(isEdgeRelation('inherits')).toBe(true);
    expect(isEdgeRelation('expertise')).toBe(true);
  });

  it('rejects invalid', () => {
    expect(isEdgeRelation('nope')).toBe(false);
    expect(isEdgeRelation(123)).toBe(false);
  });
});

describe('isGraphLayer', () => {
  it('accepts valid layers', () => {
    expect(isGraphLayer('structural')).toBe(true);
    expect(isGraphLayer('call_chain')).toBe(true);
    expect(isGraphLayer('semantic')).toBe(true);
    expect(isGraphLayer('expertise')).toBe(true);
  });

  it('rejects invalid', () => {
    expect(isGraphLayer('physical')).toBe(false);
  });
});

describe('isGraphNode', () => {
  const validNode = {
    id: 'repo:myrepo',
    kind: 'repo',
    qualified_name: 'myrepo',
    layer: ['structural'],
    version_range: { since: 'abc123' },
    last_touched: '2026-01-01T00:00:00Z',
    properties: { root_path: '/x', default_branch: 'main' },
  };

  it('accepts a valid node', () => {
    expect(isGraphNode(validNode)).toBe(true);
  });

  it('rejects null', () => {
    expect(isGraphNode(null)).toBe(false);
  });

  it('rejects array', () => {
    expect(isGraphNode([1, 2, 3])).toBe(false);
  });

  it('rejects missing id', () => {
    const { id, ...rest } = validNode;
    expect(isGraphNode(rest)).toBe(false);
  });

  it('rejects empty id', () => {
    expect(isGraphNode({ ...validNode, id: '' })).toBe(false);
  });

  it('rejects invalid kind', () => {
    expect(isGraphNode({ ...validNode, kind: 'banana' })).toBe(false);
  });

  it('rejects empty layer array', () => {
    expect(isGraphNode({ ...validNode, layer: [] })).toBe(false);
  });

  it('rejects invalid layer values', () => {
    expect(isGraphNode({ ...validNode, layer: ['nonsense'] })).toBe(false);
  });

  it('rejects missing version_range.since', () => {
    expect(isGraphNode({ ...validNode, version_range: {} })).toBe(false);
  });

  it('rejects non-object properties', () => {
    expect(isGraphNode({ ...validNode, properties: 'string' })).toBe(false);
  });
});

describe('isGraphEdge', () => {
  const validEdge = {
    id: 'e1',
    source: 'a',
    target: 'b',
    relation: 'contains',
    layer: 'structural',
    weight: 1.0,
    version_range: { since: 'abc' },
    stale: false,
  };

  it('accepts a valid edge', () => {
    expect(isGraphEdge(validEdge)).toBe(true);
  });

  it('rejects null', () => {
    expect(isGraphEdge(null)).toBe(false);
  });

  it('rejects missing source', () => {
    const { source, ...rest } = validEdge;
    expect(isGraphEdge(rest)).toBe(false);
  });

  it('rejects invalid relation', () => {
    expect(isGraphEdge({ ...validEdge, relation: 'bad' })).toBe(false);
  });

  it('rejects invalid layer', () => {
    expect(isGraphEdge({ ...validEdge, layer: 'bad' })).toBe(false);
  });

  it('rejects non-number weight', () => {
    expect(isGraphEdge({ ...validEdge, weight: 'heavy' })).toBe(false);
  });

  it('rejects non-boolean stale', () => {
    expect(isGraphEdge({ ...validEdge, stale: 'yes' })).toBe(false);
  });
});

describe('isKnowledgeGraph', () => {
  it('accepts { nodes: [], edges: [] }', () => {
    expect(isKnowledgeGraph({ nodes: [], edges: [] })).toBe(true);
  });

  it('rejects missing nodes', () => {
    expect(isKnowledgeGraph({ edges: [] })).toBe(false);
  });

  it('rejects missing edges', () => {
    expect(isKnowledgeGraph({ nodes: [] })).toBe(false);
  });

  it('rejects null', () => {
    expect(isKnowledgeGraph(null)).toBe(false);
  });

  it('rejects non-array nodes', () => {
    expect(isKnowledgeGraph({ nodes: 'not-array', edges: [] })).toBe(false);
  });
});
