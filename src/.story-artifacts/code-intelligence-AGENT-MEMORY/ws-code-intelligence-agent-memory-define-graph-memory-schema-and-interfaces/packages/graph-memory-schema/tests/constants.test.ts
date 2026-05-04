/**
 * Tests for constants module: layer lookups, containment rules, required-property edges.
 */
import { describe, it, expect } from 'vitest';
import {
  getNodeLayer,
  getRelationLayer,
  VALID_CONTAINS_CHILDREN,
  EDGES_WITH_REQUIRED_PROPERTIES,
} from '../src/constants.js';
import {
  NODE_KINDS,
  EDGE_RELATIONS,
  STRUCTURAL_NODE_KINDS,
  CODE_NODE_KINDS,
  SEMANTIC_NODE_KINDS,
  EXPERTISE_NODE_KINDS,
  STRUCTURAL_EDGE_RELATIONS,
  CALL_CHAIN_EDGE_RELATIONS,
  SEMANTIC_EDGE_RELATIONS,
  EXPERTISE_EDGE_RELATIONS,
  REFERENCE_EDGE_RELATIONS,
} from '../src/types.js';

describe('getNodeLayer', () => {
  it('maps structural kinds to structural', () => {
    for (const k of STRUCTURAL_NODE_KINDS) {
      expect(getNodeLayer(k)).toBe('structural');
    }
  });

  it('maps code kinds to structural', () => {
    for (const k of CODE_NODE_KINDS) {
      expect(getNodeLayer(k)).toBe('structural');
    }
  });

  it('maps semantic kinds to semantic', () => {
    for (const k of SEMANTIC_NODE_KINDS) {
      expect(getNodeLayer(k)).toBe('semantic');
    }
  });

  it('maps expertise kinds to expertise', () => {
    for (const k of EXPERTISE_NODE_KINDS) {
      expect(getNodeLayer(k)).toBe('expertise');
    }
  });

  it('covers every node kind', () => {
    for (const k of NODE_KINDS) {
      expect(() => getNodeLayer(k)).not.toThrow();
    }
  });

  it('throws on unknown kind', () => {
    expect(() => getNodeLayer('banana' as any)).toThrow('Unknown node kind');
  });
});

describe('getRelationLayer', () => {
  it('maps structural relations to structural', () => {
    for (const r of STRUCTURAL_EDGE_RELATIONS) {
      expect(getRelationLayer(r)).toBe('structural');
    }
  });

  it('maps call-chain relations to call_chain', () => {
    for (const r of CALL_CHAIN_EDGE_RELATIONS) {
      expect(getRelationLayer(r)).toBe('call_chain');
    }
  });

  it('maps semantic relations to semantic', () => {
    for (const r of SEMANTIC_EDGE_RELATIONS) {
      expect(getRelationLayer(r)).toBe('semantic');
    }
  });

  it('maps expertise relations to expertise', () => {
    for (const r of EXPERTISE_EDGE_RELATIONS) {
      expect(getRelationLayer(r)).toBe('expertise');
    }
  });

  it('maps reference relations to structural', () => {
    for (const r of REFERENCE_EDGE_RELATIONS) {
      expect(getRelationLayer(r)).toBe('structural');
    }
  });

  it('covers every edge relation', () => {
    for (const r of EDGE_RELATIONS) {
      expect(() => getRelationLayer(r)).not.toThrow();
    }
  });

  it('throws on unknown relation', () => {
    expect(() => getRelationLayer('banana' as any)).toThrow('Unknown edge relation');
  });
});

describe('EDGES_WITH_REQUIRED_PROPERTIES', () => {
  it('includes imports, similar_to, applies_to, expertise, frequently_used_with', () => {
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('imports')).toBe(true);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('similar_to')).toBe(true);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('applies_to')).toBe(true);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('expertise')).toBe(true);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('frequently_used_with')).toBe(true);
  });

  it('does NOT include contains, calls, etc.', () => {
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('contains')).toBe(false);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('calls')).toBe(false);
    expect(EDGES_WITH_REQUIRED_PROPERTIES.has('inherits')).toBe(false);
  });
});

describe('VALID_CONTAINS_CHILDREN', () => {
  it('repo can contain directory, package, module, file', () => {
    const children = VALID_CONTAINS_CHILDREN.get('repo')!;
    expect(children.has('directory')).toBe(true);
    expect(children.has('file')).toBe(true);
    expect(children.has('package')).toBe(true);
    expect(children.has('module')).toBe(true);
    expect(children.has('class')).toBe(false);
  });

  it('file can contain class, function, variable, etc.', () => {
    const children = VALID_CONTAINS_CHILDREN.get('file')!;
    expect(children.has('class')).toBe(true);
    expect(children.has('function')).toBe(true);
    expect(children.has('variable')).toBe(true);
    expect(children.has('interface')).toBe(true);
    expect(children.has('enum')).toBe(true);
    expect(children.has('type_alias')).toBe(true);
  });

  it('class can contain method and variable', () => {
    const children = VALID_CONTAINS_CHILDREN.get('class')!;
    expect(children.has('method')).toBe(true);
    expect(children.has('variable')).toBe(true);
    expect(children.has('class')).toBe(false);
  });
});
