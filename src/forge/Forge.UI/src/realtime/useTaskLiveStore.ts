import { useEffect } from 'react';
import { create } from 'zustand';
import { HubConnectionBuilder, LogLevel, HubConnectionState, type HubConnection } from '@microsoft/signalr';
import { forgeApi } from '@/api/forgeApi';

// Phase 56 follow-up — synthetic id generator for SignalR-pushed events when
// the server payload is missing the persisted row id (legacy server build,
// or the persistence step inside TaskChannel.EmitAsync threw). Negative
// monotonically-decreasing integers so:
//   1. The value parses as int64 — `/transcript/{eventId:long}` route still
//      matches at the constraint layer (we get a clean 404 from the handler
//      instead of a 404 from no-route-matched, which keeps client error
//      diagnostics readable).
//   2. The value is distinguishable from real DB ids (which are always
//      positive AUTOINCREMENT). PromptView's auto-fetch checks `id > 0`
//      and skips negative ids — they're not addressable, so the per-event
//      refetch would always 404. The "! truncated" hint stays visible so
//      the operator knows the live event hasn't been persisted yet.
let syntheticEventIdCounter = 0;
export function nextSyntheticEventId(): number {
  syntheticEventIdCounter -= 1;
  return syntheticEventIdCounter;
}

// ─── Types (mirrors Forge.Api/Contracts/TaskExecutionSnapshot) ────────────

export interface TaskTranscriptEvent {
  id: number;
  workItemId: string;
  kind: string;
  agentId?: string | null;
  crewId?: string | null;
  payload: unknown;
  at: string;
}

export interface WorktreeSnapshot {
  workstreamId: string;
  worktreePath?: string | null;
  added: number;
  modified: number;
  deleted: number;
  ahead: number;
  behind: number;
  dirty: number;
  headSha?: string | null;
  headMessage?: string | null;
  headAuthor?: string | null;
  headAt?: string | null;
  truncated: boolean;
  at: string;
  branchDiff?: { added: string[]; modified: string[]; deleted: string[] } | null;
}

export interface DeliverableShape {
  id: number;
  iter: number;
  score: number;
  verdict: string;
  notes?: string;
  generatorSummary?: string;
  dims: Record<string, number>;
  genCostUsd: number;
  evalCostUsd: number;
  judgeAgentId?: string | null;
  judgeScore?: number | null;
  judgeNotes?: string | null;
  createdAt: string;
}

export interface TaskExecutionSnapshot {
  workItemId: string;
  generatorCrewId?: string | null;
  generatorCrewName?: string | null;
  evaluatorCrewId?: string | null;
  evaluatorCrewName?: string | null;
  generatorAgentId?: string | null;
  generatorAgentName?: string | null;
  evaluatorAgentId?: string | null;
  evaluatorAgentName?: string | null;
  // Resolved at snapshot-load time (agent override → crew default). The
  // generator and evaluator are required to use different models per the
  // pair-diversity rule enforced on crew/agent edit.
  generatorModel?: string | null;
  evaluatorModel?: string | null;
  currentIteration: number;
  currentScore?: number | null;
  currentVerdict?: string | null;
  generatorCostUsd: number;
  evaluatorCostUsd: number;
  totalCostUsd: number;
  generatorInputTokens: number;
  generatorOutputTokens: number;
  evaluatorInputTokens: number;
  evaluatorOutputTokens: number;
  crewDailyBudgetUsd?: number | null;
  crewSpendTodayUsd?: number | null;
  maxTaskBudgetUsd?: number | null;
  autoDispatchedAt?: string | null;
  expectedFileChanges?: number | null;
  deliverables: DeliverableShape[];
  recentTranscript: TaskTranscriptEvent[];
  worktree?: WorktreeSnapshot | null;
  executionStatus?: string;
  trialCount?: number;
  maxIterations?: number;
  budgetSeconds?: number;
  transcriptTotalCount?: number;
  /** Phase 52 — current pair attempt (1 = original pair, 2 = first
   *  reassignment, 3 = second). UI badges the doc/workstream when > 1. */
  pairAttemptCount?: number;
  /** Phase 52 — max attempts before terminal failure (mirrors
   *  `ScoringConstants.MaxPairAttempts`, currently 3). */
  maxPairAttempts?: number;
  /** Phase 52 — generator tokens since `BudgetWindowStartUtc`. This is the
   *  EXACT meter the sloppy detector gates against; surfaced so the
   *  operator can see how close the run is to a sloppy reassignment. */
  generatorTokensSinceWindow?: number;
  /** Phase 52 — generator token cap (`ScoringConstants.SloppyGeneratorTokenCap`). */
  generatorTokenCap?: number;
  /**
   * Phase 54 — per-iteration prompt-size samples used by the Tokens-tab
   * sparkline and the workstream-card pill. Indexed by iter (1-based).
   * Populated from the agent_usage ledger on snapshot fetch and updated
   * in-place when `task.iteration.prompt-growth` events arrive.
   */
  promptGrowth?: IterationPromptGrowth[];
  /** Phase 54 — mirror of `ScoringConstants.PromptBloatGrowthThreshold`. */
  promptBloatGrowthThreshold?: number;
}

/** Phase 54 — one entry in `TaskExecutionSnapshot.promptGrowth`. */
export interface IterationPromptGrowth {
  iter: number;
  generatorInputTokens: number;
  generatorOutputTokens: number;
  evaluatorInputTokens: number;
  evaluatorOutputTokens: number;
  /** Raw JSON array of per-AssistantUsageEvent samples; null when not captured. */
  generatorTurnSamplesJson?: string | null;
  evaluatorTurnSamplesJson?: string | null;
  /** Set when a `task.prompt-bloat` warning fires for this iteration. */
  promptBloatRole?: 'generator' | 'evaluator';
  promptBloatDelta?: number;
}

// ─── Store (one entry per work-item) ──────────────────────────────────────

interface TaskLiveEntry {
  snapshot: TaskExecutionSnapshot | null;
  transcript: TaskTranscriptEvent[];
  worktree: WorktreeSnapshot | null;
  loading: boolean;
  error?: string;
  lastEventId: number;
  refCount: number;
  transcriptTotalCount: number;
  loadingOlder: boolean;
}

interface TaskLiveStore {
  entries: Record<string, TaskLiveEntry>;
  ensureLoaded: (workItemId: string) => Promise<void>;
  appendEvent: (workItemId: string, evt: TaskTranscriptEvent) => void;
  applyWorktree: (workItemId: string, snap: WorktreeSnapshot) => void;
  resetEntry: (workItemId: string) => void;
  attach: (workItemId: string) => void;
  detach: (workItemId: string) => void;
  loadOlderEvents: (workItemId: string) => Promise<void>;
}

const TRANSCRIPT_CAP = 1000;

const baseUrl = (typeof import.meta !== 'undefined' && import.meta.env?.VITE_FORGE_API_URL) || '';
const hubUrl = `${baseUrl}/hubs/forge`;

let taskConnection: HubConnection | null = null;
let taskStarting: Promise<void> | null = null;

async function ensureTaskConnection(): Promise<HubConnection> {
  if (taskConnection && taskConnection.state === HubConnectionState.Connected) return taskConnection;
  if (taskStarting) {
    await taskStarting;
    return taskConnection!;
  }
  if (!taskConnection) {
    taskConnection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    // Phase 16.7 — single SignalR method routes per-work-item events. The
    // payload's `workItemId` discriminator picks which entry the event
    // updates; the `kind` field picks the renderer in the UI.
    taskConnection.on('task.event', (msg: { id?: number; workItemId?: string; kind?: string; agentId?: string; crewId?: string; payload?: unknown; at?: string }) => {
      if (!msg?.workItemId || !msg.kind) return;
      // Phase 56 follow-up (2026-05-13) — TaskChannel.EmitAsync now ships the
      // persisted row id on every broadcast. Use it when present so the
      // PromptView's per-event refetch
      // (GET /api/work-items/{wi}/transcript/{eventId:long}) can resolve the
      // full untruncated payload. Fallback below is for events from older
      // server builds OR from an EmitAsync invocation whose persistence step
      // threw (the SignalR push still happens with id=0 in that case): use a
      // monotonically decreasing NEGATIVE integer so the value still parses
      // as :long (route match, not 400/404 from constraint), then PromptView
      // suppresses the per-event refetch for negative ids since they are
      // not addressable in the DB.
      const rawId = typeof msg.id === 'number' && Number.isFinite(msg.id) && msg.id > 0
        ? msg.id
        : nextSyntheticEventId();
      const evt: TaskTranscriptEvent = {
        id: rawId,
        workItemId: msg.workItemId,
        kind: msg.kind,
        agentId: msg.agentId ?? null,
        crewId: msg.crewId ?? null,
        payload: msg.payload ?? {},
        at: msg.at ?? new Date().toISOString(),
      };
      useTaskLiveStore.getState().appendEvent(msg.workItemId, evt);
      if (msg.kind === 'task.worktree-tick' && msg.payload && typeof msg.payload === 'object') {
        const p = msg.payload as Record<string, unknown>;
        const head = (p.head ?? null) as { sha?: string; message?: string; author?: string; at?: string } | null;
        const snap: WorktreeSnapshot = {
          workstreamId: typeof p.workstreamId === 'string' ? p.workstreamId : '',
          worktreePath: typeof p.worktreePath === 'string' ? p.worktreePath : null,
          added:    typeof p.added    === 'number' ? p.added    : 0,
          modified: typeof p.modified === 'number' ? p.modified : 0,
          deleted:  typeof p.deleted  === 'number' ? p.deleted  : 0,
          ahead:  typeof p.ahead  === 'number' ? p.ahead  : 0,
          behind: typeof p.behind === 'number' ? p.behind : 0,
          dirty:  typeof p.dirty  === 'number' ? p.dirty  : 0,
          headSha:     head?.sha ?? null,
          headMessage: head?.message ?? null,
          headAuthor:  head?.author ?? null,
          headAt:      head?.at ?? null,
          truncated: p.truncated === true,
          at: msg.at ?? new Date().toISOString(),
          branchDiff: (() => {
            const bd = p.branchDiff as { added?: string[]; modified?: string[]; deleted?: string[] } | null;
            if (!bd) return null;
            return { added: bd.added ?? [], modified: bd.modified ?? [], deleted: bd.deleted ?? [] };
          })(),
        };
        useTaskLiveStore.getState().applyWorktree(msg.workItemId, snap);
      }
    });
    // Server-side SignalR group membership is keyed by ConnectionId and is
    // wiped on every reconnect. withAutomaticReconnect re-establishes the
    // transport but does NOT replay invocations, so without this handler the
    // conversation tab silently stops receiving task.event after the first
    // network blip — events then only re-appear when the user toggles the
    // tab (which forces detach + re-attach + Subscribe).
    taskConnection.onreconnected(async () => {
      const conn = taskConnection;
      if (!conn) return;
      const entries = useTaskLiveStore.getState().entries;
      for (const [workItemId, entry] of Object.entries(entries)) {
        if (entry.refCount > 0) {
          try { await conn.invoke('Subscribe', `task.${workItemId}`); } catch { /* ignore */ }
        }
      }
    });
  }
  taskStarting = taskConnection.start().catch((err) => {
    if (typeof import.meta !== 'undefined' && import.meta.env?.DEV) {
      console.warn('[forge] task SignalR connect failed', err);
    }
  }).finally(() => {
    taskStarting = null;
  });
  await taskStarting;
  return taskConnection;
}

export const useTaskLiveStore = create<TaskLiveStore>((set, get) => ({
  entries: {},

  ensureLoaded: async (workItemId) => {
    const cur = get().entries[workItemId];
    if (cur?.snapshot) return; // already hydrated
    set({ entries: { ...get().entries, [workItemId]: cur ? { ...cur, loading: true, error: undefined } : { snapshot: null, transcript: [], worktree: null, loading: true, lastEventId: 0, refCount: 0, transcriptTotalCount: 0, loadingOlder: false } } });
    try {
      const snap: TaskExecutionSnapshot = await forgeApi.getWorkItemExecution(workItemId) as TaskExecutionSnapshot;
      set({
        entries: {
          ...get().entries,
          [workItemId]: {
            snapshot: snap,
            transcript: snap.recentTranscript ?? [],
            worktree: snap.worktree ?? null,
            loading: false,
            lastEventId: 0,
            refCount: get().entries[workItemId]?.refCount ?? 0,
            transcriptTotalCount: snap.transcriptTotalCount ?? (snap.recentTranscript?.length ?? 0),
            loadingOlder: false,
          },
        },
      });
    } catch (err) {
      set({
        entries: {
          ...get().entries,
          [workItemId]: {
            snapshot: get().entries[workItemId]?.snapshot ?? null,
            transcript: get().entries[workItemId]?.transcript ?? [],
            worktree: get().entries[workItemId]?.worktree ?? null,
            loading: false,
            error: err instanceof Error ? err.message : String(err),
            lastEventId: 0,
            refCount: get().entries[workItemId]?.refCount ?? 0,
            transcriptTotalCount: get().entries[workItemId]?.transcriptTotalCount ?? 0,
            loadingOlder: false,
          },
        },
      });
    }
  },

  appendEvent: (workItemId, evt) => {
    const cur = get().entries[workItemId] ?? { snapshot: null, transcript: [], worktree: null, loading: false, lastEventId: 0, refCount: 0, transcriptTotalCount: 0, loadingOlder: false };
    const next = cur.transcript.length >= TRANSCRIPT_CAP ? [...cur.transcript.slice(-TRANSCRIPT_CAP + 1), evt] : [...cur.transcript, evt];
    // Patch snapshot agent/crew fields from lifecycle events that carry
    // authoritative identifiers. Fixes the "—" generator glitch after
    // restart: the initial snapshot is fetched before the agent picks up,
    // so generatorAgentId is null. When the claim emits task.delegated or
    // task.iteration.started, we patch the snapshot in-place so the header
    // updates without a full re-fetch.
    let snap = cur.snapshot;
    if (snap && evt.payload && typeof evt.payload === 'object') {
      const p = evt.payload as Record<string, unknown>;
      if (evt.kind === 'task.delegated' || evt.kind === 'task.iteration.started') {
        const patched = { ...snap };
        let changed = false;
        if (typeof p.generatorCrewId === 'string' && p.generatorCrewId) { patched.generatorCrewId = p.generatorCrewId; changed = true; }
        if (typeof p.evaluatorCrewId === 'string' && p.evaluatorCrewId) { patched.evaluatorCrewId = p.evaluatorCrewId; changed = true; }
        if (typeof p.generatorAgentId === 'string' && p.generatorAgentId) { patched.generatorAgentId = p.generatorAgentId; changed = true; }
        if (typeof p.evaluatorAgentId === 'string' && p.evaluatorAgentId) { patched.evaluatorAgentId = p.evaluatorAgentId; changed = true; }
        // Resolve display names from crewName/agentName if provided.
        if (typeof p.generatorCrewName === 'string') { patched.generatorCrewName = p.generatorCrewName; changed = true; }
        if (typeof p.evaluatorCrewName === 'string') { patched.evaluatorCrewName = p.evaluatorCrewName; changed = true; }
        if (typeof p.generatorAgentName === 'string') { patched.generatorAgentName = p.generatorAgentName; changed = true; }
        if (typeof p.evaluatorAgentName === 'string') { patched.evaluatorAgentName = p.evaluatorAgentName; changed = true; }
        if (changed) snap = patched;
      }
      if (evt.kind === 'task.iteration.scored') {
        const patched = { ...snap };
        if (typeof p.score === 'number') patched.currentScore = p.score;
        if (typeof p.verdict === 'string') patched.currentVerdict = p.verdict;
        if (typeof p.iter === 'number') patched.currentIteration = p.iter;
        // Cost: server emits totalCostUsd as the cumulative figure and
        // generatorCostUsd / evaluatorCostUsd as per-iter deltas. Replace
        // total with the cumulative value (authoritative); accumulate the
        // role-split deltas onto the snapshot's role buckets so the
        // workstream/doc Conversation pane's TOTAL COST + pair-side
        // tokens refresh during live runs without needing a snapshot
        // re-fetch.
        if (typeof p.totalCostUsd === 'number') patched.totalCostUsd = p.totalCostUsd;
        if (typeof p.generatorCostUsd === 'number') {
          patched.generatorCostUsd = (patched.generatorCostUsd ?? 0) + p.generatorCostUsd;
        }
        if (typeof p.evaluatorCostUsd === 'number') {
          patched.evaluatorCostUsd = (patched.evaluatorCostUsd ?? 0) + p.evaluatorCostUsd;
        }
        snap = patched;
      }
      // Per-event token deltas — the runner emits them on task.token /
      // task.tool-result with role and inputTokens/outputTokens. Accumulate
      // onto the right side of the snapshot so the live pair tokens render
      // even between iteration boundaries.
      if (evt.kind === 'task.token' || evt.kind === 'task.tool-result' || evt.kind === 'task.toolresult') {
        const role = (p.role as string) ?? '';
        const inTok = typeof p.inputTokens === 'number' ? p.inputTokens : 0;
        const outTok = typeof p.outputTokens === 'number' ? p.outputTokens : 0;
        if (snap && (inTok > 0 || outTok > 0)) {
          const patched = { ...snap };
          if (role === 'evaluator') {
            patched.evaluatorInputTokens = (patched.evaluatorInputTokens ?? 0) + inTok;
            patched.evaluatorOutputTokens = (patched.evaluatorOutputTokens ?? 0) + outTok;
          } else {
            patched.generatorInputTokens = (patched.generatorInputTokens ?? 0) + inTok;
            patched.generatorOutputTokens = (patched.generatorOutputTokens ?? 0) + outTok;
          }
          // Per-event cost is also commonly attached.
          if (typeof p.costUsd === 'number' && p.costUsd > 0) {
            patched.totalCostUsd = (patched.totalCostUsd ?? 0) + p.costUsd;
            if (role === 'evaluator') {
              patched.evaluatorCostUsd = (patched.evaluatorCostUsd ?? 0) + p.costUsd;
            } else {
              patched.generatorCostUsd = (patched.generatorCostUsd ?? 0) + p.costUsd;
            }
          }
          snap = patched;
        }
      }
      // task.prompt carries the actual model the runner just dispatched
      // with — patch the snapshot so the header reflects runtime overrides
      // (agent.GeneratorModel / agent.EvaluatorModel) without a full refetch.
      if (evt.kind === 'task.prompt' && typeof p.model === 'string' && p.model) {
        const patched = { ...snap };
        if (p.role === 'evaluator') {
          if (patched.evaluatorModel !== p.model) { patched.evaluatorModel = p.model; snap = patched; }
        } else {
          if (patched.generatorModel !== p.model) { patched.generatorModel = p.model; snap = patched; }
        }
      }
      // Phase 54 — per-iteration prompt-size growth + bloat warnings.
      if (evt.kind === 'task.iteration.prompt-growth' && typeof p.iter === 'number' && snap) {
        const iter = p.iter as number;
        const gen = (p.generator ?? {}) as Record<string, unknown>;
        const ev  = (p.evaluator ?? {}) as Record<string, unknown>;
        const sample: IterationPromptGrowth = {
          iter,
          generatorInputTokens:  typeof gen.inputTokens === 'number'  ? gen.inputTokens  : 0,
          generatorOutputTokens: typeof gen.outputTokens === 'number' ? gen.outputTokens : 0,
          evaluatorInputTokens:  typeof ev.inputTokens === 'number'   ? ev.inputTokens   : 0,
          evaluatorOutputTokens: typeof ev.outputTokens === 'number'  ? ev.outputTokens  : 0,
          generatorTurnSamplesJson: typeof gen.turnSamplesJson === 'string' ? (gen.turnSamplesJson as string) : null,
          evaluatorTurnSamplesJson: typeof ev.turnSamplesJson === 'string'  ? (ev.turnSamplesJson as string)  : null,
        };
        const cur = snap.promptGrowth ?? [];
        const existingIdx = cur.findIndex((g) => g.iter === iter);
        const next = existingIdx >= 0
          ? cur.map((g, i) => i === existingIdx ? { ...g, ...sample } : g)
          : [...cur, sample].sort((a, b) => a.iter - b.iter);
        snap = { ...snap, promptGrowth: next };
      }
      if (evt.kind === 'task.prompt-bloat' && typeof p.iter === 'number' && snap) {
        const iter = p.iter as number;
        const role = p.role === 'evaluator' ? 'evaluator' : 'generator';
        const delta = typeof p.delta === 'number' ? p.delta : 0;
        const cur = snap.promptGrowth ?? [];
        const existingIdx = cur.findIndex((g) => g.iter === iter);
        if (existingIdx >= 0) {
          const next = cur.map((g, i) => i === existingIdx
            ? { ...g, promptBloatRole: role as 'generator' | 'evaluator', promptBloatDelta: delta }
            : g);
          snap = { ...snap, promptGrowth: next };
        }
      }
    }
    // Patch executionStatus from lifecycle events. The snapshot is fetched
    // once on attach, so post-fetch transitions (failed → restart → live,
    // or live → completed) would otherwise leave the badge stuck on the
    // stale status — any event arriving for this work item proves the run
    // is in motion; the terminal kinds flip to their respective end states.
    if (snap) {
      let nextStatus: string | undefined;
      if (evt.kind === 'task.failed' || evt.kind === 'task.delivery-failed') {
        nextStatus = 'failed';
      } else if (evt.kind === 'task.completed') {
        nextStatus = 'completed';
      } else if (evt.kind === 'task.impl.spawn-finished') {
        // task.impl.spawn-finished is a per-cycle marker, not a terminal
        // lifecycle event — but when success === false the orchestrator
        // backed off and the work item is NOT currently in motion, so we
        // must NOT fall through to the catch-all 'live' branch (that
        // would keep the Retry button disabled because canRetry requires
        // !isLive when the latest verdict is not terminal-bad). Surface
        // a "failed" badge so operators can act. When success === true
        // leave executionStatus alone: the next event (spawn-started,
        // iteration.started, or the snapshot refresh on pr-opened) will
        // drive the correct next state.
        const p = evt.payload as Record<string, unknown> | null | undefined;
        if (p && p.success === false) {
          nextStatus = 'failed';
        }
      } else if (evt.kind.startsWith('task.')) {
        nextStatus = 'live';
      }
      if (nextStatus && snap.executionStatus !== nextStatus) {
        snap = { ...snap, executionStatus: nextStatus };
      }
    }
    set({ entries: { ...get().entries, [workItemId]: { ...cur, snapshot: snap, transcript: next, lastEventId: Math.max(cur.lastEventId, evt.id), transcriptTotalCount: cur.transcriptTotalCount + 1 } } });
  },

  applyWorktree: (workItemId, snap) => {
    const cur = get().entries[workItemId] ?? { snapshot: null, transcript: [], worktree: null, loading: false, lastEventId: 0, refCount: 0, transcriptTotalCount: 0, loadingOlder: false };
    set({ entries: { ...get().entries, [workItemId]: { ...cur, worktree: snap } } });
  },

  resetEntry: (workItemId) => {
    const cur = get().entries[workItemId];
    set({
      entries: {
        ...get().entries,
        [workItemId]: {
          snapshot: null,
          transcript: [],
          worktree: null,
          loading: false,
          error: undefined,
          lastEventId: 0,
          refCount: cur?.refCount ?? 0,
          transcriptTotalCount: 0,
          loadingOlder: false,
        },
      },
    });
  },

  attach: (workItemId) => {
    const cur = get().entries[workItemId] ?? { snapshot: null, transcript: [], worktree: null, loading: false, lastEventId: 0, refCount: 0, transcriptTotalCount: 0, loadingOlder: false };
    const next = { ...cur, refCount: cur.refCount + 1 };
    set({ entries: { ...get().entries, [workItemId]: next } });
    if (cur.refCount === 0) {
      // First subscriber — wire SignalR group on the shared connection.
      void (async () => {
        const conn = await ensureTaskConnection();
        try { await conn.invoke('Subscribe', `task.${workItemId}`); } catch { /* ignore */ }
      })();
    }
  },

  detach: (workItemId) => {
    const cur = get().entries[workItemId];
    if (!cur) return;
    const next = { ...cur, refCount: Math.max(0, cur.refCount - 1) };
    set({ entries: { ...get().entries, [workItemId]: next } });
    if (next.refCount === 0 && taskConnection?.state === HubConnectionState.Connected) {
      void taskConnection.invoke('Unsubscribe', `task.${workItemId}`).catch(() => { /* ignore */ });
    }
  },

  loadOlderEvents: async (workItemId) => {
    const cur = get().entries[workItemId];
    if (!cur || cur.loadingOlder) return;
    // Find the oldest event id in the current transcript.
    const oldestId = cur.transcript.reduce((min, e) => (typeof e.id === 'number' && e.id < min ? e.id : min), Infinity);
    if (!isFinite(oldestId)) return;
    set({ entries: { ...get().entries, [workItemId]: { ...cur, loadingOlder: true } } });
    try {
      const result = await forgeApi.getWorkItemTranscript(workItemId, { before: oldestId, limit: 500, order: 'desc' });
      const events = (result.events ?? []) as TaskTranscriptEvent[];
      if (events.length === 0) {
        set({ entries: { ...get().entries, [workItemId]: { ...get().entries[workItemId]!, loadingOlder: false } } });
        return;
      }
      const existing = get().entries[workItemId]!;
      const existingIds = new Set(existing.transcript.map((e) => e.id));
      const newEvents = events.filter((e) => !existingIds.has(e.id));
      const merged = [...newEvents, ...existing.transcript];
      set({
        entries: {
          ...get().entries,
          [workItemId]: {
            ...existing,
            transcript: merged,
            loadingOlder: false,
            transcriptTotalCount: result.totalCount ?? existing.transcriptTotalCount,
          },
        },
      });
    } catch {
      set({ entries: { ...get().entries, [workItemId]: { ...get().entries[workItemId]!, loadingOlder: false } } });
    }
  },
}));

/**
 * Phase 16.13 — subscribe to a work item's live channel.
 * Hydrates the snapshot via the REST endpoint, joins the SignalR group, and
 * leaves on unmount. Multiple components viewing the same work item share
 * one server-side subscription via the ref-counted attach/detach.
 */
export function useTaskLive(workItemId: string | null | undefined): TaskLiveEntry | null {
  const entry = useTaskLiveStore((s) => (workItemId ? s.entries[workItemId] : null)) ?? null;

  useEffect(() => {
    if (!workItemId) return;
    void useTaskLiveStore.getState().ensureLoaded(workItemId);
    useTaskLiveStore.getState().attach(workItemId);
    return () => {
      useTaskLiveStore.getState().detach(workItemId);
    };
  }, [workItemId]);

  return entry;
}
