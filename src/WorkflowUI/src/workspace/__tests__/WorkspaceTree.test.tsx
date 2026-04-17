/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { WorkspaceTree } from '../WorkspaceTree';
import type { TreeNode } from '../../api/files';

const mockTree: TreeNode = {
  name: '',
  isFolder: true,
  children: [
    {
      name: 'marketing',
      isFolder: true,
      children: [
        {
          name: 'newsletter',
          isFolder: false,
          workflowId: 'marketing/newsletter',
          children: [],
        },
      ],
    },
    {
      name: 'simple',
      isFolder: false,
      workflowId: 'simple',
      children: [],
    },
  ],
};

describe('WorkspaceTree', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (url === '/api/files' && (!init || init.method === undefined)) {
          return { ok: true, json: async () => mockTree };
        }
        return { ok: true, json: async () => ({}) };
      }),
    );
  });

  it('renders tree with folders and files', async () => {
    render(<WorkspaceTree />);
    await waitFor(() => {
      expect(screen.getByText('marketing')).toBeDefined();
      expect(screen.getByText('simple')).toBeDefined();
      expect(screen.getByText('newsletter')).toBeDefined();
    });
  });

  it('calls onSelect when file clicked', async () => {
    const handler = vi.fn();
    render(<WorkspaceTree onSelect={handler} />);

    await waitFor(() => {
      expect(screen.getByText('simple')).toBeDefined();
    });

    fireEvent.click(screen.getByTestId('tree-node-simple'));
    expect(handler).toHaveBeenCalledWith('simple');
  });

  it('renders create form', async () => {
    render(<WorkspaceTree />);
    await waitFor(() => {
      expect(screen.getByPlaceholderText('New workflow name')).toBeDefined();
      expect(screen.getByLabelText('Create workflow')).toBeDefined();
    });
  });

  it('shows rename and delete buttons for file nodes', async () => {
    render(<WorkspaceTree />);
    await waitFor(() => {
      expect(screen.getByLabelText('Rename simple')).toBeDefined();
      expect(screen.getByLabelText('Delete simple')).toBeDefined();
    });
  });
});
