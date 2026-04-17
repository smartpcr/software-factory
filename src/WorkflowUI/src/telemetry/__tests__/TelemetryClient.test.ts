import { describe, it, expect, vi } from 'vitest';
import { TelemetryClient } from '../TelemetryClient';
import type { RunEvent } from '../types';

function makeEvent(runId: string, seq: number, kind = 'NodeStarted' as const): RunEvent {
  return {
    runId,
    seq,
    kind,
    synthetic: false,
    timestampUtc: new Date().toISOString(),
    nodeId: 'n1',
  };
}

describe('TelemetryClient', () => {
  it('delivers events to attached handlers', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const handler = vi.fn();
    const runId = 'run-1';

    client.attach(runId, handler);
    client.receive(makeEvent(runId, 1));

    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith(expect.objectContaining({ seq: 1 }));
  });

  it('does not deliver events for non-attached runs', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const handler = vi.fn();

    client.attach('run-1', handler);
    client.receive(makeEvent('run-2', 1));

    expect(handler).not.toHaveBeenCalled();
  });

  it('deduplicates events with same seq', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const handler = vi.fn();
    const runId = 'run-1';

    client.attach(runId, handler);
    client.receive(makeEvent(runId, 1));
    client.receive(makeEvent(runId, 1)); // duplicate

    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('drops older seq events', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const handler = vi.fn();
    const runId = 'run-1';

    client.attach(runId, handler);
    client.receive(makeEvent(runId, 5));
    client.receive(makeEvent(runId, 3)); // older

    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('tracks lastSeenSeq', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const runId = 'run-1';
    client.attach(runId, vi.fn());

    expect(client.getLastSeenSeq(runId)).toBe(0);
    client.receive(makeEvent(runId, 1));
    expect(client.getLastSeenSeq(runId)).toBe(1);
    client.receive(makeEvent(runId, 5));
    expect(client.getLastSeenSeq(runId)).toBe(5);
  });

  it('detach stops handler delivery', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    const handler = vi.fn();
    const runId = 'run-1';

    client.attach(runId, handler);
    client.receive(makeEvent(runId, 1));
    client.detach(runId);
    client.receive(makeEvent(runId, 2));

    expect(handler).toHaveBeenCalledTimes(1);
    expect(client.isAttached(runId)).toBe(false);
  });

  it('reconnect backoff matches policy', () => {
    const delays = TelemetryClient.getReconnectDelays();
    expect(delays).toEqual([0, 2000, 10000, 30000]);
  });
});
