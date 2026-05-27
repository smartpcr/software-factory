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
