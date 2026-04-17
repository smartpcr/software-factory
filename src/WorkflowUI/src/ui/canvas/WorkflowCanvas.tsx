import { useMemo } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Node as RFNode,
  type Edge as RFEdge,
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
  const { viewport, onViewportChange } = useCanvasViewport();

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
          fitView={false}
          data-testid="react-flow-canvas"
        >
          <Background variant={BackgroundVariant.Dots} gap={CANVAS_SNAP_GRID[0]} />
          {showControls && <Controls />}
          {showMiniMap && <MiniMap />}
        </ReactFlow>
      </ReactFlowProvider>
    </CanvasSurface>
  );
}