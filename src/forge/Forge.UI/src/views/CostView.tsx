import { useMemo } from 'react';
import { useStateStore } from '@/state/useStateStore';
import { sampleState } from '@/sampleData';
import type { Card, Epic } from '@/types';

// ── Mocked daily cost series (USD) ──────────────────────────────
// One row per day for the last 7 days, broken down by epic.
// Index 0 = 6 days ago; index 6 = today.
const DAILY_COST: Array<Record<string, number>> = [
  { 'EPIC-024': 1.10, 'EPIC-021': 0.62, 'EPIC-022': 0.31, 'EPIC-026': 0.18, 'EPIC-018': 0.42 },
  { 'EPIC-024': 1.42, 'EPIC-021': 0.71, 'EPIC-022': 0.28, 'EPIC-026': 0.14, 'EPIC-018': 0.31 },
  { 'EPIC-024': 1.88, 'EPIC-021': 0.84, 'EPIC-022': 0.36, 'EPIC-026': 0.21, 'EPIC-018': 0.18 },
  { 'EPIC-024': 1.65, 'EPIC-021': 0.92, 'EPIC-022': 0.42, 'EPIC-026': 0.26, 'EPIC-018': 0.12 },
  { 'EPIC-024': 2.04, 'EPIC-021': 1.18, 'EPIC-022': 0.51, 'EPIC-026': 0.32, 'EPIC-018': 0.09 },
  { 'EPIC-024': 1.78, 'EPIC-021': 1.06, 'EPIC-022': 0.44, 'EPIC-026': 0.36, 'EPIC-018': 0.05 },
  { 'EPIC-024': 2.18, 'EPIC-021': 1.84, 'EPIC-022': 0.41, 'EPIC-026': 0.41, 'EPIC-018': 0.02 }, // today
];
const DAY_LABELS = ['Wed', 'Thu', 'Fri', 'Sat', 'Sun', 'Mon', 'Tue'];
const TODAY_INDEX = DAILY_COST.length - 1;
const DAILY_CAP = 7.5;

// Rolling 7-day cost per agent + a tiny weekly sparkline.
interface AgentBurn {
  id: string;
  name: string;
  role: string;
  cost24h: number;
  cost7d: number;
  tokens: number;
  spark: number[];
}
const AGENT_BURN: AgentBurn[] = [
  { id: 'NZ', name: 'Ne Zha',          role: 'eng',  cost24h: 0.42, cost7d: 2.18, tokens: 184_000, spark: [0.18, 0.22, 0.31, 0.28, 0.34, 0.43, 0.42] },
  { id: 'AB', name: 'Ao Bing',         role: 'eng',  cost24h: 0.38, cost7d: 1.92, tokens: 162_000, spark: [0.21, 0.18, 0.24, 0.32, 0.30, 0.29, 0.38] },
  { id: 'ZR', name: 'Zhu Rong',        role: 'eng',  cost24h: 0.31, cost7d: 1.74, tokens: 148_000, spark: [0.18, 0.22, 0.26, 0.30, 0.28, 0.19, 0.31] },
  { id: 'SG', name: 'Shen Gongbao',    role: 'rev',  cost24h: 0.21, cost7d: 1.38, tokens: 102_000, spark: [0.22, 0.19, 0.18, 0.20, 0.18, 0.20, 0.21] },
  { id: 'TY', name: 'Taiji Zhenren',   role: 'arch', cost24h: 0.42, cost7d: 1.32, tokens:  88_000, spark: [0.10, 0.14, 0.18, 0.22, 0.18, 0.08, 0.42] },
  { id: 'GG', name: 'Gong Gong',       role: 'eng',  cost24h: 0.14, cost7d: 1.08, tokens:  92_000, spark: [0.18, 0.20, 0.16, 0.14, 0.12, 0.14, 0.14] },
  { id: 'WZ', name: 'Wen Zhong',       role: 'arch', cost24h: 0.27, cost7d: 0.94, tokens:  74_000, spark: [0.08, 0.12, 0.14, 0.18, 0.16, 0.10, 0.27] },
  { id: 'SF', name: "Shun Feng'er",    role: 'qa',   cost24h: 0.09, cost7d: 0.72, tokens:  68_000, spark: [0.10, 0.12, 0.10, 0.11, 0.10, 0.10, 0.09] },
  { id: 'YX', name: 'Yun Xiao',        role: 'rev',  cost24h: 0.12, cost7d: 0.68, tokens:  54_000, spark: [0.06, 0.08, 0.10, 0.12, 0.14, 0.12, 0.12] },
  { id: 'MB', name: 'Mu Bao',          role: 'eng',  cost24h: 0.18, cost7d: 0.61, tokens:  48_000, spark: [0.04, 0.06, 0.08, 0.10, 0.12, 0.18, 0.18] },
  { id: 'ZZ', name: 'Zhi Zhi',         role: 'qa',   cost24h: 0.06, cost7d: 0.42, tokens:  41_000, spark: [0.06, 0.06, 0.06, 0.06, 0.06, 0.06, 0.06] },
];

function epicVar(color: string): string {
  return `var(--${color.replace('epic', 'epic-')})`;
}

function fmt(v: number): string {
  return v >= 10 ? `$${v.toFixed(0)}` : `$${v.toFixed(2)}`;
}

export function CostView() {
  const apiState = useStateStore((s) => s.state);
  const epics: Epic[] = apiState?.epics ?? sampleState.epics;
  const cards: Card[] = apiState?.cards ?? sampleState.cards;

  const today = DAILY_COST[TODAY_INDEX];
  const todayTotal = useMemo(() => Object.values(today).reduce((s, v) => s + v, 0), [today]);
  const week7d = useMemo(
    () => DAILY_COST.reduce((s, day) => s + Object.values(day).reduce((a, b) => a + b, 0), 0),
    [],
  );
  const burnRate = week7d / DAILY_COST.length;
  const forecast7d = burnRate * 7;

  const dayMax = useMemo(() => Math.max(
    ...DAILY_COST.map((d) => Object.values(d).reduce((a, b) => a + b, 0)),
    DAILY_CAP,
  ), []);

  const epicLegend = useMemo(() => {
    return epics.map((e) => {
      const total = DAILY_COST.reduce((s, day) => s + (day[e.id] ?? 0), 0);
      return { id: e.id, name: e.name, color: epicVar(e.color), total };
    }).sort((a, b) => b.total - a.total);
  }, [epics]);

  const ranked = useMemo(() => {
    return cards
      .filter((c) => c.cost > 0)
      .sort((a, b) => b.cost - a.cost)
      .slice(0, 8);
  }, [cards]);

  const totalAgentBurn = AGENT_BURN.reduce((s, a) => s + a.cost7d, 0);
  const budgetPct = Math.min(100, Math.round((todayTotal / DAILY_CAP) * 100));

  return (
    <section className="view active" id="view-cost">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">09</span>&nbsp;Cost</span>
            <span className="sep">/</span>
            <span>${todayTotal.toFixed(2)} today · ${week7d.toFixed(2)} week</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{budgetPct}% of daily cap</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Where the tokens went
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary">Export CSV</button>
          <button className="btn btn-primary">Adjust caps</button>
        </div>
      </div>

      <div className="workstreams-stats" style={{ margin: '0 28px 16px' }}>
        <div className="stat-cell">
          <div className="stat-cell-label">Today</div>
          <div className="stat-cell-val">${todayTotal.toFixed(2)}<span className="unit">/ ${DAILY_CAP.toFixed(2)} cap</span></div>
          <div className="stat-cell-trend">{budgetPct}% used · {Math.round(((1 - budgetPct / 100) * 24))}h headroom</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Past 7 days</div>
          <div className="stat-cell-val">${week7d.toFixed(2)}</div>
          <div className="stat-cell-trend"><span className="up">+18%</span> vs prior 7</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Burn rate</div>
          <div className="stat-cell-val">${burnRate.toFixed(2)}<span className="unit">/ day avg</span></div>
          <div className="stat-cell-trend">trending up</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">7d forecast</div>
          <div className="stat-cell-val">${forecast7d.toFixed(0)}</div>
          <div className="stat-cell-trend">at current burn</div>
        </div>
      </div>

      <div className="cost-wrap">
        <div className="cost-main">
          <div className="cost-card">
            <div className="cost-card-head">
              <span className="cost-card-title">Spend by epic — last 7 days</span>
              <span className="cost-card-eyebrow">stacked · ${week7d.toFixed(2)} total</span>
            </div>
            <div className="cost-chart">
              <div className="cost-chart-yaxis">
                <span>${dayMax.toFixed(0)}</span>
                <span>${(dayMax * 0.66).toFixed(0)}</span>
                <span>${(dayMax * 0.33).toFixed(0)}</span>
                <span>$0</span>
              </div>
              <div className="cost-chart-bars">
                <div className="cost-chart-grid">
                  <div /><div /><div /><div />
                </div>
                {DAILY_COST.map((day, i) => {
                  const dayTotal = Object.values(day).reduce((a, b) => a + b, 0);
                  const heightPct = (dayTotal / dayMax) * 100;
                  return (
                    <div className="cost-chart-col" key={i} data-today={i === TODAY_INDEX || undefined}>
                      <div className="cost-chart-stack" style={{ height: `${heightPct}%` }}>
                        {epics.map((e) => {
                          const v = day[e.id] ?? 0;
                          if (v === 0) return null;
                          const segPct = (v / dayTotal) * 100;
                          return (
                            <div
                              key={e.id}
                              className="seg"
                              style={{ height: `${segPct}%`, background: epicVar(e.color) }}
                              title={`${e.id} · ${fmt(v)}`}
                            />
                          );
                        })}
                      </div>
                      <span className="cost-total">{fmt(dayTotal)}</span>
                      <span className="label">{DAY_LABELS[i]}</span>
                    </div>
                  );
                })}
              </div>
            </div>
            <div className="cost-legend">
              {epicLegend.map((e) => (
                <span className="cost-legend-item" key={e.id} title={e.name}>
                  <span className="swatch" style={{ background: e.color }} />
                  {e.id}
                  <span className="num">${e.total.toFixed(2)}</span>
                </span>
              ))}
            </div>
          </div>

          <div className="cost-card">
            <div className="cost-card-head">
              <span className="cost-card-title">Per-agent burn</span>
              <span className="cost-card-eyebrow">7d · ${totalAgentBurn.toFixed(2)} across {AGENT_BURN.length} agents</span>
            </div>
            <table className="burn-table">
              <thead>
                <tr>
                  <th>Agent</th>
                  <th>Role</th>
                  <th className="num">24h</th>
                  <th className="num">7d</th>
                  <th className="num">Tokens</th>
                  <th>Trend</th>
                  <th>Share</th>
                </tr>
              </thead>
              <tbody>
                {AGENT_BURN.map((a) => {
                  const share = (a.cost7d / totalAgentBurn) * 100;
                  return (
                    <tr key={a.id}>
                      <td className="name">
                        <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)' }}>
                          {a.id}
                        </span>
                        {a.name}
                      </td>
                      <td><span className="role-tag">{a.role}</span></td>
                      <td className="num cost">${a.cost24h.toFixed(2)}</td>
                      <td className="num">${a.cost7d.toFixed(2)}</td>
                      <td className="num" style={{ color: 'var(--text-tertiary)' }}>{(a.tokens / 1000).toFixed(0)}k</td>
                      <td className="spark"><Sparkline values={a.spark} /></td>
                      <td className="share">
                        <span className="bar"><span className="bar-fill" style={{ width: `${share}%` }} /></span>
                        {share.toFixed(0)}%
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>

        <aside className="cost-rail">
          <div className="cost-card">
            <div className="cost-card-head">
              <span className="cost-card-title">Today vs cap</span>
              <span className="cost-card-eyebrow">live</span>
            </div>
            <div className="budget-gauge">
              <BudgetArc pct={budgetPct} />
              <div className="val">{budgetPct}%</div>
              <div className="sub">${todayTotal.toFixed(2)} of ${DAILY_CAP.toFixed(2)}</div>
            </div>
          </div>

          <div className="cost-card">
            <div className="cost-card-head">
              <span className="cost-card-title">Alerts</span>
              <span className="cost-card-eyebrow">24h</span>
            </div>
            <div className="budget-alerts">
              <div className="budget-alert warn">
                <span className="dot" />
                <span className="body">
                  <strong>EPIC-024</strong> consuming 49% of weekly burn. Watch the OIDC + IdP failover branches.
                </span>
              </div>
              <div className="budget-alert info">
                <span className="dot" />
                <span className="body">
                  <strong>EPIC-018</strong> tapering to zero — BITS epic is winding down as expected.
                </span>
              </div>
              <div className="budget-alert danger">
                <span className="dot" />
                <span className="body">
                  At current rate, daily cap will be hit at <strong>~16:40</strong>. Consider pausing low-priority agents.
                </span>
              </div>
            </div>
          </div>

          <div className="cost-card">
            <div className="cost-card-head">
              <span className="cost-card-title">Most expensive cards</span>
              <span className="cost-card-eyebrow">all time</span>
            </div>
            <div className="cost-rank-list">
              {ranked.map((c, i) => (
                <div className="cost-rank-row" key={c.id}>
                  <span className="pos">{i + 1}</span>
                  <span className="body">
                    <span className="id">{c.id}</span>
                    <span className="title" title={c.title}>{c.title}</span>
                  </span>
                  <span className="cost">${c.cost.toFixed(2)}</span>
                </div>
              ))}
            </div>
          </div>
        </aside>
      </div>
    </section>
  );
}

// 270-degree arc gauge for the today/cap dial. Drawn as a single track
// arc; the fill is the same arc clipped via stroke-dasharray.
function BudgetArc({ pct }: { pct: number }) {
  const r = 56;
  const cx = 70, cy = 70;
  // Sweep from 135° (lower-left) clockwise 270° to 45° (lower-right).
  const startRad = (135 * Math.PI) / 180;
  const endRad = (135 + 270) * Math.PI / 180;
  const start = { x: cx + r * Math.cos(startRad), y: cy + r * Math.sin(startRad) };
  const end = { x: cx + r * Math.cos(endRad), y: cy + r * Math.sin(endRad) };
  // Track path: large-arc=1 because sweep > 180°, sweep-flag=1 (clockwise).
  const trackPath = `M ${start.x.toFixed(2)} ${start.y.toFixed(2)} A ${r} ${r} 0 1 1 ${end.x.toFixed(2)} ${end.y.toFixed(2)}`;
  const arcLen = (270 / 360) * 2 * Math.PI * r;
  const filled = arcLen * (Math.min(100, Math.max(0, pct)) / 100);
  const color = pct < 60 ? 'var(--success)' : pct < 85 ? 'var(--warning)' : 'var(--danger)';

  return (
    <svg width="140" height="120" viewBox="0 0 140 130">
      <path d={trackPath} stroke="var(--bg-sunken)" strokeWidth="10" fill="none" strokeLinecap="round" />
      <path
        d={trackPath}
        stroke={color}
        strokeWidth="10"
        fill="none"
        strokeLinecap="round"
        strokeDasharray={`${filled.toFixed(2)} ${arcLen.toFixed(2)}`}
      />
    </svg>
  );
}

// Tiny inline sparkline for the per-agent burn table. Path is a simple
// linear interpolation across the 7-day series, normalised to 24×40.
function Sparkline({ values }: { values: number[] }) {
  if (values.length === 0) return null;
  const w = 64, h = 18;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = Math.max(0.001, max - min);
  const points = values.map((v, i) => {
    const x = (i / (values.length - 1)) * w;
    const y = h - ((v - min) / span) * h;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  const lastVal = values[values.length - 1];
  const firstVal = values[0];
  const trendUp = lastVal >= firstVal;
  return (
    <svg width={w} height={h} viewBox={`0 0 ${w} ${h}`} aria-hidden>
      <polyline
        points={points}
        fill="none"
        stroke={trendUp ? 'var(--success)' : 'var(--danger)'}
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <circle
        cx={w}
        cy={h - ((lastVal - min) / span) * h}
        r={1.6}
        fill={trendUp ? 'var(--success)' : 'var(--danger)'}
      />
    </svg>
  );
}
