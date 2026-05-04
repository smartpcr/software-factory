/**
 * Tests that the package barrel (index.ts) exports everything downstream needs.
 */
import { describe, it, expect } from 'vitest';
import * as barrel from '../src/index.js';

describe('barrel exports', () => {
  // Types / unions
  it('exports NODE_KINDS', () => {
    expect(barrel.NODE_KINDS).toBeDefined();
    expect(barrel.NODE_KINDS.length).toBeGreaterThan(10);
  });

  it('exports EDGE_RELATIONS', () => {
    expect(barrel.EDGE_RELATIONS).toBeDefined();
    expect(barrel.EDGE_RELATIONS.length).toBeGreaterThan(10);
  });

  it('exports GRAPH_LAYERS', () => {
    expect(barrel.GRAPH_LAYERS).toBeDefined();
    expect(barrel.GRAPH_LAYERS).toEqual(['structural', 'call_chain', 'semantic', 'expertise']);
  });

  // Constants
  it('exports getNodeLayer', () => {
    expect(typeof barrel.getNodeLayer).toBe('function');
  });

  it('exports getRelationLayer', () => {
    expect(typeof barrel.getRelationLayer).toBe('function');
  });

  it('exports VALID_CONTAINS_CHILDREN', () => {
    expect(barrel.VALID_CONTAINS_CHILDREN).toBeInstanceOf(Map);
  });

  it('exports EDGES_WITH_REQUIRED_PROPERTIES', () => {
    expect(barrel.EDGES_WITH_REQUIRED_PROPERTIES).toBeInstanceOf(Set);
  });

  // Identity helpers
  it('exports makeCanonicalId', () => {
    expect(typeof barrel.makeCanonicalId).toBe('function');
  });

  it('exports parseCanonicalId', () => {
    expect(typeof barrel.parseCanonicalId).toBe('function');
  });

  it('exports makeNodeId', () => {
    expect(typeof barrel.makeNodeId).toBe('function');
  });

  it('exports parseNodeId', () => {
    expect(typeof barrel.parseNodeId).toBe('function');
  });

  it('exports buildQualifiedName', () => {
    expect(typeof barrel.buildQualifiedName).toBe('function');
  });

  it('exports buildFileScopePath', () => {
    expect(typeof barrel.buildFileScopePath).toBe('function');
  });

  // Schema
  it('exports graphMemorySchema', () => {
    expect(barrel.graphMemorySchema).toBeDefined();
    expect(barrel.graphMemorySchema.$schema).toBe('http://json-schema.org/draft-07/schema#');
  });

  it('exports graphNodeSubSchema', () => {
    expect(barrel.graphNodeSubSchema).toBeDefined();
  });

  it('exports graphEdgeSubSchema', () => {
    expect(barrel.graphEdgeSubSchema).toBeDefined();
  });

  // Validators
  it('exports validateNode', () => {
    expect(typeof barrel.validateNode).toBe('function');
  });

  it('exports validateEdge', () => {
    expect(typeof barrel.validateEdge).toBe('function');
  });

  it('exports validateKnowledgeGraph', () => {
    expect(typeof barrel.validateKnowledgeGraph).toBe('function');
  });

  it('exports validateGraphInvariants', () => {
    expect(typeof barrel.validateGraphInvariants).toBe('function');
  });

  it('exports validateKnowledgeGraphSchema', () => {
    expect(typeof barrel.validateKnowledgeGraphSchema).toBe('function');
  });

  // Type guards
  it('exports isNodeKind', () => {
    expect(typeof barrel.isNodeKind).toBe('function');
  });

  it('exports isEdgeRelation', () => {
    expect(typeof barrel.isEdgeRelation).toBe('function');
  });

  it('exports isGraphLayer', () => {
    expect(typeof barrel.isGraphLayer).toBe('function');
  });

  it('exports isGraphNode', () => {
    expect(typeof barrel.isGraphNode).toBe('function');
  });

  it('exports isGraphEdge', () => {
    expect(typeof barrel.isGraphEdge).toBe('function');
  });

  it('exports isKnowledgeGraph', () => {
    expect(typeof barrel.isKnowledgeGraph).toBe('function');
  });

  // Node kind groups
  it('exports node kind group arrays', () => {
    expect(barrel.STRUCTURAL_NODE_KINDS).toBeDefined();
    expect(barrel.CODE_NODE_KINDS).toBeDefined();
    expect(barrel.SEMANTIC_NODE_KINDS).toBeDefined();
    expect(barrel.EXPERTISE_NODE_KINDS).toBeDefined();
  });

  // Edge relation groups
  it('exports edge relation group arrays', () => {
    expect(barrel.STRUCTURAL_EDGE_RELATIONS).toBeDefined();
    expect(barrel.CALL_CHAIN_EDGE_RELATIONS).toBeDefined();
    expect(barrel.SEMANTIC_EDGE_RELATIONS).toBeDefined();
    expect(barrel.EXPERTISE_EDGE_RELATIONS).toBeDefined();
    expect(barrel.REFERENCE_EDGE_RELATIONS).toBeDefined();
  });

  // Edge endpoint constraints
  it('exports VALID_EDGE_ENDPOINTS', () => {
    expect(barrel.VALID_EDGE_ENDPOINTS).toBeInstanceOf(Map);
  });
});
