import { useCallback, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  MiniMap,
  type Node as RFNode,
  type Edge as RFEdge,
  type EdgeMouseHandler,
  type NodeMouseHandler,
  type Connection,
  ReactFlowProvider,
  BackgroundVariant,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useGraphStore } from '../state/graphStore';
import { CanvasSurface } from './CanvasSurface';
import { CANVAS_SNAP_GRID, DEFAULT_VIEWPORT, ZOOM_LIMITS } from './canvasConstants';
import { useCanvasViewport } from './useCanvasViewport';
import { CanvasHUD } from './CanvasHUD';
import { nodeTypes } from './nodeTypes';
import { edgeTypes } from './edgeTypes';
import { useConnectionCreation } from './useConnectionCreation';
import { EdgePopover } from '../edges/EdgePopover';
import type { NodeConnectionDto, NodeDto } from '../../engine-contract';
import { validateConnection } from '../validation/connectionRules';

export interface WorkflowCanvasProps {
  showMiniMap?: boolean;
  showControls?: boolean;
}

export function WorkflowCanvas({
  showMiniMap = false,
  showControls = true,
}: WorkflowCanvasProps) {
  const nodes = useGraphStore((s) => s.nodes);
  const connections = useGraphStore((s) => s.connections);
  const sidecarMeta = useGraphStore((s) => s.sidecarMeta);
  const addConnection = useGraphStore((s) => s.addConnection);
  const removeConnection = useGraphStore((s) => s.removeConnection);
  const updateConnection = useGraphStore((s) => s.updateConnection);
  const setSelectedNodeIds = useGraphStore((s) => s.setSelectedNodeIds);
  const { viewport, onViewportChange } = useCanvasViewport();
  const { onConnect } = useConnectionCreation();

  const [editingEdge, setEditingEdge] = useState<NodeConnectionDto | null>(null);

  const rfNodes: RFNode[] = useMemo(
    () =>
      nodes.map((n) => {
        const meta = sidecarMeta.get(n.nodeId);
        return {
          id: n.nodeId,
          type: 'workflowNode',
          position: { x: meta?.x ?? 0, y: meta?.y ?? 0 },
          data: { dto: n },
        };
      }),
    [nodes, sidecarMeta],
  );

  const rfEdges: RFEdge[] = useMemo(
    () =>
      connections.map((c, i) => ({
        id: `${c.sourceNodeId}-${c.targetNodeId}-${i}`,
        source: c.sourceNodeId,
        target: c.targetNodeId,
        type: 'workflowEdge',
        data: { dto: c },
      })),
    [connections],
  );

  const onEdgeClick: EdgeMouseHandler = useCallback(
    (_event, edge) => {
      const conn = connections.find(
        (c) => c.sourceNodeId === edge.source && c.targetNodeId === edge.target,
      );
      if (conn) {
        setEditingEdge(conn);
      }
    },
    [connections],
  );

  const onEdgesDelete = useCallback(
    (edges: RFEdge[]) => {
      for (const edge of edges) {
        removeConnection(edge.source, edge.target);
      }
    },
    [removeConnection],
  );

  const onNodeClick: NodeMouseHandler = useCallback(
    (_event, node) => {
      setSelectedNodeIds(new Set([node.id]));
    },
    [setSelectedNodeIds],
  );

  const onPaneClick = useCallback(() => {
    setSelectedNodeIds(new Set());
  }, [setSelectedNodeIds]);

  const handleEdgeSave = useCallback(
    (updated: NodeConnectionDto) => {
      const sourceNode = nodes.find((n: NodeDto) => n.nodeId === updated.sourceNodeId);
      const targetNode = nodes.find((n: NodeDto) => n.nodeId === updated.targetNodeId);
      if (!sourceNode || !targetNode) return;

      const validation = validateConnection(sourceNode, targetNode, updated.triggerMessageType);
      if (!validation.valid) return;

      updateConnection(updated.sourceNodeId, updated.targetNodeId, {
        triggerMessageType: updated.triggerMessageType,
        condition: updated.condition,
        isEnabled: updated.isEnabled,
        priority: updated.priority,
      });
      setEditingEdge(null);
    },
    [updateConnection, nodes],
  );

  const onReconnect = useCallback(
    (oldEdge: RFEdge, newConnection: Connection) => {
      if (!newConnection.source || !newConnection.target) return;

      const sourceNode = nodes.find((n: NodeDto) => n.nodeId === newConnection.source);
      const targetNode = nodes.find((n: NodeDto) => n.nodeId === newConnection.target);
      if (!sourceNode || !targetNode) return;

      const oldConn = connections.find(
        (c) => c.sourceNodeId === oldEdge.source && c.targetNodeId === oldEdge.target,
      );
      const msgType = oldConn?.triggerMessageType ?? 'Complete';

      const validation = validateConnection(sourceNode, targetNode, msgType);
      if (!validation.valid) return;

      removeConnection(oldEdge.source, oldEdge.target);
      addConnection({
        sourceNodeId: newConnection.source,
        targetNodeId: newConnection.target,
        triggerMessageType: msgType,
        isEnabled: oldConn?.isEnabled ?? true,
        priority: oldConn?.priority ?? 0,
        condition: oldConn?.condition,
      });
    },
    [nodes, connections, removeConnection, addConnection],
  );

  return (
    <CanvasSurface>
      <ReactFlowProvider>
        <ReactFlow
          nodes={rfNodes}
          edges={rfEdges}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          defaultViewport={viewport ?? DEFAULT_VIEWPORT}
          snapToGrid
          snapGrid={CANVAS_SNAP_GRID}
          minZoom={ZOOM_LIMITS.min}
          maxZoom={ZOOM_LIMITS.max}
          onViewportChange={onViewportChange}
          onConnect={onConnect}
          onNodeClick={onNodeClick}
          onPaneClick={onPaneClick}
          onEdgeClick={onEdgeClick}
          onEdgesDelete={onEdgesDelete}
          onReconnect={onReconnect}
          fitView={false}
          data-testid="react-flow-canvas"
        >
          <Background variant={BackgroundVariant.Dots} gap={CANVAS_SNAP_GRID[0]} />
          {showControls && <CanvasHUD />}
          {showMiniMap && <MiniMap />}
        </ReactFlow>
        {editingEdge && (
          <EdgePopover
            connection={editingEdge}
            onSave={handleEdgeSave}
            onClose={() => setEditingEdge(null)}
          />
        )}
      </ReactFlowProvider>
    </CanvasSurface>
  );
}