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
  parentId?: string;
  parentSlot?: string;
  childIds?: string[];
  branchChildren?: Record<string, string[]>;
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
  updateNode: (nodeId: string, patch: Partial<NodeDto>) => void;
  updateNodePosition: (nodeId: string, x: number, y: number) => void;
  addConnection: (conn: NodeConnectionDto) => void;
  removeConnection: (sourceId: string, targetId: string) => void;
  updateConnection: (sourceId: string, targetId: string, patch: Partial<NodeConnectionDto>) => void;
  setViewport: (vp: ViewportState) => void;
  setSelectedNodeIds: (ids: Set<string>) => void;
  setExecutionStatus: (nodeId: string, status: NodeExecutionStatus) => void;
  clearExecutionStatuses: () => void;

  // Nesting actions (Phase D)
  createChildNode: (node: NodeDto, parentId: string, slot: string, index?: number) => void;
  reparentNode: (nodeId: string, newParentId: string, newSlot: string, index?: number) => void;
  insertNodeRelative: (node: NodeDto, refNodeId: string, position: 'before' | 'after' | 'after-error') => void;
  isDescendantOf: (maybeDescendantId: string, ancestorId: string) => boolean;
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

      updateNode: (nodeId, patch) =>
        set((state) => ({
          nodes: state.nodes.map((n) =>
            n.nodeId === nodeId
              ? ({ ...n, ...patch, runtimeType: n.runtimeType } as NodeDto)
              : n,
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

      updateConnection: (sourceId, targetId, patch) =>
        set((state) => ({
          connections: state.connections.map((c) =>
            c.sourceNodeId === sourceId && c.targetNodeId === targetId
              ? { ...c, ...patch }
              : c,
          ),
        })),

      setViewport:(vp) => set({ viewport: vp }),
      setSelectedNodeIds: (ids) => set({ selectedNodeIds: ids }),
      setExecutionStatus: (nodeId, status) =>
        set((state) => {
          const newMap = new Map(state.executionStatuses);
          newMap.set(nodeId, status);
          return { executionStatuses: newMap };
        }),
      clearExecutionStatuses: () =>
        set({ executionStatuses: new Map() }),

      // Nesting actions
      createChildNode: (node, parentId, slot, index) =>
        set((state) => {
          const newMeta = new Map(state.sidecarMeta);
          // Add node meta with parent reference
          newMeta.set(node.nodeId, { x: 0, y: 0, parentId, parentSlot: slot });

          // Add to parent's children list
          const parentMeta = newMeta.get(parentId) ?? { x: 0, y: 0 };
          if (slot === '_body') {
            const childIds = [...((parentMeta as SidecarNodeMeta).childIds ?? [])];
            const insertAt = typeof index === 'number' ? Math.max(0, Math.min(index, childIds.length)) : childIds.length;
            childIds.splice(insertAt, 0, node.nodeId);
            newMeta.set(parentId, { ...parentMeta, childIds });
          } else {
            const branchChildren = { ...((parentMeta as SidecarNodeMeta).branchChildren ?? {}) };
            const arr = [...(branchChildren[slot] ?? [])];
            const insertAt = typeof index === 'number' ? Math.max(0, Math.min(index, arr.length)) : arr.length;
            arr.splice(insertAt, 0, node.nodeId);
            branchChildren[slot] = arr;
            newMeta.set(parentId, { ...parentMeta, branchChildren });
          }

          return {
            nodes: [...state.nodes, node],
            sidecarMeta: newMeta,
          };
        }),

      reparentNode: (nodeId, newParentId, newSlot, index) =>
        set((state) => {
          const newMeta = new Map(state.sidecarMeta);
          const nodeMeta = newMeta.get(nodeId) ?? { x: 0, y: 0 };
          const oldParentId = (nodeMeta as SidecarNodeMeta).parentId;
          const oldSlot = (nodeMeta as SidecarNodeMeta).parentSlot;

          // Detach from old parent
          if (oldParentId) {
            const oldParentMeta = newMeta.get(oldParentId);
            if (oldParentMeta) {
              if (oldSlot === '_body') {
                const childIds = ((oldParentMeta as SidecarNodeMeta).childIds ?? []).filter((id) => id !== nodeId);
                newMeta.set(oldParentId, { ...oldParentMeta, childIds });
              } else if (oldSlot) {
                const branchChildren = { ...((oldParentMeta as SidecarNodeMeta).branchChildren ?? {}) };
                branchChildren[oldSlot] = (branchChildren[oldSlot] ?? []).filter((id) => id !== nodeId);
                newMeta.set(oldParentId, { ...oldParentMeta, branchChildren });
              }
            }
          }

          // Attach to new parent
          const newParentMeta = newMeta.get(newParentId) ?? { x: 0, y: 0 };
          if (newSlot === '_body') {
            const childIds = [...((newParentMeta as SidecarNodeMeta).childIds ?? [])];
            const filtered = childIds.filter((id) => id !== nodeId);
            const insertAt = typeof index === 'number' ? Math.max(0, Math.min(index, filtered.length)) : filtered.length;
            filtered.splice(insertAt, 0, nodeId);
            newMeta.set(newParentId, { ...newParentMeta, childIds: filtered });
          } else {
            const branchChildren = { ...((newParentMeta as SidecarNodeMeta).branchChildren ?? {}) };
            const arr = [...(branchChildren[newSlot] ?? [])].filter((id) => id !== nodeId);
            const insertAt = typeof index === 'number' ? Math.max(0, Math.min(index, arr.length)) : arr.length;
            arr.splice(insertAt, 0, nodeId);
            branchChildren[newSlot] = arr;
            newMeta.set(newParentId, { ...newParentMeta, branchChildren });
          }

          // Update node's own meta
          newMeta.set(nodeId, { ...nodeMeta, parentId: newParentId, parentSlot: newSlot });

          // Clean up redundant edges
          const connections = state.connections.filter(
            (c) => !(c.sourceNodeId === newParentId && c.targetNodeId === nodeId),
          );

          return { sidecarMeta: newMeta, connections };
        }),

      insertNodeRelative: (node, refNodeId, position) =>
        set((state) => {
          const newMeta = new Map(state.sidecarMeta);
          const refMeta = newMeta.get(refNodeId) as SidecarNodeMeta | undefined;

          // Check if ref is nested
          if (refMeta?.parentId) {
            const parentId = refMeta.parentId;
            const slot = refMeta.parentSlot ?? '_body';
            const parentMeta = newMeta.get(parentId) ?? { x: 0, y: 0 };

            let arr: string[];
            if (slot === '_body') {
              arr = [...((parentMeta as SidecarNodeMeta).childIds ?? [])];
            } else {
              const branchChildren = (parentMeta as SidecarNodeMeta).branchChildren ?? {};
              arr = [...(branchChildren[slot] ?? [])];
            }

            const refIdx = arr.indexOf(refNodeId);
            const insertAt = position === 'before' ? refIdx : refIdx + 1;
            arr.splice(Math.max(0, insertAt), 0, node.nodeId);

            if (slot === '_body') {
              newMeta.set(parentId, { ...parentMeta, childIds: arr });
            } else {
              const branchChildren = { ...((parentMeta as SidecarNodeMeta).branchChildren ?? {}) };
              branchChildren[slot] = arr;
              newMeta.set(parentId, { ...parentMeta, branchChildren });
            }

            newMeta.set(node.nodeId, { x: 0, y: 0, parentId, parentSlot: slot });
            return { nodes: [...state.nodes, node], sidecarMeta: newMeta };
          }

          // Top-level: splice into edge chain
          const refX = refMeta?.x ?? 0;
          const refY = refMeta?.y ?? 0;
          const newY = position === 'before' ? refY - 180 : refY + 180;
          newMeta.set(node.nodeId, { x: refX, y: newY });

          let connections = [...state.connections];

          if (position === 'before') {
            const incoming = connections.find((c) => c.targetNodeId === refNodeId);
            if (incoming) {
              connections = connections.map((c) =>
                c === incoming ? { ...c, targetNodeId: node.nodeId } : c,
              );
              connections.push({
                sourceNodeId: node.nodeId,
                targetNodeId: refNodeId,
                triggerMessageType: 'Complete',
                isEnabled: true,
                priority: 0,
              });
            }
          } else {
            const sourcePort = position === 'after-error' ? 'Fail' : 'Complete';
            const outgoing = connections.find(
              (c) => c.sourceNodeId === refNodeId && c.triggerMessageType === sourcePort,
            );
            if (outgoing) {
              const oldTarget = outgoing.targetNodeId;
              connections = connections.map((c) =>
                c === outgoing ? { ...c, targetNodeId: node.nodeId } : c,
              );
              connections.push({
                sourceNodeId: node.nodeId,
                targetNodeId: oldTarget,
                triggerMessageType: 'Complete',
                isEnabled: true,
                priority: 0,
              });
            }
          }

          return {
            nodes: [...state.nodes, node],
            connections,
            sidecarMeta: newMeta,
          };
        }),

      isDescendantOf: (maybeDescendantId, ancestorId) => {
        const state = useGraphStore.getState();
        const visited = new Set<string>();
        const queue = [ancestorId];
        while (queue.length > 0) {
          const current = queue.pop()!;
          if (visited.has(current)) continue;
          visited.add(current);
          const meta = state.sidecarMeta.get(current) as SidecarNodeMeta | undefined;
          const children = [
            ...(meta?.childIds ?? []),
            ...Object.values(meta?.branchChildren ?? {}).flat(),
          ];
          for (const cid of children) {
            if (cid === maybeDescendantId) return true;
            queue.push(cid);
          }
        }
        return false;
      },
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