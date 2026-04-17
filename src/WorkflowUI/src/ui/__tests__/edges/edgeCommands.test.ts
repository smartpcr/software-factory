import { describe, it, expect, beforeEach } from 'vitest';
import { createConnection, deleteConnection } from '../../edges/edgeCommands';
import { useGraphStore } from '../../state/graphStore';

describe('edgeCommands', () => {
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

  it('createConnection adds to store', () => {
    const conn = createConnection('a', 'b', 'Fail');
    expect(conn.triggerMessageType).toBe('Fail');
    expect(useGraphStore.getState().connections).toHaveLength(1);
  });

  it('deleteConnection removes from store', () => {
    createConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(1);
    deleteConnection('a', 'b');
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('defaults to Complete message type', () => {
    const conn = createConnection('x', 'y');
    expect(conn.triggerMessageType).toBe('Complete');
  });
});
