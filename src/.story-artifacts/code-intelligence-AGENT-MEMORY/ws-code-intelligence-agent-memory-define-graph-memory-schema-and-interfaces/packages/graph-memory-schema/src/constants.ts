/**
 * Canonical mappings between graph layers, node kinds, and edge relations.
 *
 * These tables are the single source of truth for layer assignment and are
 * consumed by the validator to enforce layer-consistency invariants.
 */

import type { NodeKind, EdgeRelation, GraphLayer } from './types.js';
import {
  NODE_KINDS,
  STRUCTURAL_NODE_KINDS,
  CODE_NODE_KINDS,
  SEMANTIC_NODE_KINDS,
  EXPERTISE_NODE_KINDS,
  STRUCTURAL_EDGE_RELATIONS,
  CALL_CHAIN_EDGE_RELATIONS,
  SEMANTIC_EDGE_RELATIONS,
  EXPERTISE_EDGE_RELATIONS,
  REFERENCE_EDGE_RELATIONS,
} from './types.js';

// ─── Node kind → canonical layer ─────────────────────────────────────────

const NODE_LAYER_MAP = new Map<NodeKind, GraphLayer>();
for (const k of STRUCTURAL_NODE_KINDS) NODE_LAYER_MAP.set(k, 'structural');
for (const k of CODE_NODE_KINDS) NODE_LAYER_MAP.set(k, 'structural');
for (const k of SEMANTIC_NODE_KINDS) NODE_LAYER_MAP.set(k, 'semantic');
for (const k of EXPERTISE_NODE_KINDS) NODE_LAYER_MAP.set(k, 'expertise');

/** Returns the canonical (primary) layer for a node kind. */
export function getNodeLayer(kind: NodeKind): GraphLayer {
  const layer = NODE_LAYER_MAP.get(kind);
  if (!layer) throw new Error(`Unknown node kind: ${kind}`);
  return layer;
}

// ─── Edge relation → canonical layer ─────────────────────────────────────

const RELATION_LAYER_MAP = new Map<EdgeRelation, GraphLayer>();
for (const r of STRUCTURAL_EDGE_RELATIONS) RELATION_LAYER_MAP.set(r, 'structural');
for (const r of CALL_CHAIN_EDGE_RELATIONS) RELATION_LAYER_MAP.set(r, 'call_chain');
for (const r of SEMANTIC_EDGE_RELATIONS) RELATION_LAYER_MAP.set(r, 'semantic');
for (const r of EXPERTISE_EDGE_RELATIONS) RELATION_LAYER_MAP.set(r, 'expertise');
for (const r of REFERENCE_EDGE_RELATIONS) RELATION_LAYER_MAP.set(r, 'structural');

/** Returns the canonical layer for an edge relation. */
export function getRelationLayer(relation: EdgeRelation): GraphLayer {
  const layer = RELATION_LAYER_MAP.get(relation);
  if (!layer) throw new Error(`Unknown edge relation: ${relation}`);
  return layer;
}

// ─── Edge relation → required `properties` flag ──────────────────────────

/**
 * Set of relations whose TypeScript edge interface marks `properties` as
 * required (i.e. the property bag contains at least one required field).
 */
export const EDGES_WITH_REQUIRED_PROPERTIES: ReadonlySet<EdgeRelation> = new Set([
  'imports',
  'similar_to',
  'applies_to',
  'expertise',
  'frequently_used_with',
]);

// ─── Valid containment parent/child kinds ─────────────────────────────────

/** Maps a parent node kind to the set of valid child kinds for `contains`. */
export const VALID_CONTAINS_CHILDREN: ReadonlyMap<NodeKind, ReadonlySet<NodeKind>> = new Map([
  ['repo', new Set<NodeKind>(['directory', 'package', 'module', 'file'])],
  ['directory', new Set<NodeKind>(['directory', 'file', 'module', 'package'])],
  ['package', new Set<NodeKind>(['directory', 'file', 'module'])],
  ['module', new Set<NodeKind>(['class', 'interface', 'enum', 'type_alias', 'function', 'variable'])],
  ['file', new Set<NodeKind>(['class', 'interface', 'enum', 'type_alias', 'function', 'variable'])],
  ['class', new Set<NodeKind>(['method', 'variable'])],
  ['interface', new Set<NodeKind>(['method'])],
  ['enum', new Set<NodeKind>([])],
  ['type_alias', new Set<NodeKind>([])],
  ['function', new Set<NodeKind>([])],
  ['method', new Set<NodeKind>([])],
  ['variable', new Set<NodeKind>([])],
]);

// ─── Valid endpoint kinds for relation-specific edges ─────────────────────

/** Source/target kind constraints for edges beyond `contains`. */
export interface EndpointConstraint {
  source_kinds: ReadonlySet<NodeKind>;
  target_kinds: ReadonlySet<NodeKind>;
}

const codeSymbols = new Set<NodeKind>([
  'class', 'interface', 'enum', 'type_alias', 'function', 'method', 'variable',
]);

const callableSymbols = new Set<NodeKind>(['function', 'method']);

const classLike = new Set<NodeKind>(['class', 'interface']);

const expertiseKinds = new Set<NodeKind>(['pattern', 'pitfall', 'preference', 'insight']);

const allNodeKinds = new Set<NodeKind>([...NODE_KINDS] as NodeKind[]);

const fileOrModule = new Set<NodeKind>(['file', 'module']);

const fileOrModuleOrPackage = new Set<NodeKind>(['file', 'module', 'package']);

const semanticKinds = new Set<NodeKind>(['semantic_cluster', 'topic']);

const codeOrFileOrModule = new Set<NodeKind>([...codeSymbols, ...fileOrModule]);

/**
 * Maps edge relations to valid source/target kind pairs.
 * Every edge relation is constrained here to prevent semantically
 * invalid graphs from passing invariant validation.
 */
export const VALID_EDGE_ENDPOINTS: ReadonlyMap<EdgeRelation, EndpointConstraint> = new Map([
  // Structural
  ['imports', { source_kinds: fileOrModule, target_kinds: new Set<NodeKind>([...fileOrModule, ...codeSymbols]) }],
  ['exports', { source_kinds: fileOrModule, target_kinds: codeSymbols }],
  ['re_exports', { source_kinds: fileOrModule, target_kinds: new Set<NodeKind>([...fileOrModule, ...codeSymbols]) }],
  // Call-chain
  ['calls', { source_kinds: callableSymbols, target_kinds: callableSymbols }],
  ['called_by', { source_kinds: callableSymbols, target_kinds: callableSymbols }],
  ['overrides', { source_kinds: new Set<NodeKind>(['method']), target_kinds: new Set<NodeKind>(['method']) }],
  ['implements', { source_kinds: new Set<NodeKind>(['class']), target_kinds: new Set<NodeKind>(['interface']) }],
  ['instantiates', { source_kinds: callableSymbols, target_kinds: new Set<NodeKind>(['class']) }],
  // Semantic
  ['similar_to', { source_kinds: codeSymbols, target_kinds: codeSymbols }],
  ['belongs_to_cluster', { source_kinds: codeSymbols, target_kinds: new Set<NodeKind>(['semantic_cluster']) }],
  // Expertise / agent-learning
  ['applies_to', { source_kinds: expertiseKinds, target_kinds: allNodeKinds }],
  ['supersedes', { source_kinds: expertiseKinds, target_kinds: expertiseKinds }],
  ['derived_from', { source_kinds: new Set<NodeKind>([...expertiseKinds, ...semanticKinds]), target_kinds: allNodeKinds }],
  ['expertise', { source_kinds: allNodeKinds, target_kinds: expertiseKinds }],
  ['frequently_used_with', { source_kinds: codeSymbols, target_kinds: codeSymbols }],
  // Reference
  ['references', { source_kinds: codeOrFileOrModule, target_kinds: codeOrFileOrModule }],
  ['depends_on', { source_kinds: fileOrModuleOrPackage, target_kinds: fileOrModuleOrPackage }],
  ['inherits', { source_kinds: classLike, target_kinds: classLike }],
]);
