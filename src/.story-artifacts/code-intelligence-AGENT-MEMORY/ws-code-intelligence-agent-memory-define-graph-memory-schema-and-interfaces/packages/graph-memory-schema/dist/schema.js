/**
 * JSON Schema (draft-07) for the agent-memory knowledge graph.
 *
 * This module is the programmatic source of truth.  A static copy lives at
 * `schema.json` in the package root; the test suite asserts parity between
 * the two so they can never drift.
 */
/* ------------------------------------------------------------------ */
/*  Shared sub-schemas                                                 */
/* ------------------------------------------------------------------ */
const graphLayerEnum = ['structural', 'call_chain', 'semantic', 'expertise'];
const nodeKindEnum = [
    'repo', 'directory', 'package', 'module', 'file',
    'class', 'interface', 'enum', 'type_alias', 'function', 'method', 'variable',
    'semantic_cluster', 'topic',
    'pattern', 'pitfall', 'preference', 'insight',
];
const edgeRelationEnum = [
    'contains', 'exports', 'imports', 're_exports',
    'calls', 'called_by', 'overrides', 'implements', 'instantiates',
    'similar_to', 'belongs_to_cluster',
    'applies_to', 'supersedes', 'derived_from', 'expertise', 'frequently_used_with',
    'references', 'depends_on', 'inherits',
];
const sourceLocation = {
    type: 'object',
    properties: {
        file_path: { type: 'string', minLength: 1 },
        start_line: { type: 'integer', minimum: 1 },
        start_column: { type: 'integer', minimum: 0 },
        end_line: { type: 'integer', minimum: 1 },
        end_column: { type: 'integer', minimum: 0 },
    },
    required: ['file_path', 'start_line', 'end_line'],
    additionalProperties: false,
};
const versionRange = {
    type: 'object',
    properties: {
        since: { type: 'string', minLength: 1 },
        until: { type: 'string' },
    },
    required: ['since'],
    additionalProperties: false,
};
const provenance = {
    type: 'object',
    properties: {
        agent_session_id: { type: 'string' },
        git_commit: { type: 'string' },
        operation: { type: 'string', minLength: 1 },
        timestamp: { type: 'string', format: 'date-time' },
    },
    required: ['operation', 'timestamp'],
    additionalProperties: false,
};
const citation = {
    type: 'object',
    properties: {
        node_id: { type: 'string', minLength: 1 },
        commit_sha: { type: 'string' },
        snippet: { type: 'string' },
    },
    required: ['node_id'],
    additionalProperties: false,
};
/* ------------------------------------------------------------------ */
/*  Node property-bag sub-schemas                                      */
/* ------------------------------------------------------------------ */
const repoProps = {
    type: 'object',
    properties: {
        root_path: { type: 'string', minLength: 1 },
        vcs_url: { type: 'string' },
        default_branch: { type: 'string', minLength: 1 },
    },
    required: ['root_path', 'default_branch'],
    additionalProperties: false,
};
const directoryProps = {
    type: 'object',
    properties: {
        path: { type: 'string', minLength: 1 },
        depth: { type: 'integer', minimum: 0 },
    },
    required: ['path', 'depth'],
    additionalProperties: false,
};
const packageProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        version: { type: 'string' },
        path: { type: 'string', minLength: 1 },
        manager: { type: 'string' },
    },
    required: ['name', 'path'],
    additionalProperties: false,
};
const moduleProps = {
    type: 'object',
    properties: {
        name: { type: 'string' },
        language: { type: 'string', minLength: 1 },
    },
    required: ['language'],
    additionalProperties: false,
};
const fileProps = {
    type: 'object',
    properties: {
        path: { type: 'string', minLength: 1 },
        language: { type: 'string', minLength: 1 },
        hash: { type: 'string', minLength: 1 },
        line_count: { type: 'integer', minimum: 0 },
    },
    required: ['path', 'language', 'hash'],
    additionalProperties: false,
};
const classProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        is_abstract: { type: 'boolean' },
        is_exported: { type: 'boolean' },
        docstring: { type: 'string' },
        constructor_signature: { type: 'string' },
    },
    required: ['name', 'is_abstract', 'is_exported'],
    additionalProperties: false,
};
const interfaceProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        is_exported: { type: 'boolean' },
        docstring: { type: 'string' },
    },
    required: ['name', 'is_exported'],
    additionalProperties: false,
};
const enumProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        is_exported: { type: 'boolean' },
        members: { type: 'array', items: { type: 'string' } },
    },
    required: ['name', 'is_exported'],
    additionalProperties: false,
};
const typeAliasProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        is_exported: { type: 'boolean' },
        type_expression: { type: 'string' },
    },
    required: ['name', 'is_exported'],
    additionalProperties: false,
};
const functionProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        signature: { type: 'string', minLength: 1 },
        is_exported: { type: 'boolean' },
        is_async: { type: 'boolean' },
        complexity: { type: 'number', minimum: 0 },
        docstring: { type: 'string' },
    },
    required: ['name', 'signature', 'is_exported', 'is_async'],
    additionalProperties: false,
};
const methodProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        signature: { type: 'string', minLength: 1 },
        is_static: { type: 'boolean' },
        is_async: { type: 'boolean' },
        visibility: { type: 'string', enum: ['public', 'protected', 'private'] },
        complexity: { type: 'number', minimum: 0 },
        docstring: { type: 'string' },
    },
    required: ['name', 'signature', 'is_static', 'is_async'],
    additionalProperties: false,
};
const variableProps = {
    type: 'object',
    properties: {
        name: { type: 'string', minLength: 1 },
        is_exported: { type: 'boolean' },
        type_annotation: { type: 'string' },
        is_const: { type: 'boolean' },
    },
    required: ['name', 'is_exported', 'is_const'],
    additionalProperties: false,
};
const semanticClusterProps = {
    type: 'object',
    properties: {
        label: { type: 'string', minLength: 1 },
        description: { type: 'string' },
    },
    required: ['label'],
    additionalProperties: false,
};
const topicProps = {
    type: 'object',
    properties: {
        title: { type: 'string', minLength: 1 },
        summary: { type: 'string', minLength: 1 },
        related_terms: { type: 'array', items: { type: 'string' } },
    },
    required: ['title', 'summary'],
    additionalProperties: false,
};
const expertiseNodeProps = {
    type: 'object',
    properties: {
        title: { type: 'string', minLength: 1 },
        body: { type: 'string', minLength: 1 },
        confidence: { type: 'number', minimum: 0, maximum: 1 },
        source_agent: { type: 'string', minLength: 1 },
        tags: { type: 'array', items: { type: 'string' } },
        evidence: { type: 'array', items: citation },
    },
    required: ['title', 'body', 'confidence', 'source_agent'],
    additionalProperties: false,
};
/* ------------------------------------------------------------------ */
/*  Edge property-bag sub-schemas                                      */
/* ------------------------------------------------------------------ */
const containsEdgeProps = {
    type: 'object',
    properties: { order: { type: 'integer', minimum: 0 } },
    additionalProperties: false,
};
const exportsEdgeProps = {
    type: 'object',
    properties: {
        alias: { type: 'string' },
        is_default: { type: 'boolean' },
    },
    additionalProperties: false,
};
const importsEdgeProps = {
    type: 'object',
    properties: {
        alias: { type: 'string' },
        is_wildcard: { type: 'boolean' },
        is_type_only: { type: 'boolean' },
    },
    required: ['is_wildcard', 'is_type_only'],
    additionalProperties: false,
};
const reExportsEdgeProps = {
    type: 'object',
    properties: {
        alias: { type: 'string' },
        is_wildcard: { type: 'boolean' },
    },
    additionalProperties: false,
};
const callsEdgeProps = {
    type: 'object',
    properties: {
        arg_count: { type: 'integer', minimum: 0 },
        is_async: { type: 'boolean' },
        is_conditional: { type: 'boolean' },
        call_site: sourceLocation,
        resolved: { type: 'boolean' },
        target_name: { type: 'string' },
    },
    additionalProperties: false,
};
const calledByEdgeProps = {
    type: 'object',
    properties: {
        arg_count: { type: 'integer', minimum: 0 },
        call_site: sourceLocation,
    },
    additionalProperties: false,
};
const overridesEdgeProps = {
    type: 'object',
    properties: { is_abstract: { type: 'boolean' } },
    additionalProperties: false,
};
const implementsEdgeProps = {
    type: 'object',
    properties: { partial: { type: 'boolean' } },
    additionalProperties: false,
};
const instantiatesEdgeProps = {
    type: 'object',
    properties: { arg_count: { type: 'integer', minimum: 0 } },
    additionalProperties: false,
};
const similarToEdgeProps = {
    type: 'object',
    properties: {
        cosine_similarity: { type: 'number', minimum: -1, maximum: 1 },
    },
    required: ['cosine_similarity'],
    additionalProperties: false,
};
const belongsToClusterEdgeProps = {
    type: 'object',
    properties: {
        membership_score: { type: 'number', minimum: 0, maximum: 1 },
    },
    additionalProperties: false,
};
const appliesToEdgeProps = {
    type: 'object',
    properties: {
        confidence: { type: 'number', minimum: 0, maximum: 1 },
        scope: { type: 'string', enum: ['exact', 'subtree'] },
    },
    required: ['confidence', 'scope'],
    additionalProperties: false,
};
const supersedesEdgeProps = {
    type: 'object',
    properties: {
        reason: { type: 'string' },
        superseded_at: { type: 'string', format: 'date-time' },
    },
    additionalProperties: false,
};
const derivedFromEdgeProps = {
    type: 'object',
    properties: {
        derivation_type: { type: 'string' },
    },
    additionalProperties: false,
};
const expertiseEdgeProps = {
    type: 'object',
    properties: {
        confidence: { type: 'number', minimum: 0, maximum: 1 },
        kind: { type: 'string' },
    },
    required: ['confidence'],
    additionalProperties: false,
};
const frequentlyUsedWithEdgeProps = {
    type: 'object',
    properties: {
        cooccurrence_count: { type: 'integer', minimum: 0 },
        correlation: { type: 'number', minimum: -1, maximum: 1 },
    },
    required: ['cooccurrence_count'],
    additionalProperties: false,
};
const referencesEdgeProps = {
    type: 'object',
    properties: { reference_kind: { type: 'string' } },
    additionalProperties: false,
};
const dependsOnEdgeProps = {
    type: 'object',
    properties: {
        is_dev_dependency: { type: 'boolean' },
        version_constraint: { type: 'string' },
    },
    additionalProperties: false,
};
const inheritsEdgeProps = {
    type: 'object',
    properties: { is_virtual: { type: 'boolean' } },
    additionalProperties: false,
};
/* ------------------------------------------------------------------ */
/*  if / then helpers for discriminated unions                          */
/* ------------------------------------------------------------------ */
function nodeIfThen(kind, propsSchema) {
    return {
        if: { properties: { kind: { const: kind } } },
        then: { properties: { properties: propsSchema } },
    };
}
function edgeIfThen(relation, propsSchema, propsRequired) {
    const then = {
        properties: { properties: propsSchema },
    };
    if (propsRequired) {
        then.required = ['properties'];
    }
    return {
        if: { properties: { relation: { const: relation } } },
        then,
    };
}
/* ------------------------------------------------------------------ */
/*  GraphNode / GraphEdge                                              */
/* ------------------------------------------------------------------ */
const graphNodeSchema = {
    type: 'object',
    properties: {
        id: { type: 'string', minLength: 1 },
        kind: { type: 'string', enum: nodeKindEnum },
        qualified_name: { type: 'string', minLength: 1 },
        layer: { type: 'array', items: { type: 'string', enum: graphLayerEnum }, minItems: 1 },
        source_location: sourceLocation,
        content_hash: { type: 'string' },
        summary: { type: 'string' },
        version_range: versionRange,
        last_touched: { type: 'string', format: 'date-time' },
        embedding: { type: 'array', items: { type: 'number' } },
        metadata: { type: 'object' },
        properties: { type: 'object' },
    },
    required: ['id', 'kind', 'qualified_name', 'layer', 'version_range', 'last_touched', 'properties'],
    additionalProperties: false,
    allOf: [
        nodeIfThen('repo', repoProps),
        nodeIfThen('directory', directoryProps),
        nodeIfThen('package', packageProps),
        nodeIfThen('module', moduleProps),
        nodeIfThen('file', fileProps),
        nodeIfThen('class', classProps),
        nodeIfThen('interface', interfaceProps),
        nodeIfThen('enum', enumProps),
        nodeIfThen('type_alias', typeAliasProps),
        nodeIfThen('function', functionProps),
        nodeIfThen('method', methodProps),
        nodeIfThen('variable', variableProps),
        nodeIfThen('semantic_cluster', semanticClusterProps),
        nodeIfThen('topic', topicProps),
        nodeIfThen('pattern', expertiseNodeProps),
        nodeIfThen('pitfall', expertiseNodeProps),
        nodeIfThen('preference', expertiseNodeProps),
        nodeIfThen('insight', expertiseNodeProps),
    ],
};
const graphEdgeSchema = {
    type: 'object',
    properties: {
        id: { type: 'string', minLength: 1 },
        source: { type: 'string', minLength: 1 },
        target: { type: 'string', minLength: 1 },
        relation: { type: 'string', enum: edgeRelationEnum },
        layer: { type: 'string', enum: graphLayerEnum },
        weight: { type: 'number', minimum: 0, maximum: 1 },
        version_range: versionRange,
        stale: { type: 'boolean' },
        provenance: provenance,
        properties: { type: 'object' },
    },
    required: ['id', 'source', 'target', 'relation', 'layer', 'weight', 'version_range', 'stale'],
    additionalProperties: false,
    allOf: [
        edgeIfThen('contains', containsEdgeProps, false),
        edgeIfThen('exports', exportsEdgeProps, false),
        edgeIfThen('imports', importsEdgeProps, true),
        edgeIfThen('re_exports', reExportsEdgeProps, false),
        edgeIfThen('calls', callsEdgeProps, false),
        edgeIfThen('called_by', calledByEdgeProps, false),
        edgeIfThen('overrides', overridesEdgeProps, false),
        edgeIfThen('implements', implementsEdgeProps, false),
        edgeIfThen('instantiates', instantiatesEdgeProps, false),
        edgeIfThen('similar_to', similarToEdgeProps, true),
        edgeIfThen('belongs_to_cluster', belongsToClusterEdgeProps, false),
        edgeIfThen('applies_to', appliesToEdgeProps, true),
        edgeIfThen('supersedes', supersedesEdgeProps, false),
        edgeIfThen('derived_from', derivedFromEdgeProps, false),
        edgeIfThen('expertise', expertiseEdgeProps, true),
        edgeIfThen('frequently_used_with', frequentlyUsedWithEdgeProps, true),
        edgeIfThen('references', referencesEdgeProps, false),
        edgeIfThen('depends_on', dependsOnEdgeProps, false),
        edgeIfThen('inherits', inheritsEdgeProps, false),
    ],
};
/* ------------------------------------------------------------------ */
/*  Snapshot / GraphStats / KnowledgeGraph                             */
/* ------------------------------------------------------------------ */
const graphStats = {
    type: 'object',
    properties: {
        node_count: { type: 'integer', minimum: 0 },
        edge_count: { type: 'integer', minimum: 0 },
        expertise_node_count: { type: 'integer', minimum: 0 },
    },
    required: ['node_count', 'edge_count', 'expertise_node_count'],
    additionalProperties: false,
};
const snapshot = {
    type: 'object',
    properties: {
        id: { type: 'string', minLength: 1 },
        commit_sha: { type: 'string', minLength: 1 },
        parent_sha: { type: 'string' },
        created_at: { type: 'string', format: 'date-time' },
        stats: graphStats,
    },
    required: ['id', 'commit_sha', 'created_at', 'stats'],
    additionalProperties: false,
};
const knowledgeGraph = {
    type: 'object',
    properties: {
        nodes: { type: 'array', items: graphNodeSchema },
        edges: { type: 'array', items: graphEdgeSchema },
        snapshot: snapshot,
    },
    required: ['nodes', 'edges'],
    additionalProperties: false,
};
/* ------------------------------------------------------------------ */
/*  Root schema export                                                 */
/* ------------------------------------------------------------------ */
export const graphMemorySchema = {
    $schema: 'http://json-schema.org/draft-07/schema#',
    $id: 'https://agent-memory.github.io/graph-memory-schema/v0.1.0/knowledge-graph.json',
    title: 'Agent Memory Knowledge Graph',
    description: 'JSON Schema for the code-intelligence agent-memory knowledge graph. ' +
        'Validates the structure of nodes, edges, snapshots, and the top-level ' +
        'KnowledgeGraph container.',
    definitions: {
        GraphLayer: { type: 'string', enum: graphLayerEnum },
        NodeKind: { type: 'string', enum: nodeKindEnum },
        EdgeRelation: { type: 'string', enum: edgeRelationEnum },
        SourceLocation: sourceLocation,
        VersionRange: versionRange,
        Provenance: provenance,
        Citation: citation,
        // Node property bags
        RepoProperties: repoProps,
        DirectoryProperties: directoryProps,
        PackageProperties: packageProps,
        ModuleProperties: moduleProps,
        FileProperties: fileProps,
        ClassProperties: classProps,
        InterfaceProperties: interfaceProps,
        EnumProperties: enumProps,
        TypeAliasProperties: typeAliasProps,
        FunctionProperties: functionProps,
        MethodProperties: methodProps,
        VariableProperties: variableProps,
        SemanticClusterProperties: semanticClusterProps,
        TopicProperties: topicProps,
        ExpertiseNodeProperties: expertiseNodeProps,
        // Edge property bags
        ContainsEdgeProperties: containsEdgeProps,
        ExportsEdgeProperties: exportsEdgeProps,
        ImportsEdgeProperties: importsEdgeProps,
        ReExportsEdgeProperties: reExportsEdgeProps,
        CallsEdgeProperties: callsEdgeProps,
        CalledByEdgeProperties: calledByEdgeProps,
        OverridesEdgeProperties: overridesEdgeProps,
        ImplementsEdgeProperties: implementsEdgeProps,
        InstantiatesEdgeProperties: instantiatesEdgeProps,
        SimilarToEdgeProperties: similarToEdgeProps,
        BelongsToClusterEdgeProperties: belongsToClusterEdgeProps,
        AppliesToEdgeProperties: appliesToEdgeProps,
        SupersedesEdgeProperties: supersedesEdgeProps,
        DerivedFromEdgeProperties: derivedFromEdgeProps,
        ExpertiseEdgeProperties: expertiseEdgeProps,
        FrequentlyUsedWithEdgeProperties: frequentlyUsedWithEdgeProps,
        ReferencesEdgeProperties: referencesEdgeProps,
        DependsOnEdgeProperties: dependsOnEdgeProps,
        InheritsEdgeProperties: inheritsEdgeProps,
        // Top-level types
        GraphNode: graphNodeSchema,
        GraphEdge: graphEdgeSchema,
        GraphStats: graphStats,
        Snapshot: snapshot,
        KnowledgeGraph: knowledgeGraph,
    },
    ...knowledgeGraph,
};
/** The GraphNode sub-schema, for standalone node validation. */
export const graphNodeSubSchema = graphNodeSchema;
/** The GraphEdge sub-schema, for standalone edge validation. */
export const graphEdgeSubSchema = graphEdgeSchema;
//# sourceMappingURL=schema.js.map