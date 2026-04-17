import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { allKindFixtures } from '../fixtures/property-grid/all-kinds-form-data';
import type { NodeDto } from '../../../engine-contract';

import { renderHook, act } from '@testing-library/react';
import { useWorkflowValidation } from '../../validation/useWorkflowValidation';

function resetStore() {
  useGraphStore.setState({
    nodes: [],
    connections: [],
    workflowId: 'test-wf',
    workflowName: '',
    selectedNodeIds: new Set<string>(),
    executionStatuses: new Map(),
    sidecarMeta: new Map(),
  });
}

describe('useWorkflowValidation', () => {
  beforeEach(() => {
    resetStore();
    // Mock fetch to simulate host unavailable — falls back to client-side
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network error')));
  });

  it('returns empty entries for valid workflow (client-side fallback)', async () => {
    useGraphStore.setState({ nodes: allKindFixtures });

    const { result } = renderHook(() => useWorkflowValidation());
    await act(async () => {
      await result.current.validate();
    });

    expect(result.current.entries).toHaveLength(0);
    expect(result.current.isValidating).toBe(false);
  });

  it('returns entries for invalid nodes (client-side fallback)', async () => {
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
    await act(async () => {
      await result.current.validate();
    });

    expect(result.current.entries.length).toBeGreaterThan(0);
    expect(result.current.entries.some((e) => e.nodeId === 'bad-ifelse')).toBe(true);
  });

  it('uses host validation results when available', async () => {
    const hostResults = [
      { nodeId: 'node-1', field: 'nodeName', message: 'Name required by engine', severity: 'error' as const },
    ];

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve(hostResults),
    }));

    useGraphStore.setState({ nodes: allKindFixtures, workflowId: 'wf-1' });

    const { result } = renderHook(() => useWorkflowValidation());
    await act(async () => {
      await result.current.validate();
    });

    expect(result.current.entries).toHaveLength(1);
    expect(result.current.entries[0].message).toBe('Name required by engine');
  });
});
