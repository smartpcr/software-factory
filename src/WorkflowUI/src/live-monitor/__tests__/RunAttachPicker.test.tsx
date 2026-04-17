/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { RunAttachPicker } from '../components/RunAttachPicker';
import type { RunInfo } from '../components/RunAttachPicker';

describe('RunAttachPicker', () => {
  const mockRuns: RunInfo[] = [
    { runId: 'run-1', workflowId: 'wf-1', workflowName: 'WF One', status: 'Running', startedAt: new Date().toISOString() },
    { runId: 'run-2', workflowId: 'wf-2', workflowName: 'WF Two', status: 'Completed', startedAt: new Date().toISOString() },
  ];

  it('renders nothing when closed', () => {
    const { container } = render(
      <RunAttachPicker isOpen={false} onClose={vi.fn()} onSelectRun={vi.fn()} />,
    );
    expect(container.querySelector('.run-attach-picker')).toBeNull();
  });

  it('shows runs when open', async () => {
    const fetchRuns = vi.fn().mockResolvedValue(mockRuns);

    render(
      <RunAttachPicker isOpen={true} onClose={vi.fn()} onSelectRun={vi.fn()} fetchRuns={fetchRuns} />,
    );

    await waitFor(() => {
      expect(screen.getByText('WF One')).toBeDefined();
      expect(screen.getByText('WF Two')).toBeDefined();
    });
  });

  it('calls onSelectRun when a run is clicked', async () => {
    const fetchRuns = vi.fn().mockResolvedValue(mockRuns);
    const onSelectRun = vi.fn();

    render(
      <RunAttachPicker isOpen={true} onClose={vi.fn()} onSelectRun={onSelectRun} fetchRuns={fetchRuns} />,
    );

    await waitFor(() => {
      expect(screen.getByText('WF One')).toBeDefined();
    });

    fireEvent.click(screen.getByText('WF One'));
    expect(onSelectRun).toHaveBeenCalledWith('run-1');
  });

  it('closes when close button clicked', async () => {
    const onClose = vi.fn();
    render(
      <RunAttachPicker isOpen={true} onClose={onClose} onSelectRun={vi.fn()} fetchRuns={vi.fn().mockResolvedValue([])} />,
    );

    fireEvent.click(screen.getByLabelText('Close picker'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows no runs message when empty', async () => {
    render(
      <RunAttachPicker isOpen={true} onClose={vi.fn()} onSelectRun={vi.fn()} fetchRuns={vi.fn().mockResolvedValue([])} />,
    );

    await waitFor(() => {
      expect(screen.getByText('No active runs')).toBeDefined();
    });
  });
});
