/**
 * Graph Memory Schema — TypeScript interface definitions.
 *
 * Auto-generated from the canonical C# models and JSON Schema (v0.3.0).
 * These interfaces are the cross-language contract that all downstream
 * workstreams code against.
 *
 * GraphNode and GraphEdge are discriminated unions: the `kind` field
 * narrows the `properties` type for TypeScript consumers.
 *
 * @see json-schema/graph-memory.schema.json  (JSON Schema)
 * @see src/GraphMemory/Models/               (C# canonical models)
 */

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

export type NodeKind =
  | "repo"
  | "package"
  | "module"
  | "directory"
  | "file"
  | "class"
  | "interface"
  | "type_alias"
  | "function"
  | "method"
  | "variable"
  | "enum"
  | "symbol"
  | "semantic_cluster"
  | "topic"
  | "expertise_note"
  | "pattern"
  | "pitfall"
  | "preference"
  | "insight"
  | "workspace";

export type EdgeKind =
  | "contains"
  | "calls"
  | "called_by"
  | "imports"
  | "re_exports"
  | "exports"
  | "inherits"
  | "overrides"
  | "implements"
  | "instantiates"
  | "expertise"
  | "applies_to"
  | "similar_to"
  | "belongs_to_cluster"
  | "supersedes"
  | "derived_from"
  | "frequently_used_with"
  | "depends_on"
  | "references";

export type Layer = "structural" | "call_chain" | "semantic" | "expertise";

export type InheritanceMechanism = "extends" | "implements" | "mixin";

export type SymbolKindValue =
  | "enum"
  | "enum_member"
  | "constant"
  | "namespace"
  | "decorator"
  | "other";

export type Visibility = "public" | "protected" | "private";

export type VariableKindValue = "const" | "let" | "var" | "property";

// ---------------------------------------------------------------------------
// Supporting types
// ---------------------------------------------------------------------------

export interface Provenance {
  analyzer?: string;
  analyzerVersion?: string;
  analyzedAt?: string;
  commitSha?: string;
}

export interface VersionRange {
  introducedIn: string;
  lastSeenIn: string;
  removedIn?: string;
}

export interface SourceLocation {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
}

export interface FunctionParameter {
  name: string;
  typeAnnotation?: string;
  isOptional?: boolean;
  defaultValue?: string;
}

export interface CallSite {
  line: number;
  column?: number;
}

export interface ImportedSymbol {
  name: string;
  alias?: string;
}

export interface ExpertiseEvidence {
  source: string;
  description?: string;
  timestamp?: string;
  confidence?: number;
}

export interface GraphMetadata {
  createdAt: string;
  updatedAt: string;
  createdBy?: string;
  description?: string;
  commitSha?: string;
  repositoryRoot?: string;
  branch?: string;
  /** Extension keys must start with "x-" (e.g. "x-myTool", "x-neo4j"). */
  extensions?: Record<`x-${string}`, unknown>;
}

// ---------------------------------------------------------------------------
// Node property bags — one per NodeKind
// ---------------------------------------------------------------------------

export interface RepoProperties {
  languages: string[];
  url?: string;
  defaultBranch?: string;
}

export interface PackageProperties {
  packageManager: string;
  version?: string;
  registryUrl?: string;
}

export interface ModuleProperties {
  path: string;
  packageName?: string;
}

export interface DirectoryProperties {
  path: string;
}

export interface FileProperties {
  path: string;
  language?: string;
  loc?: number;
  hash?: string;
}

export interface ClassProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  isAbstract?: boolean;
  isExported?: boolean;
}

export interface InterfaceProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  isExported?: boolean;
}

export interface TypeAliasProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  typeExpression?: string;
  isExported?: boolean;
}

export interface FunctionProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  parameters: FunctionParameter[];
  isAsync?: boolean;
  isGenerator?: boolean;
  isExported?: boolean;
  returnType?: string;
}

export interface MethodProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  parameters: FunctionParameter[];
  isAsync?: boolean;
  isStatic?: boolean;
  isExported?: boolean;
  visibility?: Visibility;
  returnType?: string;
}

export interface VariableProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  variableKind: VariableKindValue;
  isExported?: boolean;
  typeAnnotation?: string;
}

export interface EnumProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  isExported?: boolean;
  members?: string[];
}

export interface SymbolProperties {
  filePath: string;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  symbolKind: SymbolKindValue;
  isExported?: boolean;
}

export interface SemanticClusterProperties {
  label?: string;
  centroid?: number[];
  memberCount?: number;
}

export interface TopicProperties {
  description?: string;
  keywords?: string[];
}

export interface ExpertiseNoteProperties {
  content: string;
  author?: string;
  createdAt?: string;
  tags?: string[];
}

export interface PatternProperties {
  content: string;
  confidence?: number;
  tags?: string[];
}

export interface PitfallProperties {
  content: string;
  severity?: "low" | "medium" | "high";
  tags?: string[];
}

export interface PreferenceProperties {
  content: string;
  author?: string;
  tags?: string[];
}

export interface InsightProperties {
  content: string;
  confidence?: number;
  source?: string;
  tags?: string[];
}

export interface WorkspaceProperties {
  rootPath: string;
  repoIds?: string[];
  description?: string;
}

/** Union of all node property types. */
export type NodeProperties =
  | RepoProperties
  | PackageProperties
  | ModuleProperties
  | DirectoryProperties
  | FileProperties
  | ClassProperties
  | InterfaceProperties
  | TypeAliasProperties
  | FunctionProperties
  | MethodProperties
  | VariableProperties
  | EnumProperties
  | SymbolProperties
  | SemanticClusterProperties
  | TopicProperties
  | ExpertiseNoteProperties
  | PatternProperties
  | PitfallProperties
  | PreferenceProperties
  | InsightProperties
  | WorkspaceProperties;

// ---------------------------------------------------------------------------
// Edge property bags — one per EdgeKind
// ---------------------------------------------------------------------------

export interface ContainsProperties {
  order?: number;
}

export interface CallsProperties {
  callSite?: CallSite;
  isConditional?: boolean;
  isDynamic?: boolean;
  isAsync?: boolean;
  confidence?: number;
}

export interface CalledByProperties {
  callSite?: CallSite;
  isConditional?: boolean;
  isDynamic?: boolean;
  isAsync?: boolean;
  confidence?: number;
}

export interface ImportsProperties {
  importedSymbols?: ImportedSymbol[];
  isTypeOnly?: boolean;
  isDefault?: boolean;
  isReExport?: boolean;
}

export interface ReExportsProperties {
  importedSymbols?: ImportedSymbol[];
  isTypeOnly?: boolean;
}

export interface ExportsProperties {
  exportedSymbols?: ImportedSymbol[];
  isDefault?: boolean;
  isTypeOnly?: boolean;
}

export interface InheritsProperties {
  mechanism: InheritanceMechanism;
}

export interface OverridesProperties {
  callsSuper?: boolean;
}

export interface ImplementsProperties {
  isPartial?: boolean;
}

export interface InstantiatesProperties {
  typeArguments?: string[];
}

export interface ExpertiseEdgeProperties {
  weight: number;
  confidence: number;
  insight?: string;
  reinforcementCount?: number;
  relationship?: string;
  lastUpdated?: string;
  observationCount?: number;
  sourceAgent?: string;
  evidence?: ExpertiseEvidence[];
}

export interface AppliesToProperties {
  scope?: string;
}

export interface SimilarToProperties {
  cosine_similarity: number;
  algorithm?: string;
}

export interface BelongsToClusterProperties {
  strength?: number;
}

export interface SupersedesProperties {
  reason?: string;
}

export interface DerivedFromProperties {
  method?: string;
}

export interface FrequentlyUsedWithProperties {
  coOccurrenceCount?: number;
  strength?: number;
  window?: string;
}

export interface DependsOnProperties {
  dependencyType?: string;
  versionConstraint?: string;
  isDev?: boolean;
}

export interface ReferencesProperties {
  referenceKind?: string;
  isWeak?: boolean;
}

/** Union of all edge property types. */
export type EdgeProperties =
  | ContainsProperties
  | CallsProperties
  | CalledByProperties
  | ImportsProperties
  | ReExportsProperties
  | ExportsProperties
  | InheritsProperties
  | OverridesProperties
  | ImplementsProperties
  | InstantiatesProperties
  | ExpertiseEdgeProperties
  | AppliesToProperties
  | SimilarToProperties
  | BelongsToClusterProperties
  | SupersedesProperties
  | DerivedFromProperties
  | FrequentlyUsedWithProperties
  | DependsOnProperties
  | ReferencesProperties;

// ---------------------------------------------------------------------------
// Common optional fields shared by all node/edge variants
// ---------------------------------------------------------------------------

interface GraphNodeCommon {
  id: string;
  name: string;
  qualifiedName?: string;
  repoId?: string;
  provenance?: Provenance;
  version?: number;
  layer?: Layer;
  createdAt?: string;
  updatedAt?: string;
  versionRange?: VersionRange;
  snapshotId?: string;
  stale?: boolean;
  contentHash?: string;
  embedding?: number[];
  /** Extension keys must start with "x-" (e.g. "x-myTool", "x-neo4j"). */
  extensions?: Record<`x-${string}`, unknown>;
}

interface GraphEdgeCommon {
  id: string;
  source: string;
  target: string;
  provenance?: Provenance;
  version?: number;
  layer?: Layer;
  createdAt?: string;
  updatedAt?: string;
  versionRange?: VersionRange;
  snapshotId?: string;
  stale?: boolean;
  sourceRepoId?: string;
  targetRepoId?: string;
  /** Extension keys must start with "x-" (e.g. "x-myTool", "x-neo4j"). */
  extensions?: Record<`x-${string}`, unknown>;
}

// ---------------------------------------------------------------------------
// Discriminated union: GraphNode — kind narrows properties
// ---------------------------------------------------------------------------

export type RepoNode = GraphNodeCommon & { kind: "repo"; properties: RepoProperties };
export type PackageNode = GraphNodeCommon & { kind: "package"; properties: PackageProperties };
export type ModuleNode = GraphNodeCommon & { kind: "module"; properties: ModuleProperties };
export type DirectoryNode = GraphNodeCommon & { kind: "directory"; properties: DirectoryProperties };
export type FileNode = GraphNodeCommon & { kind: "file"; properties: FileProperties };
export type ClassNode = GraphNodeCommon & { kind: "class"; properties: ClassProperties };
export type InterfaceNode = GraphNodeCommon & { kind: "interface"; properties: InterfaceProperties };
export type TypeAliasNode = GraphNodeCommon & { kind: "type_alias"; properties: TypeAliasProperties };
export type FunctionNode = GraphNodeCommon & { kind: "function"; properties: FunctionProperties };
export type MethodNode = GraphNodeCommon & { kind: "method"; properties: MethodProperties };
export type VariableNode = GraphNodeCommon & { kind: "variable"; properties: VariableProperties };
export type EnumNode = GraphNodeCommon & { kind: "enum"; properties: EnumProperties };
export type SymbolNode = GraphNodeCommon & { kind: "symbol"; properties: SymbolProperties };
export type SemanticClusterNode = GraphNodeCommon & { kind: "semantic_cluster"; properties: SemanticClusterProperties };
export type TopicNode = GraphNodeCommon & { kind: "topic"; properties: TopicProperties };
export type ExpertiseNoteNode = GraphNodeCommon & { kind: "expertise_note"; properties: ExpertiseNoteProperties };
export type PatternNode = GraphNodeCommon & { kind: "pattern"; properties: PatternProperties };
export type PitfallNode = GraphNodeCommon & { kind: "pitfall"; properties: PitfallProperties };
export type PreferenceNode = GraphNodeCommon & { kind: "preference"; properties: PreferenceProperties };
export type InsightNode = GraphNodeCommon & { kind: "insight"; properties: InsightProperties };
export type WorkspaceNode = GraphNodeCommon & { kind: "workspace"; properties: WorkspaceProperties };

/**
 * Discriminated union of all graph node types.
 * Use `node.kind` to narrow `node.properties` to the correct type.
 *
 * @example
 * ```ts
 * function handle(node: GraphNode) {
 *   if (node.kind === "file") {
 *     console.log(node.properties.path); // FileProperties
 *   }
 * }
 * ```
 */
export type GraphNode =
  | RepoNode
  | PackageNode
  | ModuleNode
  | DirectoryNode
  | FileNode
  | ClassNode
  | InterfaceNode
  | TypeAliasNode
  | FunctionNode
  | MethodNode
  | VariableNode
  | EnumNode
  | SymbolNode
  | SemanticClusterNode
  | TopicNode
  | ExpertiseNoteNode
  | PatternNode
  | PitfallNode
  | PreferenceNode
  | InsightNode
  | WorkspaceNode;

// ---------------------------------------------------------------------------
// Discriminated union: GraphEdge — kind narrows properties
// ---------------------------------------------------------------------------

export type ContainsEdge = GraphEdgeCommon & { kind: "contains"; properties: ContainsProperties };
export type CallsEdge = GraphEdgeCommon & { kind: "calls"; properties: CallsProperties };
export type CalledByEdge = GraphEdgeCommon & { kind: "called_by"; properties: CalledByProperties };
export type ImportsEdge = GraphEdgeCommon & { kind: "imports"; properties: ImportsProperties };
export type ReExportsEdge = GraphEdgeCommon & { kind: "re_exports"; properties: ReExportsProperties };
export type ExportsEdge = GraphEdgeCommon & { kind: "exports"; properties: ExportsProperties };
export type InheritsEdge = GraphEdgeCommon & { kind: "inherits"; properties: InheritsProperties };
export type OverridesEdge = GraphEdgeCommon & { kind: "overrides"; properties: OverridesProperties };
export type ImplementsEdge = GraphEdgeCommon & { kind: "implements"; properties: ImplementsProperties };
export type InstantiatesEdge = GraphEdgeCommon & { kind: "instantiates"; properties: InstantiatesProperties };
export type ExpertiseEdge = GraphEdgeCommon & { kind: "expertise"; properties: ExpertiseEdgeProperties };
export type AppliesToEdge = GraphEdgeCommon & { kind: "applies_to"; properties: AppliesToProperties };
export type SimilarToEdge = GraphEdgeCommon & { kind: "similar_to"; properties: SimilarToProperties };
export type BelongsToClusterEdge = GraphEdgeCommon & { kind: "belongs_to_cluster"; properties: BelongsToClusterProperties };
export type SupersedesEdge = GraphEdgeCommon & { kind: "supersedes"; properties: SupersedesProperties };
export type DerivedFromEdge = GraphEdgeCommon & { kind: "derived_from"; properties: DerivedFromProperties };
export type FrequentlyUsedWithEdge = GraphEdgeCommon & { kind: "frequently_used_with"; properties: FrequentlyUsedWithProperties };
export type DependsOnEdge = GraphEdgeCommon & { kind: "depends_on"; properties: DependsOnProperties };
export type ReferencesEdge = GraphEdgeCommon & { kind: "references"; properties: ReferencesProperties };

/**
 * Discriminated union of all graph edge types.
 * Use `edge.kind` to narrow `edge.properties` to the correct type.
 *
 * @example
 * ```ts
 * function handle(edge: GraphEdge) {
 *   if (edge.kind === "calls") {
 *     console.log(edge.properties.callSite); // CallsProperties
 *   }
 * }
 * ```
 */
export type GraphEdge =
  | ContainsEdge
  | CallsEdge
  | CalledByEdge
  | ImportsEdge
  | ReExportsEdge
  | ExportsEdge
  | InheritsEdge
  | OverridesEdge
  | ImplementsEdge
  | InstantiatesEdge
  | ExpertiseEdge
  | AppliesToEdge
  | SimilarToEdge
  | BelongsToClusterEdge
  | SupersedesEdge
  | DerivedFromEdge
  | FrequentlyUsedWithEdge
  | DependsOnEdge
  | ReferencesEdge;

// ---------------------------------------------------------------------------
// Root graph container
// ---------------------------------------------------------------------------

export interface CodeGraph {
  schemaVersion: "0.3.0";
  id: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
  metadata: GraphMetadata;
}

// ---------------------------------------------------------------------------
// Graph store interface (mirrors C# IGraphStore)
// ---------------------------------------------------------------------------

export type TraversalDirection = "outgoing" | "incoming" | "both";

export interface TraversalOptions {
  startNodeId: string;
  edgeKindFilter?: EdgeKind;
  maxDepth?: number;
  direction?: TraversalDirection;
}

export interface TraversalPath {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export interface VectorSearchOptions {
  queryEmbedding: number[];
  topK?: number;
  kindFilter?: NodeKind;
  minSimilarity?: number;
}

export interface VectorSearchResult {
  node: GraphNode;
  similarity: number;
}

export interface GraphDiff {
  addNodes?: GraphNode[];
  updateNodes?: GraphNode[];
  removeNodeIds?: string[];
  addEdges?: GraphEdge[];
  updateEdges?: GraphEdge[];
  removeEdgeIds?: string[];
}

export interface DiffResult {
  nodesAdded: number;
  nodesUpdated: number;
  nodesRemoved: number;
  edgesAdded: number;
  edgesUpdated: number;
  edgesRemoved: number;
}

export interface GraphSnapshot {
  snapshotId: string;
  createdAt: string;
  commitSha?: string;
  description?: string;
  nodeCount: number;
  edgeCount: number;
}

export interface IGraphStore {
  addNode(node: GraphNode): Promise<void>;
  getNode(id: string): Promise<GraphNode | null>;
  updateNode(node: GraphNode): Promise<void>;
  removeNode(id: string): Promise<void>;
  getNodesByKind(kind: NodeKind): Promise<GraphNode[]>;
  findByName(name: string, kind?: NodeKind): Promise<GraphNode[]>;
  getNodeAtCommit(id: string, commitSha: string): Promise<GraphNode | null>;

  addEdge(edge: GraphEdge): Promise<void>;
  getEdge(id: string): Promise<GraphEdge | null>;
  updateEdge(edge: GraphEdge): Promise<void>;
  removeEdge(id: string): Promise<void>;

  getOutgoingEdges(nodeId: string, kind?: EdgeKind): Promise<GraphEdge[]>;
  getIncomingEdges(nodeId: string, kind?: EdgeKind): Promise<GraphEdge[]>;
  getNeighbours(nodeId: string, edgeKind?: EdgeKind): Promise<GraphNode[]>;
  traverse(options: TraversalOptions): Promise<TraversalPath[]>;

  vectorSearch(options: VectorSearchOptions): Promise<VectorSearchResult[]>;

  applyDiff(diff: GraphDiff): Promise<DiffResult>;

  snapshot(description?: string): Promise<GraphSnapshot>;
  restoreSnapshot(snapshotId: string): Promise<void>;
  listSnapshots(): Promise<GraphSnapshot[]>;

  export(): Promise<CodeGraph>;
  import(graph: CodeGraph): Promise<void>;
  clear(): Promise<void>;
}

// ---------------------------------------------------------------------------
// Validation types
// ---------------------------------------------------------------------------

export interface ValidationError {
  code: string;
  message: string;
  elementId?: string;
}

export interface ValidationResult {
  isValid: boolean;
  errors: ValidationError[];
  warnings: string[];
}

export interface IGraphValidator {
  validate(graph: CodeGraph): ValidationResult;
}
