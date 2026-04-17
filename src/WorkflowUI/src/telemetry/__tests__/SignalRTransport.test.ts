import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock @microsoft/signalr before importing SignalRTransport
const mockConnection = {
  state: 'Disconnected',
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  invoke: vi.fn().mockResolvedValue(undefined),
  on: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
};

const mockBuilder = {
  withUrl: vi.fn().mockReturnThis(),
  configureLogging: vi.fn().mockReturnThis(),
  withAutomaticReconnect: vi.fn().mockReturnThis(),
  build: vi.fn().mockReturnValue(mockConnection),
};

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn().mockImplementation(() => mockBuilder),
  HubConnectionState: {
    Disconnected: 'Disconnected',
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
  },
  HttpTransportType: {
    WebSockets: 1,
    ServerSentEvents: 2,
  },
  LogLevel: {
    Warning: 3,
    Information: 2,
  },
}));

import { SignalRTransport, createSignalRTransport } from '../SignalRTransport';

describe('SignalRTransport', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockConnection.state = 'Disconnected';
  });

  it('constructs HubConnection with correct URL and defaults', () => {
    new SignalRTransport({ hubUrl: 'http://localhost/hubs/telemetry' });

    expect(mockBuilder.withUrl).toHaveBeenCalledWith(
      'http://localhost/hubs/telemetry',
      expect.objectContaining({ transport: expect.any(Number) }),
    );
    expect(mockBuilder.configureLogging).toHaveBeenCalledWith(3); // LogLevel.Warning
    expect(mockBuilder.withAutomaticReconnect).toHaveBeenCalledWith([0, 2000, 10000, 30000]);
    expect(mockBuilder.build).toHaveBeenCalled();
  });

  it('connect() calls connection.start() when disconnected', async () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    await transport.connect();
    expect(mockConnection.start).toHaveBeenCalledTimes(1);
  });

  it('connect() skips start() when already connected', async () => {
    mockConnection.state = 'Connected';
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    await transport.connect();
    expect(mockConnection.start).not.toHaveBeenCalled();
  });

  it('disconnect() calls connection.stop()', async () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    await transport.disconnect();
    expect(mockConnection.stop).toHaveBeenCalledTimes(1);
  });

  it('invoke() delegates to connection.invoke()', async () => {
    mockConnection.invoke.mockResolvedValueOnce({ events: [] });
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    const result = await transport.invoke('Replay', 'run-1', 5);
    expect(mockConnection.invoke).toHaveBeenCalledWith('Replay', 'run-1', 5);
    expect(result).toEqual({ events: [] });
  });

  it('on() delegates to connection.on()', () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    const cb = vi.fn();
    transport.on('ReceiveRunEvent', cb);
    expect(mockConnection.on).toHaveBeenCalledWith('ReceiveRunEvent', cb);
  });

  it('onReconnecting() delegates to connection.onreconnecting()', () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    const cb = vi.fn();
    transport.onReconnecting(cb);
    expect(mockConnection.onreconnecting).toHaveBeenCalledWith(cb);
  });

  it('onReconnected() delegates to connection.onreconnected()', () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    const cb = vi.fn();
    transport.onReconnected(cb);
    expect(mockConnection.onreconnected).toHaveBeenCalledWith(cb);
  });

  it('onClose() delegates to connection.onclose()', () => {
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    const cb = vi.fn();
    transport.onClose(cb);
    expect(mockConnection.onclose).toHaveBeenCalledWith(cb);
  });

  it('getState() returns connection state', () => {
    mockConnection.state = 'Connected';
    const transport = new SignalRTransport({ hubUrl: '/hubs/telemetry' });
    expect(transport.getState()).toBe('Connected');
  });
});

describe('createSignalRTransport', () => {
  it('creates a SignalRTransport instance', () => {
    const transport = createSignalRTransport('/hubs/telemetry');
    expect(transport).toBeInstanceOf(SignalRTransport);
  });
});
