/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { EventLogPanel } from '../components/EventLogPanel';
import { useTelemetryStore } from '../store/telemetryStore';
import type { RunEvent } from '../../telemetry/types';

function makeEvent(seq: number, kind: RunEvent['kind'] = 'NodeStarted', nodeId = 'n1'): RunEvent {
  return {
    runId: 'run-1',
    seq,
    kind,
    synthetic: kind === 'EdgeActivated' || kind === 'NodeSkipped',
    timestampUtc: new Date().toISOString(),
    nodeId,
  };
}

describe('EventLogPanel', () => {
  it('renders event rows', () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvents([
      makeEvent(1, 'NodeStarted', 'n1'),
      makeEvent(2, 'NodeCompleted', 'n1'),
    ]);

    render(<EventLogPanel />);

    expect(screen.getByText('#1')).toBeDefined();
    expect(screen.getByText('#2')).toBeDefined();
    expect(screen.getAllByText('n1')).toHaveLength(2);
  });

  it('filters by nodeId', async () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvents([
      makeEvent(1, 'NodeStarted', 'n1'),
      makeEvent(2, 'NodeStarted', 'n2'),
    ]);

    render(<EventLogPanel />);

    const input = screen.getByPlaceholderText('Filter by nodeId');
    fireEvent.change(input, { target: { value: 'n1' } });

    await waitFor(() => {
      const rows = screen.getAllByRole('listitem');
      expect(rows).toHaveLength(1);
    });
  });

  it('calls onEventSelect when event clicked', () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvent(makeEvent(1));

    const handler = vi.fn();
    render(<EventLogPanel onEventSelect={handler} />);

    const row = screen.getByRole('listitem');
    fireEvent.click(row);
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('shows synthetic badge for synthetic events', () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvent(
      makeEvent(1, 'NodeSkipped', 'n1'),
    );

    render(<EventLogPanel />);
    expect(screen.getByLabelText('synthetic')).toBeDefined();
  });
});
