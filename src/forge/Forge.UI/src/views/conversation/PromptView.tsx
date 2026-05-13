// Insight I.4 (docs/forge/workstream-insights.md) — verbatim
// system + user prompt for the active iter, per role. Sourced from
// `task.prompt` events emitted by GeneratorDelegate / EvaluatorDelegate.
// Operator-facing audit: "what context did the agent actually see"
// for the iter whose chip is selected.
//
// Truncation handling: the live transcript stream caps string fields
// at 320 chars and appends a "[truncated N → 320 — GET …]" marker.
// PromptView is exactly the surface that needs the FULL bytes, so
// when the marker is present we hit
// `GET /api/work-items/{wi}/transcript/{eventId}` per affected event
// and replace the payload with the untruncated version before render.

import { useEffect, useMemo, useState } from 'react';
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { forgeApi } from '@/api/forgeApi';

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
}

interface Props {
  /** Full transcript; we filter to task.prompt locally so the view
   *  follows the live store without an extra fetch. */
  events: TaskTranscriptEvent[];
  /** 1-based iter number to display. When null the latest iter wins. */
  activeIter: number | null;
  /** Work-item id — needed to hit the per-event /transcript/{id}
   *  endpoint when the live payload was truncated. */
  workItemId: string;
}

const TRUNCATION_MARKER = '[truncated';

function isTruncated(s: string | null | undefined): boolean {
  return !!s && s.includes(TRUNCATION_MARKER);
}

export function PromptView({ events, activeIter, workItemId }: Props): JSX.Element {
  // Pair payloads with the source event id so we can re-fetch per-event.
  const promptEvents = useMemo(() => {
    const list: { id: number; payload: PromptPayload }[] = [];
    for (const e of events) {
      if (e.kind !== 'task.prompt') continue;
      list.push({ id: e.id, payload: (e.payload ?? {}) as PromptPayload });
    }
    return list;
  }, [events]);

  // Determine the active iter — explicit selection wins, else latest.
  const iter = useMemo(() => {
    if (activeIter !== null && activeIter !== undefined) return activeIter;
    let latest = 0;
    for (const p of promptEvents) if ((p.payload.iter ?? 0) > latest) latest = p.payload.iter ?? 0;
    return latest > 0 ? latest : null;
  }, [activeIter, promptEvents]);

  const forIter = useMemo(
    () => promptEvents.filter((p) => p.payload.iter === iter),
    [promptEvents, iter],
  );

  // Per-event "full payload" cache. Keyed by event id so a re-render
  // on transcript append doesn't refetch; cleared implicitly when the
  // operator picks a different iter (different event ids).
  const [fullById, setFullById] = useState<Record<number, PromptPayload>>({});
  const [loadingIds, setLoadingIds] = useState<Set<number>>(new Set());
  // Once a per-event fetch fails (404 from the :long route constraint
  // when the event id is a transient SignalR-synthesized one, network
  // error, missing row after eviction, etc.) we record the id +
  // error message and STOP auto-retrying. Without this guard the
  // useEffect re-fires every time `loadingIds` flips, producing an
  // infinite retry loop: the "↻ loading full prompt…" indicator
  // never resolves and the manual "Load full" button only renders
  // for one microtask between retries, so the operator can never
  // actually click it.
  const [failedById, setFailedById] = useState<Record<number, string>>({});

  useEffect(() => {
    const needsFetch = forIter.filter((p) => {
      if (fullById[p.id]) return false;
      if (loadingIds.has(p.id)) return false;
      if (failedById[p.id] !== undefined) return false;
      return isTruncated(p.payload.systemPrompt) || isTruncated(p.payload.userPrompt);
    });
    if (needsFetch.length === 0) return;

    let cancelled = false;
    setLoadingIds((prev) => {
      const next = new Set(prev);
      for (const p of needsFetch) next.add(p.id);
      return next;
    });

    void Promise.allSettled(
      needsFetch.map((p) => forgeApi.getWorkItemTranscriptEvent(workItemId, p.id)),
    ).then((results) => {
      if (cancelled) return;
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

    return () => { cancelled = true; };
  }, [forIter, workItemId, fullById, loadingIds, failedById]);

  if (promptEvents.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO PROMPTS YET</div>
        <div className="sub">
          Once the gen↔eval loop fires its first <code>task.prompt</code>
          event, this tab will show the verbatim system + user prompt
          the agent saw — useful for auditing how the context grew
          across iterations.
        </div>
      </div>
    );
  }

  if (forIter.length === 0) {
    return (
      <div className="tab-placeholder">
        <em>No prompts recorded for iter {iter ?? '—'}.</em>
      </div>
    );
  }

  // Generator first, evaluator second. Prefer the untruncated full
  // payload when we've fetched it; fall back to the truncated live
  // copy (with a visible "Loading full prompt…" header) otherwise.
  const ordered = [...forIter].sort(
    (a, b) => roleOrder(a.payload.role) - roleOrder(b.payload.role),
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 8 }}>
      <header style={{ fontSize: 11, color: 'var(--text-secondary, #8d94a3)', letterSpacing: '0.06em' }}>
        Prompts for iter {iter}
      </header>
      {ordered.map((p) => {
        const full = fullById[p.id];
        const payload = full ?? p.payload;
        const isLoadingFull = loadingIds.has(p.id);
        const failureReason = failedById[p.id];
        const wasTruncated = !full
          && (isTruncated(p.payload.systemPrompt) || isTruncated(p.payload.userPrompt));
        const onLoadFull = async () => {
          // Manual click — clear the prior failure marker so the
          // auto-retry guard doesn't immediately short-circuit this
          // attempt, then re-fetch directly (bypassing the useEffect
          // path) so the button gives the operator immediate feedback.
          setFailedById((prev) => {
            if (prev[p.id] === undefined) return prev;
            const next = { ...prev };
            delete next[p.id];
            return next;
          });
          setLoadingIds((prev) => {
            const next = new Set(prev);
            next.add(p.id);
            return next;
          });
          try {
            const r = await forgeApi.getWorkItemTranscriptEvent(workItemId, p.id);
            if (r && r.payload) {
              setFullById((prev) => ({ ...prev, [p.id]: r.payload as PromptPayload }));
            } else {
              setFailedById((prev) => ({ ...prev, [p.id]: 'empty payload' }));
            }
          } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            setFailedById((prev) => ({ ...prev, [p.id]: msg }));
            // eslint-disable-next-line no-console
            console.warn('PromptView: failed to load full payload for event', p.id, err);
          } finally {
            setLoadingIds((prev) => {
              const next = new Set(prev);
              next.delete(p.id);
              return next;
            });
          }
        };
        return (
          <PromptCard
            key={p.id}
            prompt={payload}
            loadingFull={isLoadingFull}
            stillTruncated={wasTruncated && !isLoadingFull}
            failureReason={!isLoadingFull ? failureReason : undefined}
            onLoadFull={wasTruncated && !isLoadingFull ? onLoadFull : undefined}
          />
        );
      })}
    </div>
  );
}

function PromptCard({
  prompt,
  loadingFull,
  stillTruncated,
  failureReason,
  onLoadFull,
}: {
  prompt: PromptPayload;
  loadingFull?: boolean;
  stillTruncated?: boolean;
  failureReason?: string;
  onLoadFull?: () => void;
}) {
  const [sysOpen, setSysOpen] = useState(false);
  const role = prompt.role ?? 'unknown';
  const sysLen = prompt.systemPrompt?.length ?? 0;
  const userLen = prompt.userPrompt?.length ?? 0;
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
        }}
      >
        <span style={{ color: roleColor(role), fontWeight: 700, textTransform: 'uppercase' }}>
          {role}
        </span>
        {prompt.model && (
          <span style={{ color: 'var(--text-secondary, #8d94a3)' }}>{prompt.model}</span>
        )}
        {(prompt.backend || prompt.invokeMode) && (
          <span style={{ color: 'var(--text-tertiary, #8d94a3)' }}>
            {prompt.backend ?? ''}{prompt.backend && prompt.invokeMode ? ' / ' : ''}{prompt.invokeMode ?? ''}
          </span>
        )}
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
        {stillTruncated && (
          <>
            <span style={{ color: 'var(--sc-red, #f87171)' }}>! truncated</span>
            {onLoadFull && (
              <button
                type="button"
                onClick={onLoadFull}
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
            )}
          </>
        )}
        {prompt.agentId && (
          <span style={{ marginLeft: 'auto', color: 'var(--text-tertiary, #8d94a3)' }}>
            agent {prompt.agentId}
          </span>
        )}
      </header>

      {prompt.systemPrompt && (
        <div style={{ padding: '6px 10px', borderBottom: '1px solid var(--border, #2a2f3a)' }}>
          <button
            type="button"
            onClick={() => setSysOpen((v) => !v)}
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
            aria-expanded={sysOpen}
          >
            {sysOpen ? '▾' : '▸'} system prompt ({formatBytes(sysLen)})
          </button>
          {sysOpen && (
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
                maxHeight: 320,
                overflow: 'auto',
              }}
            >
              {prompt.systemPrompt}
            </pre>
          )}
        </div>
      )}

      <div style={{ padding: '6px 10px' }}>
        <div style={{ fontSize: 11, color: 'var(--text-secondary, #8d94a3)', letterSpacing: '0.04em', marginBottom: 4 }}>
          user prompt ({formatBytes(userLen)})
        </div>
        <pre
          style={{
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            color: 'var(--text-primary, #e3e6eb)',
            fontFamily: 'inherit',
            fontSize: 11,
            lineHeight: 1.5,
            margin: 0,
            maxHeight: 600,
            overflow: 'auto',
          }}
        >
          {prompt.userPrompt ?? ''}
        </pre>
      </div>
    </section>
  );
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
  // Keep the inline header short — the full message is exposed via
  // the `title` attribute so the operator can hover for the details.
  const trimmed = msg.replace(/\s+/g, ' ').trim();
  return trimmed.length > 60 ? `${trimmed.slice(0, 57)}…` : trimmed;
}
