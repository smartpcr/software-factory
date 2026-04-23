// -----------------------------------------------------------------------
// TypeScript type definitions for Flow Studio
// -----------------------------------------------------------------------

export interface NodeTypePorts {
  in: boolean;
  out: boolean;
  error: boolean;
}

export interface NodeTypeConfig {
  [key: string]: string | number | boolean;
}

export interface NodeTypeDefinition {
  label: string;
  category: string;
  color: string;
  icon: string;
  desc: string;
  ports: NodeTypePorts;
  branches?: string[];
  /** Optional custom tooltip labels for ports */
  portLabels?: {
    in?: string;
    out?: string;
    error?: string;
    branches?: Record<string, string>;
  };
  config: NodeTypeConfig;
}

export interface FlowNode {
  nodeId: string;
  runtimeType: string;
  nodeName: string;
  description?: string;
  x: number;
  y: number;
  width: number;
  height?: number;
  config: NodeTypeConfig;
  status: string;
  parentId: string | null;
  parentSlot: string | null;
  branches?: string[];
  children?: Record<string, string[]>;
  collapsed?: boolean;
  // Engine fields
  joinType?: string;
  priority?: string;
  maxConcurrentExecutions?: number;
  retryPolicy?: Record<string, unknown>;
  circuitBreakerPolicy?: Record<string, unknown>;
  compensationNodeId?: string;
  fallbackNodeId?: string;
  // UI-only transient fields
  _cachedHeight?: number;
  _iterCount?: number;
  _forceFail?: boolean;
  _runDuration?: string;
  _runError?: string;
  _runData?: Record<string, unknown>;
  validationErrors?: string[];
}

export interface FlowEdge {
  id: string;
  sourceNodeId: string;
  sourcePort: string;
  sourcePortName?: string;
  targetNodeId: string;
  targetPort: string;
  triggerMessageType?: string;
  condition?: string;
  isEnabled?: boolean;
  priority?: number;
  metadata?: Record<string, unknown>;
  label?: string;
  _active?: boolean;
  _runtimeData?: Record<string, unknown>;
  _internal?: boolean; // auto-generated container edge, hidden from canvas
}

export interface Selection {
  kind: 'node' | 'edge' | null;
  id: string | null;
}

export interface CanvasState {
  x: number;
  y: number;
  zoom: number;
}

export interface LogEntry {
  t: string;
  level: string;
  nodeId: string | null;
  msg: string;
}

export interface WorkflowData {
  workflowId: string;
  workflowName: string;
  description?: string;
  version?: string;
  nodes: FlowNode[];
  edges: FlowEdge[];
  metadata?: Record<string, unknown>;
  entryPointNodeId?: string;
  maxConcurrency?: number;
  allowPause?: boolean;
  timeoutSeconds?: number;
  defaultVariables?: Record<string, unknown>;
  // UI sidecar fields
  createdAt: number;
  updatedAt: number;
  author?: string;
}

export interface PaletteSection {
  category: string;
  types: string[];
}

export type ExecState = 'idle' | 'running' | 'paused' | 'success' | 'failed' | 'cancelled';

export interface Point {
  x: number;
  y: number;
}

export interface CreateNodeOptions {
  parentId?: string;
  parentSlot?: string;
  index?: number;
}

export interface CreateEdgeParams {
  sourceNodeId: string;
  sourcePort: string;
  sourcePortName?: string;
  targetNodeId: string;
  targetPort: string;
}

export interface ModalOptions {
  title: string;
  body: string;
  confirmLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
}

/** Callbacks that renderer modules use to trigger app-level actions */
export interface AppCallbacks {
  select(kind: 'node' | 'edge' | null, id?: string): void;
  createNode(type: string, x: number, y: number, opts?: CreateNodeOptions): string;
  createChildNode(type: string, parentId: string, slot: string, index?: number): string | null;
  insertNodeRelative(type: string, refNodeId: string, position: string): string | null;
  reparentNode(nodeId: string, newParentId: string, newSlot: string, index?: number): void;
  unparentNode(nodeId: string, canvasX: number, canvasY: number): void;
  updateNode(id: string, patch: Partial<FlowNode>): void;
  updateNodeConfig(id: string, key: string, value: unknown): void;
  deleteNode(id: string): void;
  deleteEdge(id: string): void;
  toggleCollapse(id: string): void;
  addBranch(nodeId: string): void;
  removeBranch(nodeId: string, index: number): void;
  renameBranch(nodeId: string, index: number, newName: string): void;
  markDirty(): void;
  isDescendantOf(maybeDescendantId: string, ancestorId: string): boolean;
  createEdge(params: CreateEdgeParams): void;
  renderAll(): void;
}
