import { create } from 'zustand';
import { temporal } from 'zundo';
import type {
  NodeDto,
  NodeConnectionDto,
  WorkflowDefinitionDto,
  NodeExecutionStatus,
} from '../../engine-contract';

export interface ViewportState {
  x: number;
  y: number;
  zoom: number;
}

export interface SidecarNodeMeta {
  x: number;
  y: number;
  width?: number;
  height?: number;
  color?: string;
  notes?: string[];
}

export interface GraphState {
  // Engine-owned (undo-able)
  nodes: NodeDto[];
  connections: NodeConnectionDto[];
  workflowId: string;
  workflowName: string;

  // UI-only (NOT in undo history)
  viewport: ViewportState;
  selectedNodeIds: Set<string>;
  executionStatuses: Map<string, NodeExecutionStatus>;
  sidecarMeta: Map<string, SidecarNodeMeta>;

  // Actions
  setWorkflow: (def: WorkflowDefinitionDto) => void;
  addNode: (node: NodeDto, meta?: SidecarNodeMeta) => void;
  removeNode: (nodeId: string) => void;
  updateNodePosition: (nodeId: string, x: number, y: number) => void;
  addConnection: (conn: NodeConnectionDto) => void;
  removeConnection: (sourceId: string, targetId: string) => void;
  setViewport: (vp: ViewportState) => void;
  setSelectedNodeIds: (ids: Set<string>) => void;
  setExecutionStatus: (nodeId: string, status: NodeExecutionStatus) => void;
  clearExecutionStatuses: () => void;
}

const isHistoryExcluded = (key: string): boolean => {
  const excluded = new Set(['viewport', 'selectedNodeIds', 'executionStatuses', 'sidecarMeta']);
  return excluded.has(key);
};

export const useGraphStore = create<GraphState>()(
  temporal(
    (set) => ({
      nodes: [],
      connections: [],
      workflowId: '',
      workflowName: '',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set<string>(),
      executionStatuses: new Map<string, NodeExecutionStatus>(),
      sidecarMeta: new Map<string, SidecarNodeMeta>(),

      setWorkflow: (def) =>
        set({
          nodes: def.nodes ?? [],
          connections: def.connections ?? [],
          workflowId: def.workflowId ?? '',
          workflowName: def.workflowName ?? '',
        }),

      addNode: (node, meta) =>
        set((state) => {
          const newMeta = new Map(state.sidecarMeta);
          if (meta) newMeta.set(node.nodeId, meta);
          return {
            nodes: [...state.nodes, node],
            sidecarMeta: newMeta,
          };
        }),

      removeNode: (nodeId) =>
        set((state) => ({
          nodes: state.nodes.filter((n) => n.nodeId !== nodeId),
          connections: state.connections.filter(
            (c) => c.sourceNodeId !== nodeId && c.targetNodeId !== nodeId,
          ),
        })),

      updateNodePosition: (nodeId, x, y) =>
        set((state) => {
          const newMeta = new Map(state.sidecarMeta);
          const existing = newMeta.get(nodeId) ?? { x: 0, y: 0 };
          newMeta.set(nodeId, { ...existing, x, y });
          return { sidecarMeta: newMeta };
        }),

      addConnection: (conn) =>
        set((state) => ({ connections: [...state.connections, conn] })),

      removeConnection: (sourceId, targetId) =>
        set((state) => ({
          connections: state.connections.filter(
            (c) => !(c.sourceNodeId === sourceId && c.targetNodeId === targetId),
          ),
        })),

      setViewport: (vp) => set({ viewport: vp }),
      setSelectedNodeIds: (ids) => set({ selectedNodeIds: ids }),
      setExecutionStatus: (nodeId, status) =>
        set((state) => {
          const newMap = new Map(state.executionStatuses);
          newMap.set(nodeId, status);
          return { executionStatuses: newMap };
        }),
      clearExecutionStatuses: () =>
        set({ executionStatuses: new Map() }),
    }),
    {
      partialize: (state) => {
        const partial: Record<string, unknown> = {};
        for (const [key, value] of Object.entries(state)) {
          if (typeof value !== 'function' && !isHistoryExcluded(key)) {
            partial[key] = value;
          }
        }
        return partial as Pick<GraphState, 'nodes' | 'connections' | 'workflowId' | 'workflowName'>;
      },
    },
  ),
);