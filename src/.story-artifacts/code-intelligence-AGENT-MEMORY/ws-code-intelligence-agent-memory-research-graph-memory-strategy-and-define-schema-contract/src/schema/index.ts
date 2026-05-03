/**
 * Barrel re-exports for the graph schema contract.
 *
 * All downstream consumers should import from this module:
 *   import { NodeKind, GraphNode, buildNodeId, ... } from '@agent-memory/graph-schema';
 */

// Core types and enums
export {
  SCHEMA_VERSION,
  NodeKind,
  NODE_KINDS,
  SYMBOL_NODE_KINDS,
  EdgeKind,
  EDGE_KINDS,
  STRUCTURAL_EDGE_KINDS,
  CALL_CHAIN_EDGE_KINDS,
  ConfidenceTier,
  AnalysisBackend,
  Staleness,
  Visibility,
} from './types.js';

export type {
  SourceSpan,
  Observation,
  RepositoryProperties,
  PackageProperties,
  DirectoryProperties,
  FileProperties,
  SymbolProperties,
  NodeProperties,
  GraphNode,
  ContainsEdgeProperties,
  CallsEdgeProperties,
  ImportsEdgeProperties,
  ExtendsEdgeProperties,
  ImplementsEdgeProperties,
  ReferencesEdgeProperties,
  DependsOnEdgeProperties,
  DefaultEdgeProperties,
  EdgeProperties,
  GraphEdge,
  ExtractionScope,
  ExtractionResult,
  ExtractionError,
} from './types.js';

// Canonical ID
export {
  encodeSegment,
  decodeSegment,
  repoIdFromUrl,
  buildNodeId,
  parseNodeId,
  buildEdgeId,
  isValidNodeId,
  isValidEdgeId,
  repositoryNodeId,
  fileNodeId,
  directoryNodeId,
  symbolNodeId,
  packageNodeId,
} from './canonical-id.js';

export type { NodeIdComponents } from './canonical-id.js';

// Validation
export {
  validateNode,
  validateEdge,
  validateExtractionResult,
} from './validation.js';

export type { ValidationError, ValidationResult } from './validation.js';

// Upsert semantics
export {
  FieldMergeRule,
  NODE_FIELD_MERGE_RULES,
  EDGE_FIELD_MERGE_RULES,
  UpsertConflictError,
  mergeObservations,
  mergeAllObservations,
  deriveConfidence,
  deriveLastSeenAt,
  upsertNode,
  upsertEdge,
  isNodeInScope,
  isEdgeInScope,
  matchesGlobPattern,
  computeStaleNodeIds,
  computeStaleEdgeIds,
  softDeleteNode,
  softDeleteEdge,
  markNodeStale,
  markEdgeStale,
} from './upsert-semantics.js';
