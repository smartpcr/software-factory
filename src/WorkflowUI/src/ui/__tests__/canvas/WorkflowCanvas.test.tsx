import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { WorkflowCanvas } from '../../canvas/WorkflowCanvas';
import { useGraphStore } from '../../state/graphStore';
import { emptyWorkflow } from '../fixtures/workflows/empty-workflow';

describe('WorkflowCanvas', () => {
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

  it('renders empty workflow canvas without errors', () => {
    useGraphStore.getState().setWorkflow(emptyWorkflow);
    const { container } = render(<WorkflowCanvas />);
    expect(screen.getByTestId('canvas-surface')).toBeDefined();
    expect(container.querySelector('.react-flow')).toBeDefined();
  });

  it('renders with showMiniMap and showControls', () => {
    render(<WorkflowCanvas showMiniMap showControls />);
    expect(screen.getByTestId('canvas-surface')).toBeDefined();
  });

  it('canvas surface has accessibility role', () => {
    render(<WorkflowCanvas />);
    const surface = screen.getByTestId('canvas-surface');
    expect(surface.getAttribute('role')).toBe('application');
    expect(surface.getAttribute('aria-label')).toBe('Workflow canvas');
  });

  it('does not create any edges for empty workflow', () => {
    useGraphStore.getState().setWorkflow(emptyWorkflow);
    render(<WorkflowCanvas />);
    expect(useGraphStore.getState().connections).toHaveLength(0);
  });
});
