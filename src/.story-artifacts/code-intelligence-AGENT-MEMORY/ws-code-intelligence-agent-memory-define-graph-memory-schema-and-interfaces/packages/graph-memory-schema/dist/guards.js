/**
 * Runtime type guards for discriminated graph unions.
 *
 * These allow downstream consumers to narrow `unknown` JSON into typed
 * graph structures without importing AJV. Each guard performs a minimal
 * structural check (kind/relation discriminant + required base fields)
 * without full schema validation.
 *
 * Three tiers of guards:
 *   1. Literal guards  — `isNodeKind`, `isEdgeRelation`, `isGraphLayer`
 *   2. Base guards      — `isGraphNode`, `isGraphEdge`, `isKnowledgeGraph`
 *   3. Per-kind/relation — `isRepoNode`, `isCallsEdge`, etc. (narrow a typed union)
 *   4. Group guards     — `isStructuralNode`, `isCodeNode`, `isSemanticNode`, `isExpertiseNode`
 */
import { NODE_KINDS, EDGE_RELATIONS, GRAPH_LAYERS, STRUCTURAL_NODE_KINDS, CODE_NODE_KINDS, SEMANTIC_NODE_KINDS, EXPERTISE_NODE_KINDS, } from './types.js';
const NODE_KIND_SET = new Set(NODE_KINDS);
const EDGE_RELATION_SET = new Set(EDGE_RELATIONS);
const GRAPH_LAYER_SET = new Set(GRAPH_LAYERS);
function isNonNullObject(v) {
    return typeof v === 'object' && v !== null && !Array.isArray(v);
}
// ─── Tier 1: Literal guards ─────────────────────────────────────────────
/** Check whether a value is a valid NodeKind string. */
export function isNodeKind(v) {
    return typeof v === 'string' && NODE_KIND_SET.has(v);
}
/** Check whether a value is a valid EdgeRelation string. */
export function isEdgeRelation(v) {
    return typeof v === 'string' && EDGE_RELATION_SET.has(v);
}
/** Check whether a value is a valid GraphLayer string. */
export function isGraphLayer(v) {
    return typeof v === 'string' && GRAPH_LAYER_SET.has(v);
}
// ─── Tier 2: Base guards ────────────────────────────────────────────────
/**
 * Structural type guard for GraphNode.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export function isGraphNode(v) {
    if (!isNonNullObject(v))
        return false;
    if (typeof v.id !== 'string' || v.id.length === 0)
        return false;
    if (!isNodeKind(v.kind))
        return false;
    if (typeof v.qualified_name !== 'string' || v.qualified_name.length === 0)
        return false;
    if (!Array.isArray(v.layer) || v.layer.length === 0)
        return false;
    if (!v.layer.every(isGraphLayer))
        return false;
    if (!isNonNullObject(v.version_range))
        return false;
    if (typeof v.version_range.since !== 'string')
        return false;
    if (typeof v.last_touched !== 'string')
        return false;
    if (!isNonNullObject(v.properties))
        return false;
    return true;
}
/**
 * Structural type guard for GraphEdge.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export function isGraphEdge(v) {
    if (!isNonNullObject(v))
        return false;
    if (typeof v.id !== 'string' || v.id.length === 0)
        return false;
    if (typeof v.source !== 'string' || v.source.length === 0)
        return false;
    if (typeof v.target !== 'string' || v.target.length === 0)
        return false;
    if (!isEdgeRelation(v.relation))
        return false;
    if (!isGraphLayer(v.layer))
        return false;
    if (typeof v.weight !== 'number')
        return false;
    if (!isNonNullObject(v.version_range))
        return false;
    if (typeof v.version_range.since !== 'string')
        return false;
    if (typeof v.stale !== 'boolean')
        return false;
    return true;
}
/**
 * Structural type guard for KnowledgeGraph.
 *
 * Checks top-level shape only; does not recurse into individual nodes/edges.
 */
export function isKnowledgeGraph(v) {
    if (!isNonNullObject(v))
        return false;
    if (!Array.isArray(v.nodes))
        return false;
    if (!Array.isArray(v.edges))
        return false;
    return true;
}
// ─── Tier 3: Per-kind node guards ───────────────────────────────────────
export function isRepoNode(n) { return n.kind === 'repo'; }
export function isDirectoryNode(n) { return n.kind === 'directory'; }
export function isPackageNode(n) { return n.kind === 'package'; }
export function isModuleNode(n) { return n.kind === 'module'; }
export function isFileNode(n) { return n.kind === 'file'; }
export function isClassNode(n) { return n.kind === 'class'; }
export function isInterfaceNode(n) { return n.kind === 'interface'; }
export function isEnumNode(n) { return n.kind === 'enum'; }
export function isTypeAliasNode(n) { return n.kind === 'type_alias'; }
export function isFunctionNode(n) { return n.kind === 'function'; }
export function isMethodNode(n) { return n.kind === 'method'; }
export function isVariableNode(n) { return n.kind === 'variable'; }
export function isSemanticClusterNode(n) { return n.kind === 'semantic_cluster'; }
export function isTopicNode(n) { return n.kind === 'topic'; }
export function isPatternNode(n) { return n.kind === 'pattern'; }
export function isPitfallNode(n) { return n.kind === 'pitfall'; }
export function isPreferenceNode(n) { return n.kind === 'preference'; }
export function isInsightNode(n) { return n.kind === 'insight'; }
/** Lookup table from NodeKind to its per-kind guard. */
export const nodeGuards = {
    repo: isRepoNode,
    directory: isDirectoryNode,
    package: isPackageNode,
    module: isModuleNode,
    file: isFileNode,
    class: isClassNode,
    interface: isInterfaceNode,
    enum: isEnumNode,
    type_alias: isTypeAliasNode,
    function: isFunctionNode,
    method: isMethodNode,
    variable: isVariableNode,
    semantic_cluster: isSemanticClusterNode,
    topic: isTopicNode,
    pattern: isPatternNode,
    pitfall: isPitfallNode,
    preference: isPreferenceNode,
    insight: isInsightNode,
};
// ─── Tier 3: Per-relation edge guards ───────────────────────────────────
export function isContainsEdge(e) { return e.relation === 'contains'; }
export function isExportsEdge(e) { return e.relation === 'exports'; }
export function isImportsEdge(e) { return e.relation === 'imports'; }
export function isReExportsEdge(e) { return e.relation === 're_exports'; }
export function isCallsEdge(e) { return e.relation === 'calls'; }
export function isCalledByEdge(e) { return e.relation === 'called_by'; }
export function isOverridesEdge(e) { return e.relation === 'overrides'; }
export function isImplementsEdge(e) { return e.relation === 'implements'; }
export function isInstantiatesEdge(e) { return e.relation === 'instantiates'; }
export function isSimilarToEdge(e) { return e.relation === 'similar_to'; }
export function isBelongsToClusterEdge(e) { return e.relation === 'belongs_to_cluster'; }
export function isAppliesToEdge(e) { return e.relation === 'applies_to'; }
export function isSupersedesEdge(e) { return e.relation === 'supersedes'; }
export function isDerivedFromEdge(e) { return e.relation === 'derived_from'; }
export function isExpertiseEdge(e) { return e.relation === 'expertise'; }
export function isFrequentlyUsedWithEdge(e) { return e.relation === 'frequently_used_with'; }
export function isReferencesEdge(e) { return e.relation === 'references'; }
export function isDependsOnEdge(e) { return e.relation === 'depends_on'; }
export function isInheritsEdge(e) { return e.relation === 'inherits'; }
/** Lookup table from EdgeRelation to its per-relation guard. */
export const edgeGuards = {
    contains: isContainsEdge,
    exports: isExportsEdge,
    imports: isImportsEdge,
    re_exports: isReExportsEdge,
    calls: isCallsEdge,
    called_by: isCalledByEdge,
    overrides: isOverridesEdge,
    implements: isImplementsEdge,
    instantiates: isInstantiatesEdge,
    similar_to: isSimilarToEdge,
    belongs_to_cluster: isBelongsToClusterEdge,
    applies_to: isAppliesToEdge,
    supersedes: isSupersedesEdge,
    derived_from: isDerivedFromEdge,
    expertise: isExpertiseEdge,
    frequently_used_with: isFrequentlyUsedWithEdge,
    references: isReferencesEdge,
    depends_on: isDependsOnEdge,
    inherits: isInheritsEdge,
};
// ─── Tier 4: Group guards ───────────────────────────────────────────────
const STRUCTURAL_SET = new Set(STRUCTURAL_NODE_KINDS);
const CODE_SET = new Set(CODE_NODE_KINDS);
const SEMANTIC_SET = new Set(SEMANTIC_NODE_KINDS);
const EXPERTISE_SET = new Set(EXPERTISE_NODE_KINDS);
/** True if the node is in the structural group (repo, directory, package, module, file). */
export function isStructuralNode(n) {
    return STRUCTURAL_SET.has(n.kind);
}
/** True if the node is a code entity (class, interface, enum, type_alias, function, method, variable). */
export function isCodeNode(n) {
    return CODE_SET.has(n.kind);
}
/** True if the node is in the semantic group (semantic_cluster, topic). */
export function isSemanticNode(n) {
    return SEMANTIC_SET.has(n.kind);
}
/** True if the node is an expertise entry (pattern, pitfall, preference, insight). */
export function isExpertiseNode(n) {
    return EXPERTISE_SET.has(n.kind);
}
//# sourceMappingURL=guards.js.map