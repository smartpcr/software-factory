import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { validateConnection } from '../../validation/connectionRules';
import type { NodeDto } from '../../../../engine-contract';

describe('useConnectionCreation port-matrix enforcement', () => {
  const noopA: NodeDto = {
    nodeId: 'a',
    nodeName: 'A',
    runtimeType: 'Noop',
    joinType: 'Any',
    maxConcurrentExecutions: 0,
    priority: 'Normal',
  };

  const noopB: NodeDto = {
    nodeId: 'b',
    nodeName: 'B',
    runtimeType: 'Noop',
    joinType: 'Any',
    maxConcurrentExecutions: 0,
    priority: 'Normal',
  };

  beforeEach(() => {
    useGraphStore.setState({
      nodes: [noopA, noopB],
      connections: [],
      workflowId: 'test',
      workflowName: 'Test',
      viewport: { x: 0, y: 0, zoom: 1 },
      selectedNodeIds: new Set(),
      executionStatuses: new Map(),
      sidecarMeta: new Map(),
    });
  });

  it('validates connection before adding - valid Complete connection allowed', () => {
    const validation = validateConnection(noopA, noopB, 'Complete');
    expect(validation.valid).toBe(true);
  });

  it('rejects self-connection via validation', () => {
    const validation = validateConnection(noopA, noopA, 'Complete');
    expect(validation.valid).toBe(false);
    expect(validation.reason).toContain('itself');
  });

  it('rejects forbidden port-matrix connection via validation', () => {
    const validation = validateConnection(noopA, noopB, 'Next');
    expect(validation.valid).toBe(false);
    expect(validation.reason).toContain('cannot send');
  });

  it('forbidden connection does not reach the store', () => {
    // Simulate what useConnectionCreation does: validate first
    const validation = validateConnection(noopA, noopB, 'Next');
    if (validation.valid) {
      useGraphStore.getState().addConnection({
        sourceNodeId: 'a',
        targetNodeId: 'b',
        triggerMessageType: 'Next',
        isEnabled: true,
        priority: 0,
      });
    }
    // Connection should NOT have been added
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });

  it('valid connection reaches the store', () => {
    const validation = validateConnection(noopA, noopB, 'Complete');
    if (validation.valid) {
      useGraphStore.getState().addConnection({
        sourceNodeId: 'a',
        targetNodeId: 'b',
        triggerMessageType: 'Complete',
        isEnabled: true,
        priority: 0,
      });
    }
    expect(useGraphStore.getState().connections).toHaveLength(1);
  });
});
