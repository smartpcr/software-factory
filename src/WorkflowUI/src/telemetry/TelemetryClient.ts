/**
 * TypeScript client wrapper for the SignalR TelemetryHub.
 * Handles auto-reconnect with backoff and sequence-based resume.
 *
 * Uses a transport abstraction so unit tests don't need a real hub.
 */
import type { RunEvent, ReplayResponse } from './types';
import { isDuplicate, detectSequenceGap } from './sequenceGapDetector';

/** Reconnect backoff policy: 0, 2s, 10s, 30s, 30s, ... */
const RECONNECT_DELAYS = [0, 2000, 10000, 30000];

export type RunEventHandler = (event: RunEvent) => void;

/**
 * Transport abstraction for the SignalR hub.
 * In production, wrap @microsoft/signalr HubConnection.
 * In tests, provide a mock implementation.
 */
export interface TelemetryTransport {
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  invoke<T>(method: string, ...args: unknown[]): Promise<T>;
  on(event: string, callback: (...args: unknown[]) => void): void;
  onReconnecting(callback: () => void): void;
  onReconnected(callback: () => void): void;
  onClose(callback: (error?: Error) => void): void;
}

export interface TelemetryClientOptions {
  hubUrl: string;
  transport?: TelemetryTransport;
}

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * Manages connection to TelemetryHub via a pluggable transport.
 * Provides attach/detach per run, deduplication, gap detection,
 * auto-reconnect with backoff, and sequence-based replay on resume.
 */
export class TelemetryClient {
  private readonly hubUrl: string;
  private readonly transport: TelemetryTransport | null;
  private lastSeenSeq: Map<string, number> = new Map();
  private handlers: Map<string, Set<RunEventHandler>> = new Map();
  private attachedRuns: Set<string> = new Set();
  private connectionState: ConnectionState = 'disconnected';
  private reconnectAttempt = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(options: TelemetryClientOptions) {
    this.hubUrl = options.hubUrl;
    this.transport = options.transport ?? null;

    if (this.transport) {
      this.wireTransportCallbacks(this.transport);
    }
  }

  /** Returns the hub URL this client was configured with. */
  getHubUrl(): string {
    return this.hubUrl;
  }

  /** Returns the current connection state. */
  getConnectionState(): ConnectionState {
    return this.connectionState;
  }

  /** Connects the transport (no-op if no transport provided). */
  async connect(): Promise<void> {
    if (!this.transport) return;
    this.connectionState = 'connecting';
    await this.transport.connect();
    this.connectionState = 'connected';
    this.reconnectAttempt = 0;
  }

  /** Disconnects the transport and clears reconnect timers. */
  async disconnect(): Promise<void> {
    if (this.reconnectTimer != null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (!this.transport) return;
    await this.transport.disconnect();
    this.connectionState = 'disconnected';
  }

  /** Attaches to a run and starts receiving events. */
  attach(runId: string, handler: RunEventHandler): void {
    this.attachedRuns.add(runId);
    const handlers = this.handlers.get(runId) ?? new Set<RunEventHandler>();
    handlers.add(handler);
    this.handlers.set(runId, handlers);

    if (this.transport && this.connectionState === 'connected') {
      void this.transport.invoke('AttachRun', runId);
    }
  }

  /** Detaches from a run and stops firing handlers. */
  detach(runId: string): void {
    this.attachedRuns.delete(runId);
    this.handlers.delete(runId);
    this.lastSeenSeq.delete(runId);

    if (this.transport && this.connectionState === 'connected') {
      void this.transport.invoke('DetachRun', runId);
    }
  }

  /** Processes an incoming event with dedup and ordering. */
  receive(event: RunEvent): void {
    const runId = event.runId;
    if (!this.attachedRuns.has(runId)) {
      return;
    }

    const lastSeen = this.lastSeenSeq.get(runId) ?? 0;
    if (isDuplicate(lastSeen, event.seq)) {
      return;
    }

    const gap = detectSequenceGap(lastSeen, event.seq);
    if (gap) {
      void this.requestReplay(runId, gap.gapStart);
    }

    this.lastSeenSeq.set(runId, event.seq);
    const runHandlers = this.handlers.get(runId);
    if (runHandlers) {
      for (const handler of runHandlers) {
        handler(event);
      }
    }
  }

  /** Returns the last seen sequence for a run. */
  getLastSeenSeq(runId: string): number {
    return this.lastSeenSeq.get(runId) ?? 0;
  }

  /** Returns whether a run is currently attached. */
  isAttached(runId: string): boolean {
    return this.attachedRuns.has(runId);
  }

  /** Returns the reconnect backoff delays. */
  static getReconnectDelays(): readonly number[] {
    return RECONNECT_DELAYS;
  }

  /**
   * Re-attaches all subscribed runs after a reconnect and replays
   * from each run's last seen sequence to close gaps.
   */
  async resumeAfterReconnect(): Promise<void> {
    if (!this.transport) return;

    for (const runId of this.attachedRuns) {
      await this.transport.invoke('AttachRun', runId);
      const lastSeen = this.lastSeenSeq.get(runId) ?? 0;
      await this.requestReplay(runId, lastSeen + 1);
    }
  }

  /**
   * Requests replay of events from the hub and delivers them
   * through the normal receive pipeline.
   */
  private async requestReplay(runId: string, fromSeq: number): Promise<void> {
    if (!this.transport) return;
    try {
      const response = await this.transport.invoke<ReplayResponse>(
        'Replay',
        runId,
        fromSeq,
      );
      if (response && !response.notFound && response.events) {
        for (const event of response.events) {
          this.receive(event);
        }
      }
    } catch {
      // Replay failures are non-fatal; events may arrive naturally
    }
  }

  /** Schedules a reconnect attempt using the backoff policy. */
  private scheduleReconnect(): void {
    if (!this.transport) return;
    const delayIndex = Math.min(this.reconnectAttempt, RECONNECT_DELAYS.length - 1);
    const delay = RECONNECT_DELAYS[delayIndex];
    this.reconnectAttempt++;
    this.connectionState = 'reconnecting';

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.performReconnect();
    }, delay);
  }

  /** Executes a single reconnect attempt. */
  private async performReconnect(): Promise<void> {
    if (!this.transport) return;
    try {
      this.connectionState = 'connecting';
      await this.transport.connect();
      this.connectionState = 'connected';
      this.reconnectAttempt = 0;
      await this.resumeAfterReconnect();
    } catch {
      this.scheduleReconnect();
    }
  }

  /** Wires transport lifecycle callbacks. */
  private wireTransportCallbacks(transport: TelemetryTransport): void {
    transport.on('ReceiveRunEvent', (...args: unknown[]) => {
      const event = args[0] as RunEvent;
      if (event) {
        this.receive(event);
      }
    });

    transport.onReconnecting(() => {
      this.connectionState = 'reconnecting';
    });

    transport.onReconnected(() => {
      this.connectionState = 'connected';
      this.reconnectAttempt = 0;
      void this.resumeAfterReconnect();
    });

    transport.onClose(() => {
      this.connectionState = 'disconnected';
      this.scheduleReconnect();
    });
  }
}
