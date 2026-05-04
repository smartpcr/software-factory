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
] as const;

export type NodeKind = (typeof NODE_KINDS)[number];

export const STRUCTURAL_NODE_KINDS: readonly NodeKind[] = [
  'repo', 'directory', 'package', 'module', 'file',
];

export const CODE_NODE_KINDS: readonly NodeKind[] = [
  'class', 'interface', 'enum', 'type_alias',
  'function', 'method', 'variable',
];

export const SEMANTIC_NODE_KINDS: readonly NodeKind[] = [
  'semantic_cluster', 'topic',
];

export const EXPERTISE_NODE_KINDS: readonly NodeKind[] = [
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
] as const;

export type EdgeRelation = (typeof EDGE_RELATIONS)[number];

export const STRUCTURAL_EDGE_RELATIONS: readonly EdgeRelation[] = [
  'contains', 'exports', 'imports', 're_exports',
];

export const CALL_CHAIN_EDGE_RELATIONS: readonly EdgeRelation[] = [
  'calls', 'called_by', 'overrides', 'implements', 'instantiates',
];

export const SEMANTIC_EDGE_RELATIONS: readonly EdgeRelation[] = [
  'similar_to', 'belongs_to_cluster',
];

export const EXPERTISE_EDGE_RELATIONS: readonly EdgeRelation[] = [
  'applies_to', 'supersedes', 'derived_from',
  'expertise', 'frequently_used_with',
];

export const REFERENCE_EDGE_RELATIONS: readonly EdgeRelation[] = [
  'references', 'depends_on', 'inherits',
];

export const GRAPH_LAYERS = [
  'structural',
  'call_chain',
  'semantic',
  'expertise',
] as const;

export type GraphLayer = (typeof GRAPH_LAYERS)[number];

// ─── Shared Value Types ──────────────────────────────────────────────────

/** Byte or line range within a source file. */
export interface SourceLocation {
  file_path: string;
  start_line: number;
  start_column?: number;
  end_line: number;
  end_column?: number;
}

/** Commit-SHA-based temporal range. `until` is undefined for current facts. */
export interface VersionRange {
  since: string;
  until?: string;
}

/** Audit trail entry attached to write operations. */
export interface Provenance {
  agent_session_id?: string;
  git_commit?: string;
  operation: string;
  timestamp: string;
}

/** A reference to a code span used as evidence for expertise entries. */
export interface Citation {
  node_id: string;
  commit_sha?: string;
  snippet?: string;
}

// ─── Node Property Bags ─────────────────────────────────────────────────

export interface RepoProperties {
  root_path: string;
  vcs_url?: string;
  default_branch: string;
}

export interface DirectoryProperties {
  path: string;
  depth: number;
}

export interface PackageProperties {
  name: string;
  version?: string;
  path: string;
  manager?: string;
}

export interface ModuleProperties {
  name?: string;
  language: string;
}

export interface FileProperties {
  path: string;
  language: string;
  hash: string;
  line_count?: number;
}

export interface ClassProperties {
  name: string;
  is_abstract: boolean;
  is_exported: boolean;
  docstring?: string;
  constructor_signature?: string;
}

export interface InterfaceProperties {
  name: string;
  is_exported: boolean;
  docstring?: string;
}

export interface EnumProperties {
  name: string;
  is_exported: boolean;
  members?: string[];
}

export interface TypeAliasProperties {
  name: string;
  is_exported: boolean;
  type_expression?: string;
}

export interface FunctionProperties {
  name: string;
  signature: string;
  is_exported: boolean;
  is_async: boolean;
  complexity?: number;
  docstring?: string;
}

export interface MethodProperties {
  name: string;
  signature: string;
  is_static: boolean;
  is_async: boolean;
  visibility?: 'public' | 'protected' | 'private';
  complexity?: number;
  docstring?: string;
}

export interface VariableProperties {
  name: string;
  is_exported: boolean;
  type_annotation?: string;
  is_const: boolean;
}

export interface SemanticClusterProperties {
  label: string;
  description?: string;
}

export interface TopicProperties {
  title: string;
  summary: string;
  related_terms?: string[];
}

/** Shared shape for all expertise node kinds (pattern, pitfall, preference, insight). */
export interface ExpertiseNodeProperties {
  title: string;
  body: string;
  confidence: number;
  source_agent: string;
  tags?: string[];
  evidence?: Citation[];
}

// ─── Node Types (Discriminated Union on `kind`) ──────────────────────────

export interface NodeBase {
  id: string;
  qualified_name: string;
  layer: GraphLayer[];
  source_location?: SourceLocation;
  content_hash?: string;
  summary?: string;
  version_range: VersionRange;
  last_touched: string;
  embedding?: number[];
  metadata?: Record<string, unknown>;
}

export interface RepoNode extends NodeBase {
  kind: 'repo';
  properties: RepoProperties;
}

export interface DirectoryNode extends NodeBase {
  kind: 'directory';
  properties: DirectoryProperties;
}

export interface PackageNode extends NodeBase {
  kind: 'package';
  properties: PackageProperties;
}

export interface ModuleNode extends NodeBase {
  kind: 'module';
  properties: ModuleProperties;
}

export interface FileNode extends NodeBase {
  kind: 'file';
  properties: FileProperties;
}

export interface ClassNode extends NodeBase {
  kind: 'class';
  properties: ClassProperties;
}

export interface InterfaceNode extends NodeBase {
  kind: 'interface';
  properties: InterfaceProperties;
}

export interface EnumNode extends NodeBase {
  kind: 'enum';
  properties: EnumProperties;
}

export interface TypeAliasNode extends NodeBase {
  kind: 'type_alias';
  properties: TypeAliasProperties;
}

export interface FunctionNode extends NodeBase {
  kind: 'function';
  properties: FunctionProperties;
}

export interface MethodNode extends NodeBase {
  kind: 'method';
  properties: MethodProperties;
}

export interface VariableNode extends NodeBase {
  kind: 'variable';
  properties: VariableProperties;
}

export interface SemanticClusterNode extends NodeBase {
  kind: 'semantic_cluster';
  properties: SemanticClusterProperties;
}

export interface TopicNode extends NodeBase {
  kind: 'topic';
  properties: TopicProperties;
}

export interface PatternNode extends NodeBase {
  kind: 'pattern';
  properties: ExpertiseNodeProperties;
}

export interface PitfallNode extends NodeBase {
  kind: 'pitfall';
  properties: ExpertiseNodeProperties;
}

export interface PreferenceNode extends NodeBase {
  kind: 'preference';
  properties: ExpertiseNodeProperties;
}

export interface InsightNode extends NodeBase {
  kind: 'insight';
  properties: ExpertiseNodeProperties;
}

/** Discriminated union of all node types, keyed on `kind`. */
export type GraphNode =
  | RepoNode
  | DirectoryNode
  | PackageNode
  | ModuleNode
  | FileNode
  | ClassNode
  | InterfaceNode
  | EnumNode
  | TypeAliasNode
  | FunctionNode
  | MethodNode
  | VariableNode
  | SemanticClusterNode
  | TopicNode
  | PatternNode
  | PitfallNode
  | PreferenceNode
  | InsightNode;

// ─── Edge Property Bags ──────────────────────────────────────────────────

export interface ContainsEdgeProperties {
  order?: number;
}

export interface ExportsEdgeProperties {
  alias?: string;
  is_default?: boolean;
}

export interface ImportsEdgeProperties {
  alias?: string;
  is_wildcard: boolean;
  is_type_only: boolean;
}

export interface ReExportsEdgeProperties {
  alias?: string;
  is_wildcard?: boolean;
}

export interface CallsEdgeProperties {
  arg_count?: number;
  is_async?: boolean;
  is_conditional?: boolean;
  call_site?: SourceLocation;
  /** Whether the call target was resolved to a concrete node. */
  resolved?: boolean;
  /** Unresolved target name (e.g. dynamic call or external symbol). */
  target_name?: string;
}

export interface CalledByEdgeProperties {
  arg_count?: number;
  call_site?: SourceLocation;
}

export interface OverridesEdgeProperties {
  is_abstract?: boolean;
}

export interface ImplementsEdgeProperties {
  partial?: boolean;
}

export interface InstantiatesEdgeProperties {
  arg_count?: number;
}

export interface SimilarToEdgeProperties {
  cosine_similarity: number;
}

export interface BelongsToClusterEdgeProperties {
  membership_score?: number;
}

export interface AppliesToEdgeProperties {
  confidence: number;
  scope: 'exact' | 'subtree';
}

export interface SupersedesEdgeProperties {
  reason?: string;
  superseded_at?: string;
}

export interface DerivedFromEdgeProperties {
  derivation_type?: string;
}

export interface ExpertiseEdgeProperties {
  confidence: number;
  kind?: string;
}

export interface FrequentlyUsedWithEdgeProperties {
  cooccurrence_count: number;
  correlation?: number;
}

export interface ReferencesEdgeProperties {
  reference_kind?: string;
}

export interface DependsOnEdgeProperties {
  is_dev_dependency?: boolean;
  version_constraint?: string;
}

export interface InheritsEdgeProperties {
  is_virtual?: boolean;
}

// ─── Edge Types (Discriminated Union on `relation`) ──────────────────────
//
// Edges whose relation-specific property bag contains required fields
// declare `properties` as mandatory.  Others keep it optional so callers
// are not forced to supply an empty object.

export interface EdgeBase {
  id: string;
  source: string;
  target: string;
  layer: GraphLayer;
  weight: number;
  version_range: VersionRange;
  stale: boolean;
  provenance?: Provenance;
}

/* Required-properties edges */

export interface ImportsEdge extends EdgeBase {
  relation: 'imports';
  properties: ImportsEdgeProperties;
}

export interface SimilarToEdge extends EdgeBase {
  relation: 'similar_to';
  properties: SimilarToEdgeProperties;
}

export interface AppliesToEdge extends EdgeBase {
  relation: 'applies_to';
  properties: AppliesToEdgeProperties;
}

export interface ExpertiseEdge extends EdgeBase {
  relation: 'expertise';
  properties: ExpertiseEdgeProperties;
}

export interface FrequentlyUsedWithEdge extends EdgeBase {
  relation: 'frequently_used_with';
  properties: FrequentlyUsedWithEdgeProperties;
}

/* Optional-properties edges */

export interface ContainsEdge extends EdgeBase {
  relation: 'contains';
  properties?: ContainsEdgeProperties;
}

export interface ExportsEdge extends EdgeBase {
  relation: 'exports';
  properties?: ExportsEdgeProperties;
}

export interface ReExportsEdge extends EdgeBase {
  relation: 're_exports';
  properties?: ReExportsEdgeProperties;
}

export interface CallsEdge extends EdgeBase {
  relation: 'calls';
  properties?: CallsEdgeProperties;
}

export interface CalledByEdge extends EdgeBase {
  relation: 'called_by';
  properties?: CalledByEdgeProperties;
}

export interface OverridesEdge extends EdgeBase {
  relation: 'overrides';
  properties?: OverridesEdgeProperties;
}

export interface ImplementsEdge extends EdgeBase {
  relation: 'implements';
  properties?: ImplementsEdgeProperties;
}

export interface InstantiatesEdge extends EdgeBase {
  relation: 'instantiates';
  properties?: InstantiatesEdgeProperties;
}

export interface BelongsToClusterEdge extends EdgeBase {
  relation: 'belongs_to_cluster';
  properties?: BelongsToClusterEdgeProperties;
}

export interface SupersedesEdge extends EdgeBase {
  relation: 'supersedes';
  properties?: SupersedesEdgeProperties;
}

export interface DerivedFromEdge extends EdgeBase {
  relation: 'derived_from';
  properties?: DerivedFromEdgeProperties;
}

export interface ReferencesEdge extends EdgeBase {
  relation: 'references';
  properties?: ReferencesEdgeProperties;
}

export interface DependsOnEdge extends EdgeBase {
  relation: 'depends_on';
  properties?: DependsOnEdgeProperties;
}

export interface InheritsEdge extends EdgeBase {
  relation: 'inherits';
  properties?: InheritsEdgeProperties;
}

/** Discriminated union of all edge types, keyed on `relation`. */
export type GraphEdge =
  | ContainsEdge
  | ExportsEdge
  | ImportsEdge
  | ReExportsEdge
  | CallsEdge
  | CalledByEdge
  | OverridesEdge
  | ImplementsEdge
  | InstantiatesEdge
  | SimilarToEdge
  | BelongsToClusterEdge
  | AppliesToEdge
  | SupersedesEdge
  | DerivedFromEdge
  | ExpertiseEdge
  | FrequentlyUsedWithEdge
  | ReferencesEdge
  | DependsOnEdge
  | InheritsEdge;
