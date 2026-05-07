import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useToastStore } from '@/state/useToastStore';
import { SwarmNavBar } from './SwarmNavBar';
import { type SwarmMode } from './SwarmPipelineStrip';
import { SwarmMasterPanel, type Selection } from './SwarmMasterPanel';
import { SwarmDetailPanel, type DetailTab } from './SwarmDetailPanel';
import { Toaster } from '@/board/Toaster';

/** Map the StorySpawn lifecycle phase to one of the three swarm-control modes. */
function modeForPhase(p: StorySpawnPhase): SwarmMode {
  if (p === 'plan' || p === 'decompose') return 'design';
  if (p === 'done') return 'release';
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
    setModeState(m);
    updateParams({ mode: m });
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

  // When mode flips via pipeline click (not initial URL restore), drop
  // selection back to overview so the master panel renders cleanly.
  const modeRef = useMemo(() => ({ initial: true }), []);
  useEffect(() => {
    if (modeRef.initial) { modeRef.initial = false; return; }
    setSelection({ kind: 'overview', id: null });
  }, [mode]);

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
              phase · {detail.spawn.phase.toUpperCase()} · {detail.workstreams.length} workstreams · repo {detail.spawn.repoId ?? '—'}
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

  return (
    <div className="spawn-error-banner" role="alert">
      <div className="spawn-error-banner__icon" aria-hidden>!</div>
      <div className="spawn-error-banner__body">
        <div className="spawn-error-banner__title">
          ▌ ORCHESTRATOR HALTED
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
          title="Dismiss this error without retrying"
          style={{ borderColor: 'var(--sc-text-dim, #8d94a3)', color: 'var(--sc-text-dim, #8d94a3)' }}
        >
          {dismissing ? '⏳…' : '✕ dismiss'}
        </button>
        <button
          type="button"
          className="spawn-error-banner__retry"
          disabled={busy || dismissing}
          onClick={() => void handleRetry()}
        >
          {busy ? '⏳ retrying…' : '↻ retry'}
        </button>
      </div>
    </div>
  );
}
