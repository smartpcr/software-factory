import { useCallback, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Node as RFNode,
  type Edge as RFEdge,
  type EdgeMouseHandler,
  ReactFlowProvider,
  BackgroundVariant,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useGraphStore } from '../state/graphStore';
import { CanvasSurface } from './CanvasSurface';
import { CANVAS_SNAP_GRID, DEFAULT_VIEWPORT, ZOOM_LIMITS } from './canvasConstants';
import { useCanvasViewport } from './useCanvasViewport';
import { nodeTypes } from './nodeTypes';
import { edgeTypes } from './edgeTypes';
import { useConnectionCreation } from './useConnectionCreation';
import { EdgePopover } from '../edges/EdgePopover';
import type { NodeConnectionDto } from '../../engine-contract';

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
  const removeConnection = useGraphStore((s) => s.removeConnection);
  const updateConnection = useGraphStore((s) => s.updateConnection);
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

  const handleEdgeSave = useCallback(
    (updated: NodeConnectionDto) => {
      updateConnection(updated.sourceNodeId, updated.targetNodeId, {
        triggerMessageType: updated.triggerMessageType,
        condition: updated.condition,
        isEnabled: updated.isEnabled,
        priority: updated.priority,
      });
      setEditingEdge(null);
    },
    [updateConnection],
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
          onEdgeClick={onEdgeClick}
          onEdgesDelete={onEdgesDelete}
          fitView={false}
          data-testid="react-flow-canvas"
        >
          <Background variant={BackgroundVariant.Dots} gap={CANVAS_SNAP_GRID[0]} />
          {showControls && <Controls />}
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