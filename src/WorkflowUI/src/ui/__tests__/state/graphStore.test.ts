import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { emptyWorkflow } from '../fixtures/workflows/empty-workflow';
import type { NodeDto, NodeConnectionDto } from '../../../../engine-contract';

describe('graphStore', () => {
  beforeEach(() => {
    useGraphStore.setState({
      nodes: [],
      connections: [],
      workflowId: '',
      workflowName: '',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
  });

  it('setWorkflow populates nodes and connections', () => {
    useGraphStore.getState().setWorkflow(emptyWorkflow);
    const state = useGraphStore.getState();
    expect(state.workflowId).toBe('empty-test');
    expect(state.nodes).toHaveLength(0);
    expect(state.connections).toHaveLength(0);
  });

  it('addNode appends a node', () => {
    const node: NodeDto = {
      nodeId: 'n1',
      nodeName: 'Test',
      runtimeType: 'Noop',
      joinType: 'Any',
      maxConcurrentExecutions: 0,
      priority: 'Normal',
    };
    useGraphStore.getState().addNode(node, { x: 100, y: 200 });
    expect(useGraphStore.getState().nodes).toHaveLength(1);
    expect(useGraphStore.getState().sidecarMeta.get('n1')).toEqual({ x: 100, y: 200 });
  });

  it('removeNode removes a node and its connections', () => {
    const node: NodeDto = {
      nodeId: 'n1',
      nodeName: 'Test',
      runtimeType: 'Noop',
      joinType: 'Any',
      maxConcurrentExecutions: 0,
      priority: 'Normal',
    };
    const conn: NodeConnectionDto = {
      sourceNodeId: 'n1',
      targetNodeId: 'n2',
      triggerMessageType: 'Complete',
      isEnabled: true,
      priority: 0,
    };
    useGraphStore.getState().addNode(node);
    useGraphStore.getState().addConnection(conn);
    useGraphStore.getState().removeNode('n1');
    expect(useGraphStore.getState().nodes).toHaveLength(0);
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('setViewport updates viewport without affecting nodes', () => {
    useGraphStore.getState().setViewport({ x: 10, y: 20, zoom: 2 });
    const state = useGraphStore.getState();
    expect(state.viewport).toEqual({ x: 10, y: 20, zoom: 2 });
    expect(state.nodes).toHaveLength(0);
  });

  it('setExecutionStatus tracks per-node status', () => {
    useGraphStore.getState().setExecutionStatus('n1', 'Running');
    expect(useGraphStore.getState().executionStatuses.get('n1')).toBe('Running');
  });

  it('clearExecutionStatuses empties the map', () => {
    useGraphStore.getState().setExecutionStatus('n1', 'Running');
    useGraphStore.getState().clearExecutionStatuses();
    expect(useGraphStore.getState().executionStatuses.size).toBe(0);
  });

  it('addConnection and removeConnection', () => {
    const conn: NodeConnectionDto = {
      sourceNodeId: 'a',
      targetNodeId: 'b',
      triggerMessageType: 'Complete',
      isEnabled: true,
      priority: 0,
    };
    useGraphStore.getState().addConnection(conn);
    expect(useGraphStore.getState().connections).toHaveLength(1);
    useGraphStore.getState().removeConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('updateNodePosition updates sidecar meta', () => {
    useGraphStore.getState().updateNodePosition('n1', 50, 60);
    expect(useGraphStore.getState().sidecarMeta.get('n1')).toEqual({ x: 50, y: 60 });
  });

  it('viewport is excluded from undo history', () => {
    const store = useGraphStore;
    const temporal = store.temporal;
    // Clear history
    temporal.getState().clear();

    // Change viewport (should NOT create history)
    store.getState().setViewport({ x: 99, y: 99, zoom: 3 });

    // History should be empty since viewport is excluded
    const pastStates = temporal.getState().pastStates;
    const hasViewportInHistory = pastStates.some(
      (s: Record<string, unknown>) => 'viewport' in s,
    );
    expect(hasViewportInHistory).toBe(false);
  });
});
