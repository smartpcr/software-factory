/**
 * Tests for JSON Schema parity with TypeScript types and AJV validation.
 */
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  graphMemorySchema,
  graphNodeSubSchema,
  graphEdgeSubSchema,
} from '../src/schema.js';
import {
  NODE_KINDS,
  EDGE_RELATIONS,
  GRAPH_LAYERS,
} from '../src/types.js';
import type { GraphNode, GraphEdge } from '../src/types.js';
import type { KnowledgeGraph } from '../src/graph.js';
import {
  validateNode,
  validateEdge,
  validateKnowledgeGraphSchema,
  validateKnowledgeGraph,
  validateGraphInvariants,
} from '../src/validate.js';

// ─── Helpers ─────────────────────────────────────────────────────────────

function makeValidRepoNode(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'repo:myrepo',
    kind: 'repo',
    qualified_name: 'myrepo',
    layer: ['structural'],
    version_range: { since: 'abc123' },
    last_touched: '2026-01-01T00:00:00Z',
    properties: { root_path: '/home/user/myrepo', default_branch: 'main' },
    ...overrides,
  };
}

function makeValidFileNode(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'file:myrepo/src/a.ts',
    kind: 'file',
    qualified_name: 'myrepo/src/a.ts',
    layer: ['structural'],
    version_range: { since: 'abc123' },
    last_touched: '2026-01-01T00:00:00Z',
    properties: { path: 'src/a.ts', language: 'typescript', hash: 'deadbeef' },
    ...overrides,
  };
}

function makeValidFunctionNode(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'func:myrepo/src/a.ts#doWork',
    kind: 'function',
    qualified_name: 'myrepo/src/a.ts#doWork',
    layer: ['structural'],
    version_range: { since: 'abc123' },
    last_touched: '2026-01-01T00:00:00Z',
    properties: { name: 'doWork', signature: '() => void', is_exported: true, is_async: false },
    ...overrides,
  };
}

function makeValidContainsEdge(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'edge:contains:repo-file',
    source: 'repo:myrepo',
    target: 'file:myrepo/src/a.ts',
    relation: 'contains',
    layer: 'structural',
    weight: 1.0,
    version_range: { since: 'abc123' },
    stale: false,
    ...overrides,
  };
}

function makeValidImportsEdge(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'edge:imports:1',
    source: 'file:myrepo/src/a.ts',
    target: 'file:myrepo/src/b.ts',
    relation: 'imports',
    layer: 'structural',
    weight: 1.0,
    version_range: { since: 'abc123' },
    stale: false,
    properties: { is_wildcard: false, is_type_only: false },
    ...overrides,
  };
}

// ─── Schema.json parity ─────────────────────────────────────────────────

describe('schema.json parity with programmatic schema', () => {
  it('schema.json exists and matches the exported schema object', () => {
    const __dirname = dirname(fileURLToPath(import.meta.url));
    const schemaPath = resolve(__dirname, '..', 'src', 'schema.json');
    const staticSchema = JSON.parse(readFileSync(schemaPath, 'utf-8'));
    expect(staticSchema).toEqual(graphMemorySchema);
  });

  it('schema $schema is draft-07', () => {
    expect(graphMemorySchema.$schema).toBe('http://json-schema.org/draft-07/schema#');
  });

  it('schema defines all node kinds', () => {
    const schemaKinds = graphNodeSubSchema.properties.kind.enum;
    expect(schemaKinds).toEqual([...NODE_KINDS]);
  });

  it('schema defines all edge relations', () => {
    const schemaRelations = graphEdgeSubSchema.properties.relation.enum;
    expect(schemaRelations).toEqual([...EDGE_RELATIONS]);
  });

  it('schema defines all graph layers', () => {
    const schemaLayers = graphNodeSubSchema.properties.layer.items.enum;
    expect(schemaLayers).toEqual([...GRAPH_LAYERS]);
  });
});

// ─── Level 1: Structural (AJV) node validation ─────────────────────────

describe('validateNode', () => {
  it('accepts a valid repo node', () => {
    expect(validateNode(makeValidRepoNode())).toEqual([]);
  });

  it('accepts a valid file node', () => {
    expect(validateNode(makeValidFileNode())).toEqual([]);
  });

  it('accepts a valid function node', () => {
    expect(validateNode(makeValidFunctionNode())).toEqual([]);
  });

  it('rejects a node missing required field (id)', () => {
    const node = makeValidRepoNode();
    delete node.id;
    const issues = validateNode(node);
    expect(issues.length).toBeGreaterThan(0);
    expect(issues[0].message).toContain('id');
  });

  it('rejects a node with unknown kind', () => {
    const issues = validateNode(makeValidRepoNode({ kind: 'banana' }));
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects a node with invalid layer', () => {
    const issues = validateNode(makeValidRepoNode({ layer: ['nonsense'] }));
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects a node with wrong properties for its kind', () => {
    // repo node with file properties
    const issues = validateNode(makeValidRepoNode({
      properties: { path: 'src/a.ts', language: 'ts', hash: 'dead' },
    }));
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects extra fields on node', () => {
    const issues = validateNode(makeValidRepoNode({ bogus_field: 42 }));
    expect(issues.length).toBeGreaterThan(0);
  });

  it('accepts a valid expertise (pattern) node', () => {
    const node = {
      id: 'pattern:saga',
      kind: 'pattern',
      qualified_name: 'saga-pattern',
      layer: ['expertise'],
      version_range: { since: 'abc' },
      last_touched: '2026-01-01T00:00:00Z',
      properties: {
        title: 'Saga pattern',
        body: 'Use saga for distributed transactions',
        confidence: 0.9,
        source_agent: 'copilot-session-1',
      },
    };
    expect(validateNode(node)).toEqual([]);
  });
});

// ─── Level 1: Structural (AJV) edge validation ─────────────────────────

describe('validateEdge', () => {
  it('accepts a valid contains edge', () => {
    expect(validateEdge(makeValidContainsEdge())).toEqual([]);
  });

  it('accepts a valid imports edge with required properties', () => {
    expect(validateEdge(makeValidImportsEdge())).toEqual([]);
  });

  it('rejects an imports edge missing required properties', () => {
    const edge = makeValidImportsEdge();
    delete edge.properties;
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects an imports edge with missing is_wildcard', () => {
    const edge = makeValidImportsEdge({ properties: { is_type_only: false } });
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects similar_to edge without cosine_similarity', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:sim:1',
      relation: 'similar_to',
      layer: 'semantic',
      properties: {},
    };
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('accepts a valid similar_to edge', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:sim:1',
      relation: 'similar_to',
      layer: 'semantic',
      properties: { cosine_similarity: 0.92 },
    };
    expect(validateEdge(edge)).toEqual([]);
  });

  it('rejects applies_to edge without confidence and scope', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:at:1',
      relation: 'applies_to',
      layer: 'expertise',
    };
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('accepts a valid applies_to edge', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:at:1',
      relation: 'applies_to',
      layer: 'expertise',
      properties: { confidence: 0.85, scope: 'subtree' },
    };
    expect(validateEdge(edge)).toEqual([]);
  });

  it('rejects expertise edge without confidence', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:exp:1',
      relation: 'expertise',
      layer: 'expertise',
    };
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects frequently_used_with edge without cooccurrence_count', () => {
    const edge = {
      ...makeValidContainsEdge(),
      id: 'edge:fuw:1',
      relation: 'frequently_used_with',
      layer: 'expertise',
    };
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects edge missing required field (source)', () => {
    const edge = makeValidContainsEdge();
    delete edge.source;
    const issues = validateEdge(edge);
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects edge with weight outside [0, 1]', () => {
    const issues = validateEdge(makeValidContainsEdge({ weight: 1.5 }));
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects edge with weight below 0', () => {
    const issues = validateEdge(makeValidContainsEdge({ weight: -0.1 }));
    expect(issues.length).toBeGreaterThan(0);
  });
});

// ─── Level 1: Full graph schema validation ──────────────────────────────

describe('validateKnowledgeGraphSchema', () => {
  it('accepts a valid minimal graph', () => {
    const graph = {
      nodes: [makeValidRepoNode()],
      edges: [],
    };
    expect(validateKnowledgeGraphSchema(graph)).toEqual([]);
  });

  it('rejects a graph missing nodes', () => {
    const issues = validateKnowledgeGraphSchema({ edges: [] });
    expect(issues.length).toBeGreaterThan(0);
  });

  it('rejects a graph with invalid node inside', () => {
    const graph = {
      nodes: [{ id: 'bad', kind: 'INVALID' }],
      edges: [],
    };
    const issues = validateKnowledgeGraphSchema(graph);
    expect(issues.length).toBeGreaterThan(0);
  });
});

// ─── Level 2: Graph invariant validation ────────────────────────────────

describe('validateGraphInvariants', () => {
  function makeTestGraph(overrides: Partial<KnowledgeGraph> = {}): KnowledgeGraph {
    const repoNode = makeValidRepoNode() as unknown as GraphNode;
    const fileNode = makeValidFileNode() as unknown as GraphNode;
    const funcNode = makeValidFunctionNode() as unknown as GraphNode;
    const containsEdge1 = makeValidContainsEdge() as unknown as GraphEdge;
    const containsEdge2 = {
      ...makeValidContainsEdge(),
      id: 'edge:contains:file-func',
      source: 'file:myrepo/src/a.ts',
      target: 'func:myrepo/src/a.ts#doWork',
    } as unknown as GraphEdge;

    return {
      nodes: [repoNode, fileNode, funcNode],
      edges: [containsEdge1, containsEdge2],
      ...overrides,
    };
  }

  it('accepts a valid graph with no issues', () => {
    const issues = validateGraphInvariants(makeTestGraph());
    expect(issues).toEqual([]);
  });

  it('detects duplicate node IDs', () => {
    const g = makeTestGraph();
    g.nodes.push({ ...g.nodes[0] });
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('Duplicate node ID'))).toBe(true);
  });

  it('detects duplicate edge IDs', () => {
    const g = makeTestGraph();
    g.edges.push({ ...g.edges[0] });
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('Duplicate edge ID'))).toBe(true);
  });

  it('detects edge referencing non-existent source', () => {
    const g = makeTestGraph();
    g.edges[0] = { ...g.edges[0], source: 'nonexistent:node' } as GraphEdge;
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('non-existent node'))).toBe(true);
  });

  it('detects edge referencing non-existent target', () => {
    const g = makeTestGraph();
    g.edges[0] = { ...g.edges[0], target: 'nonexistent:node' } as GraphEdge;
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('non-existent node'))).toBe(true);
  });

  it('detects layer mismatch for edge relation', () => {
    const g = makeTestGraph();
    // contains should be structural, set to semantic
    g.edges[0] = { ...g.edges[0], layer: 'semantic' } as unknown as GraphEdge;
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('does not match canonical layer'))).toBe(true);
  });

  it('detects missing canonical layer on node', () => {
    const g = makeTestGraph();
    // repo node should have structural, set to expertise
    (g.nodes[0] as any).layer = ['expertise'];
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('must include canonical layer'))).toBe(true);
  });

  it('detects invalid contains parent/child', () => {
    const g = makeTestGraph();
    // function cannot contain a repo
    g.edges.push({
      id: 'edge:contains:bad',
      source: 'func:myrepo/src/a.ts#doWork',
      target: 'repo:myrepo',
      relation: 'contains',
      layer: 'structural',
      weight: 1.0,
      version_range: { since: 'abc' },
      stale: false,
    } as unknown as GraphEdge);
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('cannot contain'))).toBe(true);
  });

  it('detects weight outside bounds', () => {
    const g = makeTestGraph();
    (g.edges[0] as any).weight = 2.0;
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('outside bounds'))).toBe(true);
  });

  it('detects source-location start > end line', () => {
    const g = makeTestGraph();
    (g.nodes[2] as any).source_location = {
      file_path: 'src/a.ts',
      start_line: 50,
      end_line: 10,
    };
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('start_line'))).toBe(true);
  });

  it('detects snapshot stats mismatch', () => {
    const g = makeTestGraph({
      snapshot: {
        id: 'snap1',
        commit_sha: 'abc123',
        created_at: '2026-01-01T00:00:00Z',
        stats: { node_count: 99, edge_count: 99, expertise_node_count: 0 },
      },
    });
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes('node_count'))).toBe(true);
    expect(issues.some(i => i.message.includes('edge_count'))).toBe(true);
  });

  it('detects correct snapshot stats', () => {
    const g = makeTestGraph({
      snapshot: {
        id: 'snap1',
        commit_sha: 'abc123',
        created_at: '2026-01-01T00:00:00Z',
        stats: { node_count: 3, edge_count: 2, expertise_node_count: 0 },
      },
    });
    const issues = validateGraphInvariants(g);
    expect(issues).toEqual([]);
  });

  it('detects required edge properties missing for imports', () => {
    const g = makeTestGraph();
    const bNode = makeValidFileNode({ id: 'file:myrepo/src/b.ts', properties: { path: 'src/b.ts', language: 'typescript', hash: 'cafe' } }) as unknown as GraphNode;
    g.nodes.push(bNode);
    g.edges.push({
      id: 'edge:imports:noprops',
      source: 'file:myrepo/src/a.ts',
      target: 'file:myrepo/src/b.ts',
      relation: 'imports',
      layer: 'structural',
      weight: 1.0,
      version_range: { since: 'abc' },
      stale: false,
    } as unknown as GraphEdge);
    const issues = validateGraphInvariants(g);
    expect(issues.some(i => i.message.includes("requires a properties object"))).toBe(true);
  });
});

// ─── Full validation (Level 1 + Level 2) ────────────────────────────────

describe('validateKnowledgeGraph (combined)', () => {
  it('returns schema errors even for grossly invalid input', () => {
    const issues = validateKnowledgeGraph({ nodes: 'not-an-array', edges: [] });
    expect(issues.length).toBeGreaterThan(0);
    expect(issues.some(i => i.path.startsWith('graph'))).toBe(true);
  });

  it('does not throw on grossly malformed input', () => {
    expect(() => validateKnowledgeGraph(null)).not.toThrow();
    expect(() => validateKnowledgeGraph(42)).not.toThrow();
    expect(() => validateKnowledgeGraph('garbage')).not.toThrow();
  });

  it('collects both schema and invariant issues when possible', () => {
    // A graph with a valid structure but an invalid node inside (bad kind)
    // will produce schema errors AND possibly invariant issues
    const repoNode = makeValidRepoNode();
    const fileNode = makeValidFileNode();
    const edge = makeValidContainsEdge();
    // Duplicate the edge to create an invariant violation
    const dupEdge = { ...edge, id: 'edge:contains:repo-file' };
    const graph = { nodes: [repoNode, fileNode], edges: [edge, dupEdge] };
    const issues = validateKnowledgeGraph(graph);
    // Should detect the duplicate edge ID via invariant checks
    expect(issues.some(i => i.message.includes('Duplicate edge ID'))).toBe(true);
  });

  it('runs invariant checks on a schema-valid graph', () => {
    const repoNode = makeValidRepoNode();
    const fileNode = makeValidFileNode();
    const edge = makeValidContainsEdge();
    const graph = { nodes: [repoNode, fileNode], edges: [edge] };
    const issues = validateKnowledgeGraph(graph);
    expect(issues).toEqual([]);
  });
});
