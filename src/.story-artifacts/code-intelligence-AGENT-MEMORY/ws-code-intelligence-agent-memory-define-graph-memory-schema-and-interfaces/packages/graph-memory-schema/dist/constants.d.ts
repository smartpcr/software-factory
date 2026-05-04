/**
 * Canonical mappings between graph layers, node kinds, and edge relations.
 *
 * These tables are the single source of truth for layer assignment and are
 * consumed by the validator to enforce layer-consistency invariants.
 */
import type { NodeKind, EdgeRelation, GraphLayer } from './types.js';
/** Returns the canonical (primary) layer for a node kind. */
export declare function getNodeLayer(kind: NodeKind): GraphLayer;
/** Returns the canonical layer for an edge relation. */
export declare function getRelationLayer(relation: EdgeRelation): GraphLayer;
/**
 * Set of relations whose TypeScript edge interface marks `properties` as
 * required (i.e. the property bag contains at least one required field).
 */
export declare const EDGES_WITH_REQUIRED_PROPERTIES: ReadonlySet<EdgeRelation>;
/** Maps a parent node kind to the set of valid child kinds for `contains`. */
export declare const VALID_CONTAINS_CHILDREN: ReadonlyMap<NodeKind, ReadonlySet<NodeKind>>;
/** Source/target kind constraints for edges beyond `contains`. */
export interface EndpointConstraint {
    source_kinds: ReadonlySet<NodeKind>;
    target_kinds: ReadonlySet<NodeKind>;
}
/**
 * Maps edge relations to valid source/target kind pairs.
 * Every edge relation is constrained here to prevent semantically
 * invalid graphs from passing invariant validation.
 */
export declare const VALID_EDGE_ENDPOINTS: ReadonlyMap<EdgeRelation, EndpointConstraint>;
//# sourceMappingURL=constants.d.ts.map