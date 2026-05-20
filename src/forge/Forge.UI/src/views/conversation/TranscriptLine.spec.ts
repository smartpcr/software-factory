// Phase 59 Stage B — Vitest unit coverage for the `hasLogAttachment`
// predicate exported from TranscriptLine.tsx. The predicate is what
// drives the "Has logs" filter chip in WorkstreamConversationPane;
// component-render assertions for the chip itself live in the
// Playwright spec (phase59-log-chip.spec.ts) where the live UI
// actually mounts.
//
// Pinning this in a fast unit test catches the common regression:
// someone tweaks the payload shape on the server and forgets to
// keep the type-narrowing here in lockstep, silently making the
// chip never render.

import { describe, expect, it } from 'vitest';
import { hasLogAttachment } from './TranscriptLine';
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';

const baseEvent: TaskTranscriptEvent = {
  id: 1,
  kind: 'task.test-gate.passed',
  at: '2026-05-19T00:00:00Z',
  workItemId: 'ws-test',
  agentId: null,
  crewId: null,
  payload: null,
};

describe('hasLogAttachment', () => {
  it('returns false when payload is null', () => {
    expect(hasLogAttachment({ ...baseEvent, payload: null })).toBe(false);
  });

  it('returns false when payload has no logUrl field', () => {
    expect(hasLogAttachment({
      ...baseEvent,
      payload: { stage: 'per-iter', iter: 1 },
    })).toBe(false);
  });

  it('returns false when logUrl is an empty string', () => {
    // Skipped events stamp `null`/missing rather than empty string,
    // but defend against an upstream bug that emits "" so the
    // filter doesn't trip false-positive.
    expect(hasLogAttachment({
      ...baseEvent,
      payload: { logUrl: '' },
    })).toBe(false);
  });

  it('returns false when logUrl is not a string (defensive)', () => {
    // payload comes off SignalR / JSON wire — defend against a
    // server-side type drift that emits a number or object here.
    expect(hasLogAttachment({
      ...baseEvent,
      payload: { logUrl: 12345 as unknown as string },
    })).toBe(false);
    expect(hasLogAttachment({
      ...baseEvent,
      payload: { logUrl: null as unknown as string },
    })).toBe(false);
  });

  it('returns true when logUrl is a non-empty string', () => {
    expect(hasLogAttachment({
      ...baseEvent,
      payload: { logUrl: '/api/workstreams/ws-test/logs/test-1-12345.log' },
    })).toBe(true);
  });

  it('matches the canonical gate-passed payload shape', () => {
    // Mirrors what CrewStoryArtifactStrategy.RunGateChainAsync
    // stamps on a green test gate (see line ~989 in
    // CrewStoryArtifactStrategy.cs).
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.test-gate.passed',
      payload: {
        stage: 'per-iter',
        gateStage: 'test',
        iter: 3,
        durationSec: 12.5,
        warningCount: 0,
        testCommand: 'dotnet test',
        effectiveTestCommand: 'dotnet test src/Foo.Tests/Foo.Tests.csproj',
        impactedRewritten: true,
        impactedProjects: ['src/Foo.Tests/Foo.Tests.csproj'],
        wholeSuiteFallback: false,
        allTestProjectCount: 5,
        impactedMode: 'roslyn',
        workspace: '/temp/worktree',
        logUrl: '/api/workstreams/ws-test/logs/test-3-12345.log',
        logRelPath: 'test-3-12345.log',
      },
    })).toBe(true);
  });

  it('matches the canonical gate-failed payload shape', () => {
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.test-gate.failed',
      payload: {
        stage: 'per-iter',
        gateStage: 'test',
        iter: 3,
        exitCode: 1,
        errorCount: 7,
        warningCount: 2,
        durationSec: 480.82,
        timedOut: false,
        errorSummary: 'Foo.Tests.SomeTest failed: expected 42, got 41',
        testCommand: 'dotnet test',
        impactedMode: 'roslyn',
        workspace: '/temp/worktree',
        logUrl: '/api/workstreams/ws-test/logs/test-3-12345.log',
        logRelPath: 'test-3-12345.log',
      },
    })).toBe(true);
  });

  it('returns false for skipped events (no log was written)', () => {
    // Per Phase 59 Stage B: skipped events DO NOT carry logUrl
    // because no command actually ran. The predicate must
    // reflect that so the "Has logs" filter correctly hides
    // skip events when on.
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.test-gate.skipped',
      payload: {
        stage: 'per-iter',
        gateStage: 'test',
        iter: 3,
        reason: 'no impacted test projects',
        testCommand: 'dotnet test',
        workspace: '/temp/worktree',
        impactedOnly: true,
        allTestProjectCount: 30,
        impactedMode: 'roslyn',
      },
    })).toBe(false);
  });

  it('also catches build-gate events with log attachments', () => {
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.build-gate.passed',
      payload: {
        stage: 'per-iter',
        gateStage: 'build',
        iter: 1,
        durationSec: 8.2,
        buildCommand: 'dotnet build',
        workspace: '/temp/worktree',
        logUrl: '/api/workstreams/ws-test/logs/build-1-12345.log',
        logRelPath: 'build-1-12345.log',
      },
    })).toBe(true);
  });

  it('returns false for non-gate transcript events', () => {
    // Generator deltas, iteration boundaries, etc. don't carry
    // logUrl. Predicate stays false so they don't ghost-render
    // a chip.
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.prompt',
      payload: { backend: 'claude', model: 'claude-opus-4-7' },
    })).toBe(false);
    expect(hasLogAttachment({
      ...baseEvent,
      kind: 'task.iteration.scored',
      payload: { iter: 1, score: 86, verdict: 'iterate' },
    })).toBe(false);
  });
});
