import { useEffect, useMemo, useState } from 'react';
import type { StorySpawnDetail } from '@/api/forgeApi';
import type { SwarmMode } from './SwarmPipelineStrip';
import type { Selection } from './SwarmMasterPanel';

export type DetailTab = 'overview' | 'conv' | 'arch' | 'pr' | 'qa' | 'trace';

interface Props {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  selection: Selection;
  tab: DetailTab;
  onTab: (t: DetailTab) => void;
}

const TAB_LABELS: { key: DetailTab; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'conv',     label: 'Conversation' },
  { key: 'arch',     label: 'Arch Doc' },
  { key: 'pr',       label: 'PR' },
  { key: 'qa',       label: 'QA / E2E' },
  { key: 'trace',    label: 'Trace' },
];

export function SwarmDetailPanel({ mode, detail, selection, tab, onTab }: Props) {
  const tabsAvailable = useMemo(() => availableTabs(mode, selection, detail), [mode, selection, detail]);

  // If the current tab isn't available for this selection, jump to the first available.
  useEffect(() => {
    if (!tabsAvailable.includes(tab)) {
      const next = tabsAvailable[0];
      if (next) onTab(next);
    }
  }, [tabsAvailable, tab, onTab]);

  return (
    <section className="panel detail-panel">
      <div className="tabs">
        {TAB_LABELS.map((t) => {
          const enabled = tabsAvailable.includes(t.key);
          return (
            <button
              key={t.key}
              className={`tab${tab === t.key ? ' active' : ''}${enabled ? '' : ' disabled'}`}
              onClick={() => enabled && onTab(t.key)}
              disabled={!enabled}
              type="button"
            >
              {t.label}
              <span className="ct">{tabCount(t.key, mode, selection, detail)}</span>
            </button>
          );
        })}
      </div>
      <div className="tab-body">
        <div className={`tab-pane${tab === 'overview' ? ' active' : ''}`}>
          {tab === 'overview' && <OverviewPane mode={mode} detail={detail} selection={selection} />}
        </div>
        <div className={`tab-pane${tab === 'conv' ? ' active' : ''}`}>
          {tab === 'conv' && <Placeholder
            label="conversation tab"
            sub="paired-agent transcript with iteration history, score-bumps, and live tail. Wired to /api/work-items/:id/transcript once a workstream is selected."
          />}
        </div>
        <div className={`tab-pane${tab === 'arch' ? ' active' : ''}`}>
          {tab === 'arch' && <Placeholder
            label="arch doc tab"
            sub="long-form view of the four merged plan artifacts (architecture / tech-spec / implementation-plan / e2e-scenarios) with sticky TOC. Wired to artifacts.plan.files."
          />}
        </div>
        <div className={`tab-pane${tab === 'pr' ? ' active' : ''}`}>
          {tab === 'pr' && <Placeholder
            label="PR tab"
            sub="file tree + side-by-side diff + comment side-rail + check-run rail. Wired to /api/prs/:id."
          />}
        </div>
        <div className={`tab-pane${tab === 'qa' ? ' active' : ''}`}>
          {tab === 'qa' && <Placeholder
            label="QA / E2E tab"
            sub="Gherkin scenario list (pass / fail / warn) + coverage rail per file. Wired to QA PR diff + Phase 27 build results."
          />}
        </div>
        <div className={`tab-pane${tab === 'trace' ? ' active' : ''}`}>
          {tab === 'trace' && <Placeholder
            label="trace tab"
            sub="flat event log: time | KIND | actor | description with kind colors (tool / gen / evl / score / pass / fail)."
          />}
        </div>
      </div>
    </section>
  );
}

/* ─── TAB AVAILABILITY ───────────────────────────────────────────────── */

function availableTabs(mode: SwarmMode, sel: Selection, detail: StorySpawnDetail): DetailTab[] {
  // Overview is always available — even at story root.
  const tabs: DetailTab[] = ['overview'];
  if (sel.kind === 'phase') {
    const ws = detail.workstreams.find((w) => w.id === sel.id);
    if (ws) {
      tabs.push('conv');
      if (ws.prs.length > 0) tabs.push('pr');
    }
    if (mode === 'impl') tabs.push('qa', 'trace');
  } else if (sel.kind === 'doc') {
    tabs.push('arch', 'conv');
    if (detail.spawn.planPrId) tabs.push('pr');
  } else if (sel.kind === 'gate') {
    tabs.push('trace');
  } else {
    // overview-level selection — show trace too so the global event log is always reachable.
    tabs.push('trace');
  }
  return tabs;
}

function tabCount(tab: DetailTab, mode: SwarmMode, sel: Selection, detail: StorySpawnDetail): string {
  if (tab === 'pr') {
    if (sel.kind === 'phase') {
      const ws = detail.workstreams.find((w) => w.id === sel.id);
      return String(ws?.prs.length ?? 0);
    }
    if (sel.kind === 'doc' && detail.spawn.planPrId) return '1';
    return '0';
  }
  if (tab === 'overview' && sel.kind === 'overview') return String(detail.workstreams.length);
  if (tab === 'arch' && mode === 'design') return '4';
  return '·';
}

/* ─── OVERVIEW PANE ──────────────────────────────────────────────────── */

interface OvProps {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  selection: Selection;
}

interface AgentRow {
  id: string;
  name: string;
  role: string; // arch / eng / rev / qa
  crewId: string | null;
  state: string;
  generatorModel: string | null;
  evaluatorModel: string | null;
}

function OverviewPane({ mode, detail, selection }: OvProps) {
  const [agents, setAgents] = useState<AgentRow[]>([]);
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await fetch('/api/agents', { credentials: 'include' });
        if (!r.ok) return;
        const json = await r.json();
        if (!cancelled && Array.isArray(json)) setAgents(json as AgentRow[]);
      } catch { /* swallow */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 30_000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  const headlinePre = selection.kind === 'phase' ? 'WORKSTREAM' : selection.kind === 'doc' ? 'DOC' : selection.kind === 'gate' ? 'GATE' : 'STORY';
  const headlineId  = selection.id ?? detail.spawn.storyId;
  const subline = selection.kind === 'overview'
    ? `${detail.workstreams.length} workstreams · phase ${detail.spawn.phase}`
    : selection.id ?? '';

  // Pair cards: one per workstream (the generator / evaluator pair that owns it).
  // `assignedAgent.role` is not yet on the contract (EX.2) — show pair shape with
  // role-derived names from the workstream id and TBD models.
  const pairs = detail.workstreams
    .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
    .map((w, i) => ({
      id: `WS·${String(i + 1).padStart(2, '0')}`,
      team: pairTeamForStatus(w.status),
      cls: pairClsForStatus(w.status),
      task: w.name,
      gen:  '—',
      genM: 'tbd',
      evl:  '—',
      evlM: 'tbd',
      meters: pairMetersForStatus(w.status),
      status: w.status,
    }));

  // Count day-shift only (`crew-{role}`, not `crew-{role}-night`) so the
  // 20-cell swarm grid matches the spec's "~20 agents per team" capacity.
  // Without this filter, day + night roster sums (e.g., 37 architects)
  // produce nonsense ratios like "37/20".
  const teamCounts: Record<string, number> = { arch: 0, eng: 0, rev: 0, qa: 0 };
  for (const a of agents) {
    if (!a.crewId || a.crewId.endsWith('-night')) continue;
    const team = a.crewId.replace(/^crew-/, '');
    if (team in teamCounts) teamCounts[team]++;
  }

  return (
    <div className="ov-grid">
      <div className="ov-meta">
        <div>
          <div className="ov-headline"><span className="pre">{headlinePre}</span>{prettyId(headlineId)}</div>
          <div className="ov-sub">{subline}</div>
        </div>
        <div style={{ fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.18em', color: 'var(--sc-text-dim)' }}>
          MODE · {mode.toUpperCase()}
        </div>
      </div>

      <div className="ov-split">
        <div className="ov-split-l">
          {pairs.length === 0 ? (
            <div className="tab-placeholder">
              <div className="lbl">▌ NO ACTIVE PAIRS</div>
              <div className="sub">Pair cards will appear once the architect crew decomposes the story and engineer pairs claim workstreams.</div>
            </div>
          ) : (
            <div className="pairs">
              {pairs.map((p) => (
                <div key={p.id} className={`pair ${p.cls}`}>
                  <div className="pair-hd">
                    <div className="pair-id">
                      <span className="team">{p.team}</span>{p.id}
                    </div>
                    <div className="pair-task" title={p.task}>{p.task}</div>
                  </div>
                  <div className="pair-agents">
                    <div className="agent-tag gen">
                      <span className="role">GENERATOR</span>
                      <span className="name">{p.gen}</span>
                      <span className="model">{p.genM}</span>
                    </div>
                    <div className="pair-link">⇄</div>
                    <div className="agent-tag evl">
                      <span className="role">EVALUATOR</span>
                      <span className="name">{p.evl}</span>
                      <span className="model">{p.evlM}</span>
                    </div>
                  </div>
                  <div className="pair-meters">
                    {p.meters.map((m, i) => (
                      <div className="pair-m" key={i}>
                        <div className="k">{m.k}</div>
                        <div className="v">{m.v}</div>
                      </div>
                    ))}
                  </div>
                  <div className="iter-bars">
                    {[1,2,3,4,5].map((n) => (
                      <div key={n} className={`iter-bar${n === 1 ? ' curr' : ''}`} style={{ height: 24 }}>
                        <div className="fill" style={{ height: `${n === 1 ? 60 : 0}%` }} />
                        <span className="lbl">·</span>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="ov-split-r">
          <div className="swarm">
            {(['arch', 'eng', 'rev', 'qa'] as const).map((teamKey) => {
              const visible = Math.min(teamCounts[teamKey], 20);
              const pct = Math.round((visible / 20) * 100);
              return (
                <div key={teamKey} className={`team ${teamKey}`}>
                  <div className="team-hd">
                    <span className="team-name">{teamKey}</span>
                    <div className="team-load"><div className="bar" style={{ width: `${pct}%` }} /></div>
                    <span className="team-count">{visible}<span className="tot">/20</span></span>
                    <span style={{ fontFamily: 'var(--sc-font-display)', fontSize: 8, letterSpacing: '0.16em', color: 'var(--sc-muted)' }}>IDLE</span>
                  </div>
                  <div className="team-grid">
                    {Array.from({ length: 20 }).map((_, i) => {
                      const filled = i < visible;
                      return <div key={i} className={`a ${filled ? 'idle' : ''}`} />;
                    })}
                  </div>
                </div>
              );
            })}
          </div>
          <div className="legend" style={{ marginTop: 8 }}>
            <div className="lg"><span className="d idle" /> idle</div>
            <div className="lg"><span className="d gen" /> gen</div>
            <div className="lg"><span className="d evl" /> evl</div>
            <div className="lg"><span className="d rev" /> rev</div>
            <div className="lg"><span className="d e2e" /> e2e</div>
            <div className="lg"><span className="d stl" /> stl</div>
          </div>
        </div>
      </div>
    </div>
  );
}

/* ─── helpers ───────────────────────────────────────────────────────── */

function pairTeamForStatus(status: string): string {
  if (status === 'review' || status === 'merging') return 'REV';
  if (status === 'complete') return 'QA';
  if (status === 'planning' || status === 'blocked') return 'ARCH';
  return 'ENG';
}

function pairClsForStatus(status: string): string {
  if (status === 'review' || status === 'merging') return 'review';
  if (status === 'complete') return 'e2e';
  if (status === 'stalled') return 'stalled';
  return '';
}

function pairMetersForStatus(status: string): { k: string; v: string }[] {
  if (status === 'review' || status === 'merging') {
    return [{ k: 'COMMENTS', v: '—' }, { k: 'REQ-CHG', v: '—' }, { k: 'SCORE', v: '—' }];
  }
  if (status === 'complete') {
    return [{ k: 'PASS', v: '—' }, { k: 'FAIL', v: '0' }, { k: 'COVER', v: '—' }];
  }
  if (status === 'stalled') {
    return [{ k: 'TRIAL', v: '3/3' }, { k: 'LAST SCR', v: '—' }, { k: 'STATE', v: 'stalled' }];
  }
  return [{ k: 'TURNS', v: '—' }, { k: 'FILES', v: '—' }, { k: 'ELAPSED', v: '—' }];
}

function prettyId(id: string): string {
  // Trim long workstream ids for the headline, leaving a recognizable suffix.
  if (id.length <= 36) return id;
  return id.slice(0, 18) + '…' + id.slice(-16);
}

function Placeholder({ label, sub }: { label: string; sub: string }) {
  return (
    <div className="tab-placeholder">
      <div className="lbl">▌ {label}</div>
      <div className="sub">{sub}</div>
    </div>
  );
}
