import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { validateConnection } from '../../validation/connectionRules';
import type { NodeDto, NodeConnectionDto } from '../../../../engine-contract';

const nodeA: NodeDto = {
  nodeId: 'a',
  nodeName: 'A',
  runtimeType: 'Noop',
  joinType: 'Any',
  maxConcurrentExecutions: 0,
  priority: 'Normal',
};

const nodeB: NodeDto = {
  nodeId: 'b',
  nodeName: 'B',
  runtimeType: 'Noop',
  joinType: 'Any',
  maxConcurrentExecutions: 0,
  priority: 'Normal',
};

const nodeC: NodeDto = {
  nodeId: 'c',
  nodeName: 'C',
  runtimeType: 'CSharp',
  joinType: 'Any',
  maxConcurrentExecutions: 0,
  priority: 'Normal',
};

const baseConn: NodeConnectionDto = {
  sourceNodeId: 'a',
  targetNodeId: 'b',
  triggerMessageType: 'Complete',
  isEnabled: true,
  priority: 0,
};

/**
 * Mirrors the validation logic used in WorkflowCanvas.handleEdgeSave:
 * validates before persisting, returning false if blocked.
 */
function validatedUpdateConnection(
  sourceId: string,
  targetId: string,
  patch: Partial<NodeConnectionDto>,
): boolean {
  const state = useGraphStore.getState();
  const sourceNode = state.nodes.find((n) => n.nodeId === sourceId);
  const targetNode = state.nodes.find((n) => n.nodeId === targetId);
  if (!sourceNode || !targetNode) return false;

  const existing = state.connections.find(
    (c) => c.sourceNodeId === sourceId && c.targetNodeId === targetId,
  );
  const msgType = patch.triggerMessageType ?? existing?.triggerMessageType ?? 'Complete';

  const validation = validateConnection(sourceNode, targetNode, msgType);
  if (!validation.valid) return false;

  state.updateConnection(sourceId, targetId, patch);
  return true;
}

describe('edge interactions — store integration', () => {
  beforeEach(() => {
    useGraphStore.setState({
      nodes: [nodeA, nodeB, nodeC],
      connections: [baseConn],
      workflowId: 'test',
      workflowName: 'Test',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
  });

  it('updateConnection patches message type', () => {
    useGraphStore.getState().updateConnection('a', 'b', { triggerMessageType: 'Fail' });
    const conn = useGraphStore.getState().connections[0];
    expect(conn.triggerMessageType).toBe('Fail');
    expect(conn.isEnabled).toBe(true);
  });

  it('updateConnection patches condition', () => {
    useGraphStore.getState().updateConnection('a', 'b', { condition: 'x > 0' });
    expect(useGraphStore.getState().connections[0].condition).toBe('x > 0');
  });

  it('updateConnection patches isEnabled', () => {
    useGraphStore.getState().updateConnection('a', 'b', { isEnabled: false });
    expect(useGraphStore.getState().connections[0].isEnabled).toBe(false);
  });

  it('updateConnection patches priority', () => {
    useGraphStore.getState().updateConnection('a', 'b', { priority: 10 });
    expect(useGraphStore.getState().connections[0].priority).toBe(10);
  });

  it('updateConnection does not affect other connections', () => {
    const conn2: NodeConnectionDto = {
      sourceNodeId: 'b',
      targetNodeId: 'c',
      triggerMessageType: 'Complete',
      isEnabled: true,
      priority: 0,
    };
    useGraphStore.getState().addConnection(conn2);
    expect(useGraphStore.getState().connections).toHaveLength(2);

    useGraphStore.getState().updateConnection('a', 'b', { triggerMessageType: 'Cancel' });

    const conns = useGraphStore.getState().connections;
    expect(conns[0].triggerMessageType).toBe('Cancel');
    expect(conns[1].triggerMessageType).toBe('Complete');
  });

  it('removeConnection removes correct edge', () => {
    const conn2: NodeConnectionDto = {
      sourceNodeId: 'b',
      targetNodeId: 'c',
      triggerMessageType: 'Complete',
      isEnabled: true,
      priority: 0,
    };
    useGraphStore.getState().addConnection(conn2);
    expect(useGraphStore.getState().connections).toHaveLength(2);

    useGraphStore.getState().removeConnection('a', 'b');
    const remaining = useGraphStore.getState().connections;
    expect(remaining).toHaveLength(1);
    expect(remaining[0].sourceNodeId).toBe('b');
    expect(remaining[0].targetNodeId).toBe('c');
  });

  it('edge edit + delete sequence works correctly', () => {
    useGraphStore.getState().updateConnection('a', 'b', { priority: 99 });
    expect(useGraphStore.getState().connections[0].priority).toBe(99);

    useGraphStore.getState().removeConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('validated update rejects forbidden message type edit (Noop->Noop with Next)', () => {
    const accepted = validatedUpdateConnection('a', 'b', { triggerMessageType: 'Next' });
    expect(accepted).toBe(false);
    // Connection should remain unchanged
    expect(useGraphStore.getState().connections[0].triggerMessageType).toBe('Complete');
  });

  it('validated update allows valid message type edit (CSharp->Noop with Fail)', () => {
    // CSharp can emit Fail, and Noop can receive Complete (which port matrix allows as fallback)
    // First set up a CSharp->Noop connection
    useGraphStore.setState({
      nodes: [nodeA, nodeB, nodeC],
      connections: [
        {
          sourceNodeId: 'c',
          targetNodeId: 'a',
          triggerMessageType: 'Complete',
          isEnabled: true,
          priority: 0,
        },
      ],
      workflowId: 'test',
      workflowName: 'Test',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
    const accepted = validatedUpdateConnection('c', 'a', { triggerMessageType: 'Fail' });
    expect(accepted).toBe(true);
    expect(useGraphStore.getState().connections[0].triggerMessageType).toBe('Fail');
  });

  it('validated update allows condition change without message type change', () => {
    const accepted = validatedUpdateConnection('a', 'b', { condition: 'x > 1' });
    expect(accepted).toBe(true);
    expect(useGraphStore.getState().connections[0].condition).toBe('x > 1');
  });
});

describe('edge rerouting — store integration', () => {
  beforeEach(() => {
    useGraphStore.setState({
      nodes: [nodeA, nodeB, nodeC],
      connections: [baseConn],
      workflowId: 'test',
      workflowName: 'Test',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
  });

  it('reroute removes old connection and adds new one', () => {
    // Simulate reroute: a->b becomes a->c
    const oldSource = 'a';
    const oldTarget = 'b';
    const newTarget = 'c';

    const sourceNode = useGraphStore.getState().nodes.find((n) => n.nodeId === oldSource);
    const targetNode = useGraphStore.getState().nodes.find((n) => n.nodeId === newTarget);
    const validation = validateConnection(sourceNode as NodeDto, targetNode as NodeDto, 'Complete');
    expect(validation.valid).toBe(true);

    useGraphStore.getState().removeConnection(oldSource, oldTarget);
    useGraphStore.getState().addConnection({
      sourceNodeId: oldSource,
      targetNodeId: newTarget,
      triggerMessageType: 'Complete',
      isEnabled: true,
      priority: 0,
    });

    const conns = useGraphStore.getState().connections;
    expect(conns).toHaveLength(1);
    expect(conns[0].sourceNodeId).toBe('a');
    expect(conns[0].targetNodeId).toBe('c');
  });

  it('reroute rejects forbidden target', () => {
    // Noop -> Noop with Next is forbidden
    const sourceNode = nodeA;
    const targetNode = nodeB;
    const validation = validateConnection(sourceNode, targetNode, 'Next');
    expect(validation.valid).toBe(false);
    // Connection should remain unchanged
    expect(useGraphStore.getState().connections).toHaveLength(1);
    expect(useGraphStore.getState().connections[0].targetNodeId).toBe('b');
  });
});

describe('edge operations — undo/redo integration', () => {
  beforeEach(() => {
    useGraphStore.setState({
      nodes: [nodeA, nodeB, nodeC],
      connections: [],
      workflowId: 'test',
      workflowName: 'Test',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
    // Clear temporal history
    useGraphStore.temporal.getState().clear();
  });

  it('addConnection is undoable', () => {
    useGraphStore.getState().addConnection(baseConn);
    expect(useGraphStore.getState().connections).toHaveLength(1);

    useGraphStore.temporal.getState().undo();
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('undo then redo restores connection', () => {
    useGraphStore.getState().addConnection(baseConn);
    expect(useGraphStore.getState().connections).toHaveLength(1);

    useGraphStore.temporal.getState().undo();
    expect(useGraphStore.getState().connections).toHaveLength(0);

    useGraphStore.temporal.getState().redo();
    expect(useGraphStore.getState().connections).toHaveLength(1);
    expect(useGraphStore.getState().connections[0].sourceNodeId).toBe('a');
  });

  it('removeConnection is undoable', () => {
    useGraphStore.getState().addConnection(baseConn);
    expect(useGraphStore.getState().connections).toHaveLength(1);

    useGraphStore.getState().removeConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(0);

    useGraphStore.temporal.getState().undo();
    expect(useGraphStore.getState().connections).toHaveLength(1);
  });

  it('updateConnection is undoable', () => {
    useGraphStore.getState().addConnection(baseConn);
    useGraphStore.getState().updateConnection('a', 'b', { triggerMessageType: 'Fail' });
    expect(useGraphStore.getState().connections[0].triggerMessageType).toBe('Fail');

    useGraphStore.temporal.getState().undo();
    expect(useGraphStore.getState().connections[0].triggerMessageType).toBe('Complete');
  });

  it('viewport changes are preserved across undo (excluded from history)', () => {
    useGraphStore.getState().addConnection(baseConn);
    useGraphStore.getState().setViewport({ x: 100, y: 200, zoom: 2 });

    // Undo until connections are reverted
    const maxUndos = 5;
    for (let i = 0; i < maxUndos && useGraphStore.getState().connections.length > 0; i++) {
      useGraphStore.temporal.getState().undo();
    }

    // Connection should be removed after undo
    expect(useGraphStore.getState().connections).toHaveLength(0);
    // Viewport should be preserved since it's not part of undo history
    const vp = useGraphStore.getState().viewport;
    expect(vp.x).toBe(100);
    expect(vp.y).toBe(200);
    expect(vp.zoom).toBe(2);
  });
});
