/**
 * Runtime validation for graph schema entities.
 *
 * Validates that nodes, edges, and extraction results conform to the
 * schema contract. Used by the ingestion pipeline to reject malformed
 * extractor output before it reaches the graph store.
 */

import {
  type GraphNode,
  type GraphEdge,
  type ExtractionResult,
  type Observation,
  type SourceSpan,
  NODE_KINDS,
  EDGE_KINDS,
  SCHEMA_VERSION,
  Staleness,
  NodeKind,
  AnalysisBackend,
  ConfidenceTier,
  SYMBOL_NODE_KINDS,
  type SymbolProperties,
  type PackageProperties,
  type DirectoryProperties,
  type FileProperties,
} from './types.js';
import { isValidNodeId, isValidEdgeId, parseNodeId, buildEdgeId } from './canonical-id.js';

// ─── Validation result ───────────────────────────────────────────

export interface ValidationError {
  readonly path: string;
  readonly message: string;
}

export interface ValidationResult {
  readonly valid: boolean;
  readonly errors: readonly ValidationError[];
}

function ok(): ValidationResult {
  return { valid: true, errors: [] };
}

function fail(errors: ValidationError[]): ValidationResult {
  return { valid: false, errors };
}

function merge(results: ValidationResult[]): ValidationResult {
  const errors = results.flatMap((r) => r.errors);
  return { valid: errors.length === 0, errors };
}

// ─── Source span validation ──────────────────────────────────────

function validateSourceSpan(span: SourceSpan, path: string): ValidationResult {
  const errors: ValidationError[] = [];
  if (span.startLine < 0) errors.push({ path: `${path}.startLine`, message: 'must be >= 0' });
  if (span.startColumn < 0) errors.push({ path: `${path}.startColumn`, message: 'must be >= 0' });
  if (span.endLine < span.startLine) {
    errors.push({ path: `${path}.endLine`, message: 'must be >= startLine' });
  }
  if (span.endLine === span.startLine && span.endColumn < span.startColumn) {
    errors.push({ path: `${path}.endColumn`, message: 'must be >= startColumn when on same line' });
  }
  return errors.length > 0 ? fail(errors) : ok();
}

// ─── Observation validation ──────────────────────────────────────

const VALID_ANALYSIS_BACKENDS: readonly string[] = Object.values(AnalysisBackend);
const VALID_CONFIDENCE_TIERS: readonly string[] = Object.values(ConfidenceTier);

function validateObservation(obs: Observation, path: string): ValidationResult {
  const errors: ValidationError[] = [];

  if (!obs.extractorName || obs.extractorName.trim().length === 0) {
    errors.push({ path: `${path}.extractorName`, message: 'must be non-empty' });
  }
  if (!obs.extractorVersion || !/^\d+\.\d+\.\d+/.test(obs.extractorVersion)) {
    errors.push({ path: `${path}.extractorVersion`, message: 'must be semver format (x.y.z)' });
  }
  if (!VALID_ANALYSIS_BACKENDS.includes(obs.analysisBackend)) {
    errors.push({ path: `${path}.analysisBackend`, message: `unknown analysis backend: "${obs.analysisBackend}"` });
  }
  if (!VALID_CONFIDENCE_TIERS.includes(obs.confidenceTier)) {
    errors.push({ path: `${path}.confidenceTier`, message: `unknown confidence tier: "${obs.confidenceTier}"` });
  }
  if (obs.confidence < 0 || obs.confidence > 1) {
    errors.push({ path: `${path}.confidence`, message: 'must be between 0.0 and 1.0' });
  }
  if (!obs.commitSha || obs.commitSha.trim().length === 0) {
    errors.push({ path: `${path}.commitSha`, message: 'must be non-empty' });
  }
  if (!obs.timestamp || isNaN(Date.parse(obs.timestamp))) {
    errors.push({ path: `${path}.timestamp`, message: 'must be a valid ISO 8601 timestamp' });
  }
  if (!obs.sourceFile || obs.sourceFile.trim().length === 0) {
    errors.push({ path: `${path}.sourceFile`, message: 'must be non-empty' });
  }

  if (obs.sourceSpan) {
    errors.push(...validateSourceSpan(obs.sourceSpan, `${path}.sourceSpan`).errors);
  }

  return errors.length > 0 ? fail(errors) : ok();
}

// ─── Node validation ─────────────────────────────────────────────

/** Structural node kinds that represent file-system containers with paths. */
const PATH_BEARING_KINDS: readonly NodeKind[] = [
  NodeKind.Package,
  NodeKind.Directory,
  NodeKind.File,
];

/** Node kinds that are backed by a file and must carry filePath. */
const FILE_BACKED_KINDS: readonly NodeKind[] = [
  NodeKind.File,
  ...SYMBOL_NODE_KINDS,
];

/**
 * Validate that properties have the correct required fields for a given kind.
 */
function validateNodeProperties(kind: NodeKind, properties: Record<string, unknown>, errors: ValidationError[]): void {
  // All nodes require name
  if (!properties['name'] || (typeof properties['name'] === 'string' && properties['name'].trim().length === 0)) {
    errors.push({ path: 'properties.name', message: 'node properties must include name' });
  }

  // Path-bearing structural nodes require path
  if (PATH_BEARING_KINDS.includes(kind)) {
    const path = properties['path'];
    if (!path || (typeof path === 'string' && path.trim().length === 0)) {
      errors.push({ path: 'properties.path', message: `${kind} nodes must have a non-empty path` });
    }
  }

  // Symbol nodes require qualifiedName
  if (SYMBOL_NODE_KINDS.includes(kind)) {
    const props = properties as Record<string, unknown>;
    if (!props['qualifiedName'] || (typeof props['qualifiedName'] === 'string' && (props['qualifiedName'] as string).trim().length === 0)) {
      errors.push({ path: 'properties.qualifiedName', message: 'symbol nodes must have qualifiedName' });
    }
    if (!props['name'] || (typeof props['name'] === 'string' && (props['name'] as string).trim().length === 0)) {
      errors.push({ path: 'properties.name', message: 'symbol nodes must have name' });
    }
  }
}

export function validateNode(node: GraphNode): ValidationResult {
  const errors: ValidationError[] = [];

  // ID format
  if (!isValidNodeId(node.id)) {
    errors.push({ path: 'id', message: `invalid canonical node ID: "${node.id}"` });
  } else {
    // Verify the kind encoded in the ID matches the node's kind field
    const parsed = parseNodeId(node.id);
    if (parsed && parsed.kind !== node.kind) {
      errors.push({
        path: 'id',
        message: `node ID kind "${parsed.kind}" does not match node.kind "${node.kind}"`,
      });
    }
  }

  // Kind
  if (!NODE_KINDS.includes(node.kind)) {
    errors.push({ path: 'kind', message: `unknown node kind: "${node.kind}"` });
  }

  // Staleness
  if (!Object.values(Staleness).includes(node.staleness)) {
    errors.push({ path: 'staleness', message: `invalid staleness: "${node.staleness}"` });
  }

  // Per-kind property validation
  validateNodeProperties(node.kind, node.properties as unknown as Record<string, unknown>, errors);

  // File-backed nodes must have filePath for scope computation
  if (FILE_BACKED_KINDS.includes(node.kind) && !node.filePath) {
    errors.push({ path: 'filePath', message: `${node.kind} nodes must have filePath` });
  }

  // Source span
  if (node.sourceSpan) {
    errors.push(...validateSourceSpan(node.sourceSpan, 'sourceSpan').errors);
  }

  // Observations (must have at least one for non-deleted nodes)
  if (node.staleness !== Staleness.Deleted && node.observations.length === 0) {
    errors.push({ path: 'observations', message: 'non-deleted nodes must have at least one observation' });
  }

  for (let i = 0; i < node.observations.length; i++) {
    errors.push(...validateObservation(node.observations[i]!, `observations[${i}]`).errors);
  }

  // Timestamps
  if (!node.createdAt || isNaN(Date.parse(node.createdAt))) {
    errors.push({ path: 'createdAt', message: 'must be a valid ISO 8601 timestamp' });
  }
  if (!node.lastSeenAt || isNaN(Date.parse(node.lastSeenAt))) {
    errors.push({ path: 'lastSeenAt', message: 'must be a valid ISO 8601 timestamp' });
  }

  return errors.length > 0 ? fail(errors) : ok();
}

// ─── Edge validation ─────────────────────────────────────────────

export function validateEdge(edge: GraphEdge): ValidationResult {
  const errors: ValidationError[] = [];

  // ID format
  if (!isValidEdgeId(edge.id)) {
    errors.push({ path: 'id', message: `invalid edge ID: "${edge.id}"` });
  }

  // Source and target
  const sourceValid = isValidNodeId(edge.sourceId);
  const targetValid = isValidNodeId(edge.targetId);
  if (!sourceValid) {
    errors.push({ path: 'sourceId', message: `invalid source node ID: "${edge.sourceId}"` });
  }
  if (!targetValid) {
    errors.push({ path: 'targetId', message: `invalid target node ID: "${edge.targetId}"` });
  }

  // Kind
  const kindValid = EDGE_KINDS.includes(edge.kind);
  if (!kindValid) {
    errors.push({ path: 'kind', message: `unknown edge kind: "${edge.kind}"` });
  }

  // Verify edge ID is correctly derived from its components.
  // Only check when all constituent fields are individually valid,
  // to avoid duplicate noise from already-reported field errors.
  if (isValidEdgeId(edge.id) && sourceValid && targetValid && kindValid) {
    const expectedId = buildEdgeId(edge.sourceId, edge.targetId, edge.kind, edge.sourceSpan);
    if (edge.id !== expectedId) {
      errors.push({
        path: 'id',
        message: `edge ID "${edge.id}" does not match recomputed ID "${expectedId}" from (sourceId, targetId, kind, sourceSpan)`,
      });
    }
  }

  // Staleness
  if (!Object.values(Staleness).includes(edge.staleness)) {
    errors.push({ path: 'staleness', message: `invalid staleness: "${edge.staleness}"` });
  }

  // Source span
  if (edge.sourceSpan) {
    errors.push(...validateSourceSpan(edge.sourceSpan, 'sourceSpan').errors);
  }

  // Observations
  if (edge.staleness !== Staleness.Deleted && edge.observations.length === 0) {
    errors.push({ path: 'observations', message: 'non-deleted edges must have at least one observation' });
  }

  for (let i = 0; i < edge.observations.length; i++) {
    errors.push(...validateObservation(edge.observations[i]!, `observations[${i}]`).errors);
  }

  // Timestamps
  if (!edge.createdAt || isNaN(Date.parse(edge.createdAt))) {
    errors.push({ path: 'createdAt', message: 'must be a valid ISO 8601 timestamp' });
  }
  if (!edge.lastSeenAt || isNaN(Date.parse(edge.lastSeenAt))) {
    errors.push({ path: 'lastSeenAt', message: 'must be a valid ISO 8601 timestamp' });
  }

  return errors.length > 0 ? fail(errors) : ok();
}

// ─── Extraction result validation ────────────────────────────────

export function validateExtractionResult(result: ExtractionResult): ValidationResult {
  const errors: ValidationError[] = [];

  // Schema version
  if (result.schemaVersion !== SCHEMA_VERSION) {
    errors.push({
      path: 'schemaVersion',
      message: `expected "${SCHEMA_VERSION}", got "${result.schemaVersion}"`,
    });
  }

  // Extraction ID
  if (!result.extractionId || result.extractionId.trim().length === 0) {
    errors.push({ path: 'extractionId', message: 'must be non-empty' });
  }

  // Scope
  if (!result.scope.extractorName || result.scope.extractorName.trim().length === 0) {
    errors.push({ path: 'scope.extractorName', message: 'must be non-empty' });
  }
  if (result.scope.includedPaths.length === 0) {
    errors.push({ path: 'scope.includedPaths', message: 'must include at least one path pattern' });
  }

  // Commit SHA
  if (!result.commitSha || result.commitSha.trim().length === 0) {
    errors.push({ path: 'commitSha', message: 'must be non-empty' });
  }

  // Timestamps
  if (!result.startedAt || isNaN(Date.parse(result.startedAt))) {
    errors.push({ path: 'startedAt', message: 'must be a valid ISO 8601 timestamp' });
  }
  if (!result.completedAt || isNaN(Date.parse(result.completedAt))) {
    errors.push({ path: 'completedAt', message: 'must be a valid ISO 8601 timestamp' });
  }

  // Validate individual nodes
  for (let i = 0; i < result.nodes.length; i++) {
    const nodeResult = validateNode(result.nodes[i]!);
    for (const error of nodeResult.errors) {
      errors.push({ path: `nodes[${i}].${error.path}`, message: error.message });
    }
  }

  // Validate individual edges
  for (let i = 0; i < result.edges.length; i++) {
    const edgeResult = validateEdge(result.edges[i]!);
    for (const error of edgeResult.errors) {
      errors.push({ path: `edges[${i}].${error.path}`, message: error.message });
    }
  }

  // Verify edge source/target references exist in the node set
  const nodeIds = new Set(result.nodes.map((n) => n.id));
  for (let i = 0; i < result.edges.length; i++) {
    const edge = result.edges[i]!;
    if (!nodeIds.has(edge.sourceId)) {
      errors.push({
        path: `edges[${i}].sourceId`,
        message: `references node "${edge.sourceId}" not present in this extraction result`,
      });
    }
    if (!nodeIds.has(edge.targetId)) {
      errors.push({
        path: `edges[${i}].targetId`,
        message: `references node "${edge.targetId}" not present in this extraction result`,
      });
    }
  }

  return errors.length > 0 ? fail(errors) : ok();
}
