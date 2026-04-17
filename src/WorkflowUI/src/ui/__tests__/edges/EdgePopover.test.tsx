import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { EdgePopover } from '../../edges/EdgePopover';
import type { NodeConnectionDto } from '../../../../engine-contract';

const baseConn: NodeConnectionDto = {
  sourceNodeId: 'a',
  targetNodeId: 'b',
  triggerMessageType: 'Complete',
  isEnabled: true,
  priority: 0,
};

describe('EdgePopover', () => {
  it('renders with connection data', () => {
    render(
      <EdgePopover connection={baseConn} onSave={() => {}} onClose={() => {}} />,
    );
    expect(screen.getByTestId('edge-popover')).toBeDefined();
    expect(screen.getByTestId('edge-message-type')).toBeDefined();
    expect(screen.getByTestId('edge-condition')).toBeDefined();
    expect(screen.getByTestId('edge-enabled')).toBeDefined();
    expect(screen.getByTestId('edge-priority')).toBeDefined();
  });

  it('save button fires onSave with updated values', () => {
    let saved: NodeConnectionDto | null = null;
    render(
      <EdgePopover
        connection={baseConn}
        onSave={(c) => { saved = c; }}
        onClose={() => {}}
      />,
    );

    // Change message type to Fail
    fireEvent.change(screen.getByTestId('edge-message-type'), {
      target: { value: 'Fail' },
    });

    // Change priority
    fireEvent.change(screen.getByTestId('edge-priority'), {
      target: { value: '7' },
    });

    fireEvent.click(screen.getByTestId('edge-save'));

    expect(saved).not.toBeNull();
    const result = saved as NodeConnectionDto;
    expect(result.triggerMessageType).toBe('Fail');
    expect(result.priority).toBe(7);
    expect(result.sourceNodeId).toBe('a');
    expect(result.targetNodeId).toBe('b');
  });

  it('cancel button fires onClose', () => {
    let closed = false;
    render(
      <EdgePopover
        connection={baseConn}
        onSave={() => {}}
        onClose={() => { closed = true; }}
      />,
    );

    fireEvent.click(screen.getByTestId('edge-cancel'));
    expect(closed).toBe(true);
  });

  it('has accessible dialog role', () => {
    render(
      <EdgePopover connection={baseConn} onSave={() => {}} onClose={() => {}} />,
    );
    const popover = screen.getByTestId('edge-popover');
    expect(popover.getAttribute('role')).toBe('dialog');
    expect(popover.getAttribute('aria-label')).toBe('Edit connection');
  });
});
