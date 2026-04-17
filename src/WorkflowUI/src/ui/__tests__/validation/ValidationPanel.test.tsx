import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ValidationPanel, type ValidationEntry } from '../../validation/ValidationPanel';

describe('ValidationPanel', () => {
  it('renders entries with node IDs', () => {
    const entries: ValidationEntry[] = [
      { nodeId: 'node-1', field: 'nodeName', message: 'Name is required', severity: 'error' },
      { nodeId: 'node-2', message: 'Invalid condition', severity: 'warning' },
    ];

    render(<ValidationPanel entries={entries} onJumpToNode={() => {}} />);

    expect(screen.getByTestId('validation-panel')).toBeDefined();
    expect(screen.getByTestId('validation-entry-0')).toBeDefined();
    expect(screen.getByTestId('validation-entry-1')).toBeDefined();
    expect(screen.getByTestId('jump-to-node-1')).toBeDefined();
    expect(screen.getByTestId('jump-to-node-2')).toBeDefined();
  });

  it('jump-to-node callback fires on click', () => {
    const entries: ValidationEntry[] = [
      { nodeId: 'target-node', message: 'Error here', severity: 'error' },
    ];

    let jumpedTo: string | null = null;
    const onJumpToNode = (nodeId: string) => {
      jumpedTo = nodeId;
    };

    render(<ValidationPanel entries={entries} onJumpToNode={onJumpToNode} />);
    fireEvent.click(screen.getByTestId('jump-to-target-node'));
    expect(jumpedTo).toBe('target-node');
  });

  it('empty entries shows empty state', () => {
    render(<ValidationPanel entries={[]} onJumpToNode={() => {}} />);
    expect(screen.getByTestId('validation-panel-empty')).toBeDefined();
    expect(screen.getByText('No validation issues')).toBeDefined();
  });
});
