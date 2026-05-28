import { beforeEach, describe, expect, it } from 'vitest';
import {
  useTaskLiveStore,
  type TaskExecutionSnapshot,
  type TaskTranscriptEvent,
  type WorktreeSnapshot,
} from './useTaskLiveStore';

describe('useTaskLiveStore restart reset', () => {
  beforeEach(() => {
    useTaskLiveStore.setState({ entries: {} });
  });

  it('clears iteration, build/test transcript, and worktree state while preserving subscriptions', () => {
    const workItemId = 'ws-reset-entry';
    const buildEvent: TaskTranscriptEvent = {
      id: 41,
      workItemId,
      kind: 'task.gate.failed',
      agentId: 'agent-gen',
      crewId: 'crew-eng',
      payload: { gate: 'build', logUrl: '/api/workstreams/ws-reset-entry/logs/build-1.log' },
      at: '2026-05-27T00:00:00Z',
    };
    const testEvent: TaskTranscriptEvent = {
      id: 42,
      workItemId,
      kind: 'task.gate.failed',
      agentId: 'agent-gen',
      crewId: 'crew-eng',
      payload: { gate: 'test', logUrl: '/api/workstreams/ws-reset-entry/logs/test-1.log' },
      at: '2026-05-27T00:01:00Z',
    };
    const worktree: WorktreeSnapshot = {
      workstreamId: workItemId,
      worktreePath: 'E:\\forge\\repo\\.worktree\\ws-reset-entry',
      added: 1,
      modified: 2,
      deleted: 0,
      ahead: 3,
      behind: 0,
      dirty: 3,
      headSha: 'abc123',
      headMessage: 'old attempt',
      headAuthor: 'agent',
      headAt: '2026-05-27T00:02:00Z',
      truncated: false,
      at: '2026-05-27T00:02:00Z',
      branchDiff: { added: ['src/New.cs'], modified: ['src/Old.cs'], deleted: [] },
    };
    const snapshot: TaskExecutionSnapshot = {
      workItemId,
      currentIteration: 4,
      currentScore: 52,
      currentVerdict: 'stalled',
      generatorCostUsd: 1,
      evaluatorCostUsd: 2,
      totalCostUsd: 3,
      generatorInputTokens: 100,
      generatorOutputTokens: 50,
      evaluatorInputTokens: 80,
      evaluatorOutputTokens: 40,
      deliverables: [{
        id: 7,
        iter: 4,
        score: 52,
        verdict: 'stalled',
        notes: 'old failed attempt',
        dims: {},
        genCostUsd: 1,
        evalCostUsd: 2,
        createdAt: '2026-05-27T00:03:00Z',
      }],
      recentTranscript: [buildEvent, testEvent],
      worktree,
      executionStatus: 'failed',
      transcriptTotalCount: 2,
    };

    useTaskLiveStore.setState({
      entries: {
        [workItemId]: {
          snapshot,
          transcript: [buildEvent, testEvent],
          worktree,
          loading: false,
          error: 'stale load error',
          lastEventId: 42,
          refCount: 2,
          transcriptTotalCount: 2,
          loadingOlder: true,
        },
      },
    });

    useTaskLiveStore.getState().resetEntry(workItemId);

    const entry = useTaskLiveStore.getState().entries[workItemId];
    expect(entry.snapshot).toBeNull();
    expect(entry.transcript).toEqual([]);
    expect(entry.worktree).toBeNull();
    expect(entry.transcriptTotalCount).toBe(0);
    expect(entry.lastEventId).toBe(0);
    expect(entry.loadingOlder).toBe(false);
    expect(entry.error).toBeUndefined();
    expect(entry.refCount).toBe(2);
  });
});

// Phase 87 regression — when the orchestrator's qa-e2e spawn fails on a
// Windows command-line-length error (or any other non-terminal-classified
// failure), it emits `task.impl.spawn-finished {success:false}` and backs
// off. The previous catch-all `task.*` → 'live' branch kept
// executionStatus stuck on 'live', which combined with the canRetry
// formula (`!isComplete && lastDeliverable !== null && (!isLive ||
// lastVerdictIsTerminalBad)`) left the Retry button permanently
// disabled whenever the last deliverable's verdict was non-terminal
// (e.g. 'pass'). The fix special-cases spawn-finished:
//   - success === false → executionStatus = 'failed' (re-enables Retry)
//   - success === true  → leave executionStatus alone (don't flip back
//                          to 'live' for a normal cycle end)
describe('useTaskLiveStore spawn-finished status patch', () => {
  beforeEach(() => {
    useTaskLiveStore.setState({ entries: {} });
  });

  const baseSnapshot = (executionStatus: string): TaskExecutionSnapshot => ({
    workItemId: 'ws-spawn-finished',
    currentIteration: 3,
    currentScore: 94,
    currentVerdict: 'pass',
    generatorCostUsd: 1,
    evaluatorCostUsd: 2,
    totalCostUsd: 3,
    generatorInputTokens: 100,
    generatorOutputTokens: 50,
    evaluatorInputTokens: 80,
    evaluatorOutputTokens: 40,
    deliverables: [{
      id: 9,
      iter: 3,
      score: 94,
      verdict: 'pass',
      notes: 'iter3',
      dims: {},
      genCostUsd: 1,
      evalCostUsd: 2,
      createdAt: '2026-05-28T07:50:00Z',
    }],
    recentTranscript: [],
    worktree: null,
    executionStatus,
    transcriptTotalCount: 0,
  });

  const seed = (executionStatus: string) => {
    const snapshot = baseSnapshot(executionStatus);
    useTaskLiveStore.setState({
      entries: {
        'ws-spawn-finished': {
          snapshot,
          transcript: [],
          worktree: null,
          loading: false,
          lastEventId: 0,
          refCount: 1,
          transcriptTotalCount: 0,
          loadingOlder: false,
        },
      },
    });
  };

  it('flips executionStatus to "failed" on spawn-finished {success:false}', () => {
    seed('live');
    const failed: TaskTranscriptEvent = {
      id: 101,
      workItemId: 'ws-spawn-finished',
      kind: 'task.impl.spawn-finished',
      agentId: null,
      crewId: null,
      payload: { success: false, reason: 'Win32Exception: The filename or extension is too long.' },
      at: '2026-05-28T07:55:00Z',
    };

    useTaskLiveStore.getState().appendEvent('ws-spawn-finished', failed);

    const snap = useTaskLiveStore.getState().entries['ws-spawn-finished'].snapshot;
    expect(snap?.executionStatus).toBe('failed');
  });

  it('leaves executionStatus untouched on spawn-finished {success:true}', () => {
    seed('live');
    const ok: TaskTranscriptEvent = {
      id: 102,
      workItemId: 'ws-spawn-finished',
      kind: 'task.impl.spawn-finished',
      agentId: null,
      crewId: null,
      payload: { success: true },
      at: '2026-05-28T07:55:00Z',
    };

    useTaskLiveStore.getState().appendEvent('ws-spawn-finished', ok);

    const snap = useTaskLiveStore.getState().entries['ws-spawn-finished'].snapshot;
    // Must NOT have flipped to 'live' via the catch-all branch — preserve
    // the prior value so a normal cycle end doesn't disable Retry.
    expect(snap?.executionStatus).toBe('live');
  });

  it('still flips other task.* events to "live" via the catch-all', () => {
    seed('idle');
    const tick: TaskTranscriptEvent = {
      id: 103,
      workItemId: 'ws-spawn-finished',
      kind: 'task.iteration.started',
      agentId: null,
      crewId: null,
      payload: { iter: 4 },
      at: '2026-05-28T07:56:00Z',
    };

    useTaskLiveStore.getState().appendEvent('ws-spawn-finished', tick);

    const snap = useTaskLiveStore.getState().entries['ws-spawn-finished'].snapshot;
    expect(snap?.executionStatus).toBe('live');
  });
});
