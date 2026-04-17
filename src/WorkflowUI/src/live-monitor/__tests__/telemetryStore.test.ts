import { describe, it, expect, beforeEach } from 'vitest';
import { useTelemetryStore, filterEvents } from '../store/telemetryStore';
import type { RunEvent, RunEventKind } from '../../telemetry/types';
import type { EventLogFilters } from '../store/telemetryStore';

function makeEvent(
  seq: number,
  kind: RunEventKind = 'NodeStarted',
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

describe('telemetryStore', () => {
  beforeEach(() => {
    useTelemetryStore.getState().reset();
  });

  it('attachRun sets runId and clears state', () => {
    useTelemetryStore.getState().attachRun('run-1');
    const state = useTelemetryStore.getState();
    expect(state.attachedRunId).toBe('run-1');
    expect(state.events).toEqual([]);
    expect(state.nodeStates.size).toBe(0);
  });

  it('addEvent appends and projects', () => {
    useTelemetryStore.getState().attachRun('run-1');
    useTelemetryStore.getState().addEvent(makeEvent(1, 'NodeStarted', 'n1'));

    const state = useTelemetryStore.getState();
    expect(state.events).toHaveLength(1);
    expect(state.nodeStates.get('n1')?.status).toBe('running');
  });

  it('addEvents batches multiple events', () => {
    useTelemetryStore.getState().attachRun('run-1');
    useTelemetryStore.getState().addEvents([
      makeEvent(1, 'NodeStarted', 'n1'),
      makeEvent(2, 'NodeCompleted', 'n1'),
      makeEvent(3, 'NodeStarted', 'n2'),
    ]);

    const state = useTelemetryStore.getState();
    expect(state.events).toHaveLength(3);
    expect(state.nodeStates.get('n1')?.status).toBe('completed');
    expect(state.nodeStates.get('n2')?.status).toBe('running');
  });

  it('reset clears everything', () => {
    useTelemetryStore.getState().attachRun('run-1');
    useTelemetryStore.getState().addEvent(makeEvent(1));
    useTelemetryStore.getState().reset();

    const state = useTelemetryStore.getState();
    expect(state.attachedRunId).toBeNull();
    expect(state.events).toEqual([]);
    expect(state.nodeStates.size).toBe(0);
  });
});

describe('filterEvents', () => {
  const events: RunEvent[] = [
    makeEvent(1, 'NodeStarted', 'n1'),
    makeEvent(2, 'NodeCompleted', 'n1'),
    makeEvent(3, 'EdgeActivated', 'n1', { triggerMessageType: 'Complete', sourceNodeId: 'n1', targetNodeId: 'n2' }),
    makeEvent(4, 'NodeStarted', 'n2'),
    makeEvent(5, 'NodeSkipped', 'n3'),
  ];

  it('empty filters return all events', () => {
    const filters: EventLogFilters = { kinds: new Set(), nodeId: null, messageType: null };
    expect(filterEvents(events, filters)).toHaveLength(5);
  });

  it('kind filter excludes unchecked kinds', () => {
    const filters: EventLogFilters = {
      kinds: new Set(['NodeStarted']),
      nodeId: null,
      messageType: null,
    };
    const result = filterEvents(events, filters);
    expect(result).toHaveLength(2);
    expect(result.every((e) => e.kind === 'NodeStarted')).toBe(true);
  });

  it('nodeId filter matches exactly', () => {
    const filters: EventLogFilters = { kinds: new Set(), nodeId: 'n1', messageType: null };
    const result = filterEvents(events, filters);
    expect(result).toHaveLength(3);
    expect(result.every((e) => e.nodeId === 'n1')).toBe(true);
  });

  it('messageType filter applies only to EdgeActivated rows', () => {
    const filters: EventLogFilters = { kinds: new Set(), nodeId: null, messageType: 'Fail' };
    const result = filterEvents(events, filters);
    // Non-edge rows are always visible; edge with Complete trigger is excluded
    const edgeEvents = result.filter((e) => e.kind === 'EdgeActivated');
    expect(edgeEvents).toHaveLength(0);
    // Non-edge rows should still be there
    const nonEdge = result.filter((e) => e.kind !== 'EdgeActivated');
    expect(nonEdge).toHaveLength(4);
  });

  it('cleared filters restore full log', () => {
    const filtered: EventLogFilters = { kinds: new Set(['NodeSkipped']), nodeId: null, messageType: null };
    expect(filterEvents(events, filtered)).toHaveLength(1);

    const cleared: EventLogFilters = { kinds: new Set(), nodeId: null, messageType: null };
    expect(filterEvents(events, cleared)).toHaveLength(5);
  });
});
