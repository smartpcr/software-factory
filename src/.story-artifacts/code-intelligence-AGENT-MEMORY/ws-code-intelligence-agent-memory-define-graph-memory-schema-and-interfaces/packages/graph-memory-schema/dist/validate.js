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
// AJV and ajv-formats are CJS packages; under module:"Node16" the default
// import resolves to the module namespace rather than the constructor/plugin.
// We use the named `Ajv` re-export and cast the ajv-formats namespace to its
// callable shape to satisfy both the compiler and the Node ESM runtime.
import { Ajv } from 'ajv';
import addFormatsNs from 'ajv-formats';
const addFormats = addFormatsNs;
import { graphMemorySchema, graphNodeSubSchema, graphEdgeSubSchema } from './schema.js';
import { getNodeLayer, getRelationLayer, VALID_CONTAINS_CHILDREN, EDGES_WITH_REQUIRED_PROPERTIES, VALID_EDGE_ENDPOINTS } from './constants.js';
import { EXPERTISE_NODE_KINDS } from './types.js';
// ─── AJV instance ────────────────────────────────────────────────────────
const ajv = new Ajv({ allErrors: true, strict: false });
addFormats(ajv);
const _validateNodeAjv = ajv.compile(graphNodeSubSchema);
const _validateEdgeAjv = ajv.compile(graphEdgeSubSchema);
const _validateGraphAjv = ajv.compile(graphMemorySchema);
function ajvErrorsToIssues(errors, prefix) {
    if (!errors)
        return [];
    return errors.map((e) => ({
        severity: 'error',
        path: `${prefix}${e.instancePath}`,
        message: e.message ?? 'unknown validation error',
    }));
}
// ─── Level 1: Structural (JSON Schema) validation ────────────────────────
/** Validate a single node against the JSON Schema. */
export function validateNode(node) {
    _validateNodeAjv(node);
    return ajvErrorsToIssues(_validateNodeAjv.errors, 'node');
}
/** Validate a single edge against the JSON Schema. */
export function validateEdge(edge) {
    _validateEdgeAjv(edge);
    return ajvErrorsToIssues(_validateEdgeAjv.errors, 'edge');
}
/** Validate a full KnowledgeGraph container against the JSON Schema. */
export function validateKnowledgeGraphSchema(graph) {
    _validateGraphAjv(graph);
    return ajvErrorsToIssues(_validateGraphAjv.errors, 'graph');
}
// ─── Level 2: Graph invariant validation ─────────────────────────────────
/** Validate semantic invariants that JSON Schema cannot enforce. */
export function validateGraphInvariants(graph) {
    const issues = [];
    // ── 1. Unique node IDs ──
    const nodeIds = new Set();
    for (const node of graph.nodes) {
        if (nodeIds.has(node.id)) {
            issues.push({
                severity: 'error',
                path: `nodes[id=${node.id}]`,
                message: `Duplicate node ID: ${node.id}`,
            });
        }
        nodeIds.add(node.id);
    }
    // ── 2. Unique edge IDs ──
    const edgeIds = new Set();
    for (const edge of graph.edges) {
        if (edgeIds.has(edge.id)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]`,
                message: `Duplicate edge ID: ${edge.id}`,
            });
        }
        edgeIds.add(edge.id);
    }
    // ── 3. Edge source / target existence ──
    for (const edge of graph.edges) {
        if (!nodeIds.has(edge.source)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/source`,
                message: `Edge source '${edge.source}' references non-existent node`,
            });
        }
        if (!nodeIds.has(edge.target)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/target`,
                message: `Edge target '${edge.target}' references non-existent node`,
            });
        }
    }
    // ── 4. Edge layer matches canonical relation layer ──
    for (const edge of graph.edges) {
        try {
            const canonical = getRelationLayer(edge.relation);
            if (edge.layer !== canonical) {
                issues.push({
                    severity: 'error',
                    path: `edges[id=${edge.id}]/layer`,
                    message: `Edge layer '${edge.layer}' does not match canonical layer '${canonical}' for relation '${edge.relation}'`,
                });
            }
        }
        catch {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/relation`,
                message: `Unknown edge relation '${edge.relation}'`,
            });
        }
    }
    // ── 5. Node layer includes canonical layer; no duplicates ──
    for (const node of graph.nodes) {
        try {
            const canonical = getNodeLayer(node.kind);
            if (!node.layer.includes(canonical)) {
                issues.push({
                    severity: 'error',
                    path: `nodes[id=${node.id}]/layer`,
                    message: `Node layer must include canonical layer '${canonical}' for kind '${node.kind}'`,
                });
            }
        }
        catch {
            issues.push({
                severity: 'error',
                path: `nodes[id=${node.id}]/kind`,
                message: `Unknown node kind '${node.kind}'`,
            });
        }
        const layerSet = new Set(node.layer);
        if (layerSet.size !== node.layer.length) {
            issues.push({
                severity: 'warning',
                path: `nodes[id=${node.id}]/layer`,
                message: 'Node layer array contains duplicates',
            });
        }
    }
    // ── 6. Contains edge: parent/child kind validity + hierarchy invariants ──
    const nodeKindMap = new Map();
    for (const node of graph.nodes) {
        nodeKindMap.set(node.id, node.kind);
    }
    for (const edge of graph.edges) {
        if (edge.relation !== 'contains')
            continue;
        // 6a. Self-containment
        if (edge.source === edge.target) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]`,
                message: `Self-containment: node '${edge.source}' cannot contain itself`,
            });
            continue;
        }
        const parentKind = nodeKindMap.get(edge.source);
        const childKind = nodeKindMap.get(edge.target);
        if (!parentKind || !childKind)
            continue; // already caught by source/target existence
        const validChildren = VALID_CONTAINS_CHILDREN.get(parentKind);
        if (validChildren && !validChildren.has(childKind)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]`,
                message: `Invalid contains: '${parentKind}' cannot contain '${childKind}'`,
            });
        }
        if (!validChildren) {
            issues.push({
                severity: 'warning',
                path: `edges[id=${edge.id}]`,
                message: `No containment rules defined for parent kind '${parentKind}'`,
            });
        }
    }
    // ── 7. Weight bounds [0, 1] ──
    for (const edge of graph.edges) {
        if (edge.weight < 0 || edge.weight > 1) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/weight`,
                message: `Edge weight ${edge.weight} is outside bounds [0, 1]`,
            });
        }
    }
    // ── 8. Source-location ordering ──
    for (const node of graph.nodes) {
        const loc = node.source_location;
        if (!loc)
            continue;
        if (loc.start_line > loc.end_line) {
            issues.push({
                severity: 'error',
                path: `nodes[id=${node.id}]/source_location`,
                message: `start_line (${loc.start_line}) > end_line (${loc.end_line})`,
            });
        }
        if (loc.start_line === loc.end_line &&
            loc.start_column !== undefined &&
            loc.end_column !== undefined &&
            loc.start_column > loc.end_column) {
            issues.push({
                severity: 'error',
                path: `nodes[id=${node.id}]/source_location`,
                message: `start_column (${loc.start_column}) > end_column (${loc.end_column}) on same line`,
            });
        }
    }
    // ── 9. Snapshot stats reconciliation ──
    if (graph.snapshot) {
        const { stats } = graph.snapshot;
        if (stats.node_count !== graph.nodes.length) {
            issues.push({
                severity: 'error',
                path: 'snapshot/stats/node_count',
                message: `Snapshot node_count (${stats.node_count}) does not match actual (${graph.nodes.length})`,
            });
        }
        if (stats.edge_count !== graph.edges.length) {
            issues.push({
                severity: 'error',
                path: 'snapshot/stats/edge_count',
                message: `Snapshot edge_count (${stats.edge_count}) does not match actual (${graph.edges.length})`,
            });
        }
        const expertiseCount = graph.nodes.filter((n) => EXPERTISE_NODE_KINDS.includes(n.kind)).length;
        if (stats.expertise_node_count !== expertiseCount) {
            issues.push({
                severity: 'error',
                path: 'snapshot/stats/expertise_node_count',
                message: `Snapshot expertise_node_count (${stats.expertise_node_count}) does not match actual (${expertiseCount})`,
            });
        }
    }
    // ── 10. Required edge properties enforcement ──
    for (const edge of graph.edges) {
        if (EDGES_WITH_REQUIRED_PROPERTIES.has(edge.relation)) {
            if (!('properties' in edge) || edge.properties === undefined || edge.properties === null) {
                issues.push({
                    severity: 'error',
                    path: `edges[id=${edge.id}]/properties`,
                    message: `Relation '${edge.relation}' requires a properties object`,
                });
            }
        }
    }
    // ── 11. Edge endpoint kind constraints ──
    for (const edge of graph.edges) {
        if (edge.relation === 'contains')
            continue; // already handled by rule 6
        const constraint = VALID_EDGE_ENDPOINTS.get(edge.relation);
        if (!constraint)
            continue;
        const srcKind = nodeKindMap.get(edge.source);
        const tgtKind = nodeKindMap.get(edge.target);
        if (!srcKind || !tgtKind)
            continue; // already caught by source/target existence
        if (!constraint.source_kinds.has(srcKind)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/source`,
                message: `Relation '${edge.relation}' does not allow source kind '${srcKind}'`,
            });
        }
        if (!constraint.target_kinds.has(tgtKind)) {
            issues.push({
                severity: 'error',
                path: `edges[id=${edge.id}]/target`,
                message: `Relation '${edge.relation}' does not allow target kind '${tgtKind}'`,
            });
        }
    }
    return issues;
}
/**
 * Full validation: structural (AJV) + graph invariants.
 *
 * Always runs both validation levels.  If the input is so malformed that
 * invariant checking throws, a synthetic error is appended and the schema
 * issues (which explain the root cause) are still returned.
 */
export function validateKnowledgeGraph(graph) {
    const issues = [];
    issues.push(...validateKnowledgeGraphSchema(graph));
    try {
        issues.push(...validateGraphInvariants(graph));
    }
    catch (err) {
        const detail = err instanceof Error ? err.message : String(err);
        issues.push({
            severity: 'error',
            path: 'graph',
            message: `Invariant validation could not complete: ${detail}`,
        });
    }
    return issues;
}
//# sourceMappingURL=validate.js.map