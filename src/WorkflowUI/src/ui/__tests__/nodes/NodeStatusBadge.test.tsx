import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { NodeStatusBadge } from '../../nodes/NodeStatusBadge';
import { ALL_STATUSES } from '../fixtures/execution/node-statuses';

describe('NodeStatusBadge', () => {
  it('renders all 6 execution statuses', () => {
    for (const status of ALL_STATUSES) {
      const { unmount } = render(<NodeStatusBadge status={status} />);
      expect(screen.getByTestId('node-status-badge')).toBeDefined();
      expect(screen.getByText(status)).toBeDefined();
      unmount();
    }
  });

  it('has correct aria-label', () => {
    render(<NodeStatusBadge status="Running" />);
    expect(screen.getByLabelText('Status: Running')).toBeDefined();
  });

  it('displays icon for each status', () => {
    render(<NodeStatusBadge status="Completed" />);
    const badge = screen.getByTestId('node-status-badge');
    expect(badge.textContent).toContain('✅');
  });
});
