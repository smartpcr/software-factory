import { useEffect, useMemo, useState } from 'react';
import type { StorySpawnArtifacts, StorySpawnDetail } from '@/api/forgeApi';
import type { SwarmMode } from './SwarmPipelineStrip';
import type { Selection } from './SwarmMasterPanel';
import { DocumentPane } from './DocumentPane';

export type DetailTab =
  | 'overview' | 'conv' | 'arch' | 'pr' | 'qa' | 'trace'
  | 'document' | 'impl' | 'contributors';

interface Props {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  artifacts: StorySpawnArtifacts | null;
  selection: Selection;
  tab: DetailTab;
  onTab: (t: DetailTab) => void;
}

const TAB_LABELS: { key: DetailTab; label: string }[] = [
  { key: 'overview',     label: 'Overview' },
  { key: 'document',     label: 'Document' },
  { key: 'conv',         label: 'Conversation' },
  { key: 'arch',         label: 'Arch Doc' },
  { key: 'pr',           label: 'PR' },
  { key: 'impl',         label: 'Impl Progress' },
  { key: 'contributors', label: 'Contributors' },
  { key: 'qa',           label: 'QA / E2E' },
  { key: 'trace',        label: 'Trace' },
];

export function SwarmDetailPanel({ mode, detail, artifacts, selection, tab, onTab }: Props) {
  const tabsAvailable = useMemo(() => availableTabs(mode, selection, detail), [mode, selection, detail]);

  // If the current tab isn't available for this selection, jump to the first available.
  useEffect(() => {
    if (!tabsAvailable.includes(tab)) {
      const next = tabsAvailable[0];
      if (next) onTab(next);
    }
  }, [tabsAvailable, tab, onTab]);

  // Order tabs visually as they appear in `tabsAvailable` so the doc-mode
  // tab strip reads `Document · Conversation · PR · Impl Progress · Contributors`
  // rather than the canonical order from TAB_LABELS.
  const tabsToRender = tabsAvailable
    .map((k) => TAB_LABELS.find((t) => t.key === k))
    .filter((t): t is { key: DetailTab; label: string } => !!t);

  return (
    <section className="panel sc-detail-panel">
      <div className="tabs">
        {tabsToRender.map((t) => (
          <button
            key={t.key}
            className={`tab${tab === t.key ? ' active' : ''}`}
            onClick={() => onTab(t.key)}
            type="button"
          >
            {t.label}
            <span className="ct">{tabCount(t.key, mode, selection, detail, artifacts)}</span>
          </button>
        ))}
      </div>
      <div className="tab-body">
        <div className={`tab-pane${tab === 'overview' ? ' active' : ''}`}>
          {tab === 'overview' && <OverviewPane mode={mode} detail={detail} selection={selection} />}
        </div>
        <div className={`tab-pane${tab === 'document' ? ' active' : ''}`}>
          {tab === 'document' && selection.kind === 'doc' && selection.id && (
            <DocumentPane
              docId={selection.id}
              planPr={artifacts?.plan.pr ?? null}
              planBranch={artifacts?.plan.branch ?? null}
            />
          )}
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
        <div className={`tab-pane${tab === 'impl' ? ' active' : ''}`}>
          {tab === 'impl' && <Placeholder
            label="impl progress tab"
            sub="per-section completion rail derived from the implementation-plan doc. Wired once impl-plan section ↔ workstream mapping lands."
          />}
        </div>
        <div className={`tab-pane${tab === 'contributors' ? ' active' : ''}`}>
          {tab === 'contributors' && <Placeholder
            label="contributors tab"
            sub="agent-by-agent edit history with per-section authorship. Wired to git blame on the doc file."
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
  if (sel.kind === 'doc') {
    // Doc-centric tab strip: the document body is the headline; everything
    // else hangs off the doc's lifecycle (plan PR, contributors, downstream
    // implementation progress that this doc seeded).
    const tabs: DetailTab[] = ['document', 'conv'];
    if (detail.spawn.planPrId) tabs.push('pr');
    tabs.push('impl', 'contributors');
    return tabs;
  }
  // Overview is always available — even at story root.
  const tabs: DetailTab[] = ['overview'];
  if (sel.kind === 'phase') {
    const ws = detail.workstreams.find((w) => w.id === sel.id);
    if (ws) {
      tabs.push('conv');
      if (ws.prs.length > 0) tabs.push('pr');
    }
    if (mode === 'impl') tabs.push('qa', 'trace');
  } else if (sel.kind === 'gate') {
    tabs.push('trace');
  } else {
    // overview-level selection — show trace too so the global event log is always reachable.
    tabs.push('trace');
  }
  return tabs;
}

function tabCount(
  tab: DetailTab,
  mode: SwarmMode,
  sel: Selection,
  detail: StorySpawnDetail,
  artifacts: StorySpawnArtifacts | null,
): string {
  if (tab === 'pr') {
    if (sel.kind === 'phase') {
      const ws = detail.workstreams.find((w) => w.id === sel.id);
      return String(ws?.prs.length ?? 0);
    }
    if (sel.kind === 'doc' && detail.spawn.planPrId) {
      // Render the actual plan PR id (e.g. "PR-1812") rather than just a count
      // so the tab strip matches the mock.
      return shortPrId(detail.spawn.planPrId);
    }
    return '0';
  }
  if (tab === 'document' && sel.kind === 'doc') {
    return '·';
  }
  if (tab === 'conv' && sel.kind === 'doc') {
    // Mocked count of in-thread comments / scratch refs until a real
    // comment-stream API lands.
    return '·';
  }
  if (tab === 'contributors' && sel.kind === 'doc') {
    return String(contributorCountForDoc(sel.id, artifacts));
  }
  if (tab === 'impl' && sel.kind === 'doc') {
    return String(detail.workstreams.length);
  }
  if (tab === 'overview' && sel.kind === 'overview') return String(detail.workstreams.length);
  if (tab === 'arch' && mode === 'design') return '4';
  return '·';
}

function shortPrId(id: string): string {
  // Accept "PR #1812" / "PR-1812" / "1812" forms — render as compact "PR-1812"
  // for the tab chip.
  const trimmed = id.replace(/^pr[\s#-]*/i, '');
  return `PR-${trimmed}`;
}

/** Mocked contributor counts per doc key — matches DocumentPane's MOCKS. */
function contributorCountForDoc(docId: string | null, _artifacts: StorySpawnArtifacts | null): number {
  if (!docId) return 0;
  const key = docId.startsWith('doc:') ? docId.slice(4) : docId;
  switch (key) {
    case 'architecture':         return 3;
    case 'tech-spec':            return 2;
    case 'implementation-plan':  return 2;
    case 'e2e-scenarios':        return 2;
    case 'decomposition':        return 1;
    default:                     return 0;
  }
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
  // All values are sourced from the StorySpawnWorkstreamSummary contract — the
  // backend joins WorkstreamRow.AgentsJson + the deliverables table + the
  // agent_usage cost ledger so we don't need per-card round-trips.
  const agentsById = new Map(agents.map((a) => [a.id, a]));
  const displayName = (id: string | null | undefined): string => {
    if (!id) return '—';
    const a = agentsById.get(id);
    return a?.name ?? id;
  };
  const modelFor = (id: string | null | undefined, role: 'gen' | 'evl'): string => {
    if (!id) return '—';
    const a = agentsById.get(id);
    if (!a) return '—';
    return (role === 'gen' ? a.generatorModel : a.evaluatorModel) ?? '—';
  };
  const teamFor = (id: string | null | undefined): string => {
    if (!id) return 'ENG';
    const a = agentsById.get(id);
    if (!a?.crewId) return 'ENG';
    const team = a.crewId.replace(/^crew-/, '').replace(/-night$/, '');
    return team.toUpperCase();
  };
  const pairs = detail.workstreams
    .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
    .map((w, i) => ({
      id: `WS·${String(i + 1).padStart(2, '0')}`,
      team: teamFor(w.latestGenAgentId ?? w.agents?.[0]),
      cls: pairClsForStatus(w.status),
      task: w.name,
      gen:  displayName(w.latestGenAgentId ?? w.agents?.[0]),
      genM: modelFor(w.latestGenAgentId ?? w.agents?.[0], 'gen'),
      evl:  displayName(w.latestEvalAgentId ?? w.agents?.[1]),
      evlM: modelFor(w.latestEvalAgentId ?? w.agents?.[1], 'evl'),
      meters: pairMetersForWorkstream(w),
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

function pairClsForStatus(status: string): string {
  if (status === 'review' || status === 'merging') return 'review';
  if (status === 'complete') return 'e2e';
  if (status === 'stalled') return 'stalled';
  return '';
}

/** Real-data meters: TURNS = deliverables.count; SCORE = latest deliverable
 *  (or '—' if none); ELAPSED = since-latest-deliverable; COST = sum of
 *  agent_usage rows for this workstream. */
function pairMetersForWorkstream(w: import('@/api/forgeApi').StorySpawnWorkstreamSummary): { k: string; v: string }[] {
  const iter = w.iter ?? 0;
  const score = w.latestScore;
  const elapsed = w.lastActiveAt ? formatElapsed(w.lastActiveAt) : '—';
  const cost = w.costUsd ?? 0;
  return [
    { k: 'TURNS',   v: iter > 0 ? String(iter) : '—' },
    { k: 'SCORE',   v: score == null ? '—' : String(score) },
    { k: 'ELAPSED', v: elapsed },
    { k: 'COST',    v: cost > 0 ? `$${cost.toFixed(2)}` : '—' },
  ];
}

function formatElapsed(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '—';
  const ms = Date.now() - t;
  if (ms < 0) return '—';
  const sec = Math.floor(ms / 1000);
  if (sec < 60)    return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60)    return `${min}m`;
  const hr  = Math.floor(min / 60);
  if (hr  < 48)    return `${hr}h ${min % 60}m`;
  const day = Math.floor(hr / 24);
  return `${day}d`;
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
