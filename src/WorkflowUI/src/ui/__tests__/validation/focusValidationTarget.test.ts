import { describe, it, expect, beforeEach } from 'vitest';
import { useGraphStore } from '../../state/graphStore';
import { focusValidationTarget } from '../../validation/focusValidationTarget';
import { noopFixture } from '../fixtures/property-grid/all-kinds-form-data';

function resetStore() {
  useGraphStore.setState({
    nodes: [],
    connections: [],
    workflowId: '',
    workflowName: '',
    selectedNodeIds: new Set<string>(),
    executionStatuses: new Map(),
    sidecarMeta: new Map(),
    viewport: { x: 0, y: 0, zoom: 1 },
  });
}

describe('focusValidationTarget', () => {
  beforeEach(() => {
    resetStore();
  });

  it('updates selectedNodeIds in graph store', () => {
    useGraphStore.setState({
      nodes: [noopFixture],
      sidecarMeta: new Map([[noopFixture.nodeId, { x: 100, y: 200 }]]),
    });

    focusValidationTarget(noopFixture.nodeId);

    const state = useGraphStore.getState();
    expect(state.selectedNodeIds.has(noopFixture.nodeId)).toBe(true);
    expect(state.selectedNodeIds.size).toBe(1);
  });

  it('centers viewport on target node position', () => {
    const nodeX = 300;
    const nodeY = 400;

    useGraphStore.setState({
      nodes: [noopFixture],
      sidecarMeta: new Map([[noopFixture.nodeId, { x: nodeX, y: nodeY }]]),
      viewport: { x: 0, y: 0, zoom: 1.5 },
    });

    focusValidationTarget(noopFixture.nodeId);

    const state = useGraphStore.getState();
    expect(state.viewport.x).toBe(-nodeX + 400);
    expect(state.viewport.y).toBe(-nodeY + 300);
    expect(state.viewport.zoom).toBe(1.5);
  });
});
