import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useToastStore } from '@/state/useToastStore';
import { SwarmTopbar } from './SwarmTopbar';
import { SwarmNavBar } from './SwarmNavBar';
import { SwarmPipelineStrip, type PipelineStageSpec, type SwarmMode } from './SwarmPipelineStrip';
import { SwarmMasterPanel, type Selection } from './SwarmMasterPanel';
import { SwarmDetailPanel, type DetailTab } from './SwarmDetailPanel';
import { Toaster } from '@/board/Toaster';

const PHASES: StorySpawnPhase[] = ['plan', 'decompose', 'implement', 'review', 'qa', 'done'];

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
  const tick = useSpawnStore((s) => s.ticks[storyId] ?? 0);

  const [detail, setDetail] = useState<StorySpawnDetail | null>(null);
  const [artifacts, setArtifacts] = useState<StorySpawnArtifacts | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [mode, setMode] = useState<SwarmMode | null>(null);
  const [selection, setSelection] = useState<Selection>({ kind: 'overview', id: null });
  const [tab, setTab] = useState<DetailTab>('overview');
  // Master defaults to collapsed (36px rail) so the tabbed detail panel
  // takes the whole width on first render. User clicks the chevron to
  // expand the master back to its 360px navigation list.
  const [masterCollapsed, setMasterCollapsed] = useState(true);

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
          setMode((curr) => curr ?? modeForPhase(d.spawn.phase));
        }
      } catch (e) {
        if (!cancelled) setErr(e instanceof Error ? e.message : 'Failed to load story');
      }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, tick]);

  // When mode flips, drop selection back to overview so the master panel renders cleanly.
  useEffect(() => { setSelection({ kind: 'overview', id: null }); }, [mode]);

  const stages: PipelineStageSpec[] = useMemo(() => {
    if (!detail) return [];
    const phaseIdx = PHASES.indexOf(detail.spawn.phase);
    const ws = detail.workstreams;
    const planning = ws.filter((w) => w.status === 'planning').length;
    const active = ws.filter((w) => w.status === 'active').length;
    const review = ws.filter((w) => w.status === 'review').length;
    const merging = ws.filter((w) => w.status === 'merging').length;
    const complete = ws.filter((w) => w.status === 'complete').length;

    return [
      {
        mode: 'design',
        num: '01',
        label: 'DESIGN & PLAN',
        detail: phaseIdx <= 1 ? 'arch team · in progress' : 'arch team · 100% complete',
        state: phaseIdx <= 1 ? 'active' : 'done',
        badges: [
          { text: '▣ 4 DOCS' },
          { text: '◉ ARCH CREW' },
          { text: artifacts?.plan.pr ? `▦ ${artifacts.plan.pr}` : '▦ NO PR YET' },
          ...(detail.spawn.decompositionJson ? [{ text: '⬡ DECOMPOSED' }] : []),
        ],
      },
      {
        mode: 'impl',
        num: '02',
        label: 'IMPL ⇄ REVIEW + E2E',
        detail: phaseIdx >= 2 && phaseIdx <= 4
          ? `eng + rev + qa · ${ws.length} workstreams`
          : phaseIdx > 4 ? 'cycle complete' : 'awaiting plan',
        state: phaseIdx >= 2 && phaseIdx <= 4 ? 'active' : phaseIdx > 4 ? 'done' : 'queued',
        badges: [
          { text: `▣ ${ws.length} WORKSTREAMS` },
          { text: `◉ ${planning + active} PLANNING/ACTIVE` },
          { text: `⇄ ${review} IN REVIEW` },
          ...(merging > 0 ? [{ text: `⏵ ${merging} MERGING` }] : []),
          ...(complete > 0 ? [{ text: `✓ ${complete} DONE` }] : []),
        ],
      },
      {
        mode: 'release',
        num: '03',
        label: 'RELEASE',
        detail: detail.spawn.phase === 'done' ? 'closed' : 'awaiting impl ≥ 90',
        state: detail.spawn.phase === 'done' ? 'done' : 'queued',
        badges: [
          { text: '⏚ CI/CD' },
          { text: '⏚ ARCH DOCS' },
          { text: '⏚ E2E + LIVEDOC' },
        ],
      },
    ];
  }, [detail, artifacts]);

  const meters = useMemo(() => {
    if (!detail) return [];
    const ws = detail.workstreams;
    const phaseIdx = PHASES.indexOf(detail.spawn.phase);
    const pct = Math.round(((phaseIdx + 1) / PHASES.length) * 100);
    const stalled = ws.filter((w) => w.status === 'stalled').length;
    const activePairs = ws.filter((w) => w.status === 'active' || w.status === 'review').length;
    const merged = ws.filter((w) => w.status === 'complete' || w.status === 'merging').length;
    return [
      { k: 'Story Score',   v: '—',                                delta: 'pending model rotation', tone: 'amber' as const },
      { k: 'Phases',        v: `${phaseIdx + 1} / ${PHASES.length}`, delta: `${pct}% complete` },
      { k: 'Active Pairs',  v: String(activePairs),                delta: `${ws.length} workstreams`, tone: 'cyan' as const },
      { k: 'Iterations',    v: '—',                                delta: 'Σ this run' },
      { k: 'PR Throughput', v: `${merged} / ${ws.length}`,         delta: ws.length ? `${Math.round((merged / ws.length) * 100)}% merge` : '0%', tone: 'green' as const },
      { k: 'Stalled',       v: String(stalled),                    delta: stalled ? 'esc. to lead' : '—', tone: stalled ? ('red' as const) : undefined },
    ];
  }, [detail]);

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

  const eyebrow = `SWARM CONTROL // ${detail.spawn.storyId}`;

  return (
    <div className="swarm-ctrl">
      <div className="shell">
        <SwarmTopbar
          eyebrow={eyebrow}
          title={detail.spawn.storyTitle.toUpperCase()}
          subtitle={`phase · ${detail.spawn.phase.toUpperCase()} · ${detail.workstreams.length} workstreams · repo ${detail.spawn.repoId ?? '—'}`}
          meters={meters}
        />
        <SwarmNavBar
          crumbs={[
            { pre: 'ROOT', label: 'Workstreams', href: '/workstreams' },
            { pre: 'STORY', label: detail.spawn.storyId, active: true },
            ...(mode ? [{ pre: 'MODE', label: mode === 'design' ? 'Design & Plan' : mode === 'impl' ? 'Impl ⇄ Review' : 'Release', active: true }] : []),
            ...(selection.kind !== 'overview' && selection.id
              ? [{ pre: selection.kind.toUpperCase(), label: selection.id, active: true }]
              : []),
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
        <SwarmPipelineStrip
          stages={stages}
          active={mode}
          onSelect={(m) => setMode(m)}
        />

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
                    // Auto-expand the master when the user selects a row from the
                    // collapsed rail (so they can see the rest of the list).
                    if (masterCollapsed && sel.kind !== 'overview') setMasterCollapsed(false);
                  }}
                  collapsed={masterCollapsed}
                  onToggleCollapsed={() => setMasterCollapsed((v) => !v)}
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
      <button
        type="button"
        className="spawn-error-banner__retry"
        disabled={busy}
        onClick={() => void handleRetry()}
      >
        {busy ? '⏳ retrying…' : '↻ retry'}
      </button>
    </div>
  );
}
