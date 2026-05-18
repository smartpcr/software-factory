import { useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useToastStore } from '@/state/useToastStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import { SwarmNavBar } from './SwarmNavBar';
import { type SwarmMode } from './SwarmPipelineStrip';
import { SwarmMasterPanel, type Selection } from './SwarmMasterPanel';
import { SwarmDetailPanel, type DetailTab } from './SwarmDetailPanel';
import { Toaster } from '@/board/Toaster';

/** Map the StorySpawn lifecycle phase to one of the three swarm-control modes. */
function modeForPhase(p: StorySpawnPhase): SwarmMode {
  if (p === 'plan' || p === 'decompose') return 'design';
  if (p === 'done') return 'release';
  if (p === 'paused') return 'impl';
  return 'impl';
}

export function SwarmStoryView() {
  const { storyId: rawId = '' } = useParams<{ storyId: string }>();
  const storyId = decodeURIComponent(rawId);
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const tick = useSpawnStore((s) => s.ticks[storyId] ?? 0);

  const [detail, setDetail] = useState<StorySpawnDetail | null>(null);
  const [artifacts, setArtifacts] = useState<StorySpawnArtifacts | null>(null);
  const [err, setErr] = useState<string | null>(null);

  // Restore state from URL search params on mount so page reload preserves
  // the selected pipeline stage, master-panel item, and detail tab.
  const [mode, setModeState] = useState<SwarmMode | null>(
    () => (searchParams.get('mode') as SwarmMode | null) ?? null,
  );
  const [selection, setSelectionState] = useState<Selection>(() => {
    const kind = searchParams.get('sel') as Selection['kind'] | null;
    const id = searchParams.get('selId');
    return kind ? { kind, id: id ?? null } : { kind: 'overview', id: null };
  });
  const [tab, setTabState] = useState<DetailTab>(
    () => (searchParams.get('tab') as DetailTab) || 'overview',
  );
  const [masterCollapsed, setMasterCollapsed] = useState(
    () => searchParams.get('master') !== 'open',
  );

  // Sync state changes back to URL params (replaceState — no history push).
  function setMode(m: SwarmMode | null) {
    // A user-driven mode flip (via the nav-bar dropdown — the only caller
    // of setMode) drops selection back to overview so the master panel
    // renders cleanly in the new mode. Mode changes that come from URL
    // restore (useState initializer) or from the load() effect's
    // phase-derived fallback bypass setMode and use setModeState directly,
    // which preserves selection — that's intentional and is what enables
    // rect clicks in StoryDeliveryStrip to land with sel/selId intact.
    //
    // Guard on `m !== mode` so re-selecting the currently active mode in
    // the dropdown does NOT clear the active workstream/doc.
    const changed = m !== mode;
    setModeState(m);
    if (changed) {
      setSelectionState({ kind: 'overview', id: null });
      updateParams({ mode: m, sel: null, selId: null });
    } else {
      updateParams({ mode: m });
    }
  }
  function setSelection(sel: Selection) {
    setSelectionState(sel);
    updateParams({ sel: sel.kind, selId: sel.id });
  }
  function setTab(t: DetailTab) {
    setTabState(t);
    updateParams({ tab: t });
  }
  function updateParams(patch: Record<string, string | null | undefined>) {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(patch)) {
        if (v == null || v === '' || v === 'overview') next.delete(k);
        else next.set(k, v);
      }
      return next;
    }, { replace: true });
  }

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const [d, a] = await Promise.all([
          forgeApi.getStorySpawn(storyId),
          forgeApi.getStoryArtifacts(storyId),
        ]);
        if (!cancelled) {
          setDetail(d);
          setArtifacts(a);
          setModeState((curr) => {
            const next = curr ?? modeForPhase(d.spawn.phase);
            if (next !== curr) updateParams({ mode: next });
            return next;
          });
        }
      } catch (e) {
        if (!cancelled) setErr(e instanceof Error ? e.message : 'Failed to load story');
      }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, tick]);

  // When mode flips via the nav-bar dropdown, setMode resets selection
  // inline. We intentionally do NOT mirror that into a useEffect on
  // [mode] — under React 18 StrictMode dev, an effect that mutates a
  // module-level/useMemo "first-run" flag fires twice on mount and
  // ends up clearing sel/selId that arrived in the initial URL (the
  // exact symptom that broke StoryDeliveryStrip rect-click navigation).
  // Mode changes from useState init / load()'s phase fallback go through
  // setModeState directly and preserve selection.
  //
  // ── URL → local state sync ────────────────────────────────────────
  // The four pieces of local state above are initialised from
  // searchParams via `useState(() => …)` — initialisers only run on
  // mount. When the URL changes AFTER mount (e.g. operator clicks a
  // stage card in the DecompositionDiagram DAG and we navigate to the
  // same `/workstreams/:storyId` route with different query params),
  // React Router updates `searchParams` but local state does NOT
  // re-read the initialiser, so the page renders the OLD mode /
  // selection — symptom: "url change, page not loaded".
  //
  // This effect reconciles state ← URL on every searchParams change.
  // The diff guards (`if (urlMode !== mode)` etc.) are mandatory:
  // - They prevent feedback loops with the state → URL writers
  //   (setMode / setSelection / setTab call updateParams which calls
  //   setSearchParams, which re-fires this effect; without the guard
  //   we'd setModeState(currentMode) → re-render → no-op cycle, but
  //   it still wastes work).
  // - They keep StrictMode-safe: the dev double-mount fires this
  //   effect twice with the same searchParams; first run sets state
  //   if needed, second run sees state already matches and no-ops.
  //
  // Depending on [searchParams] (not [mode]) is also intentional —
  // this avoids the StrictMode trap that broke rect-click navigation
  // (the warning above), because searchParams is a stable React
  // Router ref that only ticks when the URL actually changes.
  useEffect(() => {
    const urlMode = (searchParams.get('mode') as SwarmMode | null) ?? null;
    const urlSelKind = searchParams.get('sel') as Selection['kind'] | null;
    const urlSelId = searchParams.get('selId');
    const urlTab = (searchParams.get('tab') as DetailTab) || 'overview';
    const urlMasterCollapsed = searchParams.get('master') !== 'open';

    if (urlMode !== mode) setModeState(urlMode);

    const wantSel: Selection = urlSelKind
      ? { kind: urlSelKind, id: urlSelId ?? null }
      : { kind: 'overview', id: null };
    if (wantSel.kind !== selection.kind || wantSel.id !== selection.id) {
      setSelectionState(wantSel);
    }

    if (urlTab !== tab) setTabState(urlTab);
    if (urlMasterCollapsed !== masterCollapsed) setMasterCollapsed(urlMasterCollapsed);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  if (err) {
    return (
      <div className="swarm-ctrl">
        <div className="shell">
          <SwarmNavBar
            crumbs={[{ pre: 'ROOT', label: 'Workstreams', href: '/workstreams' }, { label: storyId, active: true }]}
            onBack={() => navigate('/workstreams')}
          />
          <div className="empty">
            <div className="lbl">▌ {storyId}</div>
            <div className="sub" style={{ color: 'var(--sc-red)' }}>{err}</div>
          </div>
        </div>
      </div>
    );
  }

  if (!detail) {
    return (
      <div className="swarm-ctrl">
        <div className="shell">
          <SwarmNavBar
            crumbs={[{ pre: 'ROOT', label: 'Workstreams', href: '/workstreams' }, { label: storyId, active: true }]}
            onBack={() => navigate('/workstreams')}
          />
          <div className="empty">
            <div className="lbl">▌ LOADING {storyId}…</div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="swarm-ctrl">
      <div className="shell">
        {/* Simplified header — title + phase, no metrics */}
        <div className="swarm-header-slim">
          <div className="swarm-header-slim-left">
            <div className="swarm-header-slim-eyebrow">SWARM CONTROL // {detail.spawn.storyId}</div>
            <h1 className="swarm-header-slim-title">{detail.spawn.storyTitle.toUpperCase()}</h1>
            <div className="swarm-header-slim-sub">
              phase · {detail.spawn.phase.toUpperCase()}
              {detail.spawn.phase === 'paused' && detail.spawn.pausedFromPhase
                ? ` (from ${detail.spawn.pausedFromPhase})`
                : ''}
              {' '}· {detail.workstreams.length} workstreams · repo {detail.spawn.repoId ?? '—'}
              {detail.spawn.phase !== 'done' && (
                <button
                  type="button"
                  style={{
                    marginLeft: 12,
                    padding: '2px 10px',
                    fontSize: 10,
                    fontFamily: 'var(--sc-font-display)',
                    letterSpacing: '0.12em',
                    background: detail.spawn.phase === 'paused' ? 'rgba(80,200,120,0.15)' : 'rgba(255,180,0,0.12)',
                    color: detail.spawn.phase === 'paused' ? '#50c878' : '#ffb400',
                    border: `1px solid ${detail.spawn.phase === 'paused' ? 'rgba(80,200,120,0.3)' : 'rgba(255,180,0,0.25)'}`,
                    borderRadius: 3,
                    cursor: 'pointer',
                    verticalAlign: 'middle',
                  }}
                  title={detail.spawn.phase === 'paused'
                    ? `Resume spawn (restore to ${detail.spawn.pausedFromPhase ?? 'prior'} phase)`
                    : 'Pause spawn — stop all agents and workstreams'}
                  onClick={async () => {
                    const action = detail.spawn.phase === 'paused' ? 'resume' : 'pause';
                    const ask = useConfirmStore.getState().ask;
                    const ok = await ask({
                      title: `${action === 'pause' ? '⏸ PAUSE' : '▶ RESUME'} SPAWN "${detail.spawn.storyTitle}"?`,
                      body: action === 'pause'
                        ? 'This will kill all running agents and pause every workstream.'
                        : 'This will restore the prior phase and resume all paused workstreams.',
                      tone: action === 'pause' ? 'warn' : 'info',
                      confirmLabel: action === 'pause' ? 'Pause' : 'Resume',
                      cancelLabel: 'Cancel',
                    });
                    if (!ok) return;
                    try {
                      if (action === 'pause') {
                        await forgeApi.pauseStorySpawn(storyId);
                      } else {
                        await forgeApi.resumeStorySpawn(storyId);
                      }
                      useSpawnStore.getState().bump(storyId);
                    } catch (e: any) {
                      useToastStore.getState().show(`${action} failed: ${e?.message ?? e}`, 'danger');
                    }
                  }}
                >
                  {detail.spawn.phase === 'paused' ? '▶ RESUME' : '⏸ PAUSE'}
                </button>
              )}
            </div>
          </div>
          {selection.kind === 'phase' && selection.id && (() => {
            const ws = detail.workstreams.find((w) => w.id === selection.id);
            const ms = ws?.lastActiveAt ? Date.now() - Date.parse(ws.lastActiveAt) : null;
            const uptime = ms !== null ? (() => {
              const sec = Math.floor(ms / 1000);
              const min = Math.floor(sec / 60);
              const hr  = Math.floor(min / 60);
              return hr > 0 ? `${hr}h ${min % 60}m` : min > 0 ? `${min}m ${sec % 60}s` : `${sec}s`;
            })() : null;
            return (
              <div style={{ fontFamily: 'var(--sc-font-display)', fontSize: 10, letterSpacing: '0.14em', color: 'var(--sc-text-dim)', textAlign: 'right' }}>
                <a
                  href={`/work-items?focus=${encodeURIComponent(selection.id)}`}
                  style={{
                    fontSize: 10,
                    color: '#569cd6',
                    textDecoration: 'none',
                    letterSpacing: '0.12em',
                    display: 'inline-block',
                    marginBottom: 6,
                  }}
                  title="Open this work item in the Work Items view"
                >
                  ↗ VIEW WORK ITEM
                </a>
                {uptime !== null && (
                  <>
                    <br />
                    <span style={{ color: 'var(--sc-muted)' }}>UPTIME</span><br />
                    <span style={{ color: 'var(--sc-text)', fontSize: 14 }}>{uptime}</span>
                  </>
                )}
              </div>
            );
          })()}
        </div>
        <SwarmNavBar
          crumbs={[
            { pre: 'ROOT', label: 'Workstreams', href: '/workstreams' },
            { pre: 'STORY', label: detail.spawn.storyId, active: true },
            ...(mode ? [{
              pre: 'MODE',
              label: mode === 'design' ? 'Design & Plan' : mode === 'impl' ? 'Impl ⇄ Review' : 'Release',
              active: true,
              dropdown: (['design', 'impl', 'release'] as SwarmMode[]).map((m) => ({
                label: m === 'design' ? 'Design & Plan' : m === 'impl' ? 'Impl ⇄ Review' : 'Release',
                value: m,
                active: m === mode,
              })),
              onDropdownSelect: (v: string) => setMode(v as SwarmMode),
            }] : []),
            ...(selection.kind === 'doc' && selection.id ? [{
              pre: 'DOC',
              label: selection.id.replace('doc:', ''),
              active: true,
              dropdown: [
                { label: 'architecture', value: 'doc:architecture', active: selection.id === 'doc:architecture' },
                { label: 'tech-spec', value: 'doc:tech-spec', active: selection.id === 'doc:tech-spec' },
                { label: 'implementation-plan', value: 'doc:implementation-plan', active: selection.id === 'doc:implementation-plan' },
                { label: 'e2e-scenarios', value: 'doc:e2e-scenarios', active: selection.id === 'doc:e2e-scenarios' },
                { label: 'decomposition', value: 'doc:decomposition', active: selection.id === 'doc:decomposition' },
              ],
              onDropdownSelect: (v: string) => setSelection({ kind: 'doc', id: v }),
            }] : selection.kind !== 'overview' && selection.id ? [
              { pre: selection.kind.toUpperCase(), label: selection.id, active: true },
            ] : []),
          ]}
          onBack={() => navigate('/workstreams')}
        />
        {(() => {
          // Phase 78 — surface workstream-level merge-conflict stalls as
          // a dedicated banner above the spawn-level banner. Without this,
          // the operator only sees the spawn.LastError (orchestrator-halted
          // banner) which can mask the real cause when, e.g., a parallel
          // PR has merge conflicts that the orchestrator correctly stalled
          // the workstream for. The backend-side change in this phase
          // also stops the Phase 74 dispatch-race throw from overwriting
          // spawn.LastError, but the merge-conflict stall could still be
          // invisible if the operator dismisses the dispatch-race banner
          // without scrolling down to the workstream chips.
          const mergeConflicts = detail.workstreams.filter(
            (w) => w.status === 'stalled'
              && (w.stallReason ?? '').toLowerCase().includes('merge failed for pr')
          );
          if (mergeConflicts.length === 0) return null;
          return <MergeConflictBanner workstreams={mergeConflicts} />;
        })()}
        {detail.spawn.lastError && (
          <SpawnErrorBanner
            storyId={storyId}
            error={detail.spawn.lastError}
            errorAt={detail.spawn.lastErrorAt ?? null}
            onRetried={() => useSpawnStore.getState().bump(storyId)}
          />
        )}

        <div className="grid-main">
          <div className={`mode-pane${mode ? ' active' : ''}${masterCollapsed ? ' master-collapsed' : ''}`}>
            {mode && (
              <>
                <SwarmMasterPanel
                  mode={mode}
                  detail={detail}
                  artifacts={artifacts}
                  selection={selection}
                  onSelect={(sel) => {
                    setSelection(sel);
                    if (masterCollapsed && sel.kind !== 'overview') {
                      setMasterCollapsed(false);
                      updateParams({ master: 'open' });
                    }
                  }}
                  collapsed={masterCollapsed}
                  onToggleCollapsed={() => {
                    const next = !masterCollapsed;
                    setMasterCollapsed(next);
                    updateParams({ master: next ? null : 'open' });
                  }}
                />
                <SwarmDetailPanel
                  mode={mode}
                  detail={detail}
                  artifacts={artifacts}
                  selection={selection}
                  tab={tab}
                  onTab={setTab}
                />
              </>
            )}
          </div>
        </div>
      </div>
      <Toaster />
    </div>
  );
}

/** Phase 37 — surfaces the spawn's persisted LastError above the pipeline
 *  strip so the operator sees why the orchestrator halted (e.g. the plan
 *  loop fell short of pass, or the plan PR has merge conflicts). The Retry
 *  button POSTs to /api/stories/{storyId}/spawn/retry which clears the
 *  error and re-drives the orchestrator. */
function SpawnErrorBanner(props: {
  storyId: string;
  error: string;
  errorAt: string | null;
  onRetried: () => void;
}) {
  const { storyId, error, errorAt, onRetried } = props;
  const [busy, setBusy] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const showToast = useToastStore((s) => s.show);
  const when = errorAt
    ? new Date(errorAt).toLocaleString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', month: 'short', day: 'numeric' })
    : null;

  // Phase 66 — when the spawn is parked awaiting operator answers (NOT a
  // hard halt), surface a blue informational banner instead of the red
  // orchestrator-halted banner. The backend writes the sentinel prefix
  // "Awaiting operator answers" on spawn.LastError; the answer endpoint
  // clears it once the operator answers (or dismisses) the questions.
  const isAwaitingAnswers = error.startsWith('Awaiting operator answers');

  // Phase 48 — `Plan docs not all passed; phase advance blocked: <keys>`
  // is also a soft-block (the orchestrator regressed `Phase=Plan` and
  // is actively re-iterating on the non-passed doc). It is NOT a
  // human-action-required halt — once the doc converges or is accepted,
  // the orchestrator advances on its own. Surface as blue
  // "auto-recovering" banner with no retry button.
  const isPhaseAdvanceBlocked = error.startsWith('Plan docs not all passed');

  // Umbrella: any soft-block flips the banner from red→blue and hides
  // the (irrelevant) Retry button. Dismiss remains for operator override.
  const isSoftBlock = isAwaitingAnswers || isPhaseAdvanceBlocked;

  async function handleRetry() {
    setBusy(true);
    try {
      await forgeApi.retryStorySpawn(storyId);
      showToast('Retry kicked off; orchestrator re-driving the spawn.', 'success');
      onRetried();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Retry failed';
      showToast(msg, 'danger');
    } finally {
      setBusy(false);
    }
  }

  async function handleDismiss() {
    setDismissing(true);
    try {
      await forgeApi.dismissStorySpawnError(storyId);
      showToast('Error dismissed.', 'info');
      onRetried();
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Dismiss failed', 'danger');
    } finally {
      setDismissing(false);
    }
  }

  const bannerTitle = isAwaitingAnswers
    ? '▌ AWAITING ANSWERS'
    : isPhaseAdvanceBlocked
      ? '▌ AUTO-RECOVERING'
      : '▌ ORCHESTRATOR HALTED';

  const dismissTitle = isAwaitingAnswers
    ? 'Dismiss this note (answers still required to resume the loop)'
    : isPhaseAdvanceBlocked
      ? 'Dismiss this note (orchestrator continues auto-recovery regardless)'
      : 'Dismiss this error without retrying';

  return (
    <div
      className={`spawn-error-banner${isSoftBlock ? ' spawn-error-banner--awaiting-answers' : ''}`}
      role={isSoftBlock ? 'status' : 'alert'}
    >
      <div className="spawn-error-banner__icon" aria-hidden>{isSoftBlock ? '?' : '!'}</div>
      <div className="spawn-error-banner__body">
        <div className="spawn-error-banner__title">
          {bannerTitle}
          {when && <span className="spawn-error-banner__when"> · {when}</span>}
        </div>
        <div className="spawn-error-banner__msg">{error}</div>
      </div>
      <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
        <button
          type="button"
          className="spawn-error-banner__retry"
          disabled={busy || dismissing}
          onClick={() => void handleDismiss()}
          title={dismissTitle}
          style={{ borderColor: 'var(--sc-text-dim, #8d94a3)', color: 'var(--sc-text-dim, #8d94a3)' }}
        >
          {dismissing ? '⏳…' : '✕ dismiss'}
        </button>
        {!isSoftBlock && (
          <button
            type="button"
            className="spawn-error-banner__retry"
            disabled={busy || dismissing}
            onClick={() => void handleRetry()}
          >
            {busy ? '⏳ retrying…' : '↻ retry'}
          </button>
        )}
      </div>
    </div>
  );
}

/** Phase 78 — surfaces workstream-level merge conflicts as a dedicated
 *  amber banner above the spawn-level error banner. Triggered by any
 *  workstream whose `status === 'stalled'` and whose `stallReason`
 *  contains "Merge failed for PR" (set by
 *  StorySpawnOrchestrator.StallWorkstreamForMergeFailureAsync when
 *  ApproveAndMergeAsync throws on a GitHub 405 / dirty / blocked PR).
 *
 *  Resolution paths surfaced in the body:
 *    - Open the PR in GitHub, resolve the conflict (rebase the ws
 *      branch onto the current base tip and push), then Restart the
 *      workstream from the chip menu.
 *    - Or, if the work is no longer needed, cancel the workstream.
 *
 *  Intentionally has NO Retry button — the orchestrator cannot
 *  resolve a merge conflict on its own; the operator (or a future
 *  rebase-strategy implementation) must intervene first. */
function MergeConflictBanner(props: {
  workstreams: Array<{ id: string; name: string; prs: string[]; stallReason?: string | null }>;
}) {
  const { workstreams } = props;
  return (
    <div
      className="spawn-error-banner spawn-error-banner--merge-conflict"
      role="alert"
      style={{
        borderColor: 'var(--sc-warn, #d4a017)',
        background: 'rgba(212, 160, 23, 0.12)',
      }}
    >
      <div className="spawn-error-banner__icon" aria-hidden style={{ color: 'var(--sc-warn, #d4a017)' }}>⚠</div>
      <div className="spawn-error-banner__body">
        <div className="spawn-error-banner__title" style={{ color: 'var(--sc-warn, #d4a017)' }}>
          ▌ PR MERGE CONFLICT
          {' · '}
          <span style={{ opacity: 0.85, fontWeight: 'normal' }}>
            {workstreams.length} workstream{workstreams.length === 1 ? '' : 's'} blocked
          </span>
        </div>
        <div className="spawn-error-banner__msg">
          {workstreams.slice(0, 4).map((w, i) => (
            <div key={w.id} style={{ marginTop: i === 0 ? 0 : 4 }}>
              <strong>{w.name}</strong>
              {w.prs.length > 0 ? <span style={{ opacity: 0.75 }}> · {w.prs[0]}</span> : null}
            </div>
          ))}
          {workstreams.length > 4 && (
            <div style={{ marginTop: 4, opacity: 0.7 }}>+ {workstreams.length - 4} more</div>
          )}
          <div style={{ marginTop: 8, opacity: 0.85 }}>
            Resolve the conflict in GitHub (rebase the ws branch onto base + push),
            then Restart from the workstream chip menu. Auto-recovery is intentionally
            disabled — parallel PRs touching the same files require operator decision.
          </div>
        </div>
      </div>
    </div>
  );
}
