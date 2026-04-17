/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { EventLogPanel } from '../components/EventLogPanel';
import { useTelemetryStore } from '../store/telemetryStore';
import type { RunEvent } from '../../telemetry/types';

function makeEvent(
  seq: number,
  kind: RunEvent['kind'] = 'NodeStarted',
  nodeId = 'n1',
  payload?: Record<string, unknown>,
): RunEvent {
  return {
    runId: 'run-1',
    seq,
    kind,
    synthetic: kind === 'EdgeActivated' || kind === 'NodeSkipped',
    timestampUtc: new Date().toISOString(),
    nodeId,
    payload,
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

  it('filters by kind via checkboxes', async () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvents([
      makeEvent(1, 'NodeStarted', 'n1'),
      makeEvent(2, 'NodeCompleted', 'n1'),
      makeEvent(3, 'NodeFailed', 'n2'),
    ]);

    render(<EventLogPanel />);

    // Click NodeStarted checkbox to filter to only NodeStarted
    const checkbox = screen.getByLabelText('Filter NodeStarted');
    fireEvent.click(checkbox);

    await waitFor(() => {
      const rows = screen.getAllByRole('listitem');
      expect(rows).toHaveLength(1);
      // The single row should show seq #1
      expect(screen.getByText('#1')).toBeDefined();
    });
  });

  it('filters by messageType via dropdown', async () => {
    useTelemetryStore.getState().reset();
    useTelemetryStore.getState().addEvents([
      makeEvent(1, 'NodeStarted', 'n1'),
      makeEvent(2, 'EdgeActivated', 'n1', {
        triggerMessageType: 'Complete',
        sourceNodeId: 'n1',
        targetNodeId: 'n2',
      }),
      makeEvent(3, 'EdgeActivated', 'n1', {
        triggerMessageType: 'Fail',
        sourceNodeId: 'n1',
        targetNodeId: 'n3',
      }),
    ]);

    render(<EventLogPanel />);

    const select = screen.getByLabelText('Filter by message type');
    fireEvent.change(select, { target: { value: 'Complete' } });

    await waitFor(() => {
      const rows = screen.getAllByRole('listitem');
      // Non-edge rows (NodeStarted) are always visible + matching EdgeActivated
      expect(rows).toHaveLength(2);
    });
  });

  it('renders kind filter fieldset', () => {
    useTelemetryStore.getState().reset();
    render(<EventLogPanel />);

    expect(screen.getByLabelText('Filter by event kind')).toBeDefined();
    expect(screen.getByLabelText('Filter NodeStarted')).toBeDefined();
    expect(screen.getByLabelText('Filter EdgeActivated')).toBeDefined();
  });

  it('renders messageType dropdown', () => {
    useTelemetryStore.getState().reset();
    render(<EventLogPanel />);

    const select = screen.getByLabelText('Filter by message type');
    expect(select).toBeDefined();
    expect(screen.getByText('All message types')).toBeDefined();
  });
});
