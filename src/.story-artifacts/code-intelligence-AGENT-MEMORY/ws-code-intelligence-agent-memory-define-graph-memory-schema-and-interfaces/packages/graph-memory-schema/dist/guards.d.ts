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
import type { GraphNode, GraphEdge, NodeKind, EdgeRelation, GraphLayer, RepoNode, DirectoryNode, PackageNode, ModuleNode, FileNode, ClassNode, InterfaceNode, EnumNode, TypeAliasNode, FunctionNode, MethodNode, VariableNode, SemanticClusterNode, TopicNode, PatternNode, PitfallNode, PreferenceNode, InsightNode, ContainsEdge, ExportsEdge, ImportsEdge, ReExportsEdge, CallsEdge, CalledByEdge, OverridesEdge, ImplementsEdge, InstantiatesEdge, SimilarToEdge, BelongsToClusterEdge, AppliesToEdge, SupersedesEdge, DerivedFromEdge, ExpertiseEdge, FrequentlyUsedWithEdge, ReferencesEdge, DependsOnEdge, InheritsEdge } from './types.js';
import type { KnowledgeGraph } from './graph.js';
/** Check whether a value is a valid NodeKind string. */
export declare function isNodeKind(v: unknown): v is NodeKind;
/** Check whether a value is a valid EdgeRelation string. */
export declare function isEdgeRelation(v: unknown): v is EdgeRelation;
/** Check whether a value is a valid GraphLayer string. */
export declare function isGraphLayer(v: unknown): v is GraphLayer;
/**
 * Structural type guard for GraphNode.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export declare function isGraphNode(v: unknown): v is GraphNode;
/**
 * Structural type guard for GraphEdge.
 *
 * Checks that the value has the required base fields with correct
 * discriminant types. Does NOT validate the properties bag shape.
 */
export declare function isGraphEdge(v: unknown): v is GraphEdge;
/**
 * Structural type guard for KnowledgeGraph.
 *
 * Checks top-level shape only; does not recurse into individual nodes/edges.
 */
export declare function isKnowledgeGraph(v: unknown): v is KnowledgeGraph;
export declare function isRepoNode(n: GraphNode): n is RepoNode;
export declare function isDirectoryNode(n: GraphNode): n is DirectoryNode;
export declare function isPackageNode(n: GraphNode): n is PackageNode;
export declare function isModuleNode(n: GraphNode): n is ModuleNode;
export declare function isFileNode(n: GraphNode): n is FileNode;
export declare function isClassNode(n: GraphNode): n is ClassNode;
export declare function isInterfaceNode(n: GraphNode): n is InterfaceNode;
export declare function isEnumNode(n: GraphNode): n is EnumNode;
export declare function isTypeAliasNode(n: GraphNode): n is TypeAliasNode;
export declare function isFunctionNode(n: GraphNode): n is FunctionNode;
export declare function isMethodNode(n: GraphNode): n is MethodNode;
export declare function isVariableNode(n: GraphNode): n is VariableNode;
export declare function isSemanticClusterNode(n: GraphNode): n is SemanticClusterNode;
export declare function isTopicNode(n: GraphNode): n is TopicNode;
export declare function isPatternNode(n: GraphNode): n is PatternNode;
export declare function isPitfallNode(n: GraphNode): n is PitfallNode;
export declare function isPreferenceNode(n: GraphNode): n is PreferenceNode;
export declare function isInsightNode(n: GraphNode): n is InsightNode;
/** Lookup table from NodeKind to its per-kind guard. */
export declare const nodeGuards: Readonly<Record<NodeKind, (n: GraphNode) => boolean>>;
export declare function isContainsEdge(e: GraphEdge): e is ContainsEdge;
export declare function isExportsEdge(e: GraphEdge): e is ExportsEdge;
export declare function isImportsEdge(e: GraphEdge): e is ImportsEdge;
export declare function isReExportsEdge(e: GraphEdge): e is ReExportsEdge;
export declare function isCallsEdge(e: GraphEdge): e is CallsEdge;
export declare function isCalledByEdge(e: GraphEdge): e is CalledByEdge;
export declare function isOverridesEdge(e: GraphEdge): e is OverridesEdge;
export declare function isImplementsEdge(e: GraphEdge): e is ImplementsEdge;
export declare function isInstantiatesEdge(e: GraphEdge): e is InstantiatesEdge;
export declare function isSimilarToEdge(e: GraphEdge): e is SimilarToEdge;
export declare function isBelongsToClusterEdge(e: GraphEdge): e is BelongsToClusterEdge;
export declare function isAppliesToEdge(e: GraphEdge): e is AppliesToEdge;
export declare function isSupersedesEdge(e: GraphEdge): e is SupersedesEdge;
export declare function isDerivedFromEdge(e: GraphEdge): e is DerivedFromEdge;
export declare function isExpertiseEdge(e: GraphEdge): e is ExpertiseEdge;
export declare function isFrequentlyUsedWithEdge(e: GraphEdge): e is FrequentlyUsedWithEdge;
export declare function isReferencesEdge(e: GraphEdge): e is ReferencesEdge;
export declare function isDependsOnEdge(e: GraphEdge): e is DependsOnEdge;
export declare function isInheritsEdge(e: GraphEdge): e is InheritsEdge;
/** Lookup table from EdgeRelation to its per-relation guard. */
export declare const edgeGuards: Readonly<Record<EdgeRelation, (e: GraphEdge) => boolean>>;
/** True if the node is in the structural group (repo, directory, package, module, file). */
export declare function isStructuralNode(n: GraphNode): boolean;
/** True if the node is a code entity (class, interface, enum, type_alias, function, method, variable). */
export declare function isCodeNode(n: GraphNode): boolean;
/** True if the node is in the semantic group (semantic_cluster, topic). */
export declare function isSemanticNode(n: GraphNode): boolean;
/** True if the node is an expertise entry (pattern, pitfall, preference, insight). */
export declare function isExpertiseNode(n: GraphNode): boolean;
//# sourceMappingURL=guards.d.ts.map