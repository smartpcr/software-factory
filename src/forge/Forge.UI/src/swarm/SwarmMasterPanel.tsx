import type { StorySpawnArtifacts, StorySpawnDetail, StorySpawnWorkstreamSummary } from '@/api/forgeApi';
import type { SwarmMode } from './SwarmPipelineStrip';

export type SelectionKind = 'overview' | 'phase' | 'doc' | 'gate';
export interface Selection {
  kind: SelectionKind;
  id: string | null;
}

interface Props {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  artifacts: StorySpawnArtifacts | null;
  selection: Selection;
  onSelect: (sel: Selection) => void;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}

function CollapseBtn({ collapsed, onToggle }: { collapsed: boolean; onToggle: () => void }) {
  return (
    <button
      type="button"
      className="panel-collapse-btn"
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      title={collapsed ? 'Expand master panel' : 'Collapse master panel'}
      aria-label={collapsed ? 'Expand master panel' : 'Collapse master panel'}
    >
      {collapsed ? '›' : '‹'}
    </button>
  );
}

const STATUS_TO_PHASE_CLASS: Record<string, string> = {
  planning: 'queued',
  active:   'active',
  review:   'review',
  merging:  'active',
  complete: 'done',
  blocked:  'blocked',
  stalled:  'stalled',
};

const STATUS_TO_BADGE: Record<string, string> = {
  planning: 'blocked',
  active:   'active',
  review:   'review',
  merging:  'active',
  complete: 'done',
  blocked:  'blocked',
  stalled:  'stalled',
};

const STATUS_PROGRESS: Record<string, number> = {
  planning: 5,
  active:   45,
  review:   70,
  merging:  90,
  complete: 100,
  blocked:  0,
  stalled:  35,
};

export function SwarmMasterPanel(props: Props) {
  if (props.mode === 'design') return <DesignMaster {...props} />;
  if (props.mode === 'release') return <ReleaseMaster {...props} />;
  return <ImplMaster {...props} />;
}

/* ─── IMPL MODE ── workstreams as phase cards ────────────────────────── */

function ImplMaster({ detail, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const ws = detail.workstreams;
  const stalled = ws.filter((w) => w.status === 'stalled');
  const blocked = ws.filter((w) => w.status === 'blocked');
  const attention = [...stalled, ...blocked];

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ PHASE BOARD · WORKSTREAMS</span>
        <span className="ix">{ws.length} ROWS · CLICK TO DRILL</span>
      </div>
      <div className="panel-bd">
        {attention.length > 0 && (
          <div className="attention">
            <div className="attention-hd">
              <span>NEEDS ATTENTION</span>
              <span>{attention.length}</span>
            </div>
            <div className="attention-bd">
              {attention.map((w) => (
                <div key={w.id} className="att-item" onClick={() => onSelect({ kind: 'phase', id: w.id })}>
                  <span className="id">{shortId(w.id)}</span>
                  <span className="reason">
                    {w.status === 'stalled' ? 'stalled — escalate to lead' : `blocked on ${w.deps.length} upstream`}
                  </span>
                  <span style={{ color: 'var(--sc-muted)' }}>›</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {ws.length === 0 ? (
          <div className="mode-stub">
            <div className="lbl">▌ NO WORKSTREAMS</div>
            <div className="sub">
              The architect crew has not yet decomposed this story into workstreams.
              Once decomposition lands, each workstream renders here as one phase card.
            </div>
          </div>
        ) : (
          <div className="phase-board">
            {ws.map((w, i) => (
              <PhaseCard
                key={w.id}
                ws={w}
                index={i}
                selected={selection.kind === 'phase' && selection.id === w.id}
                onSelect={() => onSelect({ kind: 'phase', id: w.id })}
              />
            ))}
          </div>
        )}
      </div>
    </section>
  );
}

interface PhaseCardProps {
  ws: StorySpawnWorkstreamSummary;
  index: number;
  selected: boolean;
  onSelect: () => void;
}

function PhaseCard({ ws, index, selected, onSelect }: PhaseCardProps) {
  const phaseCls = STATUS_TO_PHASE_CLASS[ws.status] ?? 'queued';
  const badgeCls = STATUS_TO_BADGE[ws.status] ?? 'blocked';
  const pct = STATUS_PROGRESS[ws.status] ?? 0;

  // Score / points / iter aren't yet wired into the workstream contract;
  // surface neutral placeholders until EX.1 / EX.2 / EX.3 land.
  return (
    <div
      className={`phase ${phaseCls}${selected ? ' selected' : ''}`}
      onClick={onSelect}
    >
      <div className="phase-hd">
        <span className="phase-no">PHASE <b>{String(index + 1).padStart(2, '0')}</b></span>
        <span className="phase-title">{ws.name}</span>
        <span className="score-pill">—</span>
        <span className={`badge ${badgeCls}`}>{ws.status}</span>
      </div>
      <div className="phase-bd">
        <div className={`progress ${phaseCls === 'done' ? 'done' : phaseCls === 'review' ? 'review' : ''}`}>
          <div className="bar" style={{ width: `${pct}%` }} />
        </div>
        <div className="stages">
          <div className="stage-row queued">
            <span className="id">BR</span>
            <span className="name">{ws.branch}</span>
            <span className="pts">—</span>
            <span className="stat">branch</span>
          </div>
          {ws.prs.length > 0 && (
            ws.prs.map((pr) => (
              <div key={pr} className="stage-row review">
                <span className="id">PR</span>
                <span className="name">{pr}</span>
                <span className="pts">—</span>
                <span className="stat">{ws.status}</span>
              </div>
            ))
          )}
          {ws.deps.length > 0 && (
            <div className="stage-row blocked">
              <span className="id">DEPS</span>
              <span className="name">{ws.deps.length} upstream {ws.deps.length === 1 ? 'workstream' : 'workstreams'}</span>
              <span className="pts">—</span>
              <span className="stat">{ws.status === 'blocked' ? 'waiting' : 'cleared'}</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* ─── DESIGN MODE ── four planning artifacts as doc cards ────────────── */

const PLAN_DOCS: { no: string; key: 'architecture' | 'tech-spec' | 'implementation-plan' | 'e2e-scenarios'; title: string; }[] = [
  { no: '01', key: 'architecture',         title: 'architecture.md — system + components + scenarios' },
  { no: '02', key: 'tech-spec',            title: 'tech-spec.md — libs, patterns, practices' },
  { no: '03', key: 'implementation-plan',  title: 'implementation-plan.md — Phase → Stage → Step' },
  { no: '04', key: 'e2e-scenarios',        title: 'e2e-scenarios.md — Gherkin BDD per phase' },
];

function DesignMaster({ detail, artifacts, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const planMerged = !!artifacts?.plan.pr && detail.spawn.phase !== 'plan';
  const planOpen   = !!artifacts?.plan.pr && detail.spawn.phase === 'plan';
  const decomposed = !!detail.spawn.decompositionJson;

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ DESIGN DOCS · ARCHITECT CREW</span>
        <span className="ix">{PLAN_DOCS.length} ARTIFACTS</span>
      </div>
      <div className="panel-bd">
        <div className="doc-list">
          {PLAN_DOCS.map((doc) => {
            const status = planMerged ? 'merged' : planOpen ? 'review' : 'draft';
            const id = `doc:${doc.key}`;
            return (
              <div
                key={doc.key}
                className={`doc-card ${status}${selection.kind === 'doc' && selection.id === id ? ' selected' : ''}`}
                onClick={() => onSelect({ kind: 'doc', id })}
              >
                <div className="doc-no">DOC {doc.no}</div>
                <div className="doc-title">{doc.title}</div>
                <div className="doc-meta">
                  <span>plan PR · <b>{artifacts?.plan.pr ?? '—'}</b></span>
                  <span>branch · <b>{artifacts?.plan.branch ?? '—'}</b></span>
                </div>
                <div className="doc-status">{status}</div>
              </div>
            );
          })}

          <div
            className={`doc-card ${decomposed ? 'merged' : 'draft'}${selection.kind === 'doc' && selection.id === 'doc:decomposition' ? ' selected' : ''}`}
            onClick={() => onSelect({ kind: 'doc', id: 'doc:decomposition' })}
          >
            <div className="doc-no">DECOMP</div>
            <div className="doc-title">decomposition — N parallel workstreams + dep graph</div>
            <div className="doc-meta">
              <span>workstreams · <b>{detail.workstreams.length}</b></span>
              <span>approved · <b>{detail.spawn.decompositionApproved ? 'yes' : 'no'}</b></span>
            </div>
            <div className="doc-status">{decomposed ? 'merged' : 'draft'}</div>
          </div>
        </div>
      </div>
    </section>
  );
}

/* ─── RELEASE MODE ── completion gates ───────────────────────────────── */

function ReleaseMaster({ detail, artifacts, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const ws = detail.workstreams;
  const total = ws.length || 1;
  const merged = ws.filter((w) => w.status === 'complete' || w.status === 'merging').length;
  const stalled = ws.filter((w) => w.status === 'stalled').length;
  const planMerged = !!artifacts?.plan.pr && detail.spawn.phase !== 'plan';
  const qaMerged   = !!detail.spawn.qaPrId;

  const gates = [
    {
      id: 'plan',
      name: 'PLAN ARTIFACTS MERGED',
      pct: planMerged ? 100 : 0,
      status: planMerged ? 'pass' as const : 'pending' as const,
      summary: planMerged ? `${artifacts?.plan.pr} on main` : 'awaiting plan PR merge',
    },
    {
      id: 'impl',
      name: 'WORKSTREAM PRS MERGED',
      pct: Math.round((merged / total) * 100),
      status: merged === total ? 'pass' as const : merged === 0 ? 'pending' as const : 'warn' as const,
      summary: `${merged}/${total} workstream PRs merged`,
    },
    {
      id: 'qa',
      name: 'QA FEATURE FILES MERGED',
      pct: qaMerged ? 100 : 0,
      status: qaMerged ? 'pass' as const : 'pending' as const,
      summary: qaMerged ? `${detail.spawn.qaPrId} on main` : 'awaiting QA PR',
    },
    {
      id: 'tests',
      name: 'PER-PHASE UNIT + INT + E2E GREEN',
      pct: 0,
      status: 'pending' as const,
      summary: 'gate runs once step-defs land per workstream',
    },
    {
      id: 'stalled',
      name: 'NO STALLED WORK ITEMS',
      pct: stalled === 0 ? 100 : 0,
      status: stalled === 0 ? 'pass' as const : 'fail' as const,
      summary: stalled === 0 ? 'no stalled items' : `${stalled} item(s) stalled — escalate`,
    },
  ];

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ RELEASE GATES</span>
        <span className="ix">{gates.length} GATES</span>
      </div>
      <div className="panel-bd">
        <div className="gate-list">
          {gates.map((g) => (
            <div
              key={g.id}
              className={`gate-card ${g.status}${selection.kind === 'gate' && selection.id === g.id ? ' selected' : ''}`}
              onClick={() => onSelect({ kind: 'gate', id: g.id })}
            >
              <div className="gate-hd">
                <span className="gate-name">{g.name}</span>
                <span className="gate-pct">{g.pct}%</span>
              </div>
              <div className="gate-summary">{g.summary}</div>
              <div className="gate-bar"><div className="fill" style={{ width: `${g.pct}%` }} /></div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function shortId(id: string): string {
  // ws-foo-bar-baz-very-long  →  …baz-very-long
  if (id.length <= 14) return id.toUpperCase();
  return ('…' + id.slice(-13)).toUpperCase();
}
