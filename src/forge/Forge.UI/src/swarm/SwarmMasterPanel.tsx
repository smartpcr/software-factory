import { useEffect, useState } from 'react';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnWorkstreamSummary } from '@/api/forgeApi';
import type { SwarmMode } from './SwarmPipelineStrip';
import { avatarFor } from '@/agentAvatar';

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
  const unanswered = ws.unansweredQuestionCount ?? 0;

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
        <span className="score-pill">
          {ws.latestScore != null ? ws.latestScore : '—'}
        </span>
        <span className={`badge ${badgeCls}`}>{ws.status}</span>
        {unanswered > 0 && (
          <span
            className="badge stalled"
            style={{
              marginLeft: 4,
              background: 'rgba(244,71,71,0.15)',
              color: '#f44747',
              border: '1px solid rgba(244,71,71,0.3)',
              fontSize: 10,
              padding: '1px 6px',
              borderRadius: 8,
              fontWeight: 600,
            }}
            title={`${unanswered} unanswered question${unanswered === 1 ? '' : 's'} blocking pass`}
          >
            ⚠ {unanswered} Q
          </span>
        )}
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
  const docStats = useDocStats(detail.spawn.storyId, PLAN_DOCS.map((d) => d.key));

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
            const prState = planMerged ? 'merged' : planOpen ? 'review' : 'draft';
            const id = `doc:${doc.key}`;
            const stats = docStats[doc.key];
            const loopStatus = stats?.status ?? 'pending';
            const fileName = `${doc.key}.md`;
            const subtitle = doc.title.split('—').slice(1).join('—').trim();
            const tone = verdictTone(stats?.verdict ?? '');
            return (
              <div
                key={doc.key}
                className={`doc-card ${prState}${selection.kind === 'doc' && selection.id === id ? ' selected' : ''}`}
                onClick={() => onSelect({ kind: 'doc', id })}
                title={`plan PR ${artifacts?.plan.pr ?? '—'} · branch ${artifacts?.plan.branch ?? '—'}`}
              >
                <div className="doc-card__top">
                  <span className="doc-no">DOC {doc.no}</span>
                  <span className={`doc-status doc-status--${loopStatus}`}>{loopStatus}</span>
                </div>
                <div className="doc-card__name">{fileName}</div>
                {subtitle && <div className="doc-card__sub">{subtitle}</div>}
                <div className="doc-card__metrics">
                  {(stats?.generator || stats?.evaluator) && (
                    <DocAgentStrip generator={stats.generator} evaluator={stats.evaluator} />
                  )}
                  {stats && stats.iter > 0 && (
                    <span className="m-cell">
                      <span className="k">ITER</span>
                      <span className="v">{stats.iter}</span>
                    </span>
                  )}
                  {stats && stats.score != null && (
                    <span className={`m-cell score ${tone}`}>
                      <span className="k">SCORE</span>
                      <span className="v">{stats.score}</span>
                    </span>
                  )}
                  {stats && stats.cost > 0 && (
                    <span className="m-cell cost">
                      <span className="k">COST</span>
                      <span className="v">${stats.cost.toFixed(2)}</span>
                    </span>
                  )}
                </div>
              </div>
            );
          })}

          <div
            className={`doc-card ${decomposed ? 'merged' : 'draft'}${selection.kind === 'doc' && selection.id === 'doc:decomposition' ? ' selected' : ''}`}
            onClick={() => onSelect({ kind: 'doc', id: 'doc:decomposition' })}
          >
            <div className="doc-card__top">
              <span className="doc-no">DECOMP</span>
              <span className="doc-status">{decomposed ? 'merged' : 'draft'}</span>
            </div>
            <div className="doc-card__name">workstreams.json</div>
            <div className="doc-card__sub">N parallel workstreams + dependency graph</div>
            <div className="doc-card__metrics">
              <span className="m-cell">
                <span className="k">STREAMS</span>
                <span className="v">{detail.workstreams.length}</span>
              </span>
              <span className={`m-cell ${detail.spawn.decompositionApproved ? 'score pass' : ''}`}>
                <span className="k">APPROVED</span>
                <span className="v">{detail.spawn.decompositionApproved ? 'yes' : 'no'}</span>
              </span>
            </div>
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

interface DocAgentInfo {
  agentId: string;
  name: string;
  model: string | null;
}

interface DocStats {
  iter: number;
  score: number | null;
  verdict: string;
  cost: number;
  /** Loop lifecycle — `live` while events flow, `iterating` between iters
   *  (sub-pass), `stalled` when max-iters hit or rejected, `awaiting-pr-review`
   *  after a passing verdict but before the plan PR merges, `completed`
   *  after the PR merges, `pending` before the first run. */
  status: 'live' | 'iterating' | 'stalled' | 'awaiting-pr-review' | 'completed' | 'pending';
  generator: DocAgentInfo | null;
  evaluator: DocAgentInfo | null;
}

/**
 * Polls the per-doc iteration endpoint for each known plan-doc key. The
 * 12s cadence is slower than the DocumentPane's own poll because the master
 * panel is "always visible" — we don't need sub-10s freshness for a badge.
 */
function useDocStats(storyId: string, keys: string[]): Record<string, DocStats | null> {
  const [stats, setStats] = useState<Record<string, DocStats | null>>({});
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const results = await Promise.all(keys.map((k) => forgeApi.getStoryArtifactIterations(storyId, k)));
        if (cancelled) return;
        const next: Record<string, DocStats | null> = {};
        results.forEach((r, i) => {
          const last = r.iterations[r.iterations.length - 1];
          next[keys[i]] = {
            iter: r.iterations.length,
            score: last?.score ?? null,
            verdict: last?.verdict ?? '',
            cost: r.totalCostUsd,
            status: r.status,
            generator: r.generator,
            evaluator: r.evaluator,
          };
        });
        setStats(next);
      } catch {
        // silent
      }
    }
    void load();
    // Tighter cadence than the original 12s — `live` should turn over within
    // an iteration boundary, and the iterations endpoint is cheap.
    const id = setInterval(() => { void load(); }, 6_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, keys.join('|')]);
  return stats;
}

function verdictTone(verdict: string): 'pass' | 'iterate' | 'reject' | 'neutral' {
  const v = (verdict || '').toLowerCase();
  if (v.startsWith('pass')) return 'pass';
  if (v.startsWith('reject') || v.startsWith('stalled')) return 'reject';
  if (!v) return 'neutral';
  return 'iterate';
}

interface DocAgentStripProps {
  generator: DocAgentInfo | null;
  evaluator: DocAgentInfo | null;
}

function DocAgentStrip({ generator, evaluator }: DocAgentStripProps) {
  return (
    <div className="doc-agents">
      <DocAgentAvatar info={generator} role="generator" />
      <span className="doc-agents-link">⇄</span>
      <DocAgentAvatar info={evaluator} role="evaluator" />
    </div>
  );
}

function DocAgentAvatar({ info, role }: { info: DocAgentInfo | null; role: 'generator' | 'evaluator' }) {
  if (!info) {
    return <span className={`doc-agent doc-agent--${role} doc-agent--empty`} title={`no ${role} yet`}>—</span>;
  }
  const url = avatarFor(info.agentId, info.name);
  // Compact tooltip: line 1 = agent id + display name, line 2 = role + model.
  const tip = `${info.agentId} · ${info.name}\n${role} · ${info.model ?? 'unknown model'}`;
  return (
    <span className={`doc-agent doc-agent--${role}`} title={tip}>
      {url ? (
        <img src={url} alt={info.name} onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }} />
      ) : (
        <span className="doc-agent-mono">{role === 'generator' ? 'G' : 'E'}</span>
      )}
    </span>
  );
}
