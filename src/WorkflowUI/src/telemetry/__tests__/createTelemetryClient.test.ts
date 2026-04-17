import { describe, it, expect, vi } from 'vitest';

// Mock SignalR transport
vi.mock('../SignalRTransport', () => ({
  createSignalRTransport: vi.fn().mockReturnValue({
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    invoke: vi.fn().mockResolvedValue(undefined),
    on: vi.fn(),
    onReconnecting: vi.fn(),
    onReconnected: vi.fn(),
    onClose: vi.fn(),
  }),
}));

import { createTelemetryClient } from '../createTelemetryClient';
import { createSignalRTransport } from '../SignalRTransport';
import { TelemetryClient } from '../TelemetryClient';

describe('createTelemetryClient', () => {
  it('creates a TelemetryClient with a SignalR transport', () => {
    const client = createTelemetryClient('/hubs/telemetry');

    expect(createSignalRTransport).toHaveBeenCalledWith('/hubs/telemetry');
    expect(client).toBeInstanceOf(TelemetryClient);
    expect(client.getHubUrl()).toBe('/hubs/telemetry');
  });

  it('resulting client can connect through the transport', async () => {
    const client = createTelemetryClient('/hubs/telemetry');
    await client.connect();

    expect(client.getConnectionState()).toBe('connected');
  });

  it('resulting client supports attach and detach', () => {
    const client = createTelemetryClient('/hubs/telemetry');
    const handler = vi.fn();

    client.attach('run-1', handler);
    expect(client.isAttached('run-1')).toBe(true);

    client.detach('run-1');
    expect(client.isAttached('run-1')).toBe(false);
  });
});
