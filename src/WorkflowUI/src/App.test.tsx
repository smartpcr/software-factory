import { render, screen, waitFor } from '@testing-library/react';
import { vi, beforeEach } from 'vitest';
import { App } from './App';

beforeEach(() => {
  // Stub fetch for WorkspaceTree's getWorkspaceTree call
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => ({
      ok: true,
      json: async () => ({ name: '', isFolder: true, children: [] }),
    })),
  );
});

describe('App', () => {
  it('renders the app shell', () => {
    render(<App />);
    expect(screen.getByTestId('app-shell')).toBeDefined();
  });

  it('renders palette with workspace tree', async () => {
    render(<App />);
    await waitFor(() => {
      expect(screen.getByTestId('app-palette')).toBeDefined();
      expect(screen.getByLabelText('Workspace tree')).toBeDefined();
    });
  });

  it('renders import/export toggle button', () => {
    render(<App />);
    expect(screen.getByLabelText('Toggle import/export')).toBeDefined();
  });
});
