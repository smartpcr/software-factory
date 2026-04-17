import { useCallback, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Node as RFNode,
  type Edge as RFEdge,
  type EdgeMouseHandler,
  type NodeMouseHandler,
  type NodeChange,
  type Connection,
  ReactFlowProvider,
  BackgroundVariant,
  useReactFlow,
  applyNodeChanges,
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
import type { NodeConnectionDto, NodeDto } from '../../engine-contract';
import { validateConnection } from '../validation/connectionRules';
import { DRAG_TYPE, decodeDragPayload } from '../palette/dragPayload';
import { executeCreateNode } from '../state/creationCommands';

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
        <WorkflowCanvasInner
          rfNodes={rfNodes}
          rfEdges={rfEdges}
          viewport={viewport}
          onViewportChange={onViewportChange}
          onConnect={onConnect}
          onNodeClick={onNodeClick}
          onPaneClick={onPaneClick}
          onEdgeClick={onEdgeClick}
          onEdgesDelete={onEdgesDelete}
          onReconnect={onReconnect}
          showControls={showControls}
          showMiniMap={showMiniMap}
        />
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

function WorkflowCanvasInner({
  rfNodes,
  rfEdges,
  viewport,
  onViewportChange,
  onConnect,
  onNodeClick,
  onPaneClick,
  onEdgeClick,
  onEdgesDelete,
  onReconnect,
  showControls,
  showMiniMap,
}: {
  rfNodes: RFNode[];
  rfEdges: RFEdge[];
  viewport: ReturnType<typeof useCanvasViewport>['viewport'];
  onViewportChange: ReturnType<typeof useCanvasViewport>['onViewportChange'];
  onConnect: ReturnType<typeof useConnectionCreation>['onConnect'];
  onNodeClick: NodeMouseHandler;
  onPaneClick: () => void;
  onEdgeClick: EdgeMouseHandler;
  onEdgesDelete: (edges: RFEdge[]) => void;
  onReconnect: (oldEdge: RFEdge, newConnection: Connection) => void;
  showControls: boolean;
  showMiniMap: boolean;
}) {
  const { screenToFlowPosition } = useReactFlow();
  const updateNodePosition = useGraphStore((s) => s.updateNodePosition);
  const [localNodes, setLocalNodes] = useState(rfNodes);

  // Sync external rfNodes into local state when they change
  useMemo(() => {
    setLocalNodes(rfNodes);
  }, [rfNodes]);

  const onNodesChange = useCallback(
    (changes: NodeChange[]) => {
      setLocalNodes((prev) => applyNodeChanges(changes, prev));
      for (const change of changes) {
        if (change.type === 'position' && change.position) {
          updateNodePosition(change.id, change.position.x, change.position.y);
        }
      }
    },
    [updateNodePosition],
  );

  const onDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  }, []);

  const onDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      const raw = e.dataTransfer.getData(DRAG_TYPE);
      if (!raw) return;
      const payload = decodeDragPayload(raw);
      if (!payload) return;

      const position = screenToFlowPosition({ x: e.clientX, y: e.clientY });
      executeCreateNode(payload.runtimeType, position);
    },
    [screenToFlowPosition],
  );

  return (
    <ReactFlow
      nodes={localNodes}
      edges={rfEdges}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      defaultViewport={viewport ?? DEFAULT_VIEWPORT}
      snapToGrid
      snapGrid={CANVAS_SNAP_GRID}
      minZoom={ZOOM_LIMITS.min}
      maxZoom={ZOOM_LIMITS.max}
      onViewportChange={onViewportChange}
      onNodesChange={onNodesChange}
      onConnect={onConnect}
      onNodeClick={onNodeClick}
      onPaneClick={onPaneClick}
      onEdgeClick={onEdgeClick}
      onEdgesDelete={onEdgesDelete}
      onReconnect={onReconnect}
      onDragOver={onDragOver}
      onDrop={onDrop}
      fitView={false}
      data-testid="react-flow-canvas"
    >
      <Background variant={BackgroundVariant.Dots} gap={CANVAS_SNAP_GRID[0]} />
      {showControls && <Controls />}
      {showMiniMap && <MiniMap />}
    </ReactFlow>
  );
}