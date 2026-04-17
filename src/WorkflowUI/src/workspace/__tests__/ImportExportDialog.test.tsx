/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { ImportExportDialog } from '../ImportExportDialog';

describe('ImportExportDialog', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        json: async () => ({ imported: ['wf1'] }),
        blob: async () => new Blob(['zip']),
      })),
    );
  });

  it('renders export and import buttons', () => {
    render(<ImportExportDialog workflowIds={['wf1']} />);
    expect(screen.getByLabelText('Export workflows')).toBeDefined();
    expect(screen.getByLabelText('Import workflows')).toBeDefined();
  });

  it('shows no-selection message when exporting empty list', async () => {
    render(<ImportExportDialog workflowIds={[]} />);
    fireEvent.click(screen.getByLabelText('Export workflows'));
    await waitFor(() => {
      expect(
        screen.getByText('No workflows selected for export'),
      ).toBeDefined();
    });
  });

  it('shows import file input', () => {
    render(<ImportExportDialog workflowIds={[]} />);
    expect(screen.getByLabelText('Import zip file')).toBeDefined();
  });
});
