/**
 * Tests for edge endpoint kind constraints (invariant 11).
 */
import { describe, it, expect } from 'vitest';
import type { GraphNode, GraphEdge, NodeKind, EdgeRelation } from '../src/types.js';
import type { KnowledgeGraph } from '../src/graph.js';
import { VALID_EDGE_ENDPOINTS } from '../src/constants.js';
import { getNodeLayer, getRelationLayer } from '../src/constants.js';
import { validateGraphInvariants } from '../src/validate.js';

const ts = '2026-01-01T00:00:00Z';
const vr = { since: 'abc123' };

function makeNode(kind: NodeKind, id: string): GraphNode {
  const propsByKind: Record<string, Record<string, unknown>> = {
    repo: { root_path: '/repo', default_branch: 'main' },
    directory: { path: 'src', depth: 1 },
    package: { name: 'pkg', path: 'packages/pkg' },
    module: { language: 'typescript' },
    file: { path: 'src/a.ts', language: 'typescript', hash: 'deadbeef' },
    class: { name: 'A', is_abstract: false, is_exported: true },
    interface: { name: 'I', is_exported: true },
    enum: { name: 'E', is_exported: true },
    type_alias: { name: 'T', is_exported: true },
    function: { name: 'fn', signature: '() => void', is_exported: true, is_async: false },
    method: { name: 'm', signature: '() => void', is_static: false, is_async: false },
    variable: { name: 'v', is_exported: false, is_const: true },
    semantic_cluster: { label: 'cluster' },
    topic: { title: 'T', summary: 'S' },
    pattern: { title: 'P', body: 'B', confidence: 0.9, source_agent: 'a' },
    pitfall: { title: 'P', body: 'B', confidence: 0.9, source_agent: 'a' },
    preference: { title: 'P', body: 'B', confidence: 0.9, source_agent: 'a' },
    insight: { title: 'I', body: 'B', confidence: 0.9, source_agent: 'a' },
  };
  return {
    id,
    kind,
    qualified_name: id,
    layer: [getNodeLayer(kind)],
    version_range: vr,
    last_touched: ts,
    properties: propsByKind[kind] ?? {},
  } as GraphNode;
}

function makeEdge(relation: EdgeRelation, source: string, target: string, props?: Record<string, unknown>): GraphEdge {
  return {
    id: `edge:${relation}:${source}-${target}`,
    source,
    target,
    relation,
    layer: getRelationLayer(relation),
    weight: 1.0,
    version_range: vr,
    stale: false,
    ...(props !== undefined ? { properties: props } : {}),
  } as GraphEdge;
}

describe('edge endpoint kind constraints (invariant 11)', () => {
  it('accepts calls between functions', () => {
    const src = makeNode('function', 'fn:a');
    const tgt = makeNode('function', 'fn:b');
    const edge = makeEdge('calls', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues).toEqual([]);
  });

  it('rejects calls from repo to function', () => {
    const src = makeNode('repo', 'repo:x');
    const tgt = makeNode('function', 'fn:y');
    const edge = makeEdge('calls', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues.some(i => i.message.includes("does not allow source kind 'repo'"))).toBe(true);
  });

  it('accepts inherits between classes', () => {
    const src = makeNode('class', 'cls:A');
    const tgt = makeNode('class', 'cls:B');
    const edge = makeEdge('inherits', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    expect(validateGraphInvariants(graph)).toEqual([]);
  });

  it('rejects inherits from function', () => {
    const src = makeNode('function', 'fn:a');
    const tgt = makeNode('class', 'cls:B');
    const edge = makeEdge('inherits', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues.some(i => i.message.includes("does not allow source kind 'function'"))).toBe(true);
  });

  it('accepts implements from class to interface', () => {
    const src = makeNode('class', 'cls:A');
    const tgt = makeNode('interface', 'ifc:B');
    const edge = makeEdge('implements', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    expect(validateGraphInvariants(graph)).toEqual([]);
  });

  it('rejects implements from variable to class', () => {
    const src = makeNode('variable', 'var:x');
    const tgt = makeNode('class', 'cls:B');
    const edge = makeEdge('implements', src.id, tgt.id);
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues.some(i => i.message.includes("does not allow source kind 'variable'"))).toBe(true);
    expect(issues.some(i => i.message.includes("does not allow target kind 'class'"))).toBe(true);
  });

  it('accepts applies_to from pattern to any node kind', () => {
    const src = makeNode('pattern', 'pat:saga');
    const tgt = makeNode('file', 'file:x');
    const edge = makeEdge('applies_to', src.id, tgt.id, { confidence: 0.9, scope: 'exact' });
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    expect(validateGraphInvariants(graph)).toEqual([]);
  });

  it('rejects applies_to from file (non-expertise source)', () => {
    const src = makeNode('file', 'file:x');
    const tgt = makeNode('function', 'fn:y');
    const edge = makeEdge('applies_to', src.id, tgt.id, { confidence: 0.9, scope: 'exact' });
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues.some(i => i.message.includes("does not allow source kind 'file'"))).toBe(true);
  });

  it('accepts imports from file to file', () => {
    const src = makeNode('file', 'file:a');
    const tgt = makeNode('file', 'file:b');
    const edge = makeEdge('imports', src.id, tgt.id, { is_wildcard: false, is_type_only: false });
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    expect(validateGraphInvariants(graph)).toEqual([]);
  });

  it('rejects imports from class', () => {
    const src = makeNode('class', 'cls:A');
    const tgt = makeNode('file', 'file:b');
    const edge = makeEdge('imports', src.id, tgt.id, { is_wildcard: false, is_type_only: false });
    const graph: KnowledgeGraph = { nodes: [src, tgt], edges: [edge] };
    const issues = validateGraphInvariants(graph);
    expect(issues.some(i => i.message.includes("does not allow source kind 'class'"))).toBe(true);
  });

  it('VALID_EDGE_ENDPOINTS covers all constrained relations', () => {
    // At minimum, these relations should have constraints
    const expectedConstrained: EdgeRelation[] = [
      'calls', 'called_by', 'overrides', 'implements', 'instantiates',
      'inherits', 'imports', 'exports', 're_exports', 'applies_to',
      'supersedes', 'similar_to', 'belongs_to_cluster',
    ];
    for (const r of expectedConstrained) {
      expect(VALID_EDGE_ENDPOINTS.has(r)).toBe(true);
    }
  });
});
