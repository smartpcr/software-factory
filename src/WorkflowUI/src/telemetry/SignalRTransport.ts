/**
 * Concrete TelemetryTransport backed by @microsoft/signalr HubConnection.
 * Wraps the real SignalR client and adapts it to the TelemetryTransport interface.
 */
import {
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';
import type { TelemetryTransport } from './TelemetryClient';

export interface SignalRTransportOptions {
  hubUrl: string;
  /** Log level for SignalR diagnostics. Defaults to Warning. */
  logLevel?: LogLevel;
  /** Allowed transports. Defaults to WebSockets | ServerSentEvents. */
  transport?: HttpTransportType;
}

/**
 * Adapts a SignalR HubConnection to the TelemetryTransport interface
 * used by TelemetryClient. Handles connection lifecycle, event
 * subscription, and hub method invocation.
 */
export class SignalRTransport implements TelemetryTransport {
  private readonly connection: HubConnection;

  constructor(options: SignalRTransportOptions) {
    this.connection = new HubConnectionBuilder()
      .withUrl(options.hubUrl, {
        transport:
          options.transport ??
          (HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents),
      })
      .configureLogging(options.logLevel ?? LogLevel.Warning)
      .withAutomaticReconnect([0, 2000, 10000, 30000])
      .build();
  }

  /** Starts the SignalR connection if not already connected. */
  async connect(): Promise<void> {
    if (this.connection.state === HubConnectionState.Disconnected) {
      await this.connection.start();
    }
  }

  /** Stops the SignalR connection. */
  async disconnect(): Promise<void> {
    await this.connection.stop();
  }

  /** Invokes a hub method and returns the result. */
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    return this.connection.invoke(method, ...args) as Promise<T>;
  }

  /** Registers a callback for a hub event. */
  on(event: string, callback: (...args: unknown[]) => void): void {
    this.connection.on(event, callback);
  }

  /** Registers a callback for the reconnecting lifecycle event. */
  onReconnecting(callback: () => void): void {
    this.connection.onreconnecting(callback);
  }

  /** Registers a callback for the reconnected lifecycle event. */
  onReconnected(callback: () => void): void {
    this.connection.onreconnected(callback);
  }

  /** Registers a callback for the close lifecycle event. */
  onClose(callback: (error?: Error) => void): void {
    this.connection.onclose(callback);
  }

  /** Returns the underlying HubConnection state (for diagnostics). */
  getState(): HubConnectionState {
    return this.connection.state;
  }
}

/**
 * Factory: creates a SignalRTransport configured for the given hub URL.
 * Use with TelemetryClient: `new TelemetryClient({ hubUrl, transport: createSignalRTransport(hubUrl) })`
 */
export function createSignalRTransport(hubUrl: string): SignalRTransport {
  return new SignalRTransport({ hubUrl });
}
