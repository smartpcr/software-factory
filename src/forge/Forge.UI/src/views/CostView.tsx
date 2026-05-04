import { useEffect, useMemo, useRef, useState } from 'react';
import { useStateStore } from '@/state/useStateStore';
import { sampleState } from '@/sampleData';
import { forgeApi } from '@/api/forgeApi';
import type { AgentBurn, Card, CostSummary, Epic } from '@/types';

// Per Phase 11 of forge_implementation.md the agent_usage ledger is the
// source of truth for cost; these constants are kept as offline-only
// fallback fixtures matching the prototype so the page still renders if
// the API is unreachable. Replaced at runtime by /api/cost/summary.
const FALLBACK_DAY_LABELS = ['Wed', 'Thu', 'Fri', 'Sat', 'Sun', 'Mon', 'Tue'];
const FALLBACK_DAILY_BY_EPIC: Array<Record<string, number>> = [
  { 'EPIC-024': 1.10, 'EPIC-021': 0.62, 'EPIC-022': 0.31, 'EPIC-026': 0.18, 'EPIC-018': 0.42 },
  { 'EPIC-024': 1.42, 'EPIC-021': 0.71, 'EPIC-022': 0.28, 'EPIC-026': 0.14, 'EPIC-018': 0.31 },
  { 'EPIC-024': 1.88, 'EPIC-021': 0.84, 'EPIC-022': 0.36, 'EPIC-026': 0.21, 'EPIC-018': 0.18 },
  { 'EPIC-024': 1.65, 'EPIC-021': 0.92, 'EPIC-022': 0.42, 'EPIC-026': 0.26, 'EPIC-018': 0.12 },
  { 'EPIC-024': 2.04, 'EPIC-021': 1.18, 'EPIC-022': 0.51, 'EPIC-026': 0.32, 'EPIC-018': 0.09 },
  { 'EPIC-024': 1.78, 'EPIC-021': 1.06, 'EPIC-022': 0.44, 'EPIC-026': 0.36, 'EPIC-018': 0.05 },
  { 'EPIC-024': 2.18, 'EPIC-021': 1.84, 'EPIC-022': 0.41, 'EPIC-026': 0.41, 'EPIC-018': 0.02 },
];
const FALLBACK_DAILY_CAP = 7.5;

const FALLBACK_AGENT_BURN: AgentBurn[] = [
  { id: 'NZ', name: 'Ne Zha',          role: 'eng',  cost24hUsd: 0.42, cost7dUsd: 2.18, tokens: 184_000, spark: [0.18, 0.22, 0.31, 0.28, 0.34, 0.43, 0.42] },
  { id: 'AB', name: 'Ao Bing',         role: 'eng',  cost24hUsd: 0.38, cost7dUsd: 1.92, tokens: 162_000, spark: [0.21, 0.18, 0.24, 0.32, 0.30, 0.29, 0.38] },
  { id: 'ZR', name: 'Zhu Rong',        role: 'eng',  cost24hUsd: 0.31, cost7dUsd: 1.74, tokens: 148_000, spark: [0.18, 0.22, 0.26, 0.30, 0.28, 0.19, 0.31] },
  { id: 'SG', name: 'Shen Gongbao',    role: 'rev',  cost24hUsd: 0.21, cost7dUsd: 1.38, tokens: 102_000, spark: [0.22, 0.19, 0.18, 0.20, 0.18, 0.20, 0.21] },
  { id: 'TY', name: 'Taiji Zhenren',   role: 'arch', cost24hUsd: 0.42, cost7dUsd: 1.32, tokens:  88_000, spark: [0.10, 0.14, 0.18, 0.22, 0.18, 0.08, 0.42] },
  { id: 'GG', name: 'Gong Gong',       role: 'eng',  cost24hUsd: 0.14, cost7dUsd: 1.08, tokens:  92_000, spark: [0.18, 0.20, 0.16, 0.14, 0.12, 0.14, 0.14] },
  { id: 'WZ', name: 'Wen Zhong',       role: 'arch', cost24hUsd: 0.27, cost7dUsd: 0.94, tokens:  74_000, spark: [0.08, 0.12, 0.14, 0.18, 0.16, 0.10, 0.27] },
  { id: 'SF', name: "Shun Feng'er",    role: 'qa',   cost24hUsd: 0.09, cost7dUsd: 0.72, tokens:  68_000, spark: [0.10, 0.12, 0.10, 0.11, 0.10, 0.10, 0.09] },
  { id: 'YX', name: 'Yun Xiao',        role: 'rev',  cost24hUsd: 0.12, cost7dUsd: 0.68, tokens:  54_000, spark: [0.06, 0.08, 0.10, 0.12, 0.14, 0.12, 0.12] },
  { id: 'MB', name: 'Mu Bao',          role: 'eng',  cost24hUsd: 0.18, cost7dUsd: 0.61, tokens:  48_000, spark: [0.04, 0.06, 0.08, 0.10, 0.12, 0.18, 0.18] },
  { id: 'ZZ', name: 'Zhi Zhi',         role: 'qa',   cost24hUsd: 0.06, cost7dUsd: 0.42, tokens:  41_000, spark: [0.06, 0.06, 0.06, 0.06, 0.06, 0.06, 0.06] },
];

function buildFallback(): CostSummary {
  const days = FALLBACK_DAILY_BY_EPIC.map((byEpic, i) => {
    const total = Object.values(byEpic).reduce((a, b) => a + b, 0);
    return {
      date: `fixture-${i}`,
      label: FALLBACK_DAY_LABELS[i],
      totalUsd: total,
      byEpic,
    };
  });
  const epicTotals = new Map<string, number>();
  for (const d of days) {
    for (const [k, v] of Object.entries(d.byEpic)) {
      epicTotals.set(k, (epicTotals.get(k) ?? 0) + v);
    }
  }
  return {
    todayUsd: days[days.length - 1].totalUsd,
    week7dUsd: days.reduce((s, d) => s + d.totalUsd, 0),
    dailyCapUsd: FALLBACK_DAILY_CAP,
    todayIndex: days.length - 1,
    days,
    epics: [...epicTotals.entries()]
      .map(([epicId, total7dUsd]) => ({ epicId, total7dUsd }))
      .sort((a, b) => b.total7dUsd - a.total7dUsd),
    agents: FALLBACK_AGENT_BURN,
  };
}

function epicVar(color: string): string {
  return `var(--${color.replace('epic', 'epic-')})`;
}

function fmt(v: number): string {
  return v >= 10 ? `$${v.toFixed(0)}` : `$${v.toFixed(2)}`;
}

// Y-axis tick formatter — collapses thousands/millions so labels stay
// short ("$1.2k" not "$1200") and the axis column doesn't fight the bars
// for horizontal space.
function fmtAxis(v: number): string {
  if (v === 0) return '$0';
  if (v >= 1_000_000) return `$${(v / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`;
  if (v >= 1_000) return `$${(v / 1_000).toFixed(1).replace(/\.0$/, '')}k`;
  if (v >= 10) return `$${v.toFixed(0)}`;
  return `$${v.toFixed(2)}`;
}

// Pick a "nice" Y-axis scale for the spend chart. Targets `segments`
// equal divisions with a step that's a 1/1.5/2/2.5/3/4/5/7.5/10 × 10ⁿ
// number, and bumps the step until the ceiling sits at least 5% above
// the tallest bar so the data resolves visually.
function niceTicks(maxVal: number, segments: number): { max: number; ticks: number[] } {
  if (maxVal <= 0) return { max: 1, ticks: [0, 1 / 3, 2 / 3, 1] };
  const choices = [1, 1.5, 2, 2.5, 3, 4, 5, 7.5, 10];
  const rawStep = maxVal / segments;
  const exp = Math.floor(Math.log10(rawStep));
  const base = Math.pow(10, exp);
  let idx = choices.findIndex((c) => c >= rawStep / base);
  if (idx < 0) idx = choices.length - 1;
  let step = choices[idx] * base;
  while (step * segments < maxVal * 1.05) {
    idx++;
    if (idx >= choices.length) { idx = 0; step = base * 10; break; }
    step = choices[idx] * base;
  }
  const max = step * segments;
  const ticks: number[] = [];
  for (let i = 0; i <= segments; i++) ticks.push(step * i);
  return { max, ticks };
}

const TEAM_CHIPS: Array<{ key: string; label: string }> = [
  { key: 'all',  label: 'All' },
  { key: 'arch', label: 'Architect' },
  { key: 'eng',  label: 'Engineer' },
  { key: 'rev',  label: 'Review' },
  { key: 'qa',   label: 'QA' },
];

export function CostView() {
  const apiState = useStateStore((s) => s.state);
  const epics: Epic[] = apiState?.epics ?? sampleState.epics;
  const cards: Card[] = apiState?.cards ?? sampleState.cards;

  // Live cost rollup from /api/cost/summary; keep the prototype fixtures as
  // the offline fallback (Phase 11: "hard-coded values stay as fallback
  // fixtures only when the runner is offline").
  const [summary, setSummary] = useState<CostSummary>(() => buildFallback());

  // Filter state
  const [teamFilter, setTeamFilter] = useState('all');
  const [productFilter, setProductFilter] = useState<string[]>([]);
  const [repoFilter, setRepoFilter] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const live = await forgeApi.getCostSummary(7);
        if (cancelled) return;
        setSummary(live);
      } catch (err) {
        console.warn('[forge] /api/cost/summary failed, using fallback fixtures', err);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  // Apply client-side filters to agents
  const filteredAgents = useMemo(() => {
    let result = summary.agents;
    if (teamFilter !== 'all') {
      result = result.filter((a) => a.role === teamFilter);
    }
    if (productFilter.length > 0) {
      result = result.filter((a) => a.products?.some((p) => productFilter.includes(p)));
    }
    if (repoFilter.length > 0) {
      result = result.filter((a) => a.repos?.some((r) => repoFilter.includes(r)));
    }
    return result;
  }, [summary.agents, teamFilter, productFilter, repoFilter]);

  const hasFilter = teamFilter !== 'all' || productFilter.length > 0 || repoFilter.length > 0;

  // Derive filter options from agent data
  const teamCounts = useMemo(() => {
    const counts: Record<string, number> = { all: summary.agents.length };
    for (const a of summary.agents) {
      counts[a.role] = (counts[a.role] ?? 0) + 1;
    }
    return counts;
  }, [summary.agents]);

  const productOptions = useMemo(() => {
    const set = new Set<string>();
    for (const a of summary.agents) for (const p of a.products ?? []) set.add(p);
    return [...set].sort();
  }, [summary.agents]);

  const repoOptions = useMemo(() => {
    const set = new Set<string>();
    for (const a of summary.agents) for (const r of a.repos ?? []) set.add(r);
    return [...set].sort();
  }, [summary.agents]);

  const todayTotal = summary.todayUsd;
  const week7d = summary.week7dUsd;
  const dailyCap = summary.dailyCapUsd;
  const burnRate = summary.days.length > 0 ? week7d / summary.days.length : 0;
  const forecast7d = burnRate * 7;

  // Scale Y-axis to the actual spend distribution, not the daily cap. The
  // cap is shown by the right-rail gauge ("Today vs cap") and the Today
  // stat cell — including it here flattens every bar to a hairline when
  // realised spend is well below the cap (the common case during ramp-up).
  const yScale = useMemo(
    () => niceTicks(Math.max(0, ...summary.days.map((d) => d.totalUsd)), 3),
    [summary],
  );
  const dayMax = yScale.max;

  const epicById = useMemo(() => new Map(epics.map((e) => [e.id, e])), [epics]);

  const epicLegend = useMemo(() => {
    return summary.epics.map((row) => {
      const meta = epicById.get(row.epicId);
      return {
        id: row.epicId,
        name: meta?.name ?? row.epicId,
        color: meta ? epicVar(meta.color) : 'var(--text-tertiary)',
        total: row.total7dUsd,
      };
    });
  }, [summary, epicById]);

  const ranked = useMemo(() => {
    return cards
      .filter((c) => c.cost > 0)
      .sort((a, b) => b.cost - a.cost)
      .slice(0, 8);
  }, [cards]);

  const totalAgentBurn = filteredAgents.reduce((s, a) => s + a.cost7dUsd, 0);
  const budgetPct = dailyCap > 0
    ? Math.min(100, Math.round((todayTotal / dailyCap) * 100))
    : 0;

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

      {/* ── Filter bar ─────────────────────────────────────────────── */}
      <div className="filter-bar" style={{ paddingBottom: 14 }}>
        <div className="filter-chips">
          {TEAM_CHIPS.map((chip) => (
            <button
              key={chip.key}
              className={`filter-chip${teamFilter === chip.key ? ' active' : ''}`}
              onClick={() => setTeamFilter(chip.key)}
            >
              {chip.label}
              <span className="count">{teamCounts[chip.key] ?? 0}</span>
            </button>
          ))}
        </div>

        {productOptions.length > 0 && (
          <CostMultiSelect
            label="Product"
            options={productOptions.map((p) => ({ value: p, label: p }))}
            selected={productFilter}
            onChange={setProductFilter}
          />
        )}

        {repoOptions.length > 0 && (
          <CostMultiSelect
            label="Repo"
            options={repoOptions.map((r) => ({ value: r, label: r }))}
            selected={repoFilter}
            onChange={setRepoFilter}
          />
        )}

        {hasFilter && (
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            onClick={() => { setTeamFilter('all'); setProductFilter([]); setRepoFilter([]); }}
          >
            Clear filters
          </button>
        )}
      </div>

      <div className="workstreams-stats" style={{ margin: '0 28px 16px' }}>
        <div className="stat-cell">
          <div className="stat-cell-label">Today</div>
          <div className="stat-cell-val">${todayTotal.toFixed(2)}<span className="unit">/ ${dailyCap.toFixed(2)} cap</span></div>
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
                {[...yScale.ticks].reverse().map((t) => (
                  <span key={t}>{fmtAxis(t)}</span>
                ))}
              </div>
              <div className="cost-chart-bars">
                <div className="cost-chart-grid">
                  <div /><div /><div /><div />
                </div>
                {summary.days.map((day, i) => {
                  const dayTotal = day.totalUsd;
                  const heightPct = dayMax > 0 ? (dayTotal / dayMax) * 100 : 0;
                  return (
                    <div className="cost-chart-col" key={day.date} data-today={i === summary.todayIndex || undefined}>
                      <div className="cost-chart-stack" style={{ height: `${heightPct}%` }}>
                        {epics.map((e) => {
                          const v = day.byEpic[e.id] ?? 0;
                          if (v === 0) return null;
                          const segPct = dayTotal > 0 ? (v / dayTotal) * 100 : 0;
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
                      <span className="label">{day.label}</span>
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
              <span className="cost-card-eyebrow">7d · ${totalAgentBurn.toFixed(2)} across {filteredAgents.length} agents{hasFilter ? ' (filtered)' : ''}</span>
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
                {filteredAgents.map((a) => {
                  const share = totalAgentBurn > 0 ? (a.cost7dUsd / totalAgentBurn) * 100 : 0;
                  return (
                    <tr key={a.id}>
                      <td className="name">
                        <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)' }}>
                          {a.id}
                        </span>
                        {a.name}
                      </td>
                      <td><span className="role-tag">{a.role}</span></td>
                      <td className="num cost">${a.cost24hUsd.toFixed(2)}</td>
                      <td className="num">${a.cost7dUsd.toFixed(2)}</td>
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
              <div className="sub">${todayTotal.toFixed(2)} of ${dailyCap.toFixed(2)}</div>
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

function CostMultiSelect({ label, options, selected, onChange }: {
  label: string;
  options: Array<{ value: string; label: string }>;
  selected: string[];
  onChange: (v: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    if (open) document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  const toggle = (value: string) => {
    onChange(
      selected.includes(value)
        ? selected.filter((v) => v !== value)
        : [...selected, value],
    );
  };

  const displayLabel = selected.length === 0
    ? `${label}: All`
    : `${label}: ${selected.length} selected`;

  return (
    <div ref={ref} style={{ position: 'relative' }}>
      <button
        type="button"
        className="filter-select"
        onClick={() => setOpen(!open)}
        style={{
          cursor: 'pointer',
          background: selected.length > 0 ? 'var(--accent-bg, rgba(79,140,255,0.12))' : undefined,
          borderColor: selected.length > 0 ? 'var(--accent)' : undefined,
          minWidth: 120,
        }}
      >
        {displayLabel} ▾
      </button>
      {open && (
        <div style={{
          position: 'absolute',
          top: '100%',
          left: 0,
          zIndex: 100,
          background: 'var(--bg-elevated)',
          border: '1px solid var(--border)',
          borderRadius: 'var(--radius-md)',
          padding: '6px 0',
          minWidth: 180,
          maxHeight: 240,
          overflow: 'auto',
          boxShadow: '0 4px 16px rgba(0,0,0,0.2)',
        }}>
          {options.map((opt) => (
            <label
              key={opt.value}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '5px 12px',
                cursor: 'pointer',
                fontSize: 12,
                color: 'var(--text-primary)',
              }}
              onMouseDown={(e) => e.preventDefault()}
            >
              <input
                type="checkbox"
                checked={selected.includes(opt.value)}
                onChange={() => toggle(opt.value)}
                style={{ accentColor: 'var(--accent)' }}
              />
              {opt.label}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}
