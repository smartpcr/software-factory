/**
 * Core type definitions for the agent memory code knowledge graph.
 *
 * Data-model field names (nodes, edges, property bags) use snake_case to
 * match the canonical JSON-Schema and wire format so that serialised JSON
 * round-trips without a mapping layer.
 *
 * TS-only API contracts (TraversalPlan, NodeFilter, RetrievalQuery, etc.)
 * in graph.ts use camelCase to follow TypeScript convention and match
 * the architecture.md §4 contracts.
 *
 * The graph is partitioned into four layers:
 *   structural  — repo containment hierarchy (repo → directory → file → symbol)
 *   call_chain  — invocation and override relationships between symbols
 *   semantic    — embedding-based similarity clusters and topic summaries
 *   expertise   — agent-authored knowledge (patterns, pitfalls, preferences)
 *
 * Node and edge types are discriminated unions keyed on `kind` and `relation`
 * respectively, so each variant carries only the properties relevant to its
 * type.
 */
// ─── Constants & Literal Unions ──────────────────────────────────────────
export const NODE_KINDS = [
    // Structural
    'repo',
    'directory',
    'package',
    'module',
    'file',
    // Code entities
    'class',
    'interface',
    'enum',
    'type_alias',
    'function',
    'method',
    'variable',
    // Semantic
    'semantic_cluster',
    'topic',
    // Expertise
    'pattern',
    'pitfall',
    'preference',
    'insight',
];
export const STRUCTURAL_NODE_KINDS = [
    'repo', 'directory', 'package', 'module', 'file',
];
export const CODE_NODE_KINDS = [
    'class', 'interface', 'enum', 'type_alias',
    'function', 'method', 'variable',
];
export const SEMANTIC_NODE_KINDS = [
    'semantic_cluster', 'topic',
];
export const EXPERTISE_NODE_KINDS = [
    'pattern', 'pitfall', 'preference', 'insight',
];
export const EDGE_RELATIONS = [
    // Structural
    'contains',
    'exports',
    'imports',
    're_exports',
    // Call-chain
    'calls',
    'called_by',
    'overrides',
    'implements',
    'instantiates',
    // Semantic
    'similar_to',
    'belongs_to_cluster',
    // Expertise / agent-learning
    'applies_to',
    'supersedes',
    'derived_from',
    'expertise',
    'frequently_used_with',
    // Reference
    'references',
    'depends_on',
    'inherits',
];
export const STRUCTURAL_EDGE_RELATIONS = [
    'contains', 'exports', 'imports', 're_exports',
];
export const CALL_CHAIN_EDGE_RELATIONS = [
    'calls', 'called_by', 'overrides', 'implements', 'instantiates',
];
export const SEMANTIC_EDGE_RELATIONS = [
    'similar_to', 'belongs_to_cluster',
];
export const EXPERTISE_EDGE_RELATIONS = [
    'applies_to', 'supersedes', 'derived_from',
    'expertise', 'frequently_used_with',
];
export const REFERENCE_EDGE_RELATIONS = [
    'references', 'depends_on', 'inherits',
];
export const GRAPH_LAYERS = [
    'structural',
    'call_chain',
    'semantic',
    'expertise',
];
//# sourceMappingURL=types.js.map