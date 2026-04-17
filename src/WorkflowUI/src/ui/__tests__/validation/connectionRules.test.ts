import { describe, it, expect } from 'vitest';
import { validateConnection } from '../../validation/connectionRules';
import type { NodeDto } from '../../../../engine-contract';

describe('connectionRules', () => {
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

  it('allows valid connection between different nodes', () => {
    const result = validateConnection(noopA, noopB, 'Complete');
    expect(result.valid).toBe(true);
  });

  it('rejects self-connection', () => {
    const result = validateConnection(noopA, noopA, 'Complete');
    expect(result.valid).toBe(false);
    expect(result.reason).toContain('itself');
  });

  it('rejects forbidden port-matrix connection', () => {
    const result = validateConnection(noopA, noopB, 'Next');
    expect(result.valid).toBe(false);
    expect(result.reason).toContain('cannot send');
  });
});
