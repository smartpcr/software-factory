import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
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
    // Edit first
    useGraphStore.getState().updateConnection('a', 'b', { priority: 99 });
    expect(useGraphStore.getState().connections[0].priority).toBe(99);

    // Then delete
    useGraphStore.getState().removeConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });
});
