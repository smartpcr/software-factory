/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { AppFooter } from '../AppFooter';

describe('AppFooter', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        json: async () => ({
          version: '1.2.3',
          commitSha: 'abcdef0',
          buildUtc: '2025-01-01T00:00:00Z',
        }),
      })),
    );
  });

  it('renders version from API', async () => {
    render(<AppFooter />);
    await waitFor(() => {
      expect(screen.getByText(/v1\.2\.3.*abcdef0/)).toBeDefined();
    });
  });

  it('shows tooltip with build time', async () => {
    render(<AppFooter />);
    await waitFor(() => {
      const footer = screen.getByTestId('app-footer');
      expect(footer.getAttribute('title')).toContain(
        '2025-01-01T00:00:00Z',
      );
    });
  });

  it('shows fallback when API fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 500 })),
    );

    render(<AppFooter />);
    await waitFor(() => {
      expect(screen.getByText('v?')).toBeDefined();
    });
  });
});
