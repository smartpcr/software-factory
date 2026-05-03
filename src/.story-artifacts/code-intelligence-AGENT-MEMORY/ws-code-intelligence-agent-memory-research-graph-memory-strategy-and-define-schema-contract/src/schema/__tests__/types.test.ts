import { describe, it, expect } from 'vitest';
import {
  SCHEMA_VERSION,
  NodeKind,
  NODE_KINDS,
  SYMBOL_NODE_KINDS,
  EdgeKind,
  EDGE_KINDS,
  STRUCTURAL_EDGE_KINDS,
  CALL_CHAIN_EDGE_KINDS,
  ConfidenceTier,
  AnalysisBackend,
  Staleness,
  Visibility,
} from '../types.js';

describe('schema version', () => {
  it('is a semver string', () => {
    expect(SCHEMA_VERSION).toMatch(/^\d+\.\d+\.\d+$/);
  });
});

describe('NodeKind', () => {
  it('contains all expected structural kinds', () => {
    expect(NodeKind.Repository).toBe('repository');
    expect(NodeKind.Package).toBe('package');
    expect(NodeKind.Directory).toBe('directory');
    expect(NodeKind.File).toBe('file');
  });

  it('contains all expected symbol kinds', () => {
    expect(NodeKind.Class).toBe('class');
    expect(NodeKind.Function).toBe('function');
    expect(NodeKind.Method).toBe('method');
    expect(NodeKind.Interface).toBe('interface');
    expect(NodeKind.TypeAlias).toBe('type_alias');
    expect(NodeKind.Enum).toBe('enum');
    expect(NodeKind.Variable).toBe('variable');
    expect(NodeKind.Constant).toBe('constant');
    expect(NodeKind.Namespace).toBe('namespace');
  });

  it('NODE_KINDS covers all enum values', () => {
    const expected = Object.values(NodeKind);
    expect(NODE_KINDS).toEqual(expect.arrayContaining(expected));
    expect(NODE_KINDS.length).toBe(expected.length);
  });

  it('SYMBOL_NODE_KINDS excludes structural kinds', () => {
    expect(SYMBOL_NODE_KINDS).not.toContain(NodeKind.Repository);
    expect(SYMBOL_NODE_KINDS).not.toContain(NodeKind.Package);
    expect(SYMBOL_NODE_KINDS).not.toContain(NodeKind.Directory);
    expect(SYMBOL_NODE_KINDS).not.toContain(NodeKind.File);
  });

  it('SYMBOL_NODE_KINDS includes all symbol kinds', () => {
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Class);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Function);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Method);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Interface);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.TypeAlias);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Enum);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Variable);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Constant);
    expect(SYMBOL_NODE_KINDS).toContain(NodeKind.Namespace);
  });
});

describe('EdgeKind', () => {
  it('contains structural edge kinds', () => {
    expect(EdgeKind.Contains).toBe('contains');
    expect(EdgeKind.Imports).toBe('imports');
    expect(EdgeKind.Exports).toBe('exports');
    expect(EdgeKind.ReExports).toBe('re_exports');
    expect(EdgeKind.DependsOn).toBe('depends_on');
  });

  it('contains call-chain edge kinds', () => {
    expect(EdgeKind.Calls).toBe('calls');
    expect(EdgeKind.Extends).toBe('extends');
    expect(EdgeKind.Implements).toBe('implements');
    expect(EdgeKind.References).toBe('references');
    expect(EdgeKind.TypeRef).toBe('type_ref');
    expect(EdgeKind.Instantiates).toBe('instantiates');
  });

  it('EDGE_KINDS covers all enum values', () => {
    const expected = Object.values(EdgeKind);
    expect(EDGE_KINDS).toEqual(expect.arrayContaining(expected));
    expect(EDGE_KINDS.length).toBe(expected.length);
  });

  it('STRUCTURAL + CALL_CHAIN partitions all edges', () => {
    const combined = [...STRUCTURAL_EDGE_KINDS, ...CALL_CHAIN_EDGE_KINDS];
    expect(combined).toEqual(expect.arrayContaining(EDGE_KINDS as unknown as EdgeKind[]));
    expect(combined.length).toBe(EDGE_KINDS.length);
  });

  it('no overlap between structural and call-chain', () => {
    for (const kind of STRUCTURAL_EDGE_KINDS) {
      expect(CALL_CHAIN_EDGE_KINDS).not.toContain(kind);
    }
  });
});

describe('ConfidenceTier', () => {
  it('has exact, heuristic, and unresolved tiers', () => {
    expect(ConfidenceTier.Exact).toBe('exact');
    expect(ConfidenceTier.Heuristic).toBe('heuristic');
    expect(ConfidenceTier.Unresolved).toBe('unresolved');
  });
});

describe('AnalysisBackend', () => {
  it('has all expected backends', () => {
    expect(AnalysisBackend.Lsp).toBe('lsp');
    expect(AnalysisBackend.TreeSitter).toBe('tree_sitter');
    expect(AnalysisBackend.Heuristic).toBe('heuristic');
    expect(AnalysisBackend.AstParse).toBe('ast_parse');
    expect(AnalysisBackend.ManifestParse).toBe('manifest_parse');
  });
});

describe('Staleness', () => {
  it('has current, stale, and deleted states', () => {
    expect(Staleness.Current).toBe('current');
    expect(Staleness.Stale).toBe('stale');
    expect(Staleness.Deleted).toBe('deleted');
  });
});

describe('Visibility', () => {
  it('has all access modifiers', () => {
    expect(Visibility.Public).toBe('public');
    expect(Visibility.Private).toBe('private');
    expect(Visibility.Protected).toBe('protected');
    expect(Visibility.Internal).toBe('internal');
    expect(Visibility.Default).toBe('default');
  });
});
