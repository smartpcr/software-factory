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
  type: 'object' as const,
  properties: {
    file_path:     { type: 'string' as const, minLength: 1 },
    start_line:    { type: 'integer' as const, minimum: 1 },
    start_column:  { type: 'integer' as const, minimum: 0 },
    end_line:      { type: 'integer' as const, minimum: 1 },
    end_column:    { type: 'integer' as const, minimum: 0 },
  },
  required: ['file_path', 'start_line', 'end_line'],
  additionalProperties: false,
};

const versionRange = {
  type: 'object' as const,
  properties: {
    since: { type: 'string' as const, minLength: 1 },
    until: { type: 'string' as const },
  },
  required: ['since'],
  additionalProperties: false,
};

const provenance = {
  type: 'object' as const,
  properties: {
    agent_session_id: { type: 'string' as const },
    git_commit:       { type: 'string' as const },
    operation:        { type: 'string' as const, minLength: 1 },
    timestamp:        { type: 'string' as const, format: 'date-time' as const },
  },
  required: ['operation', 'timestamp'],
  additionalProperties: false,
};

const citation = {
  type: 'object' as const,
  properties: {
    node_id:    { type: 'string' as const, minLength: 1 },
    commit_sha: { type: 'string' as const },
    snippet:    { type: 'string' as const },
  },
  required: ['node_id'],
  additionalProperties: false,
};

/* ------------------------------------------------------------------ */
/*  Node property-bag sub-schemas                                      */
/* ------------------------------------------------------------------ */

const repoProps = {
  type: 'object' as const,
  properties: {
    root_path:      { type: 'string' as const, minLength: 1 },
    vcs_url:        { type: 'string' as const },
    default_branch: { type: 'string' as const, minLength: 1 },
  },
  required: ['root_path', 'default_branch'],
  additionalProperties: false,
};

const directoryProps = {
  type: 'object' as const,
  properties: {
    path:  { type: 'string' as const, minLength: 1 },
    depth: { type: 'integer' as const, minimum: 0 },
  },
  required: ['path', 'depth'],
  additionalProperties: false,
};

const packageProps = {
  type: 'object' as const,
  properties: {
    name:    { type: 'string' as const, minLength: 1 },
    version: { type: 'string' as const },
    path:    { type: 'string' as const, minLength: 1 },
    manager: { type: 'string' as const },
  },
  required: ['name', 'path'],
  additionalProperties: false,
};

const moduleProps = {
  type: 'object' as const,
  properties: {
    name:     { type: 'string' as const },
    language: { type: 'string' as const, minLength: 1 },
  },
  required: ['language'],
  additionalProperties: false,
};

const fileProps = {
  type: 'object' as const,
  properties: {
    path:       { type: 'string' as const, minLength: 1 },
    language:   { type: 'string' as const, minLength: 1 },
    hash:       { type: 'string' as const, minLength: 1 },
    line_count: { type: 'integer' as const, minimum: 0 },
  },
  required: ['path', 'language', 'hash'],
  additionalProperties: false,
};

const classProps = {
  type: 'object' as const,
  properties: {
    name:                  { type: 'string' as const, minLength: 1 },
    is_abstract:           { type: 'boolean' as const },
    is_exported:           { type: 'boolean' as const },
    docstring:             { type: 'string' as const },
    constructor_signature: { type: 'string' as const },
  },
  required: ['name', 'is_abstract', 'is_exported'],
  additionalProperties: false,
};

const interfaceProps = {
  type: 'object' as const,
  properties: {
    name:        { type: 'string' as const, minLength: 1 },
    is_exported: { type: 'boolean' as const },
    docstring:   { type: 'string' as const },
  },
  required: ['name', 'is_exported'],
  additionalProperties: false,
};

const enumProps = {
  type: 'object' as const,
  properties: {
    name:        { type: 'string' as const, minLength: 1 },
    is_exported: { type: 'boolean' as const },
    members:     { type: 'array' as const, items: { type: 'string' as const } },
  },
  required: ['name', 'is_exported'],
  additionalProperties: false,
};

const typeAliasProps = {
  type: 'object' as const,
  properties: {
    name:            { type: 'string' as const, minLength: 1 },
    is_exported:     { type: 'boolean' as const },
    type_expression: { type: 'string' as const },
  },
  required: ['name', 'is_exported'],
  additionalProperties: false,
};

const functionProps = {
  type: 'object' as const,
  properties: {
    name:        { type: 'string' as const, minLength: 1 },
    signature:   { type: 'string' as const, minLength: 1 },
    is_exported: { type: 'boolean' as const },
    is_async:    { type: 'boolean' as const },
    complexity:  { type: 'number' as const, minimum: 0 },
    docstring:   { type: 'string' as const },
  },
  required: ['name', 'signature', 'is_exported', 'is_async'],
  additionalProperties: false,
};

const methodProps = {
  type: 'object' as const,
  properties: {
    name:       { type: 'string' as const, minLength: 1 },
    signature:  { type: 'string' as const, minLength: 1 },
    is_static:  { type: 'boolean' as const },
    is_async:   { type: 'boolean' as const },
    visibility: { type: 'string' as const, enum: ['public', 'protected', 'private'] },
    complexity: { type: 'number' as const, minimum: 0 },
    docstring:  { type: 'string' as const },
  },
  required: ['name', 'signature', 'is_static', 'is_async'],
  additionalProperties: false,
};

const variableProps = {
  type: 'object' as const,
  properties: {
    name:            { type: 'string' as const, minLength: 1 },
    is_exported:     { type: 'boolean' as const },
    type_annotation: { type: 'string' as const },
    is_const:        { type: 'boolean' as const },
  },
  required: ['name', 'is_exported', 'is_const'],
  additionalProperties: false,
};

const semanticClusterProps = {
  type: 'object' as const,
  properties: {
    label:       { type: 'string' as const, minLength: 1 },
    description: { type: 'string' as const },
  },
  required: ['label'],
  additionalProperties: false,
};

const topicProps = {
  type: 'object' as const,
  properties: {
    title:         { type: 'string' as const, minLength: 1 },
    summary:       { type: 'string' as const, minLength: 1 },
    related_terms: { type: 'array' as const, items: { type: 'string' as const } },
  },
  required: ['title', 'summary'],
  additionalProperties: false,
};

const expertiseNodeProps = {
  type: 'object' as const,
  properties: {
    title:        { type: 'string' as const, minLength: 1 },
    body:         { type: 'string' as const, minLength: 1 },
    confidence:   { type: 'number' as const, minimum: 0, maximum: 1 },
    source_agent: { type: 'string' as const, minLength: 1 },
    tags:         { type: 'array' as const, items: { type: 'string' as const } },
    evidence:     { type: 'array' as const, items: citation },
  },
  required: ['title', 'body', 'confidence', 'source_agent'],
  additionalProperties: false,
};

/* ------------------------------------------------------------------ */
/*  Edge property-bag sub-schemas                                      */
/* ------------------------------------------------------------------ */

const containsEdgeProps = {
  type: 'object' as const,
  properties: { order: { type: 'integer' as const, minimum: 0 } },
  additionalProperties: false,
};

const exportsEdgeProps = {
  type: 'object' as const,
  properties: {
    alias:      { type: 'string' as const },
    is_default: { type: 'boolean' as const },
  },
  additionalProperties: false,
};

const importsEdgeProps = {
  type: 'object' as const,
  properties: {
    alias:        { type: 'string' as const },
    is_wildcard:  { type: 'boolean' as const },
    is_type_only: { type: 'boolean' as const },
  },
  required: ['is_wildcard', 'is_type_only'],
  additionalProperties: false,
};

const reExportsEdgeProps = {
  type: 'object' as const,
  properties: {
    alias:       { type: 'string' as const },
    is_wildcard: { type: 'boolean' as const },
  },
  additionalProperties: false,
};

const callsEdgeProps = {
  type: 'object' as const,
  properties: {
    arg_count:      { type: 'integer' as const, minimum: 0 },
    is_async:       { type: 'boolean' as const },
    is_conditional: { type: 'boolean' as const },
    call_site:      sourceLocation,
    resolved:       { type: 'boolean' as const },
    target_name:    { type: 'string' as const },
  },
  additionalProperties: false,
};

const calledByEdgeProps = {
  type: 'object' as const,
  properties: {
    arg_count: { type: 'integer' as const, minimum: 0 },
    call_site: sourceLocation,
  },
  additionalProperties: false,
};

const overridesEdgeProps = {
  type: 'object' as const,
  properties: { is_abstract: { type: 'boolean' as const } },
  additionalProperties: false,
};

const implementsEdgeProps = {
  type: 'object' as const,
  properties: { partial: { type: 'boolean' as const } },
  additionalProperties: false,
};

const instantiatesEdgeProps = {
  type: 'object' as const,
  properties: { arg_count: { type: 'integer' as const, minimum: 0 } },
  additionalProperties: false,
};

const similarToEdgeProps = {
  type: 'object' as const,
  properties: {
    cosine_similarity: { type: 'number' as const, minimum: -1, maximum: 1 },
  },
  required: ['cosine_similarity'],
  additionalProperties: false,
};

const belongsToClusterEdgeProps = {
  type: 'object' as const,
  properties: {
    membership_score: { type: 'number' as const, minimum: 0, maximum: 1 },
  },
  additionalProperties: false,
};

const appliesToEdgeProps = {
  type: 'object' as const,
  properties: {
    confidence: { type: 'number' as const, minimum: 0, maximum: 1 },
    scope:      { type: 'string' as const, enum: ['exact', 'subtree'] },
  },
  required: ['confidence', 'scope'],
  additionalProperties: false,
};

const supersedesEdgeProps = {
  type: 'object' as const,
  properties: {
    reason:        { type: 'string' as const },
    superseded_at: { type: 'string' as const, format: 'date-time' as const },
  },
  additionalProperties: false,
};

const derivedFromEdgeProps = {
  type: 'object' as const,
  properties: {
    derivation_type: { type: 'string' as const },
  },
  additionalProperties: false,
};

const expertiseEdgeProps = {
  type: 'object' as const,
  properties: {
    confidence: { type: 'number' as const, minimum: 0, maximum: 1 },
    kind:       { type: 'string' as const },
  },
  required: ['confidence'],
  additionalProperties: false,
};

const frequentlyUsedWithEdgeProps = {
  type: 'object' as const,
  properties: {
    cooccurrence_count: { type: 'integer' as const, minimum: 0 },
    correlation:        { type: 'number' as const, minimum: -1, maximum: 1 },
  },
  required: ['cooccurrence_count'],
  additionalProperties: false,
};

const referencesEdgeProps = {
  type: 'object' as const,
  properties: { reference_kind: { type: 'string' as const } },
  additionalProperties: false,
};

const dependsOnEdgeProps = {
  type: 'object' as const,
  properties: {
    is_dev_dependency:  { type: 'boolean' as const },
    version_constraint: { type: 'string' as const },
  },
  additionalProperties: false,
};

const inheritsEdgeProps = {
  type: 'object' as const,
  properties: { is_virtual: { type: 'boolean' as const } },
  additionalProperties: false,
};

/* ------------------------------------------------------------------ */
/*  if / then helpers for discriminated unions                          */
/* ------------------------------------------------------------------ */

function nodeIfThen(kind: string, propsSchema: object) {
  return {
    if:   { properties: { kind: { const: kind } } },
    then: { properties: { properties: propsSchema } },
  };
}

function edgeIfThen(
  relation: string,
  propsSchema: object,
  propsRequired: boolean,
) {
  const then: Record<string, unknown> = {
    properties: { properties: propsSchema },
  };
  if (propsRequired) {
    then.required = ['properties'];
  }
  return {
    if:   { properties: { relation: { const: relation } } },
    then,
  };
}

/* ------------------------------------------------------------------ */
/*  GraphNode / GraphEdge                                              */
/* ------------------------------------------------------------------ */

const graphNodeSchema = {
  type: 'object' as const,
  properties: {
    id:              { type: 'string' as const, minLength: 1 },
    kind:            { type: 'string' as const, enum: nodeKindEnum },
    qualified_name:  { type: 'string' as const, minLength: 1 },
    layer:           { type: 'array' as const, items: { type: 'string' as const, enum: graphLayerEnum }, minItems: 1 },
    source_location: sourceLocation,
    content_hash:    { type: 'string' as const },
    summary:         { type: 'string' as const },
    version_range:   versionRange,
    last_touched:    { type: 'string' as const, format: 'date-time' as const },
    embedding:       { type: 'array' as const, items: { type: 'number' as const } },
    metadata:        { type: 'object' as const },
    properties:      { type: 'object' as const },
  },
  required: ['id', 'kind', 'qualified_name', 'layer', 'version_range', 'last_touched', 'properties'],
  additionalProperties: false,
  allOf: [
    nodeIfThen('repo',             repoProps),
    nodeIfThen('directory',        directoryProps),
    nodeIfThen('package',          packageProps),
    nodeIfThen('module',           moduleProps),
    nodeIfThen('file',             fileProps),
    nodeIfThen('class',            classProps),
    nodeIfThen('interface',        interfaceProps),
    nodeIfThen('enum',             enumProps),
    nodeIfThen('type_alias',       typeAliasProps),
    nodeIfThen('function',         functionProps),
    nodeIfThen('method',           methodProps),
    nodeIfThen('variable',         variableProps),
    nodeIfThen('semantic_cluster', semanticClusterProps),
    nodeIfThen('topic',            topicProps),
    nodeIfThen('pattern',          expertiseNodeProps),
    nodeIfThen('pitfall',          expertiseNodeProps),
    nodeIfThen('preference',       expertiseNodeProps),
    nodeIfThen('insight',          expertiseNodeProps),
  ],
};

const graphEdgeSchema = {
  type: 'object' as const,
  properties: {
    id:            { type: 'string' as const, minLength: 1 },
    source:        { type: 'string' as const, minLength: 1 },
    target:        { type: 'string' as const, minLength: 1 },
    relation:      { type: 'string' as const, enum: edgeRelationEnum },
    layer:         { type: 'string' as const, enum: graphLayerEnum },
    weight:        { type: 'number' as const, minimum: 0, maximum: 1 },
    version_range: versionRange,
    stale:         { type: 'boolean' as const },
    provenance:    provenance,
    properties:    { type: 'object' as const },
  },
  required: ['id', 'source', 'target', 'relation', 'layer', 'weight', 'version_range', 'stale'],
  additionalProperties: false,
  allOf: [
    edgeIfThen('contains',             containsEdgeProps,           false),
    edgeIfThen('exports',              exportsEdgeProps,            false),
    edgeIfThen('imports',              importsEdgeProps,            true),
    edgeIfThen('re_exports',           reExportsEdgeProps,          false),
    edgeIfThen('calls',                callsEdgeProps,              false),
    edgeIfThen('called_by',            calledByEdgeProps,           false),
    edgeIfThen('overrides',            overridesEdgeProps,          false),
    edgeIfThen('implements',           implementsEdgeProps,         false),
    edgeIfThen('instantiates',         instantiatesEdgeProps,       false),
    edgeIfThen('similar_to',           similarToEdgeProps,          true),
    edgeIfThen('belongs_to_cluster',   belongsToClusterEdgeProps,   false),
    edgeIfThen('applies_to',           appliesToEdgeProps,          true),
    edgeIfThen('supersedes',           supersedesEdgeProps,         false),
    edgeIfThen('derived_from',         derivedFromEdgeProps,        false),
    edgeIfThen('expertise',            expertiseEdgeProps,          true),
    edgeIfThen('frequently_used_with', frequentlyUsedWithEdgeProps, true),
    edgeIfThen('references',           referencesEdgeProps,         false),
    edgeIfThen('depends_on',           dependsOnEdgeProps,          false),
    edgeIfThen('inherits',             inheritsEdgeProps,           false),
  ],
};

/* ------------------------------------------------------------------ */
/*  Snapshot / GraphStats / KnowledgeGraph                             */
/* ------------------------------------------------------------------ */

const graphStats = {
  type: 'object' as const,
  properties: {
    node_count:            { type: 'integer' as const, minimum: 0 },
    edge_count:            { type: 'integer' as const, minimum: 0 },
    expertise_node_count:  { type: 'integer' as const, minimum: 0 },
  },
  required: ['node_count', 'edge_count', 'expertise_node_count'],
  additionalProperties: false,
};

const snapshot = {
  type: 'object' as const,
  properties: {
    id:         { type: 'string' as const, minLength: 1 },
    commit_sha: { type: 'string' as const, minLength: 1 },
    parent_sha: { type: 'string' as const },
    created_at: { type: 'string' as const, format: 'date-time' as const },
    stats:      graphStats,
  },
  required: ['id', 'commit_sha', 'created_at', 'stats'],
  additionalProperties: false,
};

const knowledgeGraph = {
  type: 'object' as const,
  properties: {
    nodes:    { type: 'array' as const, items: graphNodeSchema },
    edges:    { type: 'array' as const, items: graphEdgeSchema },
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
  description:
    'JSON Schema for the code-intelligence agent-memory knowledge graph. ' +
    'Validates the structure of nodes, edges, snapshots, and the top-level ' +
    'KnowledgeGraph container.',
  definitions: {
    GraphLayer:     { type: 'string' as const, enum: graphLayerEnum },
    NodeKind:       { type: 'string' as const, enum: nodeKindEnum },
    EdgeRelation:   { type: 'string' as const, enum: edgeRelationEnum },
    SourceLocation: sourceLocation,
    VersionRange:   versionRange,
    Provenance:     provenance,
    Citation:       citation,

    // Node property bags
    RepoProperties:           repoProps,
    DirectoryProperties:      directoryProps,
    PackageProperties:        packageProps,
    ModuleProperties:         moduleProps,
    FileProperties:           fileProps,
    ClassProperties:          classProps,
    InterfaceProperties:      interfaceProps,
    EnumProperties:           enumProps,
    TypeAliasProperties:      typeAliasProps,
    FunctionProperties:       functionProps,
    MethodProperties:         methodProps,
    VariableProperties:       variableProps,
    SemanticClusterProperties: semanticClusterProps,
    TopicProperties:          topicProps,
    ExpertiseNodeProperties:  expertiseNodeProps,

    // Edge property bags
    ContainsEdgeProperties:           containsEdgeProps,
    ExportsEdgeProperties:            exportsEdgeProps,
    ImportsEdgeProperties:            importsEdgeProps,
    ReExportsEdgeProperties:          reExportsEdgeProps,
    CallsEdgeProperties:              callsEdgeProps,
    CalledByEdgeProperties:           calledByEdgeProps,
    OverridesEdgeProperties:          overridesEdgeProps,
    ImplementsEdgeProperties:         implementsEdgeProps,
    InstantiatesEdgeProperties:       instantiatesEdgeProps,
    SimilarToEdgeProperties:          similarToEdgeProps,
    BelongsToClusterEdgeProperties:   belongsToClusterEdgeProps,
    AppliesToEdgeProperties:          appliesToEdgeProps,
    SupersedesEdgeProperties:         supersedesEdgeProps,
    DerivedFromEdgeProperties:        derivedFromEdgeProps,
    ExpertiseEdgeProperties:          expertiseEdgeProps,
    FrequentlyUsedWithEdgeProperties: frequentlyUsedWithEdgeProps,
    ReferencesEdgeProperties:         referencesEdgeProps,
    DependsOnEdgeProperties:          dependsOnEdgeProps,
    InheritsEdgeProperties:           inheritsEdgeProps,

    // Top-level types
    GraphNode:      graphNodeSchema,
    GraphEdge:      graphEdgeSchema,
    GraphStats:     graphStats,
    Snapshot:       snapshot,
    KnowledgeGraph: knowledgeGraph,
  },
  ...knowledgeGraph,
};

/** The GraphNode sub-schema, for standalone node validation. */
export const graphNodeSubSchema = graphNodeSchema;

/** The GraphEdge sub-schema, for standalone edge validation. */
export const graphEdgeSubSchema = graphEdgeSchema;
