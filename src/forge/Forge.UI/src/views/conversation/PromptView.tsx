// Insight I.4 (docs/forge/workstream-insights.md) — verbatim
// system + user prompt for the active iter, per role. Sourced PRIMARILY
// from the durable Phase-82 `iteration_prompt` audit table via
// `GET /api/work-items/{id}/iteration-prompts` (full untruncated
// payload). Live `task.prompt` transcript events are still consumed
// as a hot-path fallback so an in-flight iter renders before the next
// persisted fetch completes.
//
// Operator-facing audit: "what context did the agent actually see"
// for the iter whose chip is selected. The view answers four
// questions per iter:
//   1) implementation-plan stage text   (the gate brief)
//   2) prior 3 iter-notes context        (carry-forward block)
//   3) prior evaluator feedback          (evalNotes chain)
//   4) global rules / decision pins      (pins block)
//
// Iter selection: PromptView owns its own pager whose chips are the
// iters that ACTUALLY have prompt rows (persisted ∪ live). The parent
// pane hides its deliverable-iter pager when the Prompt sub-tab is
// active so only one pager is visible at a time. Generator card sits
// above evaluator card per `roleOrder` (0,1).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { forgeApi, type IterationPromptDto } from '@/api/forgeApi';
import { buildPageWindow } from './filters';

interface PromptPayload {
  agentId?: string;
  crewId?: string;
  systemPrompt?: string | null;
  userPrompt?: string;
  model?: string;
  backend?: string;
  invokeMode?: string;
  role?: string;
  iter?: number;
  attempt?: number;
}

interface Props {
  /** Full transcript; we filter to task.prompt locally so the view
   *  follows the live store without an extra fetch. */
  events: TaskTranscriptEvent[];
  /** 1-based iter number to display. When null the latest iter wins. */
  activeIter: number | null;
  /** Work-item id — needed to hit the persisted iteration-prompts
   *  endpoint AND the per-event /transcript/{id} fallback. */
  workItemId: string;
}

const TRUNCATION_MARKER = '[truncated';

function isTruncated(s: string | null | undefined): boolean {
  return !!s && s.includes(TRUNCATION_MARKER);
}

// Cheap fingerprint that flips whenever a *new* persisted iter could
// be available. We refetch on mount, on workItemId change, and when
// the live transcript shows a NEW iter-numbered task.prompt event (a
// signal CrewQueueWorker just persisted the corresponding row). We
// deliberately don't refetch on every transcript append — most
// appends are token deltas, not new prompts.
function persistedFingerprint(events: TaskTranscriptEvent[]): string {
  let maxIter = 0;
  let promptCount = 0;
  for (const e of events) {
    if (e.kind !== 'task.prompt') continue;
    promptCount++;
    const it = (e.payload as PromptPayload | undefined)?.iter;
    if (typeof it === 'number' && it > maxIter) maxIter = it;
  }
  return `${maxIter}:${promptCount}`;
}

export function PromptView({ events, activeIter, workItemId }: Props): JSX.Element {
  // ─── Persisted source (primary) ───────────────────────────────
  const [persisted, setPersisted] = useState<{
    iter: number;
    generator: IterationPromptDto | null;
    evaluator: IterationPromptDto | null;
    generatorAttempts: number;
    evaluatorAttempts: number;
  }[]>([]);
  const [persistedError, setPersistedError] = useState<string | null>(null);
  const fingerprint = useMemo(() => persistedFingerprint(events), [events]);

  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        const resp = await forgeApi.getWorkItemIterationPrompts(workItemId);
        if (!active) return;
        setPersisted(resp.iters ?? []);
        setPersistedError(null);
      } catch (err) {
        if (!active) return;
        setPersistedError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => { active = false; };
  }, [workItemId, fingerprint]);

  // ─── Live source (fallback for the currently-running iter) ────
  // Pair payloads with the source event id so we can re-fetch per-event
  // when the live store truncated them. Used only when persisted is
  // missing the iter (e.g. the in-flight iter, which CrewQueueWorker
  // persists right BEFORE runner.StartAsync — there's a small window
  // where the live emit beats the SaveAsync to the wire).
  const promptEvents = useMemo(() => {
    const list: { id: number; payload: PromptPayload }[] = [];
    for (const e of events) {
      if (e.kind !== 'task.prompt') continue;
      list.push({ id: e.id, payload: (e.payload ?? {}) as PromptPayload });
    }
    return list;
  }, [events]);

  // ─── Iter selection ───────────────────────────────────────────
  // Available iters = union of persisted + live, ascending. These ARE
  // the chips the pager renders — they map 1:1 onto iters that have
  // prompt rows (no empty slots).
  const availableIters = useMemo(() => {
    const set = new Set<number>();
    for (const p of persisted) set.add(p.iter);
    for (const p of promptEvents) {
      const it = p.payload.iter;
      if (typeof it === 'number' && it > 0) set.add(it);
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [persisted, promptEvents]);

  // Local pager state. Initial value uses the parent's `activeIter`
  // when that maps onto an available prompt iter; otherwise the latest
  // available iter (so the tab opens on real data, not an empty page).
  // After mount, the operator drives navigation via the in-tab pager
  // below — we deliberately don't re-sync with `activeIter` because
  // the parent pane hides its own iter pager while the Prompt tab is
  // active, so there's no conflicting cursor to track.
  const [selectedIter, setSelectedIter] = useState<number | null>(null);

  // Effective iter: explicit in-tab selection wins; else parent's
  // `activeIter` when it's a valid prompt iter; else latest available.
  const iter = useMemo(() => {
    if (selectedIter !== null && availableIters.includes(selectedIter)) return selectedIter;
    if (activeIter !== null && activeIter !== undefined && availableIters.includes(activeIter)) return activeIter;
    return availableIters.length > 0 ? availableIters[availableIters.length - 1] : null;
  }, [selectedIter, activeIter, availableIters]);

  // ─── Resolve the rows to render for the active iter ───────────
  const persistedForIter = useMemo(
    () => persisted.find((p) => p.iter === iter) ?? null,
    [persisted, iter],
  );
  const liveForIter = useMemo(
    () => promptEvents.filter((p) => p.payload.iter === iter),
    [promptEvents, iter],
  );

  // Per-event "full payload" cache for the LIVE fallback path
  // (preserves the prior cancel-race fix that's been hardened over
  // operator-reported bugs — see refs block below).
  const [fullById, setFullById] = useState<Record<number, PromptPayload>>({});
  const [loadingIds, setLoadingIds] = useState<Set<number>>(new Set());
  const [failedById, setFailedById] = useState<Record<number, string>>({});

  // Phase 56 follow-up (operator-reported 2026-05-13: "loading full prompt…
  // never resolves; no button to click") — refs mirror the bookkeeping
  // state so the auto-fetch useEffect can read the latest values without
  // including them in its dependency array. Pre-fix the deps included
  // `loadingIds`/`failedById`/`fullById` and the effect's first action
  // was `setLoadingIds(...)`. React then ran the cleanup BEFORE the
  // fetch's `.then(...)` resolved, so the post-fetch setters bailed
  // out — leaving `loadingIds` populated forever and the "Retry"
  // button never appearing.
  const fullByIdRef = useRef(fullById);
  const loadingIdsRef = useRef(loadingIds);
  const failedByIdRef = useRef(failedById);
  useEffect(() => { fullByIdRef.current = fullById; }, [fullById]);
  useEffect(() => { loadingIdsRef.current = loadingIds; }, [loadingIds]);
  useEffect(() => { failedByIdRef.current = failedById; }, [failedById]);

  useEffect(() => {
    // Only auto-fetch when persisted source is missing for this iter —
    // if persisted has it, we use that and ignore live truncation.
    if (persistedForIter && (persistedForIter.generator || persistedForIter.evaluator)) return;

    const needsFetch = liveForIter.filter((p) => {
      if (typeof p.id !== 'number' || p.id <= 0) return false;
      if (fullByIdRef.current[p.id]) return false;
      if (loadingIdsRef.current.has(p.id)) return false;
      if (failedByIdRef.current[p.id] !== undefined) return false;
      return isTruncated(p.payload.systemPrompt) || isTruncated(p.payload.userPrompt);
    });
    if (needsFetch.length === 0) return;

    setLoadingIds((prev) => {
      const next = new Set(prev);
      for (const p of needsFetch) next.add(p.id);
      return next;
    });

    void Promise.allSettled(
      needsFetch.map((p) => forgeApi.getWorkItemTranscriptEvent(workItemId, p.id)),
    ).then((results) => {
      setFullById((prev) => {
        const next = { ...prev };
        results.forEach((r, idx) => {
          if (r.status === 'fulfilled' && r.value && r.value.payload) {
            next[needsFetch[idx].id] = r.value.payload as PromptPayload;
          }
        });
        return next;
      });
      setFailedById((prev) => {
        const next = { ...prev };
        results.forEach((r, idx) => {
          const id = needsFetch[idx].id;
          if (r.status === 'rejected') {
            next[id] = r.reason instanceof Error ? r.reason.message : String(r.reason);
          } else if (r.status === 'fulfilled' && !(r.value && r.value.payload)) {
            next[id] = 'empty payload';
          }
        });
        return next;
      });
      setLoadingIds((prev) => {
        const next = new Set(prev);
        for (const p of needsFetch) next.delete(p.id);
        return next;
      });
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liveForIter, persistedForIter, workItemId]);

  // ─── Build the ordered (generator, evaluator) render list ─────
  const rendered = useMemo<{ key: string; payload: PromptPayload; isLive: boolean; eventId?: number; attemptCount?: number }[]>(() => {
    // Persisted wins per role; live fills in only when persisted is null.
    const fromPersisted: { key: string; payload: PromptPayload; isLive: boolean; attemptCount?: number }[] = [];
    if (persistedForIter?.generator) {
      fromPersisted.push({
        key: `p-gen-${iter}`,
        payload: persistedDtoToPayload(persistedForIter.generator, iter ?? 0),
        isLive: false,
        attemptCount: persistedForIter.generatorAttempts,
      });
    }
    if (persistedForIter?.evaluator) {
      fromPersisted.push({
        key: `p-eval-${iter}`,
        payload: persistedDtoToPayload(persistedForIter.evaluator, iter ?? 0),
        isLive: false,
        attemptCount: persistedForIter.evaluatorAttempts,
      });
    }
    // Roles already covered by persisted.
    const rolesCovered = new Set(fromPersisted.map((p) => (p.payload.role ?? '').toLowerCase()));

    const fromLive: { key: string; payload: PromptPayload; isLive: boolean; eventId: number }[] = [];
    for (const p of liveForIter) {
      const role = (p.payload.role ?? '').toLowerCase();
      if (rolesCovered.has(role)) continue;
      const full = fullById[p.id];
      fromLive.push({
        key: `l-${p.id}`,
        payload: full ?? p.payload,
        isLive: true,
        eventId: p.id,
      });
    }
    return [...fromPersisted, ...fromLive].sort(
      (a, b) => roleOrder(a.payload.role) - roleOrder(b.payload.role),
    );
  }, [persistedForIter, liveForIter, fullById, iter]);

  // ─── Render ────────────────────────────────────────────────────
  const totalIters = availableIters.length;
  const activeIndex = iter !== null ? availableIters.indexOf(iter) : -1;

  if (totalIters === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO PROMPTS YET</div>
        <div className="sub">
          Once the gen↔eval loop fires its first <code>task.prompt</code>
          event, this tab will show the verbatim system + user prompt
          the agent saw. Persisted prompts (Phase 82.1) survive transcript
          retention; the live SignalR stream is used as a hot-path
          fallback for the currently-running iter.
        </div>
        {persistedError && (
          <div className="sub" style={{ color: 'var(--sc-red, #f87171)', marginTop: 8 }}>
            persisted source unavailable: {persistedError}
          </div>
        )}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 8 }}>
      {/* Prompt iteration pager — chips paginate iters with actual
          prompt rows (persisted ∪ live). The parent pane hides its
          deliverable-iter pager when this sub-tab is active so only
          ONE pager is visible at a time. Source/state badge sits
          right-aligned in the same row. */}
      <nav
        aria-label="Prompt iteration pager"
        className="live-exec__iter-pager"
        style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}
      >
        <button
          type="button"
          disabled={activeIndex <= 0}
          onClick={() => setSelectedIter(availableIters[Math.max(0, activeIndex - 1)])}
          title="Previous iter"
        >◂</button>
        {buildPageWindow(totalIters, activeIndex < 0 ? 0 : activeIndex).map((slot, i) => {
          if (slot === 'ellipsis') return <span key={`e${i}`} className="live-exec__iter-pager-ellipsis">…</span>;
          const slotIter = availableIters[slot];
          const isActive = slotIter === iter;
          return (
            <button
              key={slot}
              type="button"
              className={isActive ? 'active' : ''}
              onClick={() => setSelectedIter(slotIter)}
              title={`Iter ${slotIter}`}
            >
              {slotIter}
            </button>
          );
        })}
        <button
          type="button"
          disabled={activeIndex < 0 || activeIndex >= totalIters - 1}
          onClick={() => setSelectedIter(availableIters[Math.min(totalIters - 1, activeIndex + 1)])}
          title="Next iter"
        >▸</button>
        <span style={{ marginLeft: 'auto', fontFamily: 'var(--sc-font-display, monospace)', fontSize: 9, letterSpacing: '0.14em', color: 'var(--sc-text-dim, #8d94a3)' }}>
          iter <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{iter ?? '—'}</strong> of <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{totalIters}</strong>
          {persistedForIter && (
            <span style={{ marginLeft: 8, color: 'var(--sc-green, #4ade80)' }}>● persisted</span>
          )}
          {!persistedForIter && liveForIter.length > 0 && (
            <span style={{ marginLeft: 8, color: 'var(--sc-amber, #ffb347)' }}>◌ live only</span>
          )}
        </span>
      </nav>

      {/* Pre-Phase-82.1 hint: if the lowest available iter is > 1, then
          earlier iters definitely ran (the WS didn't start at this iter)
          but their prompts were never persisted (they pre-date the
          iteration_prompt write path that shipped 2026-05-23). Phase 82.2
          carry-forward enrichment still embeds those earlier iters'
          notes + evaluator feedback INSIDE this iter's user prompt body
          (look for the "NOTES FROM PRIOR ITERATIONS" / "EVALUATOR
          FEEDBACK" banners). The hint prevents the recurring "why is
          iter N-1 inside iter N" confusion. */}
      {availableIters.length > 0 && availableIters[0] > 1 && (
        <div
          style={{
            fontFamily: 'var(--sc-font-display, monospace)',
            fontSize: 9,
            letterSpacing: '0.12em',
            color: 'var(--sc-amber, #ffb347)',
            background: 'rgba(255, 179, 71, 0.06)',
            border: '1px solid rgba(255, 179, 71, 0.25)',
            padding: '6px 10px',
            borderRadius: 2,
          }}
          title="Earlier iters ran before persistence shipped (Phase 82.1, 2026-05-23). Their prompts can't be recovered, but their working notes and evaluator feedback are embedded inside this iter's user prompt as carry-forward enrichment (Phase 82.2)."
        >
          ⚠ EARLIER ITERS NOT PERSISTED · iter 1
          {availableIters[0] > 2 ? `–${availableIters[0] - 1}` : ''} ran before
          Phase 82.1 (2026-05-23) · their notes + evaluator feedback are
          embedded inside this prompt's carry-forward blocks (look for{' '}
          <code style={{ color: 'var(--sc-text, #e9e6dc)' }}>
            NOTES FROM PRIOR ITERATIONS
          </code>{' '}
          /{' '}
          <code style={{ color: 'var(--sc-text, #e9e6dc)' }}>
            EVALUATOR FEEDBACK
          </code>
          )
        </div>
      )}

      {rendered.length === 0 ? (
        <div className="tab-placeholder">
          <div className="lbl">▌ NO PROMPTS PERSISTED FOR ITER {iter}</div>
          <div className="sub">
            This iteration appears in the deliverable history but no
            <code> task.prompt</code> row was persisted. Common causes:
            (a) the workstream ran before Phase 82.1 shipped and the
            transcript window has since evicted the live events, or
            (b) the iter crashed before <code>runner.StartAsync</code>.
          </div>
        </div>
      ) : (
        rendered.map((r) => (
          <PromptCard
            key={r.key}
            prompt={r.payload}
            isLive={r.isLive}
            attemptCount={r.attemptCount}
            loadingFull={r.isLive && r.eventId !== undefined && loadingIds.has(r.eventId)}
            failureReason={r.isLive && r.eventId !== undefined && !loadingIds.has(r.eventId) ? failedById[r.eventId] : undefined}
            stillTruncated={
              r.isLive
              && r.eventId !== undefined
              && !loadingIds.has(r.eventId)
              && !fullById[r.eventId]
              && (isTruncated(r.payload.systemPrompt) || isTruncated(r.payload.userPrompt))
            }
            onRetryLoadFull={r.isLive && r.eventId !== undefined && r.eventId > 0
              ? () => retryLoadFull(workItemId, r.eventId!, setFailedById, setLoadingIds, setFullById)
              : undefined}
          />
        ))
      )}
    </div>
  );
}

function persistedDtoToPayload(d: IterationPromptDto, iter: number): PromptPayload {
  return {
    role: d.role,
    agentId: d.agentId,
    crewId: d.crewId,
    systemPrompt: d.systemPrompt,
    userPrompt: d.userPrompt,
    model: d.model ?? undefined,
    backend: d.backend ?? undefined,
    invokeMode: d.invokeMode ?? undefined,
    attempt: d.attempt,
    iter,
  };
}

async function retryLoadFull(
  workItemId: string,
  eventId: number,
  setFailedById: React.Dispatch<React.SetStateAction<Record<number, string>>>,
  setLoadingIds: React.Dispatch<React.SetStateAction<Set<number>>>,
  setFullById: React.Dispatch<React.SetStateAction<Record<number, PromptPayload>>>,
): Promise<void> {
  setFailedById((prev) => {
    if (prev[eventId] === undefined) return prev;
    const next = { ...prev };
    delete next[eventId];
    return next;
  });
  setLoadingIds((prev) => {
    const next = new Set(prev);
    next.add(eventId);
    return next;
  });
  try {
    const r = await forgeApi.getWorkItemTranscriptEvent(workItemId, eventId);
    if (r && r.payload) {
      setFullById((prev) => ({ ...prev, [eventId]: r.payload as PromptPayload }));
    } else {
      setFailedById((prev) => ({ ...prev, [eventId]: 'empty payload' }));
    }
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    setFailedById((prev) => ({ ...prev, [eventId]: msg }));
  } finally {
    setLoadingIds((prev) => {
      const next = new Set(prev);
      next.delete(eventId);
      return next;
    });
  }
}

function PromptCard({
  prompt,
  isLive,
  attemptCount,
  loadingFull,
  failureReason,
  stillTruncated,
  onRetryLoadFull,
}: {
  prompt: PromptPayload;
  isLive: boolean;
  attemptCount?: number;
  loadingFull?: boolean;
  failureReason?: string;
  stillTruncated?: boolean;
  onRetryLoadFull?: () => void;
}) {
  const role = prompt.role ?? 'unknown';
  // System prompt collapsed by default (it's usually the soul.md
  // boilerplate). User prompt expanded by default — that's the
  // composed brief + pins + prior notes + eval chain that the
  // operator actually wants to audit.
  const [sysOpen, setSysOpen] = useState(false);
  const [userOpen, setUserOpen] = useState(true);
  const sysLen = prompt.systemPrompt?.length ?? 0;
  const userLen = prompt.userPrompt?.length ?? 0;

  const handleCopySys = useCallback(() => copyToClipboard(prompt.systemPrompt ?? ''), [prompt.systemPrompt]);
  const handleCopyUser = useCallback(() => copyToClipboard(prompt.userPrompt ?? ''), [prompt.userPrompt]);

  return (
    <section
      style={{
        border: '1px solid var(--border, #2a2f3a)',
        borderRadius: 3,
        background: 'var(--bg-elevated, rgba(255,255,255,0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
        fontSize: 12,
      }}
    >
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '6px 10px',
          borderBottom: '1px solid var(--border, #2a2f3a)',
          fontSize: 11,
          letterSpacing: '0.04em',
          flexWrap: 'wrap',
        }}
      >
        <span style={{ color: roleColor(role), fontWeight: 700, textTransform: 'uppercase' }}>
          {role}
        </span>
        {prompt.attempt && prompt.attempt > 1 && (
          <span style={{ color: 'var(--sc-amber, #ffb347)' }}>
            attempt {prompt.attempt}{attemptCount && attemptCount > 1 ? ` / ${attemptCount}` : ''}
          </span>
        )}
        {prompt.model && (
          <span style={{ color: 'var(--text-secondary, #8d94a3)' }}>{prompt.model}</span>
        )}
        {(prompt.backend || prompt.invokeMode) && (
          <span style={{ color: 'var(--text-tertiary, #8d94a3)' }}>
            {prompt.backend ?? ''}{prompt.backend && prompt.invokeMode ? ' / ' : ''}{prompt.invokeMode ?? ''}
          </span>
        )}
        <span
          title={isLive
            ? 'This prompt is sourced from the live SignalR transcript (not yet persisted, or persistence in-flight).'
            : 'This prompt is sourced from the durable iteration_prompt audit table (Phase 82.1).'}
          style={{
            color: isLive ? 'var(--sc-amber, #ffb347)' : 'var(--sc-green, #4ade80)',
            fontSize: 10,
          }}
        >
          {isLive ? '◌ live' : '● persisted'}
        </span>
        {loadingFull && (
          <span style={{ color: 'var(--sc-amber, #ffb347)' }}>↻ loading full prompt…</span>
        )}
        {!loadingFull && failureReason && (
          <span
            title={failureReason}
            style={{ color: 'var(--sc-red, #f87171)' }}
          >
            ⚠ load failed ({truncateMsg(failureReason)})
          </span>
        )}
        {stillTruncated && onRetryLoadFull && (
          <>
            <span style={{ color: 'var(--sc-red, #f87171)' }}>! truncated</span>
            <button
              type="button"
              onClick={onRetryLoadFull}
              style={{
                background: 'transparent',
                border: '1px solid var(--sc-cyan, #5eead4)',
                color: 'var(--sc-cyan, #5eead4)',
                padding: '0 6px',
                fontSize: 10,
                fontFamily: 'inherit',
                cursor: 'pointer',
                borderRadius: 2,
              }}
            >
              {failureReason ? 'Retry' : 'Load full'}
            </button>
          </>
        )}
        {prompt.agentId && (
          <span style={{ marginLeft: 'auto', color: 'var(--text-tertiary, #8d94a3)' }}>
            agent {prompt.agentId}
          </span>
        )}
      </header>

      {prompt.systemPrompt && (
        <CollapsibleSection
          open={sysOpen}
          onToggle={() => setSysOpen((v) => !v)}
          label={`system prompt (${formatBytes(sysLen)})`}
          onCopy={handleCopySys}
          bytes={sysLen}
          body={prompt.systemPrompt}
        />
      )}

      <CollapsibleSection
        open={userOpen}
        onToggle={() => setUserOpen((v) => !v)}
        label={`user prompt (${formatBytes(userLen)})`}
        onCopy={handleCopyUser}
        bytes={userLen}
        body={prompt.userPrompt ?? ''}
      />
    </section>
  );
}

function CollapsibleSection({
  open, onToggle, label, onCopy, bytes, body,
}: {
  open: boolean;
  onToggle: () => void;
  label: string;
  onCopy: () => void | Promise<void>;
  bytes: number;
  body: string;
}) {
  const [copied, setCopied] = useState(false);
  const copyTimerRef = useRef<number | null>(null);
  useEffect(() => {
    return () => {
      if (copyTimerRef.current !== null) {
        window.clearTimeout(copyTimerRef.current);
      }
    };
  }, []);
  const handleCopy = useCallback(async () => {
    await onCopy();
    setCopied(true);
    if (copyTimerRef.current !== null) window.clearTimeout(copyTimerRef.current);
    copyTimerRef.current = window.setTimeout(() => setCopied(false), 1400);
  }, [onCopy]);
  return (
    <div style={{ padding: '6px 10px', borderBottom: '1px solid var(--border, #2a2f3a)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <button
          type="button"
          onClick={onToggle}
          style={{
            background: 'transparent',
            border: 0,
            color: 'var(--text-secondary, #8d94a3)',
            cursor: 'pointer',
            fontFamily: 'inherit',
            fontSize: 11,
            padding: 0,
            letterSpacing: '0.04em',
          }}
          aria-expanded={open}
        >
          {open ? '▾' : '▸'} {label}
        </button>
        <button
          type="button"
          onClick={() => void handleCopy()}
          disabled={bytes === 0}
          title={bytes === 0 ? 'Nothing to copy' : `Copy ${formatBytes(bytes)} to clipboard`}
          style={{
            background: 'transparent',
            border: '1px solid var(--border, #2a2f3a)',
            color: copied ? 'var(--sc-green, #4ade80)' : 'var(--text-secondary, #8d94a3)',
            cursor: bytes === 0 ? 'not-allowed' : 'pointer',
            fontFamily: 'inherit',
            fontSize: 10,
            padding: '0 6px',
            borderRadius: 2,
            letterSpacing: '0.04em',
          }}
        >
          {copied ? '✓ copied' : '⧉ copy'}
        </button>
      </div>
      {open && (
        <pre
          style={{
            marginTop: 4,
            marginBottom: 0,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            color: 'var(--text-primary, #e3e6eb)',
            fontFamily: 'inherit',
            fontSize: 11,
            lineHeight: 1.5,
            maxHeight: 600,
            overflow: 'auto',
            userSelect: 'text',
          }}
        >
          {body}
        </pre>
      )}
    </div>
  );
}

async function copyToClipboard(text: string): Promise<void> {
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Best-effort fallback for environments without the async clipboard API.
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
    } catch {
      // give up silently — copy is convenience, not correctness
    }
  }
}

function roleOrder(role: string | undefined): number {
  const lc = (role ?? '').toLowerCase();
  if (lc === 'generator') return 0;
  if (lc === 'evaluator') return 1;
  return 2;
}

function roleColor(role: string): string {
  const lc = role.toLowerCase();
  if (lc === 'generator') return 'var(--sc-cyan, #5eead4)';
  if (lc === 'evaluator') return 'var(--sc-amber, #ffb347)';
  return 'var(--text-primary, #e3e6eb)';
}

function formatBytes(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2)}MB`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}KB`;
  return `${n}B`;
}

function truncateMsg(msg: string): string {
  const trimmed = msg.replace(/\s+/g, ' ').trim();
  return trimmed.length > 60 ? `${trimmed.slice(0, 57)}…` : trimmed;
}
