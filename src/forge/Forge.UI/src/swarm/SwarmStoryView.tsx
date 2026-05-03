import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { SwarmTopbar } from './SwarmTopbar';
import { SwarmNavBar } from './SwarmNavBar';
import { SwarmPipelineStrip, type PipelineStageSpec, type SwarmMode } from './SwarmPipelineStrip';
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
    const stalled = 0;     // surfaced once `WorkItemRow.trialCount === 3 ∧ score < 90` lands
    const activePairs = ws.filter((w) => w.status === 'active' || w.status === 'review').length;
    const merged = ws.filter((w) => w.status === 'complete' || w.status === 'merging').length;
    return [
      { k: 'Story Score',   v: '—',                                delta: 'pending model rotation', tone: 'amber' as const },
      { k: 'Phases',        v: `${phaseIdx + 1} / ${PHASES.length}`, delta: `${pct}% complete` },
      { k: 'Active Pairs',  v: String(activePairs),                delta: `${ws.length} workstreams`, tone: 'cyan' as const },
      { k: 'Iterations',    v: '—',                                delta: 'Σ this run' },
      { k: 'PR Throughput', v: `${merged} / ${ws.length}`,         delta: ws.length ? `${Math.round((merged / ws.length) * 100)}% merge` : '0%', tone: 'green' as const },
      { k: 'Stalled',       v: String(stalled),                    delta: stalled ? 'esc. to lead' : '—',                                          tone: stalled ? ('red' as const) : undefined },
    ];
  }, [detail]);

  if (err) {
    return (
      <div className="swarm-ctrl">
        <div className="shell">
          <SwarmNavBar crumbs={[{ pre: 'ROOT', label: 'Workstreams', href: '/workstreams' }, { label: storyId, active: true }]} onBack={() => navigate('/workstreams')} />
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
          <SwarmNavBar crumbs={[{ pre: 'ROOT', label: 'Workstreams', href: '/workstreams' }, { label: storyId, active: true }]} onBack={() => navigate('/workstreams')} />
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
          ]}
          onBack={() => navigate('/workstreams')}
        />
        <SwarmPipelineStrip
          stages={stages}
          active={mode}
          onSelect={(m) => setMode(m)}
        />

        <div className="grid-main">
          <div className={`mode-pane${mode ? ' active' : ''}`}>
            <section className="panel">
              <div className="panel-hd">
                <span>▌ {mode === 'design' ? 'DESIGN DOCS' : mode === 'impl' ? 'PHASE BOARD · NAVIGATE' : 'RELEASE GATES'}</span>
                <span className="ix">CLICK TO DRILL</span>
              </div>
              <div className="panel-bd">
                <div className="mode-stub">
                  <div className="lbl">▌ {mode?.toUpperCase()} · MASTER PANEL</div>
                  <div className="sub">
                    Phase board / doc list / gate list — coming in the next pass. Once landed,
                    selecting a row drills into the right-side detail panel with its 6 tabs:
                    overview, conversation, arch doc, PR, QA / E2E, trace.
                  </div>
                </div>
              </div>
            </section>
            <section className="panel">
              <div className="panel-hd">
                <span>▌ DETAIL · TABS</span>
                <span className="ix">SELECT A ROW</span>
              </div>
              <div className="panel-bd">
                <div className="mode-stub">
                  <div className="lbl">▌ DETAIL PANE</div>
                  <div className="sub">
                    Tab body for the selected row will land here — overview / conversation / arch doc
                    / PR / QA / trace. Hooks into existing endpoints: `/api/work-items`, `/api/prs`,
                    `/api/work-items/:id/transcript`.
                  </div>
                </div>
              </div>
            </section>
          </div>
        </div>
      </div>
      <Toaster />
    </div>
  );
}
