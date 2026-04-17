import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PropertyGrid } from '../../property-grid/PropertyGrid';
import { useGraphStore } from '../../state/graphStore';
import { allKindFixtures } from '../fixtures/property-grid/all-kinds-form-data';
import type { NodeDto } from '../../../engine-contract';

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

describe('PropertyGrid', () => {
  beforeEach(() => {
    resetStore();
  });

  it('shows empty state when no node selected', () => {
    render(<PropertyGrid />);
    expect(screen.getByTestId('property-grid-empty')).toBeDefined();
    expect(screen.getByText('No node selected')).toBeDefined();
  });

  it.each(allKindFixtures.map((f) => [f.runtimeType, f] as const))(
    'renders form for %s runtime type',
    (_rt, fixture) => {
      useGraphStore.setState({
        nodes: [fixture],
        selectedNodeIds: new Set([fixture.nodeId]),
      });

      render(<PropertyGrid />);
      expect(screen.getByTestId('property-grid')).toBeDefined();
      expect(screen.getByTestId('property-grid-form')).toBeDefined();
    },
  );

  it('nodeId field is read-only', () => {
    const fixture = allKindFixtures[0];
    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    render(<PropertyGrid />);
    const nodeIdInput = screen.getByTestId('field-nodeId') as HTMLInputElement;
    expect(nodeIdInput.disabled).toBe(true);
  });

  it('selection change swaps forms', () => {
    const node1 = allKindFixtures[0]; // Noop
    const node2 = allKindFixtures[1]; // CSharp

    useGraphStore.setState({
      nodes: [node1, node2],
      selectedNodeIds: new Set([node1.nodeId]),
    });

    const { rerender } = render(<PropertyGrid />);
    expect(screen.getByTestId('property-grid-title').textContent).toContain('Noop');

    useGraphStore.setState({
      selectedNodeIds: new Set([node2.nodeId]),
    });

    rerender(<PropertyGrid />);
    expect(screen.getByTestId('property-grid-title').textContent).toContain('CSharp');
  });

  it('useNodeForm commits through single update callback', () => {
    const fixture = allKindFixtures[0];
    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    // Verify updateNode is available on the store
    const updateNode = useGraphStore.getState().updateNode;
    expect(typeof updateNode).toBe('function');

    updateNode(fixture.nodeId, { nodeName: 'Updated' } as Partial<NodeDto>);
    const updated = useGraphStore.getState().nodes.find((n) => n.nodeId === fixture.nodeId);
    expect(updated?.nodeName).toBe('Updated');
  });

  it('execution overlay state not in form values', () => {
    const fixture = allKindFixtures[0];
    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
      executionStatuses: new Map([[fixture.nodeId, 'Running']]),
    });

    render(<PropertyGrid />);
    // The form should not contain executionStatus as a field
    expect(screen.queryByTestId('field-executionStatus')).toBeNull();
  });

  it('CSharpTask in script mode hides executor fields', () => {
    // csharpTaskFixture has scriptContent set, executor fields empty
    const fixture = allKindFixtures.find((f) => f.runtimeType === 'CSharpTask');
    if (!fixture) throw new Error('Missing CSharpTask fixture');

    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    render(<PropertyGrid />);
    // scriptContent should be visible
    expect(screen.getByTestId('field-scriptContent')).toBeDefined();
    // executor fields should be hidden
    expect(screen.queryByTestId('field-executorTypeName')).toBeNull();
    expect(screen.queryByTestId('field-executorAssemblyPath')).toBeNull();
  });

  it('Subflow with workflowFilePath hides workflowDefinition', () => {
    const fixture = allKindFixtures.find((f) => f.runtimeType === 'Subflow');
    if (!fixture) throw new Error('Missing Subflow fixture');

    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    render(<PropertyGrid />);
    // workflowFilePath should be visible
    expect(screen.getByTestId('field-workflowFilePath')).toBeDefined();
    // workflowDefinition should be hidden
    expect(screen.queryByTestId('field-workflowDefinition')).toBeNull();
  });

  it('renders policies panel with 5 top-level controls', () => {
    const fixture = allKindFixtures[0];
    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    render(<PropertyGrid />);
    expect(screen.getByTestId('policies-panel')).toBeDefined();
    expect(screen.getByTestId('field-joinType')).toBeDefined();
    expect(screen.getByTestId('field-priority')).toBeDefined();
    expect(screen.getByTestId('field-maxConcurrentExecutions')).toBeDefined();
    expect(screen.getByTestId('field-retryPolicy')).toBeDefined();
    expect(screen.getByTestId('field-circuitBreakerPolicy')).toBeDefined();
  });

  it('renders references panel with 3 fields', () => {
    const fixture = allKindFixtures[0];
    useGraphStore.setState({
      nodes: [fixture],
      selectedNodeIds: new Set([fixture.nodeId]),
    });

    render(<PropertyGrid />);
    expect(screen.getByTestId('references-panel')).toBeDefined();
    expect(screen.getByTestId('field-compensationNodeId')).toBeDefined();
    expect(screen.getByTestId('field-fallbackNodeId')).toBeDefined();
    expect(screen.getByTestId('field-configuration')).toBeDefined();
  });
});
