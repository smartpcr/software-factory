import { useMemo, useState } from 'react';
import {
  SCORECARDS,
  PROMOTION_DECISIONS,
  MONTHLY_SNAPSHOTS,
  RUBRIC_DIMS,
  LEVEL_TITLES,
  type AgentScorecard,
  type ReviewWindow,
  type PromotionVerdict,
  type PromotionDecision,
  type MonthlySnapshot,
} from '@/sampleScorecards';

// ────────────────────────────────────────────────────────────────────
// Phase 14 — Performance view (Q14.8 + Q14.9 spec). Mocked client-side.
// Three layers stack from rubric → decision → outcome:
//   1. Headline strip — payroll, spend, avg score, promotions ready
//   2. Promotion ladder (left) + Lead-floor card (rail)
//   3. Roster scorecard table + drill-in detail (radar + trend)
//   4. Promotion-decision cards (advisory; nightly job is authoritative)
//   5. Monthly review history (rail)
// ────────────────────────────────────────────────────────────────────

const WINDOW_LABELS: Record<ReviewWindow, string> = {
  trailing20: 'Iteration · 20',
  mtd:        'This month',
  lastMonth:  'Last month',
};

export function PerformanceView() {
  const [windowKey, setWindowKey] = useState<ReviewWindow>('trailing20');
  const [selected, setSelected] = useState<string>(SCORECARDS[0].agentId);

  const selectedAgent = useMemo(
    () => SCORECARDS.find((a) => a.agentId === selected) ?? SCORECARDS[0],
    [selected],
  );

  const summary = useMemo(() => deriveSummary(SCORECARDS, MONTHLY_SNAPSHOTS), []);

  return (
    <section className="view active" id="view-performance">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">14</span>&nbsp;Performance</span>
            <span className="sep">/</span>
            <span>{SCORECARDS.length} agents · 4 teams</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />nightly job @ 02:00 PT</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            The scoreboard, stacked
          </h1>
        </div>
        <div className="page-head-right">
          <WindowToggle value={windowKey} onChange={setWindowKey} />
          <button className="btn btn-secondary">Export PDF</button>
        </div>
      </div>

      {/* ── Headline strip ─────────────────────────────────────────── */}
      <div className="perf-strip">
        <div className="perf-strip-cell accent">
          <span className="corner">USD / day</span>
          <span className="label">Payroll today</span>
          <span className="val">${summary.payrollTodayUsd.toFixed(0)}</span>
          <span className="sub">12 h × hourly · {summary.activeAgents} on shift</span>
        </div>
        <div className="perf-strip-cell">
          <span className="corner">USD / day</span>
          <span className="label">Spend today</span>
          <span className="val">${summary.spendTodayUsd.toFixed(2)}</span>
          <span className="sub">tokens billed via registry · est. on Copilot</span>
        </div>
        <div className="perf-strip-cell">
          <span className="corner">avg / 20</span>
          <span className="label">Roster average</span>
          <span className="val">{summary.rosterAvg.toFixed(1)}</span>
          <span className="sub">window · {WINDOW_LABELS[windowKey]}</span>
        </div>
        <div className="perf-strip-cell">
          <span className="corner">advisory</span>
          <span className="label">Ready for promo</span>
          <span className="val">{summary.readyToPromote} <span style={{ fontSize: 16, color: 'var(--text-tertiary)' }}>/ {summary.atRisk} risk</span></span>
          <span className="sub">{summary.leadVacancies} lead vacancy{summary.leadVacancies === 1 ? '' : 'ies'} · 9 days to fill</span>
        </div>
      </div>

      {/* ── Two-column layout ──────────────────────────────────────── */}
      <div className="perf-wrap">
        <div className="perf-main">
          <PromotionLadder agents={SCORECARDS} selectedId={selected} onSelect={setSelected} />

          <div className="scorecard-card">
            <div className="head">
              <div className="title">Roster scorecard — Q14.9 KPIs</div>
              <div className="sub">click a row to inspect · {WINDOW_LABELS[windowKey]}</div>
            </div>
            <ScorecardTable agents={SCORECARDS} selectedId={selected} onSelect={setSelected} />
            <ScorecardDrawer agent={selectedAgent} />
          </div>

          <div className="perf-section-head">
            <div className="title">Promotion decisions — advisory</div>
            <div className="sub">PromotionJob · 02:00 PT · cooldown 30 d</div>
          </div>
          <div className="decisions">
            {PROMOTION_DECISIONS.map((d) => (
              <DecisionCard key={d.agentId} d={d} />
            ))}
          </div>
        </div>

        {/* ── Right rail ──────────────────────────────────────────── */}
        <aside className="perf-rail">
          <div className="lead-floor-card">
            <div className="num">L7</div>
            <div className="copy">
              <strong>Lead floor</strong> — promoting a member to crew lead requires
              level&nbsp;≥&nbsp;7 (Q14.5). Below floor returns&nbsp;
              <code>400&nbsp;crew.lead.level_too_low</code>.
            </div>
          </div>

          <div className="rail-card">
            <div className="head">
              <div className="title">Monthly snapshots</div>
              <div className="eyebrow">last 6 mo</div>
            </div>
            <div className="month-list">
              {MONTHLY_SNAPSHOTS.slice().reverse().map((m, i, arr) => {
                const prev = arr[i + 1];
                const delta = prev ? m.avgScore - prev.avgScore : 0;
                return <MonthRow key={m.month} m={m} delta={delta} />;
              })}
            </div>
          </div>

          <div className="rail-card">
            <div className="head">
              <div className="title">KPI catalogue</div>
              <div className="eyebrow">Q14.9 locked</div>
            </div>
            <div className="kpi-legend">
              {KPI_LEGEND.map((k) => (
                <div className="kpi-row" key={k.key}>
                  <span>{k.label}</span><span className="key">{k.key}</span>
                </div>
              ))}
            </div>
          </div>

          <div className="rail-card">
            <div className="head">
              <div className="title">Iteration cadence</div>
              <div className="eyebrow">spec §3</div>
            </div>
            <div style={{ fontSize: 11.5, lineHeight: 1.55, color: 'var(--text-secondary)' }}>
              Trailing-20 final-iteration scores feed promotion. Score
              boundaries are <strong style={{ color: 'var(--success)' }}>≥ 90 pass</strong>,
              <strong> 70–89 iterate</strong>, <strong style={{ color: 'var(--danger)' }}>&lt; 70 reject</strong>.
              Hard cap at 5 iterations · stalled rolls back to lead.
            </div>
          </div>
        </aside>
      </div>
    </section>
  );
}

// ════════════════════════════════════════════════════════════════════
// Window toggle
// ════════════════════════════════════════════════════════════════════

function WindowToggle({ value, onChange }: { value: ReviewWindow; onChange: (w: ReviewWindow) => void }) {
  return (
    <div className="perf-window" role="tablist" aria-label="Review window">
      {(['trailing20', 'mtd', 'lastMonth'] as ReviewWindow[]).map((k) => (
        <button
          key={k}
          data-active={value === k}
          onClick={() => onChange(k)}
          role="tab"
          aria-selected={value === k}
        >
          {WINDOW_LABELS[k]}
        </button>
      ))}
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// Promotion ladder — vertical 0..10 with agents as pins
// ════════════════════════════════════════════════════════════════════

function PromotionLadder({ agents, selectedId, onSelect }: {
  agents: AgentScorecard[];
  selectedId: string;
  onSelect: (id: string) => void;
}) {
  // Bucket agents by level for evenly-spread horizontal placement.
  const byLevel = useMemo(() => {
    const m = new Map<number, AgentScorecard[]>();
    for (let lvl = 0; lvl <= 10; lvl++) m.set(lvl, []);
    for (const a of agents) m.get(a.level)!.push(a);
    return m;
  }, [agents]);

  return (
    <div className="perf-ladder">
      <div className="head">
        <div>
          <div className="perf-ladder-title">Promotion ladder</div>
          <div style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-secondary)', marginTop: 2 }}>
            level 0–10 · trainee → fellow · pinned by current level, arrows show pending move
          </div>
        </div>
        <div className="perf-ladder-eyebrow">spec §4 · scoring-and-promotion.md</div>
      </div>

      <div className="perf-ladder-canvas">
        <div className="perf-ladder-axis">
          {[...Array(11)].map((_, i) => (
            <div className={`tick${i === 7 ? ' lead-floor' : ''}`} key={i}>
              {`L${i} · ${LEVEL_TITLES[i]}`}
            </div>
          ))}
        </div>
        <div className="perf-ladder-rungs">
          {[...Array(11)].map((_, i) => (
            <div className={`perf-ladder-rung${i >= 7 ? ' lead-zone' : ''}`} key={i}>
              {byLevel.get(i)!.map((a, idx) => {
                const offsetPct = ((idx + 1) / (byLevel.get(i)!.length + 1)) * 100;
                const arrowEl =
                  a.verdict === 'promote' ? <span className="arrow">▲</span> :
                  a.verdict === 'demote'  ? <span className="arrow down">▼</span> :
                  null;
                return (
                  <button
                    key={a.agentId}
                    className="perf-ladder-pin"
                    data-team={a.team}
                    data-verdict={a.verdict}
                    style={{ left: `${offsetPct}%`, transform: 'translate(-50%, -50%)' }}
                    onClick={() => onSelect(a.agentId)}
                    title={`${a.name} · ${a.team} · avg ${a.avgScore.toFixed(1)}`}
                    aria-pressed={selectedId === a.agentId}
                  >
                    <span className="id">{a.agentId}</span>
                    <span style={{ color: selectedId === a.agentId ? 'var(--accent)' : 'inherit' }}>{a.name}</span>
                    {arrowEl}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      </div>

      <div className="perf-ladder-legend">
        <span><span className="dot" style={{ background: 'var(--epic-024)' }} /> Engineer</span>
        <span><span className="dot" style={{ background: 'var(--epic-021)' }} /> Review</span>
        <span><span className="dot" style={{ background: 'var(--epic-026)' }} /> Architect</span>
        <span><span className="dot" style={{ background: 'var(--epic-022)' }} /> QA</span>
        <span style={{ marginLeft: 'auto', color: 'var(--text-tertiary)' }}>
          ▲ promote · ▼ demote
        </span>
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// Scorecard table
// ════════════════════════════════════════════════════════════════════

function ScorecardTable({ agents, selectedId, onSelect }: {
  agents: AgentScorecard[];
  selectedId: string;
  onSelect: (id: string) => void;
}) {
  return (
    <table className="scorecard-table" aria-label="Roster scorecard">
      <thead>
        <tr>
          <th className="left">Agent</th>
          <th className="left">Level</th>
          <th>Avg</th>
          <th>Pass</th>
          <th>Throughput</th>
          <th>Iter P50/P90</th>
          <th>Reject</th>
          <th>Cost / pass</th>
          <th>Trail-20</th>
          <th>Promo ETA</th>
          <th className="left">Verdict</th>
        </tr>
      </thead>
      <tbody>
        {agents.map((a) => {
          const sel = a.agentId === selectedId;
          return (
            <tr
              key={a.agentId}
              data-selected={sel}
              onClick={() => onSelect(a.agentId)}
            >
              <td className="left">
                <div className="agent-cell">
                  <span className="id">{a.agentId}</span>
                  <div>
                    <div className="name">{a.name}</div>
                    <div className="meta">{a.team.toUpperCase()} · {a.crew}</div>
                  </div>
                </div>
              </td>
              <td className="left">
                <span className="level-pill">
                  <span className="num">{a.level}</span>
                  {a.title}
                </span>
              </td>
              <td>
                <span style={{ color: a.avgScore >= 92 ? 'var(--success)' : a.avgScore < 78 ? 'var(--danger)' : 'inherit', fontWeight: 600 }}>
                  {a.avgScore.toFixed(1)}
                </span>
              </td>
              <td>{(a.passRate * 100).toFixed(0)}%</td>
              <td>{a.throughput}</td>
              <td>{a.iterP50}/{a.iterP90}</td>
              <td>
                <span style={{ color: a.rejectRate > 0.10 ? 'var(--danger)' : 'inherit' }}>
                  {(a.rejectRate * 100).toFixed(0)}%
                </span>
              </td>
              <td>${a.costPerPass.toFixed(2)}</td>
              <td className="spark">
                <Spark values={a.scoreSpark} />
              </td>
              <td>
                {a.promotionEta === null ? '—'
                  : a.promotionEta === 0 ? <span className="delta-up">ready</span>
                  : `+${a.promotionEta}`}
              </td>
              <td className="left">
                <span className={`verdict ${a.verdict}`}>{verdictLabel(a.verdict)}</span>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

// ════════════════════════════════════════════════════════════════════
// Scorecard drawer (selected agent) — radar + trend
// ════════════════════════════════════════════════════════════════════

function ScorecardDrawer({ agent }: { agent: AgentScorecard }) {
  return (
    <div className="scorecard-detail">
      <div className="name-block">
        <div>
          <div className="role">{agent.team} · L{agent.level} · ${agent.hourlyUsd}/hr</div>
          <div className="name">{agent.name}</div>
          <div className="crew">{agent.crew}</div>
        </div>
        <div className="verdict-note">{agent.verdictNote}</div>
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)', letterSpacing: 0.04, marginTop: 6 }}>
          last promo · {agent.lastPromotedAt ?? '—'}<br />
          next eligible · {agent.nextEligibleAt}
        </div>
      </div>

      <div className="rubric-card">
        <RadarChart dims={agent.dims} />
        <div className="dim-list">
          {RUBRIC_DIMS.map((d) => {
            const v = agent.dims[d.key];
            const pct = (v / d.max) * 100;
            return (
              <div className="dim-row" key={d.key}>
                <span className="label">{d.label}</span>
                <span className="dim-bar"><span className="fill" style={{ width: `${pct}%` }} /></span>
                <span className="num">{v}/{d.max}</span>
              </div>
            );
          })}
        </div>
      </div>

      <div className="detail-trend">
        <div className="trend-label">trailing-20 score · oldest → today</div>
        <BigSpark values={agent.scoreSpark} />
        <div className="legend">
          <span>min {Math.min(...agent.scoreSpark)}</span>
          <span>avg {(agent.scoreSpark.reduce((s, v) => s + v, 0) / agent.scoreSpark.length).toFixed(1)}</span>
          <span>max {Math.max(...agent.scoreSpark)}</span>
        </div>
        <div className="trend-label" style={{ marginTop: 6 }}>monthly · 6 mo</div>
        <MonthlyBars values={agent.monthlyAvg} labels={agent.monthlyMonths} />
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// Promotion decision card
// ════════════════════════════════════════════════════════════════════

function DecisionCard({ d }: { d: PromotionDecision }) {
  const direction = d.toLevel > d.fromLevel ? 'up'
                  : d.toLevel < d.fromLevel ? 'down' : 'flat';
  const kind: PromotionVerdict =
    direction === 'up' && d.windowAvg >= 92 && d.rejectsInWindow === 0 && d.cooldownDaysRemaining === 0 ? 'promote'
    : direction === 'down' ? 'demote'
    : direction === 'up' && d.windowAvg < 92 ? 'hold'
    : d.toLevel >= 7 && d.windowAvg < 75 ? 'lead-pending'
    : 'watch';

  return (
    <article className="decision" data-kind={kind}>
      <div className="top">
        <div className="move">
          <div className="who">{d.name}</div>
          <div className="id">{d.agentId} · {d.team}</div>
          <div className="arrow-row">
            <span className="step">L{d.fromLevel} {d.fromTitle}</span>
            <span>{direction === 'up' ? '→' : direction === 'down' ? '↓' : '·'}</span>
            <span className="step target">L{d.toLevel} {d.toTitle}</span>
          </div>
        </div>
        <div className="verdict">{verdictLabel(kind)}</div>
      </div>

      <div className="math">
        <div className="cell">
          <div className="label">window avg</div>
          <div className={`val ${d.windowAvg >= 92 ? 'good' : d.windowAvg < 78 ? 'bad' : ''}`}>
            {d.windowAvg.toFixed(1)}
          </div>
        </div>
        <div className="cell">
          <div className="label">rejects / 20</div>
          <div className={`val ${d.rejectsInWindow > 0 && direction === 'up' ? 'bad' : d.rejectsInWindow === 0 ? 'good' : ''}`}>
            {d.rejectsInWindow}
          </div>
        </div>
        <div className="cell">
          <div className="label">cooldown</div>
          <div className={`val ${d.cooldownDaysRemaining > 0 ? 'bad' : 'good'}`}>
            {d.cooldownDaysRemaining > 0 ? `${d.cooldownDaysRemaining} d` : 'clear'}
          </div>
        </div>
      </div>

      {d.rationale.length > 0 && (
        <div className="reasons">
          <div className="head">{direction === 'down' ? 'why this fires' : 'rationale'}</div>
          <ul>{d.rationale.map((r, i) => <li key={i}>{r}</li>)}</ul>
        </div>
      )}
      {d.risks.length > 0 && (
        <div className="risks">
          <div className="head">{direction === 'down' ? 'downstream effects' : 'risks before sign-off'}</div>
          <ul>{d.risks.map((r, i) => <li key={i}>{r}</li>)}</ul>
        </div>
      )}

      <div className="actions">
        {kind === 'promote' && <button className="primary">Approve & ship</button>}
        {kind === 'demote' && <button className="primary" style={{ background: 'var(--danger)', borderColor: 'var(--danger)' }}>Confirm demotion</button>}
        {kind === 'hold' && <button>Hold for next window</button>}
        {kind === 'watch' && <button>Add to watchlist</button>}
        {kind === 'lead-pending' && <button>Open lead nomination</button>}
        <button>Open file</button>
        {d.signedOff && d.signedOffBy && (
          <span className="signed">✓ signed by {d.signedOffBy}</span>
        )}
      </div>
    </article>
  );
}

// ════════════════════════════════════════════════════════════════════
// Monthly snapshots row (rail)
// ════════════════════════════════════════════════════════════════════

function MonthRow({ m, delta }: { m: MonthlySnapshot; delta: number }) {
  const fillPct = Math.min(100, (m.avgScore / 100) * 100);
  return (
    <div className="month-row">
      <div className="label">
        <span>{m.month}</span>
        <span className={`lock ${m.locked ? '' : 'live'}`}>
          {m.locked ? 'locked' : 'live · 30 d edit'}
        </span>
      </div>
      <div className="bar">
        <div className="fill" style={{ width: `${fillPct}%` }} />
        <span className="label-num">{m.avgScore.toFixed(1)}</span>
      </div>
      <div className={`change ${delta > 0 ? 'up' : delta < 0 ? 'down' : ''}`}>
        {delta === 0 ? '·' : `${delta > 0 ? '+' : ''}${delta.toFixed(1)}`}
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// SVG primitives
// ════════════════════════════════════════════════════════════════════

function Spark({ values }: { values: number[] }) {
  if (values.length === 0) return null;
  const min = Math.min(...values), max = Math.max(...values);
  const range = Math.max(1, max - min);
  const w = 80, h = 22, pad = 1;
  const stepX = w / (values.length - 1);
  const points = values.map((v, i) => {
    const x = i * stepX;
    const y = h - pad - ((v - min) / range) * (h - pad * 2);
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  }).join(' ');
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width={w} height={h} aria-hidden>
      <polyline fill="none" stroke="var(--accent)" strokeWidth="1.4" strokeLinejoin="round" strokeLinecap="round" points={points} />
      <circle cx={(values.length - 1) * stepX} cy={h - pad - ((values[values.length - 1] - min) / range) * (h - pad * 2)} r="1.6" fill="var(--accent)" />
    </svg>
  );
}

function BigSpark({ values }: { values: number[] }) {
  if (values.length === 0) return null;
  const w = 220, h = 60, pad = 4;
  const min = Math.min(...values), max = Math.max(...values);
  const range = Math.max(1, max - min);
  const stepX = (w - pad * 2) / (values.length - 1);
  const path = values.map((v, i) => {
    const x = pad + i * stepX;
    const y = h - pad - ((v - min) / range) * (h - pad * 2);
    return `${i === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`;
  }).join(' ');
  // Threshold line @ 92
  const thresholdY = h - pad - ((92 - min) / range) * (h - pad * 2);
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} aria-hidden>
      {/* threshold line if visible */}
      {min <= 92 && max >= 92 && (
        <line x1={pad} y1={thresholdY} x2={w - pad} y2={thresholdY}
              stroke="var(--success)" strokeDasharray="2 3" strokeWidth="0.8" opacity="0.6" />
      )}
      <path d={path} fill="none" stroke="var(--accent)" strokeWidth="1.6" strokeLinejoin="round" strokeLinecap="round" />
      {/* dots at every point */}
      {values.map((v, i) => {
        const x = pad + i * stepX;
        const y = h - pad - ((v - min) / range) * (h - pad * 2);
        return <circle key={i} cx={x} cy={y} r={i === values.length - 1 ? 2.4 : 1.2} fill="var(--accent)" />;
      })}
    </svg>
  );
}

function MonthlyBars({ values, labels }: { values: number[]; labels: string[] }) {
  const max = Math.max(...values);
  const min = Math.min(...values, 70);
  const range = Math.max(1, max - min);
  return (
    <div style={{ display: 'grid', gridTemplateColumns: `repeat(${values.length}, 1fr)`, gap: 6, alignItems: 'end' }}>
      {values.map((v, i) => {
        const h = 8 + ((v - min) / range) * 38;
        return (
          <div key={i} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4 }}>
            <div style={{
              width: '100%',
              height: h,
              background: i === values.length - 1 ? 'var(--accent)' : 'var(--bg-sunken)',
              border: '1px solid var(--border)',
              borderRadius: 2,
              position: 'relative',
            }}>
              {i === values.length - 1 && (
                <span style={{
                  position: 'absolute',
                  top: -14,
                  left: 0, right: 0,
                  textAlign: 'center',
                  fontFamily: 'var(--font-mono)',
                  fontSize: 9,
                  color: 'var(--accent)',
                  fontWeight: 600,
                }}>{v.toFixed(0)}</span>
              )}
            </div>
            <div style={{
              fontFamily: 'var(--font-mono)',
              fontSize: 8.5,
              color: 'var(--text-tertiary)',
              letterSpacing: 0.04,
            }}>{labels[i]}</div>
          </div>
        );
      })}
    </div>
  );
}

function RadarChart({ dims }: { dims: AgentScorecard['dims'] }) {
  const size = 132, cx = size / 2, cy = size / 2, r = size / 2 - 12;
  // 5 axes — pentagon. Each rubric dim has its own max so we plot pct.
  const axes = RUBRIC_DIMS.map((d, i) => {
    const angle = (-Math.PI / 2) + (i / RUBRIC_DIMS.length) * 2 * Math.PI;
    return { angle, label: d.label, max: d.max, value: dims[d.key] };
  });
  const polyPts = axes.map((a) => {
    const pct = a.value / a.max;
    const x = cx + Math.cos(a.angle) * r * pct;
    const y = cy + Math.sin(a.angle) * r * pct;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden>
      {/* concentric rings */}
      {[0.25, 0.5, 0.75, 1].map((t) => (
        <polygon
          key={t}
          fill="none"
          stroke="var(--border)"
          strokeDasharray={t === 1 ? '' : '2 3'}
          strokeWidth="0.8"
          points={axes.map((a) => {
            const x = cx + Math.cos(a.angle) * r * t;
            const y = cy + Math.sin(a.angle) * r * t;
            return `${x.toFixed(1)},${y.toFixed(1)}`;
          }).join(' ')}
        />
      ))}
      {/* spokes */}
      {axes.map((a, i) => (
        <line key={i}
          x1={cx} y1={cy}
          x2={cx + Math.cos(a.angle) * r}
          y2={cy + Math.sin(a.angle) * r}
          stroke="var(--border)" strokeWidth="0.6"
        />
      ))}
      {/* value polygon */}
      <polygon
        points={polyPts}
        fill="var(--accent)"
        fillOpacity="0.18"
        stroke="var(--accent)"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      {axes.map((a) => {
        const pct = a.value / a.max;
        const x = cx + Math.cos(a.angle) * r * pct;
        const y = cy + Math.sin(a.angle) * r * pct;
        return <circle key={a.label} cx={x} cy={y} r="2" fill="var(--accent)" />;
      })}
    </svg>
  );
}

// ════════════════════════════════════════════════════════════════════
// Helpers
// ════════════════════════════════════════════════════════════════════

const KPI_LEGEND: Array<{ label: string; key: string }> = [
  { label: 'Throughput',     key: 'count' },
  { label: 'Pass-rate',      key: '% iter≤1' },
  { label: 'Avg score',      key: '0–100' },
  { label: 'Iter P50/P90',   key: 'rounds' },
  { label: 'Reject rate',    key: '% < 70' },
  { label: 'Dispute rate',   key: '% filed' },
  { label: 'Stalled rate',   key: '% capped' },
  { label: 'Utilization',    key: 'on-shift' },
  { label: 'Cost / pass',    key: 'USD' },
];

function verdictLabel(v: PromotionVerdict): string {
  switch (v) {
    case 'promote': return 'promote';
    case 'demote':  return 'demote';
    case 'hold':    return 'hold';
    case 'watch':   return 'watch';
    case 'lead-pending': return 'lead pending';
  }
}

function deriveSummary(cards: AgentScorecard[], months: MonthlySnapshot[]) {
  const payrollTodayUsd = cards.reduce((s, c) => s + c.hourlyUsd * 12, 0);
  const spendTodayUsd = cards.reduce((s, c) => s + c.costPerPass * (c.throughput / 20), 0);
  const rosterAvg = cards.reduce((s, c) => s + c.avgScore, 0) / cards.length;
  const readyToPromote = cards.filter((c) => c.verdict === 'promote').length;
  const atRisk = cards.filter((c) => c.demotionRisk).length;
  const leadVacancies = cards.filter((c) => c.verdict === 'lead-pending').length;
  const activeAgents = cards.length;
  void months;
  return { payrollTodayUsd, spendTodayUsd, rosterAvg, readyToPromote, atRisk, leadVacancies, activeAgents };
}
