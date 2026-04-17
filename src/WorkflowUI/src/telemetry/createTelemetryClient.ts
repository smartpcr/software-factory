/**
 * Runtime factory for creating a fully-wired TelemetryClient
 * with a real SignalR transport. Import this in application code.
 */
import { TelemetryClient } from './TelemetryClient';
import { createSignalRTransport } from './SignalRTransport';

/**
 * Creates a TelemetryClient backed by a real SignalR HubConnection.
 * This is the primary entry point for production runtime code.
 *
 * @param hubUrl - The URL of the TelemetryHub endpoint (e.g. "/hubs/telemetry")
 * @returns A TelemetryClient ready to connect, attach runs, and receive events.
 *
 * @example
 * ```ts
 * const client = createTelemetryClient('/hubs/telemetry');
 * await client.connect();
 * client.attach(runId, (event) => { console.log(event); });
 * ```
 */
export function createTelemetryClient(hubUrl: string): TelemetryClient {
  const transport = createSignalRTransport(hubUrl);
  return new TelemetryClient({ hubUrl, transport });
}
