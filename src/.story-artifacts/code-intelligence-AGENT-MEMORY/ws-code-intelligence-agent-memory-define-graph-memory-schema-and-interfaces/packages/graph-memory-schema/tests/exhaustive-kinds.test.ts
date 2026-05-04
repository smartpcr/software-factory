/**
 * Exhaustive table-driven validation: one valid exemplar for every node kind
 * and every edge relation.  Catches if/then wiring bugs, property-bag typos,
 * and enum drift between types.ts and schema.ts.
 */
import { describe, it, expect } from 'vitest';
import { NODE_KINDS, EDGE_RELATIONS } from '../src/types.js';
import type { NodeKind, EdgeRelation } from '../src/types.js';
import { getNodeLayer, getRelationLayer } from '../src/constants.js';
import { validateNode, validateEdge } from '../src/validate.js';

// ─── Helpers ─────────────────────────────────────────────────────────────

const ts = '2026-01-01T00:00:00Z';
const vr = { since: 'abc123' };

function baseNode(kind: NodeKind, id: string, props: Record<string, unknown>) {
  return {
    id,
    kind,
    qualified_name: id,
    layer: [getNodeLayer(kind)],
    version_range: vr,
    last_touched: ts,
    properties: props,
  };
}

function baseEdge(
  relation: EdgeRelation,
  id: string,
  props?: Record<string, unknown>,
) {
  const edge: Record<string, unknown> = {
    id,
    source: 'node:a',
    target: 'node:b',
    relation,
    layer: getRelationLayer(relation),
    weight: 1.0,
    version_range: vr,
    stale: false,
  };
  if (props !== undefined) edge.properties = props;
  return edge;
}

// ─── Per-kind node exemplars ─────────────────────────────────────────────

const nodeExemplars: Record<NodeKind, Record<string, unknown>> = {
  repo: { root_path: '/repo', default_branch: 'main' },
  directory: { path: 'src', depth: 1 },
  package: { name: 'pkg', path: 'packages/pkg' },
  module: { language: 'typescript' },
  file: { path: 'src/a.ts', language: 'typescript', hash: 'deadbeef' },
  class: { name: 'MyClass', is_abstract: false, is_exported: true },
  interface: { name: 'IFoo', is_exported: true },
  enum: { name: 'Color', is_exported: true },
  type_alias: { name: 'ID', is_exported: true },
  function: { name: 'doWork', signature: '() => void', is_exported: true, is_async: false },
  method: { name: 'run', signature: '() => void', is_static: false, is_async: true },
  variable: { name: 'count', is_exported: false, is_const: true },
  semantic_cluster: { label: 'auth-cluster' },
  topic: { title: 'Authentication', summary: 'Covers auth flows' },
  pattern: { title: 'Saga', body: 'Distributed txn pattern', confidence: 0.9, source_agent: 'copilot' },
  pitfall: { title: 'N+1', body: 'Watch for N+1 queries', confidence: 0.8, source_agent: 'copilot' },
  preference: { title: 'Prefer const', body: 'Use const over let', confidence: 0.95, source_agent: 'copilot' },
  insight: { title: 'Hot path', body: 'This loop is the hot path', confidence: 0.85, source_agent: 'copilot' },
};

describe('exhaustive per-kind node validation', () => {
  for (const kind of NODE_KINDS) {
    it(`accepts a valid '${kind}' node`, () => {
      const node = baseNode(kind, `${kind}:test`, nodeExemplars[kind]);
      const issues = validateNode(node);
      expect(issues).toEqual([]);
    });
  }

  // Negative: wrong properties for each kind
  for (const kind of NODE_KINDS) {
    it(`rejects '${kind}' with wrong properties`, () => {
      // Use a random incorrect property bag
      const wrongProps = kind === 'repo'
        ? { path: 'src/a.ts', language: 'typescript', hash: 'dead' } // file props
        : { root_path: '/repo', default_branch: 'main' }; // repo props
      const node = baseNode(kind, `${kind}:bad`, wrongProps);
      const issues = validateNode(node);
      expect(issues.length).toBeGreaterThan(0);
    });
  }
});

// ─── Per-relation edge exemplars ─────────────────────────────────────────

const edgeExemplars: Record<EdgeRelation, Record<string, unknown> | undefined> = {
  contains: undefined, // optional props
  exports: undefined,
  imports: { is_wildcard: false, is_type_only: false },
  re_exports: undefined,
  calls: undefined,
  called_by: undefined,
  overrides: undefined,
  implements: undefined,
  instantiates: undefined,
  similar_to: { cosine_similarity: 0.92 },
  belongs_to_cluster: undefined,
  applies_to: { confidence: 0.85, scope: 'subtree' },
  supersedes: undefined,
  derived_from: undefined,
  expertise: { confidence: 0.7 },
  frequently_used_with: { cooccurrence_count: 42 },
  references: undefined,
  depends_on: undefined,
  inherits: undefined,
};

describe('exhaustive per-relation edge validation', () => {
  for (const relation of EDGE_RELATIONS) {
    it(`accepts a valid '${relation}' edge`, () => {
      const edge = baseEdge(relation, `edge:${relation}:ok`, edgeExemplars[relation]);
      const issues = validateEdge(edge);
      expect(issues).toEqual([]);
    });
  }

  // Required-properties edges must fail without properties
  const requiredPropsRelations: EdgeRelation[] = [
    'imports', 'similar_to', 'applies_to', 'expertise', 'frequently_used_with',
  ];
  for (const relation of requiredPropsRelations) {
    it(`rejects '${relation}' edge without required properties`, () => {
      const edge = baseEdge(relation, `edge:${relation}:noprops`);
      const issues = validateEdge(edge);
      expect(issues.length).toBeGreaterThan(0);
    });
  }
});
