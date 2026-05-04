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

import {
  NODE_KINDS,
  EDGE_RELATIONS,
  GRAPH_LAYERS,
  STRUCTURAL_NODE_KINDS,
  CODE_NODE_KINDS,
  SEMANTIC_NODE_KINDS,
  EXPERTISE_NODE_KINDS,
} from './types.js';
import type {
  GraphNode,
  GraphEdge,
  NodeKind,
  EdgeRelation,
  GraphLayer,
  RepoNode,
  DirectoryNode,
  PackageNode,
  ModuleNode,
  FileNode,
  ClassNode,
  InterfaceNode,
  EnumNode,
  TypeAliasNode,
  FunctionNode,
  MethodNode,
  VariableNode,
  SemanticClusterNode,
  TopicNode,
  PatternNode,
  PitfallNode,
  PreferenceNode,
  InsightNode,
  ContainsEdge,
  ExportsEdge,
  ImportsEdge,
  ReExportsEdge,
  CallsEdge,
  CalledByEdge,
  OverridesEdge,
  ImplementsEdge,
  InstantiatesEdge,
  SimilarToEdge,
  BelongsToClusterEdge,
  AppliesToEdge,
  SupersedesEdge,
  DerivedFromEdge,
  ExpertiseEdge,
  FrequentlyUsedWithEdge,
  ReferencesEdge,
  DependsOnEdge,
  InheritsEdge,
} from './types.js';
import type { KnowledgeGraph } from './graph.js';

const NODE_KIND_SET: ReadonlySet<string> = new Set(NODE_KINDS);
const EDGE_RELATION_SET: ReadonlySet<string> = new Set(EDGE_RELATIONS);
const GRAPH_LAYER_SET: ReadonlySet<string> = new Set(GRAPH_LAYERS);

function isNonNullObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

// ─── Tier 1: Literal guards ─────────────────────────────────────────────

/** Check whether a value is a valid NodeKind string. */
export function isNodeKind(v: unknown): v is NodeKind {
  return typeof v === 'string' && NODE_KIND_SET.has(v);
}

/** Check whether a value is a valid EdgeRelation string. */
export function isEdgeRelation(v: unknown): v is EdgeRelation {
  return typeof v === 'string' && EDGE_RELATION_SET.has(v);
}

/** Check whether a value is a valid GraphLayer string. */
export function isGraphLayer(v: unknown): v is GraphLayer {
  return typeof v === 'string' && GRAPH_LAYER_SET.has(v);
}

// ─── Tier 2: Base guards ────────────────────────────────────────────────

/**
 * Structural type guard for GraphNode.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export function isGraphNode(v: unknown): v is GraphNode {
  if (!isNonNullObject(v)) return false;
  if (typeof v.id !== 'string' || v.id.length === 0) return false;
  if (!isNodeKind(v.kind)) return false;
  if (typeof v.qualified_name !== 'string' || v.qualified_name.length === 0) return false;
  if (!Array.isArray(v.layer) || v.layer.length === 0) return false;
  if (!v.layer.every(isGraphLayer)) return false;
  if (!isNonNullObject(v.version_range)) return false;
  if (typeof (v.version_range as Record<string, unknown>).since !== 'string') return false;
  if (typeof v.last_touched !== 'string') return false;
  if (!isNonNullObject(v.properties)) return false;
  return true;
}

/**
 * Structural type guard for GraphEdge.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export function isGraphEdge(v: unknown): v is GraphEdge {
  if (!isNonNullObject(v)) return false;
  if (typeof v.id !== 'string' || v.id.length === 0) return false;
  if (typeof v.source !== 'string' || v.source.length === 0) return false;
  if (typeof v.target !== 'string' || v.target.length === 0) return false;
  if (!isEdgeRelation(v.relation)) return false;
  if (!isGraphLayer(v.layer)) return false;
  if (typeof v.weight !== 'number') return false;
  if (!isNonNullObject(v.version_range)) return false;
  if (typeof (v.version_range as Record<string, unknown>).since !== 'string') return false;
  if (typeof v.stale !== 'boolean') return false;
  return true;
}

/**
 * Structural type guard for KnowledgeGraph.
 *
 * Checks top-level shape only; does not recurse into individual nodes/edges.
 */
export function isKnowledgeGraph(v: unknown): v is KnowledgeGraph {
  if (!isNonNullObject(v)) return false;
  if (!Array.isArray(v.nodes)) return false;
  if (!Array.isArray(v.edges)) return false;
  return true;
}

// ─── Tier 3: Per-kind node guards ───────────────────────────────────────

export function isRepoNode(n: GraphNode): n is RepoNode { return n.kind === 'repo'; }
export function isDirectoryNode(n: GraphNode): n is DirectoryNode { return n.kind === 'directory'; }
export function isPackageNode(n: GraphNode): n is PackageNode { return n.kind === 'package'; }
export function isModuleNode(n: GraphNode): n is ModuleNode { return n.kind === 'module'; }
export function isFileNode(n: GraphNode): n is FileNode { return n.kind === 'file'; }
export function isClassNode(n: GraphNode): n is ClassNode { return n.kind === 'class'; }
export function isInterfaceNode(n: GraphNode): n is InterfaceNode { return n.kind === 'interface'; }
export function isEnumNode(n: GraphNode): n is EnumNode { return n.kind === 'enum'; }
export function isTypeAliasNode(n: GraphNode): n is TypeAliasNode { return n.kind === 'type_alias'; }
export function isFunctionNode(n: GraphNode): n is FunctionNode { return n.kind === 'function'; }
export function isMethodNode(n: GraphNode): n is MethodNode { return n.kind === 'method'; }
export function isVariableNode(n: GraphNode): n is VariableNode { return n.kind === 'variable'; }
export function isSemanticClusterNode(n: GraphNode): n is SemanticClusterNode { return n.kind === 'semantic_cluster'; }
export function isTopicNode(n: GraphNode): n is TopicNode { return n.kind === 'topic'; }
export function isPatternNode(n: GraphNode): n is PatternNode { return n.kind === 'pattern'; }
export function isPitfallNode(n: GraphNode): n is PitfallNode { return n.kind === 'pitfall'; }
export function isPreferenceNode(n: GraphNode): n is PreferenceNode { return n.kind === 'preference'; }
export function isInsightNode(n: GraphNode): n is InsightNode { return n.kind === 'insight'; }

/** Lookup table from NodeKind to its per-kind guard. */
export const nodeGuards: Readonly<Record<NodeKind, (n: GraphNode) => boolean>> = {
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

export function isContainsEdge(e: GraphEdge): e is ContainsEdge { return e.relation === 'contains'; }
export function isExportsEdge(e: GraphEdge): e is ExportsEdge { return e.relation === 'exports'; }
export function isImportsEdge(e: GraphEdge): e is ImportsEdge { return e.relation === 'imports'; }
export function isReExportsEdge(e: GraphEdge): e is ReExportsEdge { return e.relation === 're_exports'; }
export function isCallsEdge(e: GraphEdge): e is CallsEdge { return e.relation === 'calls'; }
export function isCalledByEdge(e: GraphEdge): e is CalledByEdge { return e.relation === 'called_by'; }
export function isOverridesEdge(e: GraphEdge): e is OverridesEdge { return e.relation === 'overrides'; }
export function isImplementsEdge(e: GraphEdge): e is ImplementsEdge { return e.relation === 'implements'; }
export function isInstantiatesEdge(e: GraphEdge): e is InstantiatesEdge { return e.relation === 'instantiates'; }
export function isSimilarToEdge(e: GraphEdge): e is SimilarToEdge { return e.relation === 'similar_to'; }
export function isBelongsToClusterEdge(e: GraphEdge): e is BelongsToClusterEdge { return e.relation === 'belongs_to_cluster'; }
export function isAppliesToEdge(e: GraphEdge): e is AppliesToEdge { return e.relation === 'applies_to'; }
export function isSupersedesEdge(e: GraphEdge): e is SupersedesEdge { return e.relation === 'supersedes'; }
export function isDerivedFromEdge(e: GraphEdge): e is DerivedFromEdge { return e.relation === 'derived_from'; }
export function isExpertiseEdge(e: GraphEdge): e is ExpertiseEdge { return e.relation === 'expertise'; }
export function isFrequentlyUsedWithEdge(e: GraphEdge): e is FrequentlyUsedWithEdge { return e.relation === 'frequently_used_with'; }
export function isReferencesEdge(e: GraphEdge): e is ReferencesEdge { return e.relation === 'references'; }
export function isDependsOnEdge(e: GraphEdge): e is DependsOnEdge { return e.relation === 'depends_on'; }
export function isInheritsEdge(e: GraphEdge): e is InheritsEdge { return e.relation === 'inherits'; }

/** Lookup table from EdgeRelation to its per-relation guard. */
export const edgeGuards: Readonly<Record<EdgeRelation, (e: GraphEdge) => boolean>> = {
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

const STRUCTURAL_SET: ReadonlySet<string> = new Set(STRUCTURAL_NODE_KINDS);
const CODE_SET: ReadonlySet<string> = new Set(CODE_NODE_KINDS);
const SEMANTIC_SET: ReadonlySet<string> = new Set(SEMANTIC_NODE_KINDS);
const EXPERTISE_SET: ReadonlySet<string> = new Set(EXPERTISE_NODE_KINDS);

/** True if the node is in the structural group (repo, directory, package, module, file). */
export function isStructuralNode(n: GraphNode): boolean {
  return STRUCTURAL_SET.has(n.kind);
}

/** True if the node is a code entity (class, interface, enum, type_alias, function, method, variable). */
export function isCodeNode(n: GraphNode): boolean {
  return CODE_SET.has(n.kind);
}

/** True if the node is in the semantic group (semantic_cluster, topic). */
export function isSemanticNode(n: GraphNode): boolean {
  return SEMANTIC_SET.has(n.kind);
}

/** True if the node is an expertise entry (pattern, pitfall, preference, insight). */
export function isExpertiseNode(n: GraphNode): boolean {
  return EXPERTISE_SET.has(n.kind);
}
