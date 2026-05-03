/**
 * @module @agent-memory/graph-schema
 *
 * Canonical graph schema contract for the Agent Memory knowledge graph.
 *
 * This module defines the node types, edge types, canonical ID format,
 * provenance metadata, and upsert/delete semantics consumed by all
 * downstream workstreams (extractors, storage, retrieval).
 *
 * Schema version: 1.0.0
 */

// ─── Schema version ──────────────────────────────────────────────

/** Semver of this schema contract. Bumped on breaking changes. */
export const SCHEMA_VERSION = '1.0.0' as const;

// ─── Node kinds ──────────────────────────────────────────────────

/**
 * Discriminated set of node kinds in the knowledge graph.
 * These represent the structural and semantic entities extracted from
 * a repository's source code and configuration.
 */
export const NodeKind = {
  Repository: 'repository',
  Package: 'package',
  Directory: 'directory',
  File: 'file',
  Class: 'class',
  Function: 'function',
  Method: 'method',
  Interface: 'interface',
  TypeAlias: 'type_alias',
  Enum: 'enum',
  Variable: 'variable',
  Constant: 'constant',
  Namespace: 'namespace',
} as const;

export type NodeKind = (typeof NodeKind)[keyof typeof NodeKind];

/** All valid node kind values. */
export const NODE_KINDS: readonly NodeKind[] = Object.values(NodeKind);

/**
 * Symbol-bearing node kinds — these represent code symbols that have
 * qualified names, signatures, and source spans.
 */
export const SYMBOL_NODE_KINDS: readonly NodeKind[] = [
  NodeKind.Class,
  NodeKind.Function,
  NodeKind.Method,
  NodeKind.Interface,
  NodeKind.TypeAlias,
  NodeKind.Enum,
  NodeKind.Variable,
  NodeKind.Constant,
  NodeKind.Namespace,
] as const;

// ─── Edge kinds ──────────────────────────────────────────────────

/**
 * Discriminated set of edge kinds. Structural edges represent
 * containment/module hierarchy; call-chain edges represent code
 * relationships resolved via static analysis.
 */
export const EdgeKind = {
  // Structural edges
  Contains: 'contains',
  Imports: 'imports',
  Exports: 'exports',
  ReExports: 're_exports',
  DependsOn: 'depends_on',

  // Call-chain edges
  Calls: 'calls',
  Extends: 'extends',
  Implements: 'implements',
  References: 'references',
  TypeRef: 'type_ref',
  Instantiates: 'instantiates',
} as const;

export type EdgeKind = (typeof EdgeKind)[keyof typeof EdgeKind];

/** All valid edge kind values. */
export const EDGE_KINDS: readonly EdgeKind[] = Object.values(EdgeKind);

/** Structural edges — derived from file system and module hierarchy. */
export const STRUCTURAL_EDGE_KINDS: readonly EdgeKind[] = [
  EdgeKind.Contains,
  EdgeKind.Imports,
  EdgeKind.Exports,
  EdgeKind.ReExports,
  EdgeKind.DependsOn,
] as const;

/** Call-chain edges — derived from static analysis of source code. */
export const CALL_CHAIN_EDGE_KINDS: readonly EdgeKind[] = [
  EdgeKind.Calls,
  EdgeKind.Extends,
  EdgeKind.Implements,
  EdgeKind.References,
  EdgeKind.TypeRef,
  EdgeKind.Instantiates,
] as const;

// ─── Confidence & analysis ───────────────────────────────────────

/**
 * Confidence tier for edges derived from static analysis.
 * - exact: fully resolved by compiler/LSP
 * - heuristic: resolved by AST name matching or type inference
 * - unresolved: reference found but target could not be resolved
 */
export const ConfidenceTier = {
  Exact: 'exact',
  Heuristic: 'heuristic',
  Unresolved: 'unresolved',
} as const;

export type ConfidenceTier = (typeof ConfidenceTier)[keyof typeof ConfidenceTier];

/**
 * Backend used for analysis that produced an observation.
 */
export const AnalysisBackend = {
  Lsp: 'lsp',
  TreeSitter: 'tree_sitter',
  Heuristic: 'heuristic',
  AstParse: 'ast_parse',
  ManifestParse: 'manifest_parse',
} as const;

export type AnalysisBackend = (typeof AnalysisBackend)[keyof typeof AnalysisBackend];

// ─── Staleness ───────────────────────────────────────────────────

/**
 * Lifecycle state of a node or edge.
 * - current: confirmed present in the latest extraction for its scope
 * - stale: not seen in the latest extraction; may still be valid
 * - deleted: explicitly removed; excluded from retrieval
 */
export const Staleness = {
  Current: 'current',
  Stale: 'stale',
  Deleted: 'deleted',
} as const;

export type Staleness = (typeof Staleness)[keyof typeof Staleness];

// ─── Visibility ──────────────────────────────────────────────────

export const Visibility = {
  Public: 'public',
  Private: 'private',
  Protected: 'protected',
  Internal: 'internal',
  Default: 'default',
} as const;

export type Visibility = (typeof Visibility)[keyof typeof Visibility];

// ─── Source location ─────────────────────────────────────────────

/**
 * Precise source location within a file. Used for both nodes
 * (symbol spans) and edges (reference sites).
 */
export interface SourceSpan {
  readonly startLine: number;
  readonly startColumn: number;
  readonly endLine: number;
  readonly endColumn: number;
}

// ─── Observation (multi-extractor provenance) ────────────────────

/**
 * A single observation of a node or edge by an extractor.
 *
 * Multiple extractors may observe the same entity (e.g., AST parse
 * discovers a function, LSP confirms its call targets). Each
 * observation is preserved so that confidence can be aggregated
 * and provenance traced.
 */
export interface Observation {
  /** The extractor that produced this observation. */
  readonly extractorName: string;
  /** Semver of the extractor. */
  readonly extractorVersion: string;
  /** Analysis backend used. */
  readonly analysisBackend: AnalysisBackend;
  /** Confidence tier of this specific observation. */
  readonly confidenceTier: ConfidenceTier;
  /** Numeric confidence score, 0.0–1.0. */
  readonly confidence: number;
  /** Git commit SHA at extraction time. */
  readonly commitSha: string;
  /** ISO 8601 timestamp of this observation. */
  readonly timestamp: string;
  /** Source file that was parsed to produce this observation. */
  readonly sourceFile: string;
  /** Location in the source file, if applicable. */
  readonly sourceSpan?: SourceSpan;
}

// ─── Node properties by kind ─────────────────────────────────────

export interface RepositoryProperties {
  readonly name: string;
  readonly url?: string;
  readonly defaultBranch?: string;
}

export interface PackageProperties {
  readonly name: string;
  readonly path: string;
  readonly language?: string;
  readonly version?: string;
}

export interface DirectoryProperties {
  readonly name: string;
  readonly path: string;
}

export interface FileProperties {
  readonly name: string;
  readonly path: string;
  readonly language?: string;
  readonly lineCount?: number;
  readonly checksum?: string;
}

export interface SymbolProperties {
  readonly name: string;
  readonly qualifiedName: string;
  readonly signature?: string;
  readonly visibility?: Visibility;
  readonly docComment?: string;
  readonly isAsync?: boolean;
  readonly isStatic?: boolean;
  readonly isAbstract?: boolean;
  readonly language?: string;
}

/**
 * Union of all node-specific property bags.
 * Discriminated by the node's `kind` field.
 */
export type NodeProperties =
  | RepositoryProperties
  | PackageProperties
  | DirectoryProperties
  | FileProperties
  | SymbolProperties;

// ─── GraphNode ───────────────────────────────────────────────────

/**
 * A node in the agent memory knowledge graph.
 *
 * Canonical identity is the `id` field, generated by the canonical
 * ID module. Nodes carry a bag of kind-specific properties and an
 * array of observations from extractors.
 */
export interface GraphNode {
  /** Canonical ID (see canonical-id module for grammar). */
  readonly id: string;
  /** Discriminator for the node kind. */
  readonly kind: NodeKind;
  /** Kind-specific properties. */
  readonly properties: NodeProperties;
  /** Source location of the primary definition. */
  readonly sourceSpan?: SourceSpan;
  /** Relative file path within the repository. */
  readonly filePath?: string;
  /** Lifecycle state. */
  readonly staleness: Staleness;
  /** Provenance: all observations that confirm this node's existence. */
  readonly observations: readonly Observation[];
  /** ISO 8601 timestamp when this node was first observed. */
  readonly createdAt: string;
  /** ISO 8601 timestamp of the most recent observation. */
  readonly lastSeenAt: string;
}

// ─── Edge properties by kind ─────────────────────────────────────

export interface ContainsEdgeProperties {
  readonly ordinal?: number;
}

export interface CallsEdgeProperties {
  readonly isDynamic?: boolean;
}

export interface ImportsEdgeProperties {
  readonly alias?: string;
  readonly isReExport?: boolean;
  readonly importPath?: string;
}

export interface ExtendsEdgeProperties {
  readonly isMultiple?: boolean;
}

export interface ImplementsEdgeProperties {}

export interface ReferencesEdgeProperties {
  readonly accessKind?: 'read' | 'write';
}

export interface DependsOnEdgeProperties {
  readonly versionSpec?: string;
}

export interface DefaultEdgeProperties {}

/**
 * Union of all edge-specific property bags.
 */
export type EdgeProperties =
  | ContainsEdgeProperties
  | CallsEdgeProperties
  | ImportsEdgeProperties
  | ExtendsEdgeProperties
  | ImplementsEdgeProperties
  | ReferencesEdgeProperties
  | DependsOnEdgeProperties
  | DefaultEdgeProperties;

// ─── GraphEdge ───────────────────────────────────────────────────

/**
 * An edge in the agent memory knowledge graph.
 *
 * Edge identity is deterministic: derived from (sourceId, targetId, kind).
 * This ensures idempotent upserts — the same relationship always maps
 * to the same edge ID.
 */
export interface GraphEdge {
  /** Deterministic ID derived from (sourceId, targetId, kind). */
  readonly id: string;
  /** Canonical ID of the source node. */
  readonly sourceId: string;
  /** Canonical ID of the target node. */
  readonly targetId: string;
  /** Discriminator for the edge kind. */
  readonly kind: EdgeKind;
  /** Kind-specific properties. */
  readonly properties: EdgeProperties;
  /** Source location of the reference site (e.g., call site). */
  readonly sourceSpan?: SourceSpan;
  /** Lifecycle state. */
  readonly staleness: Staleness;
  /** Provenance: all observations confirming this edge. */
  readonly observations: readonly Observation[];
  /** ISO 8601 timestamp when this edge was first observed. */
  readonly createdAt: string;
  /** ISO 8601 timestamp of the most recent observation. */
  readonly lastSeenAt: string;
}

// ─── Extraction scope & result ───────────────────────────────────

/**
 * Defines the scope of an extraction run. Deletion of stale entities
 * is only valid within the declared scope of a completed extraction.
 *
 * For example, a repo-structure extractor declares its scope as all
 * files matching certain glob patterns. Only nodes owned by that
 * extractor within those file paths can be marked stale/deleted
 * after extraction completes.
 */
export interface ExtractionScope {
  /** Extractor that owns this scope. */
  readonly extractorName: string;
  /** Glob patterns of files included in this extraction. */
  readonly includedPaths: readonly string[];
  /** Glob patterns of files excluded from this extraction. */
  readonly excludedPaths?: readonly string[];
  /** Node kinds that this extractor is authoritative for. */
  readonly ownedNodeKinds: readonly NodeKind[];
  /** Edge kinds that this extractor is authoritative for. */
  readonly ownedEdgeKinds: readonly EdgeKind[];
}

/**
 * The output format of every extractor. This is the API contract
 * between extractors and the ingestion pipeline.
 *
 * Extractors emit typed triples (nodes + edges) with full provenance.
 * The ingestion pipeline consumes these results and applies upsert
 * semantics to the persistent graph store.
 */
export interface ExtractionResult {
  /** Schema version this result conforms to. */
  readonly schemaVersion: typeof SCHEMA_VERSION;
  /** Unique identifier for this extraction run. */
  readonly extractionId: string;
  /** Scope declaration: what this extraction covers. */
  readonly scope: ExtractionScope;
  /** Git commit SHA at extraction time. */
  readonly commitSha: string;
  /** ISO 8601 timestamp of extraction start. */
  readonly startedAt: string;
  /** ISO 8601 timestamp of extraction completion. */
  readonly completedAt: string;
  /** Source language(s) processed. */
  readonly languages: readonly string[];
  /** Nodes discovered/confirmed by this extraction. */
  readonly nodes: readonly GraphNode[];
  /** Edges discovered/confirmed by this extraction. */
  readonly edges: readonly GraphEdge[];
  /** Files that were actually processed (for scope verification). */
  readonly processedFiles: readonly string[];
  /** Errors encountered during extraction (non-fatal). */
  readonly errors?: readonly ExtractionError[];
}

export interface ExtractionError {
  readonly filePath: string;
  readonly message: string;
  readonly severity: 'warning' | 'error';
  readonly line?: number;
  readonly column?: number;
}
