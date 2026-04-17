import { describe, it, expect, vi, beforeEach } from 'vitest';
import { TelemetryClient } from '../TelemetryClient';
import type { TelemetryTransport } from '../TelemetryClient';
import type { RunEvent, ReplayResponse } from '../types';

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

function createMockTransport(): TelemetryTransport & {
  _listeners: Map<string, ((...args: unknown[]) => void)[]>;
  _reconnectingCb: (() => void) | null;
  _reconnectedCb: (() => void) | null;
  _closeCb: ((error?: Error) => void) | null;
} {
  const listeners = new Map<string, ((...args: unknown[]) => void)[]>();
  return {
    _listeners: listeners,
    _reconnectingCb: null,
    _reconnectedCb: null,
    _closeCb: null,
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    invoke: vi.fn().mockResolvedValue(undefined),
    on(event: string, callback: (...args: unknown[]) => void) {
      const cbs = listeners.get(event) ?? [];
      cbs.push(callback);
      listeners.set(event, cbs);
    },
    onReconnecting(cb: () => void) {
      this._reconnectingCb = cb;
    },
    onReconnected(cb: () => void) {
      this._reconnectedCb = cb;
    },
    onClose(cb: (error?: Error) => void) {
      this._closeCb = cb;
    },
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

  it('stores and exposes hubUrl', () => {
    const client = new TelemetryClient({ hubUrl: 'http://localhost/hubs/telemetry' });
    expect(client.getHubUrl()).toBe('http://localhost/hubs/telemetry');
  });
});

describe('TelemetryClient with transport', () => {
  let transport: ReturnType<typeof createMockTransport>;

  beforeEach(() => {
    transport = createMockTransport();
    vi.useFakeTimers();
  });

  it('connects through transport', async () => {
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();

    expect(transport.connect).toHaveBeenCalledTimes(1);
    expect(client.getConnectionState()).toBe('connected');
  });

  it('disconnects through transport', async () => {
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();
    await client.disconnect();

    expect(transport.disconnect).toHaveBeenCalledTimes(1);
    expect(client.getConnectionState()).toBe('disconnected');
  });

  it('calls AttachRun on transport when connected', async () => {
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();
    client.attach('run-1', vi.fn());

    expect(transport.invoke).toHaveBeenCalledWith('AttachRun', 'run-1');
  });

  it('calls DetachRun on transport when connected', async () => {
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();
    client.attach('run-1', vi.fn());
    client.detach('run-1');

    expect(transport.invoke).toHaveBeenCalledWith('DetachRun', 'run-1');
  });

  it('receives events from transport ReceiveRunEvent callback', async () => {
    const handler = vi.fn();
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    client.attach('run-1', handler);
    await client.connect();

    // Simulate server pushing an event via transport
    const cbs = transport._listeners.get('ReceiveRunEvent') ?? [];
    expect(cbs.length).toBeGreaterThan(0);

    cbs[0](makeEvent('run-1', 1));
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('resumeAfterReconnect re-attaches runs and replays from lastSeenSeq', async () => {
    const handler = vi.fn();
    const replayResponse: ReplayResponse = {
      notFound: false,
      truncated: false,
      oldestAvailableSeq: 3,
      events: [makeEvent('run-1', 3), makeEvent('run-1', 4)],
    };
    (transport.invoke as ReturnType<typeof vi.fn>).mockImplementation(
      (method: string) => {
        if (method === 'Replay') return Promise.resolve(replayResponse);
        return Promise.resolve(undefined);
      },
    );

    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    client.attach('run-1', handler);
    client.receive(makeEvent('run-1', 1));
    client.receive(makeEvent('run-1', 2));

    await client.resumeAfterReconnect();

    expect(transport.invoke).toHaveBeenCalledWith('AttachRun', 'run-1');
    expect(transport.invoke).toHaveBeenCalledWith('Replay', 'run-1', 3);
    // Replayed events delivered through handler
    expect(handler).toHaveBeenCalledWith(expect.objectContaining({ seq: 3 }));
    expect(handler).toHaveBeenCalledWith(expect.objectContaining({ seq: 4 }));
  });

  it('onReconnected triggers resume flow', async () => {
    const handler = vi.fn();
    (transport.invoke as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();
    client.attach('run-1', handler);

    // Simulate reconnect
    transport._reconnectedCb?.();

    // Wait for async resume to complete
    await vi.runAllTimersAsync();

    expect(transport.invoke).toHaveBeenCalledWith('AttachRun', 'run-1');
  });

  it('onClose triggers reconnect with backoff', async () => {
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    await client.connect();

    // Simulate connection close
    transport._closeCb?.();
    expect(client.getConnectionState()).toBe('reconnecting');

    // First reconnect delay is 0ms
    await vi.advanceTimersByTimeAsync(0);
    // connect called again (initial + reconnect)
    expect(transport.connect).toHaveBeenCalledTimes(2);
  });

  it('requests replay on sequence gap detection', async () => {
    const replayResponse: ReplayResponse = {
      notFound: false,
      truncated: false,
      oldestAvailableSeq: 2,
      events: [makeEvent('run-1', 2)],
    };
    (transport.invoke as ReturnType<typeof vi.fn>).mockImplementation(
      (method: string) => {
        if (method === 'Replay') return Promise.resolve(replayResponse);
        return Promise.resolve(undefined);
      },
    );

    const handler = vi.fn();
    const client = new TelemetryClient({
      hubUrl: 'http://localhost/hubs/telemetry',
      transport,
    });

    client.attach('run-1', handler);
    client.receive(makeEvent('run-1', 1));
    // Seq 3 arrives, gap at seq 2
    client.receive(makeEvent('run-1', 3));

    // Wait for async replay
    await vi.runAllTimersAsync();

    expect(transport.invoke).toHaveBeenCalledWith('Replay', 'run-1', 2);
  });
});
