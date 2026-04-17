/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { DuplicateDialog } from '../DuplicateDialog';

describe('DuplicateDialog', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        json: async () => ({ id: 'copy-of-test' }),
      })),
    );
  });

  it('renders source id and duplicate button', () => {
    render(<DuplicateDialog sourceId="test-wf" />);
    expect(screen.getByText('Source: test-wf')).toBeDefined();
    expect(screen.getByLabelText('Duplicate')).toBeDefined();
  });

  it('renders target id input', () => {
    render(<DuplicateDialog sourceId="test-wf" />);
    expect(screen.getByLabelText('Target workflow ID')).toBeDefined();
  });

  it('calls onDuplicated after successful duplicate', async () => {
    const handler = vi.fn();
    render(<DuplicateDialog sourceId="test-wf" onDuplicated={handler} />);

    fireEvent.click(screen.getByLabelText('Duplicate'));
    await waitFor(() => {
      expect(handler).toHaveBeenCalledWith('copy-of-test');
    });
  });

  it('shows status after duplicate', async () => {
    render(<DuplicateDialog sourceId="test-wf" />);

    fireEvent.click(screen.getByLabelText('Duplicate'));
    await waitFor(() => {
      expect(
        screen.getByText('Duplicated as copy-of-test'),
      ).toBeDefined();
    });
  });
});
