import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { allKindFixtures } from '../fixtures/property-grid/all-kinds-form-data';
import type { NodeDto } from '../../../engine-contract';

// We test useWorkflowValidation by calling the validate function directly
// Since it's a hook, we test the underlying logic via the store
import { renderHook, act } from '@testing-library/react';
import { useWorkflowValidation } from '../../validation/useWorkflowValidation';

function resetStore() {
  useGraphStore.setState({
    nodes: [],
    connections: [],
    workflowId: '',
    workflowName: '',
    selectedNodeIds: new Set<string>(),
    executionStatuses: new Map(),
    sidecarMeta: new Map(),
  });
}

describe('useWorkflowValidation', () => {
  beforeEach(() => {
    resetStore();
  });

  it('returns empty entries for valid workflow', () => {
    useGraphStore.setState({ nodes: allKindFixtures });

    const { result } = renderHook(() => useWorkflowValidation());
    act(() => {
      result.current.validate();
    });

    expect(result.current.entries).toHaveLength(0);
    expect(result.current.isValidating).toBe(false);
  });

  it('returns entries for invalid nodes', () => {
    const invalidNode: NodeDto = {
      runtimeType: 'IfElse',
      nodeId: 'bad-ifelse',
      nodeName: 'Bad',
      joinType: 'Any',
      priority: 'Normal',
      maxConcurrentExecutions: 0,
      condition: '', // min(1) violation
    };

    useGraphStore.setState({ nodes: [invalidNode] });

    const { result } = renderHook(() => useWorkflowValidation());
    act(() => {
      result.current.validate();
    });

    expect(result.current.entries.length).toBeGreaterThan(0);
    expect(result.current.entries.some((e) => e.nodeId === 'bad-ifelse')).toBe(true);
  });
});
