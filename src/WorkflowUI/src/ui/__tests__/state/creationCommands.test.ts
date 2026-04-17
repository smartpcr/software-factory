import { describe, it, expect, beforeEach } from 'vitest';
import { executeCreateNode } from '../../state/creationCommands';
import { useGraphStore } from '../../state/graphStore';
import { resetNodeCounter } from '../../palette/createNodeFromKind';

describe('creationCommands', () => {
  beforeEach(() => {
    resetNodeCounter();
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

  it('executeCreateNode adds a node to the store', () => {
    const nodeId = executeCreateNode('Noop', { x: 100, y: 200 });
    expect(nodeId).toBeTruthy();
    expect(useGraphStore.getState().nodes).toHaveLength(1);
    expect(useGraphStore.getState().sidecarMeta.get(nodeId)).toEqual({ x: 100, y: 200 });
  });

  it('creates correct runtime type', () => {
    executeCreateNode('ForEach', { x: 0, y: 0 });
    const node = useGraphStore.getState().nodes[0];
    expect(node?.runtimeType).toBe('ForEach');
  });
});
