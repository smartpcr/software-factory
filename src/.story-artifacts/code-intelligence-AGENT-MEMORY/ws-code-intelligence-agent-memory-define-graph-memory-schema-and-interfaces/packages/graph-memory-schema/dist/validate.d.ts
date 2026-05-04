/**
 * Two-level validation for the agent-memory knowledge graph.
 *
 * Level 1 — **Structural validation** (JSON Schema / AJV):
 *   Checks that a node, edge, or full graph conforms to the published
 *   draft-07 JSON Schema.
 *
 * Level 2 — **Graph invariant validation**:
 *   Semantic checks that the JSON Schema cannot express: unique IDs,
 *   edge source/target existence, layer consistency, containment rules,
 *   weight bounds, source-location ordering, snapshot stats reconciliation.
 */
import type { KnowledgeGraph, GraphValidationIssue } from './graph.js';
/** Validate a single node against the JSON Schema. */
export declare function validateNode(node: unknown): GraphValidationIssue[];
/** Validate a single edge against the JSON Schema. */
export declare function validateEdge(edge: unknown): GraphValidationIssue[];
/** Validate a full KnowledgeGraph container against the JSON Schema. */
export declare function validateKnowledgeGraphSchema(graph: unknown): GraphValidationIssue[];
/** Validate semantic invariants that JSON Schema cannot enforce. */
export declare function validateGraphInvariants(graph: KnowledgeGraph): GraphValidationIssue[];
/**
 * Full validation: structural (AJV) + graph invariants.
 *
 * Always runs both validation levels.  If the input is so malformed that
 * invariant checking throws, a synthetic error is appended and the schema
 * issues (which explain the root cause) are still returned.
 */
export declare function validateKnowledgeGraph(graph: unknown): GraphValidationIssue[];
//# sourceMappingURL=validate.d.ts.map