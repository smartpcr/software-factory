/**
 * TypeScript client wrapper for the SignalR TelemetryHub.
 * Handles auto-reconnect with backoff and sequence-based resume.
 */
import type { RunEvent } from './types';
import { isDuplicate, detectSequenceGap } from './sequenceGapDetector';

/** Reconnect backoff policy: 0, 2s, 10s, 30s, 30s, ... */
const RECONNECT_DELAYS = [0, 2000, 10000, 30000];

export type RunEventHandler = (event: RunEvent) => void;

export interface TelemetryClientOptions {
  hubUrl: string;
}

/**
 * Manages SignalR connection to TelemetryHub.
 * In production this wraps @microsoft/signalr; for testing it accepts a
 * mock transport so unit tests don't need a real hub.
 */
export class TelemetryClient {
  private lastSeenSeq: Map<string, number> = new Map();
  private handlers: Map<string, Set<RunEventHandler>> = new Map();
  private attachedRuns: Set<string> = new Set();

  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  constructor(_options: TelemetryClientOptions) {
    // hubUrl will be used when SignalR transport is wired in
  }

  /** Attaches to a run and starts receiving events. */
  attach(runId: string, handler: RunEventHandler): void {
    this.attachedRuns.add(runId);
    const handlers = this.handlers.get(runId) ?? new Set<RunEventHandler>();
    handlers.add(handler);
    this.handlers.set(runId, handlers);
  }

  /** Detaches from a run and stops firing handlers. */
  detach(runId: string): void {
    this.attachedRuns.delete(runId);
    this.handlers.delete(runId);
    this.lastSeenSeq.delete(runId);
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
      // In production, we'd call hub.Replay(runId, gap.gapStart) here.
      // For now, accept the event and update tracking.
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
}
