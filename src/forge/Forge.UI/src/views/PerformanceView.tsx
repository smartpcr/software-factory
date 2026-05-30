import React, { useEffect, useMemo, useRef, useState } from 'react';
import { avatarFor } from '@/agentAvatar';
import {
  forgeApi,
  type AgentScorecard,
  type ReviewWindow,
  type PromotionVerdict,
  type PromotionDecision,
  type MonthlySnapshot,
  type ScoreDims,
  type PerformanceFilterOptions,
} from '@/api/forgeApi';

// ────────────────────────────────────────────────────────────────────
// Phase 14 — Performance view (Q14.8 + Q14.9 spec). Wired to
// GET /api/performance/scorecards. Three layers stack from rubric →
// decision → outcome:
//   1. Headline strip — payroll, spend, avg score, promotions ready
//   2. Promotion ladder (left) + Lead-floor card (rail)
//   3. Roster scorecard table + drill-in detail (radar + trend)
//   4. Promotion-decision cards (advisory; nightly job is authoritative)
//   5. Monthly review history (rail)
// ────────────────────────────────────────────────────────────────────

const RUBRIC_DIMS: ReadonlyArray<{ key: keyof ScoreDims; label: string; max: number }> = [
  { key: 'correctness', label: 'Correctness', max: 40 },
  { key: 'quality',     label: 'Quality',     max: 25 },
  { key: 'tests',       label: 'Tests',       max: 15 },
  { key: 'scope',       label: 'Scope',       max: 10 },
  { key: 'risk',        label: 'Risk',        max: 10 },
];

const LEVEL_TITLES: Record<number, string> = {
  0: 'Trainee',
  1: 'Engineer I', 2: 'Engineer II', 3: 'Engineer III',
  4: 'Senior I', 5: 'Senior II', 6: 'Senior III',
  7: 'Staff', 8: 'Principal',
  9: 'Distinguished', 10: 'Fellow',
};

const WINDOW_LABELS: Record<ReviewWindow, string> = {
  trailing20: 'Trailing window',
  mtd:        'This month',
  lastMonth:  'Last month',
};

const TEAM_CHIPS: Array<{ key: string; label: string }> = [
  { key: 'all',       label: 'All' },
  { key: 'Architect', label: 'Architect' },
  { key: 'Engineer',  label: 'Engineer' },
  { key: 'Review',    label: 'Review' },
  { key: 'QA',        label: 'QA' },
];

export function PerformanceView() {
  const [windowKey, setWindowKey] = useState<ReviewWindow>('trailing20');
  const [allScorecards, setAllScorecards] = useState<AgentScorecard[]>([]);
  const [decisions, setDecisions] = useState<PromotionDecision[]>([]);
  const [snapshots, setSnapshots] = useState<MonthlySnapshot[]>([]);
  const [filterOpts, setFilterOpts] = useState<PerformanceFilterOptions | null>(null);
  const [loading, setLoading] = useState(true);
  // Phase 89 — distinct from `loading`: true only while a post-action
  // refetch is in flight. We keep the existing table visible during a
  // refetch (no spinner swap) so the operator's scroll position survives a
  // PROMOTE / DEMOTE / HOLD click — collapsing the page to a one-line
  // spinner forced the browser to clamp scrollTop and jumped them back to
  // the top of the view.
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string>('');
  // Phase 89 — bumped after a successful PO override so the load effect re-fires.
  const [reloadCounter, setReloadCounter] = useState(0);

  // Filter state
  const [teamFilter, setTeamFilter] = useState('all');
  const [productFilter, setProductFilter] = useState<string[]>([]);
  const [repoFilter, setRepoFilter] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;
    // Initial load (no data yet OR window switched) shows the full-page
    // spinner. Operator-triggered refreshes flip `refreshing` instead and
    // keep the table mounted so scroll position survives.
    const isRefetch = reloadCounter > 0 && allScorecards.length > 0;
    async function load() {
      if (isRefetch) setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const r = await forgeApi.getPerformanceScorecards(windowKey);
        if (cancelled) return;
        setAllScorecards(r.scorecards);
        setDecisions(r.promotionDecisions);
        setSnapshots(r.monthlySnapshots);
        setFilterOpts(r.filterOptions);
        if (!r.scorecards.find((c) => c.agentId === selected) && r.scorecards.length > 0) {
          setSelected(r.scorecards[0].agentId);
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load scorecards');
      } finally {
        if (cancelled) return;
        if (isRefetch) setRefreshing(false);
        else setLoading(false);
      }
    }
    void load();
    return () => { cancelled = true; };
    // selected is intentionally NOT a dep — we read it inside but only to
    // seed it; the effect must not re-fire when the operator picks a row.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [windowKey, reloadCounter]);

  // Phase 89 — invoked by DecisionCard / VerdictPill after a successful PO
  // override (promote / demote / hold). Bumping the counter re-fires the
  // load effect, which now sets `refreshing` (not `loading`) so the table
  // stays mounted and the operator's scroll position is preserved.
  const reload = () => setReloadCounter((n) => n + 1);

  // Apply client-side filters
  const scorecards = useMemo(() => {
    let result = allScorecards;
    if (teamFilter !== 'all') {
      result = result.filter((c) => c.team === teamFilter);
    }
    if (productFilter.length > 0) {
      result = result.filter((c) => c.products.some((p) => productFilter.includes(p)));
    }
    if (repoFilter.length > 0) {
      result = result.filter((c) => c.repos.some((r) => repoFilter.includes(r)));
    }
    return result;
  }, [allScorecards, teamFilter, productFilter, repoFilter]);

  // Filter to agents with data (throughput > 0) for the main table,
  // but show all in the ladder.
  const activeCards = useMemo(() => scorecards.filter((c) => c.throughput > 0), [scorecards]);

  const selectedAgent = useMemo(
    () => scorecards.find((a) => a.agentId === selected) ?? activeCards[0] ?? null,
    [selected, scorecards, activeCards],
  );

  const summary = useMemo(() => deriveSummary(scorecards, snapshots), [scorecards, snapshots]);

  // Derive team counts from unfiltered data (so chips always show total)
  const teamCounts = useMemo(() => {
    const counts: Record<string, number> = { all: allScorecards.length };
    for (const c of allScorecards) {
      counts[c.team] = (counts[c.team] ?? 0) + 1;
    }
    return counts;
  }, [allScorecards]);

  // Product/repo options from filterOptions
  const productOptions = filterOpts?.products ?? [];
  const repoOptions = useMemo(() => {
    // Derive from actual scorecard data — only repos that appear
    const repoSet = new Set<string>();
    for (const c of allScorecards) for (const r of c.repos) repoSet.add(r);
    return [...repoSet].sort();
  }, [allScorecards]);

  return (
    <section className="view active" id="view-performance">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">14</span>&nbsp;Performance</span>
            <span className="sep">/</span>
            <span>{scorecards.length} agents · {new Set(scorecards.map((c) => c.team)).size} teams</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />nightly job @ 02:00 PT</span>
            {refreshing && (
              <>
                <span className="sep">/</span>
                <span style={{ color: 'var(--accent)', fontStyle: 'italic' }}>refreshing…</span>
              </>
            )}
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
          <MultiSelectDropdown
            label="Product"
            options={productOptions.map((p) => ({ value: p.id, label: p.name }))}
            selected={productFilter}
            onChange={setProductFilter}
          />
        )}

        {repoOptions.length > 0 && (
          <MultiSelectDropdown
            label="Repo"
            options={repoOptions.map((r) => ({ value: r, label: r }))}
            selected={repoFilter}
            onChange={setRepoFilter}
          />
        )}

        {(teamFilter !== 'all' || productFilter.length > 0 || repoFilter.length > 0) && (
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            onClick={() => { setTeamFilter('all'); setProductFilter([]); setRepoFilter([]); }}
          >
            Clear filters
          </button>
        )}
      </div>

      {loading && (
        <div style={{ padding: 40, textAlign: 'center', color: 'var(--text-tertiary)' }}>Loading scorecards…</div>
      )}
      {error && (
        <div style={{ padding: 40, textAlign: 'center', color: 'var(--danger)' }}>{error}</div>
      )}
      {!loading && !error && scorecards.length === 0 && (
        <div style={{ padding: 40, textAlign: 'center', color: 'var(--text-tertiary)' }}>
          No agent data available yet. Scorecards appear once deliverables are recorded.
        </div>
      )}

      {!loading && !error && scorecards.length > 0 && (
        <>
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
              <PromotionLadder agents={scorecards} selectedId={selected} onSelect={setSelected} />

              <div className="scorecard-card">
                <div className="head">
                  <div className="title">Roster scorecard — Q14.9 KPIs</div>
                  <div className="sub">click a row to inspect · {WINDOW_LABELS[windowKey]} · showing {activeCards.length} agents with data</div>
                </div>
                <ScorecardTable agents={activeCards} selectedId={selected} onSelect={setSelected} onActionComplete={reload} />
                {selectedAgent && <ScorecardDrawer agent={selectedAgent} />}
              </div>

              {decisions.length > 0 && (
                <>
                  <div className="perf-section-head">
                    <div className="title">Promotion decisions — advisory</div>
                    <div className="sub">PromotionJob · 02:00 PT · cooldown 30 d</div>
                  </div>
                  <div className="decisions">
                    {decisions.map((d) => (
                      <DecisionCard key={d.agentId} d={d} onActionComplete={reload} />
                    ))}
                  </div>
                </>
              )}
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
                  {snapshots.slice().reverse().map((m, i, arr) => {
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
        </>
      )}
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
  // Bucket agents by level + sort within each rung by avgScore desc so the
  // best performers anchor the left side and weakest/insufficient-data sit
  // on the right. This is what makes KPI legible when most of the roster
  // shares Level 0 (post-bootstrap, before any promotion has run).
  const byLevel = useMemo(() => {
    const m = new Map<number, AgentScorecard[]>();
    for (let lvl = 0; lvl <= 10; lvl++) m.set(lvl, []);
    for (const a of agents) m.get(a.level)!.push(a);
    for (const list of m.values()) {
      list.sort((x, y) => (y.avgScore || 0) - (x.avgScore || 0));
    }
    return m;
  }, [agents]);

  // Score → band (drives pin border-color via data-score-band).
  const scoreBand = (avg: number, throughput: number) => {
    if (throughput === 0) return 'none';
    if (avg >= 92) return 'promote';
    if (avg >= 85) return 'strong';
    if (avg >= 75) return 'hold';
    return 'risk';
  };

  return (
    <div className="perf-ladder">
      <div className="head">
        <div>
          <div className="perf-ladder-title">Promotion ladder</div>
          <div style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-secondary)', marginTop: 2 }}>
            level 0–10 · trainee → fellow · pinned by current level, sorted by avg score · arrows show pending move
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
                const band = scoreBand(a.avgScore || 0, a.throughput || 0);
                const bandColor =
                  band === 'promote' ? 'var(--success)' :
                  band === 'strong'  ? 'var(--accent)'  :
                  band === 'hold'    ? 'var(--warning)' :
                  band === 'risk'    ? 'var(--danger)'  :
                  'var(--border)';
                const showScore = (a.throughput || 0) > 0;
                return (
                  <button
                    key={a.agentId}
                    className="perf-ladder-pin"
                    data-team={a.team}
                    data-verdict={a.verdict}
                    data-score-band={band}
                    style={{ left: `${offsetPct}%`, transform: 'translate(-50%, -50%)' }}
                    onClick={() => onSelect(a.agentId)}
                    title={
                      `${a.name} · ${a.team} · ${a.crew}\n` +
                      `avg ${(a.avgScore || 0).toFixed(1)} · pass ${((a.passRate || 0) * 100).toFixed(0)}% · throughput ${a.throughput || 0}\n` +
                      `verdict: ${a.verdict}${a.promotionEta != null ? ` · ETA ${a.promotionEta}` : ''}`
                    }
                    aria-pressed={selectedId === a.agentId}
                  >
                    {avatarFor(a.agentId, a.name) ? (
                      <img
                        src={avatarFor(a.agentId, a.name)!}
                        alt=""
                        style={{
                          width: 22,
                          height: 22,
                          borderRadius: '50%',
                          objectFit: 'cover',
                          border: `1.5px solid ${bandColor}`,
                          boxShadow: showScore ? `0 0 0 1px ${bandColor}33` : undefined,
                        }}
                      />
                    ) : (
                      <span className="id" style={{ borderColor: bandColor }}>{a.agentId}</span>
                    )}
                    <span style={{ color: selectedId === a.agentId ? 'var(--accent)' : 'inherit' }}>{a.name}</span>
                    {showScore && (
                      <span
                        style={{
                          fontFamily: 'var(--font-mono)',
                          fontSize: 10,
                          padding: '1px 4px',
                          borderRadius: 3,
                          background: `${bandColor}22`,
                          color: bandColor,
                          fontWeight: 600,
                        }}
                      >
                        {(a.avgScore || 0).toFixed(0)}
                      </span>
                    )}
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
        <span style={{ marginLeft: 16, paddingLeft: 16, borderLeft: '1px solid var(--border)' }}>
          <span className="dot" style={{ background: 'var(--success)' }} /> ≥92
        </span>
        <span><span className="dot" style={{ background: 'var(--accent)' }} /> 85–92</span>
        <span><span className="dot" style={{ background: 'var(--warning)' }} /> 75–85</span>
        <span><span className="dot" style={{ background: 'var(--danger)' }} /> &lt;75</span>
        <span><span className="dot" style={{ background: 'var(--border)' }} /> no data</span>
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

/**
 * Sortable columns on the roster scorecard. `null` means "use the order the
 * server returned" (which is implicitly the active card filter's natural
 * order). The Trail-20 column is intentionally excluded — it's a sparkline,
 * not a comparable value.
 */
export type ScorecardSortKey =
  | 'name'
  | 'level'
  | 'avgScore'
  | 'passRate'
  | 'throughput'
  | 'iter'
  | 'rejectRate'
  | 'costPerPass'
  | 'promotionEta'
  | 'lastChange'
  | 'verdict';

export type ScorecardSortDir = 'asc' | 'desc';

/**
 * Default sort direction the first time a column is clicked. Numeric
 * "higher is better" columns start descending so the operator sees top
 * performers first; "lower is better" columns (iter depth, reject rate,
 * cost/pass, promo ETA) start ascending; identifier/text columns start
 * ascending.
 */
function defaultDirFor(key: ScorecardSortKey): ScorecardSortDir {
  switch (key) {
    case 'avgScore':
    case 'passRate':
    case 'throughput':
    case 'level':
      return 'desc';
    case 'lastChange':
      // Most recent level change first — surfaces who was just moved
      // (and is therefore in cooldown) at the top.
      return 'desc';
    case 'iter':
    case 'rejectRate':
    case 'costPerPass':
    case 'promotionEta':
    case 'name':
    case 'verdict':
      return 'asc';
  }
}

/**
 * Pure comparator for the roster scorecard sort. Exported so the spec can
 * lock the behaviour (null-handling, tie-breakers, dir flips) without
 * spinning up a DOM. Stable: ties fall back to <c>agentId</c> ascending so
 * the same input order always produces the same output order. Nulls on
 * <c>promotionEta</c> always sort last regardless of direction (typical
 * "N/A goes to the bottom" UX).
 */
export function compareAgentsBySort(
  a: AgentScorecard,
  b: AgentScorecard,
  key: ScorecardSortKey,
  dir: ScorecardSortDir,
): number {
  const sign = dir === 'asc' ? 1 : -1;

  // promotionEta nulls always last, regardless of direction.
  if (key === 'promotionEta') {
    const an = a.promotionEta;
    const bn = b.promotionEta;
    if (an === null && bn === null) return a.agentId.localeCompare(b.agentId);
    if (an === null) return 1;
    if (bn === null) return -1;
    if (an !== bn) return (an - bn) * sign;
    return a.agentId.localeCompare(b.agentId);
  }

  // lastChange: agents that never moved (null) always sort last. Dates are
  // yyyy-MM-dd so a lexicographic compare is chronological.
  if (key === 'lastChange') {
    const ad = a.lastChangeAt;
    const bd = b.lastChangeAt;
    if (!ad && !bd) return a.agentId.localeCompare(b.agentId);
    if (!ad) return 1;
    if (!bd) return -1;
    if (ad !== bd) return ad.localeCompare(bd) * sign;
    return a.agentId.localeCompare(b.agentId);
  }

  let cmp = 0;
  switch (key) {
    case 'name':
      cmp = a.name.localeCompare(b.name);
      break;
    case 'level':
      cmp = a.level - b.level;
      // Secondary tiebreaker: avgScore (higher is better)
      // ALSO flipped by the same direction so a desc sort puts the
      // highest avgScore first within the same level.
      if (cmp === 0) cmp = a.avgScore - b.avgScore;
      break;
    case 'avgScore':
      cmp = a.avgScore - b.avgScore;
      break;
    case 'passRate':
      cmp = a.passRate - b.passRate;
      break;
    case 'throughput':
      cmp = a.throughput - b.throughput;
      break;
    case 'iter':
      // Primary on P50, tiebreaker on P90.
      cmp = a.iterP50 - b.iterP50;
      if (cmp === 0) cmp = a.iterP90 - b.iterP90;
      break;
    case 'rejectRate':
      cmp = a.rejectRate - b.rejectRate;
      break;
    case 'costPerPass':
      cmp = a.costPerPass - b.costPerPass;
      break;
    case 'verdict':
      cmp = a.verdict.localeCompare(b.verdict);
      break;
  }
  if (cmp !== 0) return cmp * sign;
  // Stable tiebreaker so the order is deterministic regardless of input.
  return a.agentId.localeCompare(b.agentId);
}

const SORT_STORAGE_KEY = 'forge.performance.scorecardSort';

interface ScorecardSortState {
  key: ScorecardSortKey;
  dir: ScorecardSortDir;
}

function loadSortState(): ScorecardSortState | null {
  try {
    const raw = localStorage.getItem(SORT_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as ScorecardSortState;
    if (!parsed || typeof parsed !== 'object') return null;
    if (typeof parsed.key !== 'string' || (parsed.dir !== 'asc' && parsed.dir !== 'desc')) return null;
    return parsed;
  } catch {
    return null;
  }
}

function saveSortState(s: ScorecardSortState | null): void {
  try {
    if (s === null) localStorage.removeItem(SORT_STORAGE_KEY);
    else localStorage.setItem(SORT_STORAGE_KEY, JSON.stringify(s));
  } catch { /* localStorage unavailable; ignore */ }
}

function ScorecardTable({ agents, selectedId, onSelect, onActionComplete }: {
  agents: AgentScorecard[];
  selectedId: string;
  onSelect: (id: string) => void;
  // Phase 89 — fires after a successful PO override on a row pill so the
  // parent can re-fetch the scorecards. Optional so the table still works
  // as a pure read view when no parent supplies the callback.
  onActionComplete?: () => void;
}) {
  const [sort, setSort] = useState<ScorecardSortState | null>(() => loadSortState());
  // Phase 89 — per-row override state. `busy` disables every actionable pill
  // (single in-flight slot — operators should react to one outcome at a time)
  // and `error` is keyed by agent so an inline message appears in the right
  // row without bleeding to the rest of the table.
  const [busyAgent, setBusyAgent] = useState<string | null>(null);
  const [pillError, setPillError] = useState<{ agentId: string; message: string } | null>(null);

  const sortedAgents = useMemo(() => {
    if (!sort) return agents;
    // Slice so the input array (held by the parent) isn't mutated.
    return [...agents].sort((a, b) => compareAgentsBySort(a, b, sort.key, sort.dir));
  }, [agents, sort]);

  function onHeaderClick(key: ScorecardSortKey) {
    setSort((cur) => {
      // Same column: flip direction. New column: pick the column's default.
      const next: ScorecardSortState = cur && cur.key === key
        ? { key, dir: cur.dir === 'asc' ? 'desc' : 'asc' }
        : { key, dir: defaultDirFor(key) };
      saveSortState(next);
      return next;
    });
  }

  async function runRowAction(
    a: AgentScorecard,
    kind: 'promote' | 'demote' | 'hold',
    e: React.MouseEvent,
  ) {
    e.stopPropagation(); // don't open the drawer when clicking the pill
    if (busyAgent) return;
    // Demote is destructive — require explicit confirmation since the
    // pill click is a single gesture (the DecisionCard's "Confirm demotion"
    // button is one indirection away, this isn't).
    if (kind === 'demote') {
      const ok = typeof window !== 'undefined'
        && window.confirm(`Demote ${a.name} from L${a.level} to L${a.level - 1}?`);
      if (!ok) return;
    }
    setBusyAgent(a.agentId);
    setPillError(null);
    try {
      if (kind === 'promote') await forgeApi.promoteAgent(a.agentId, a.level, a.avgScore);
      else if (kind === 'demote') await forgeApi.demoteAgent(a.agentId, a.level, a.avgScore);
      else await forgeApi.holdAgent(a.agentId, a.avgScore);
      onActionComplete?.();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Action failed';
      setPillError({ agentId: a.agentId, message: msg });
    } finally {
      setBusyAgent(null);
    }
  }

  function indicator(key: ScorecardSortKey): string {
    if (!sort || sort.key !== key) return '';
    return sort.dir === 'asc' ? ' ▲' : ' ▼';
  }

  function headerProps(key: ScorecardSortKey) {
    const active = sort?.key === key;
    return {
      role: 'button',
      tabIndex: 0,
      'aria-sort': (active ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : 'none') as 'ascending' | 'descending' | 'none',
      'data-sortable': true,
      'data-sort-active': active || undefined,
      onClick: () => onHeaderClick(key),
      onKeyDown: (e: React.KeyboardEvent) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          onHeaderClick(key);
        }
      },
    } as const;
  }

  return (
    <table className="scorecard-table" aria-label="Roster scorecard">
      <thead>
        <tr>
          <th className="left" {...headerProps('name')}>Agent{indicator('name')}</th>
          <th className="left" {...headerProps('level')}>Level{indicator('level')}</th>
          <th {...headerProps('avgScore')}>Avg{indicator('avgScore')}</th>
          <th {...headerProps('passRate')}>Pass{indicator('passRate')}</th>
          <th {...headerProps('throughput')}>Throughput{indicator('throughput')}</th>
          <th {...headerProps('iter')}>Iter P50/P90{indicator('iter')}</th>
          <th {...headerProps('rejectRate')}>Reject{indicator('rejectRate')}</th>
          <th {...headerProps('costPerPass')}>Cost / pass{indicator('costPerPass')}</th>
          {/* Trail-20 is a sparkline — not sortable */}
          <th>Trail-20</th>
          <th {...headerProps('promotionEta')}>Promo ETA{indicator('promotionEta')}</th>
          <th className="left" {...headerProps('lastChange')}>Last change{indicator('lastChange')}</th>
          <th className="left" {...headerProps('verdict')}>Verdict{indicator('verdict')}</th>
        </tr>
      </thead>
      <tbody>
        {sortedAgents.map((a) => {
          const sel = a.agentId === selectedId;
          return (
            <tr
              key={a.agentId}
              data-selected={sel}
              onClick={() => onSelect(a.agentId)}
            >
              <td className="left">
                <div className="agent-cell">
                  {avatarFor(a.agentId, a.name) ? (
                    <img
                      src={avatarFor(a.agentId, a.name)!}
                      alt=""
                      style={{ width: 28, height: 28, borderRadius: '50%', objectFit: 'cover', border: '1.5px solid var(--border)', flexShrink: 0 }}
                    />
                  ) : (
                    <span className="id">{a.agentId}</span>
                  )}
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
                {/* Phase 89 — surface cooldown countdown so HOLD-on-cooldown
                    reads "+2 d" instead of "—". Order of precedence:
                    deliverable-count ETA (set by server when promote-ready or
                    short of window) → cooldown days remaining → "—". */}
                {a.promotionEta === 0 ? <span className="delta-up">ready</span>
                  : a.promotionEta !== null ? `+${a.promotionEta}`
                  : a.verdict === 'hold' && a.cooldownDaysRemaining > 0 ? (
                    <span
                      style={{ color: 'var(--warning)' }}
                      title={`Promotion blocked by cooldown · next eligible ${a.nextEligibleAt}`}
                    >
                      +{a.cooldownDaysRemaining}&nbsp;d
                    </span>
                  ) : '—'}
              </td>
              <td className="left">
                {a.lastChangeAt ? (
                  <span
                    className={a.lastChangeKind === 'demote' ? 'delta-down' : 'delta-up'}
                    title={`Last ${a.lastChangeKind === 'demote' ? 'demoted' : 'promoted'} ${a.lastChangeAt}`
                      + (a.nextEligibleAt && a.nextEligibleAt !== 'now'
                        ? ` · next promo-eligible ${a.nextEligibleAt}`
                        : ' · promo-eligible now')}
                  >
                    {a.lastChangeKind === 'demote' ? '▼' : '▲'} {a.lastChangeAt}
                  </span>
                ) : (
                  <span style={{ color: 'var(--text-tertiary)' }} title="No level change on record">—</span>
                )}
              </td>
              <td className="left">
                <VerdictPill
                  agent={a}
                  busy={busyAgent === a.agentId}
                  anyBusy={busyAgent !== null}
                  error={pillError?.agentId === a.agentId ? pillError.message : null}
                  onAction={onActionComplete ? runRowAction : undefined}
                  onDismissError={() => setPillError(null)}
                />
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

/**
 * Phase 89 — the verdict cell. `promote` / `demote` / `hold` render as
 * `<button>`s that call the corresponding PO override API; `watch` and
 * `lead-pending` stay as read-only labels (no backend yet). Clicking a
 * pill stops propagation so the row's "open drawer" handler doesn't fire.
 * When `onAction` is omitted the cell is a pure label (parent didn't opt
 * into write-through).
 */
function VerdictPill({
  agent, busy, anyBusy, error, onAction, onDismissError,
}: {
  agent: AgentScorecard;
  busy: boolean;
  anyBusy: boolean;
  error: string | null;
  onAction?: (a: AgentScorecard, kind: 'promote' | 'demote' | 'hold', e: React.MouseEvent) => void;
  onDismissError: () => void;
}) {
  const actionable = onAction !== undefined
    && (agent.verdict === 'promote' || agent.verdict === 'demote' || agent.verdict === 'hold');

  if (!actionable) {
    // `watch` / `lead-pending` are informational, not operator actions —
    // there's no backend verb for them (watch = stable / not enough data;
    // lead-pending = lead-floor case). Render a read-only pill, but surface
    // WHY on hover so a click that "does nothing" is self-explanatory
    // instead of feeling broken.
    const why = agent.verdict === 'watch'
      ? (agent.verdictNote || 'No action — agent is stable or has too few deliverables to evaluate.')
      : agent.verdict === 'lead-pending'
        ? (agent.verdictNote || 'Lead promotion is reviewed separately — no row action.')
        : agent.verdictNote;
    return (
      <span
        className={`verdict ${agent.verdict} verdict-static`}
        title={why || undefined}
        aria-label={why ? `${verdictLabel(agent.verdict)} — ${why}` : undefined}
      >
        {verdictLabel(agent.verdict)}
      </span>
    );
  }

  const kind = agent.verdict as 'promote' | 'demote' | 'hold';
  // Phase 89 — title = action verb + verdictNote so hovering the active
  // pill shows BOTH what clicking does and the policy reasoning. For
  // hold-with-cooldown the operator now sees e.g.
  // "Promote Bei Hai L1 → L2 · Cooldown: 2 d remaining (next eligible 2026-05-31)"
  // before deciding whether to override.
  const actionVerb = kind === 'promote' ? `Promote ${agent.name} L${agent.level} → L${agent.level + 1}`
                   : kind === 'demote'  ? `Demote ${agent.name} L${agent.level} → L${agent.level - 1}`
                   : `Acknowledge ${agent.name} (audit-only hold)`;
  const title = agent.verdictNote
    ? `${actionVerb} · ${agent.verdictNote}`
    : actionVerb;

  return (
    <>
      <button
        type="button"
        className={`verdict ${kind} verdict-action`}
        title={title}
        disabled={anyBusy}
        aria-busy={busy}
        onClick={(e) => onAction!(agent, kind, e)}
      >
        {busy ? '…' : verdictLabel(kind)}
      </button>
      {error && (
        <span
          role="alert"
          onClick={(e) => { e.stopPropagation(); onDismissError(); }}
          style={{
            marginLeft: 6,
            padding: '1px 6px',
            fontSize: 9,
            background: 'var(--danger-bg)',
            color: 'var(--danger)',
            border: '1px solid var(--danger-border)',
            borderRadius: 3,
            cursor: 'pointer',
            fontFamily: 'var(--font-mono)',
          }}
          title={`${error} — click to dismiss`}
        >
          ⚠ failed
        </span>
      )}
    </>
  );
}

// ════════════════════════════════════════════════════════════════════
// Scorecard drawer (selected agent) — radar + trend
// ════════════════════════════════════════════════════════════════════

function ScorecardDrawer({ agent }: { agent: AgentScorecard }) {
  return (
    <div className="scorecard-detail">
      <div className="name-block">
        <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
          {avatarFor(agent.agentId, agent.name) && (
            <img
              src={avatarFor(agent.agentId, agent.name)!}
              alt=""
              style={{ width: 40, height: 40, borderRadius: '50%', objectFit: 'cover', border: '2px solid var(--border)', flexShrink: 0 }}
            />
          )}
          <div>
            <div className="role">{agent.team} · L{agent.level} · ${agent.hourlyUsd}/hr</div>
            <div className="name">{agent.name}</div>
            <div className="crew">{agent.crew}</div>
          </div>
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
        <div className="trend-label">trailing window · oldest → today</div>
        <BigSpark values={agent.scoreSpark} />
        {agent.scoreSpark.length > 0 && (
          <div className="legend">
            <span>min {Math.min(...agent.scoreSpark)}</span>
            <span>avg {(agent.scoreSpark.reduce((s, v) => s + v, 0) / agent.scoreSpark.length).toFixed(1)}</span>
            <span>max {Math.max(...agent.scoreSpark)}</span>
          </div>
        )}
        <div className="trend-label" style={{ marginTop: 6 }}>monthly · 6 mo</div>
        <MonthlyBars values={agent.monthlyAvg} labels={agent.monthlyMonths} />
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// Promotion decision card
// ════════════════════════════════════════════════════════════════════

function DecisionCard({ d, onActionComplete }: {
  d: PromotionDecision;
  onActionComplete: () => void;
}) {
  const direction = d.toLevel > d.fromLevel ? 'up'
                  : d.toLevel < d.fromLevel ? 'down' : 'flat';
  // Phase 89 — trust the server-derived verdict (tier-aware). The previous
  // client-side derivation hardcoded `>= 92` and so rendered easy-tier
  // (L0-L2, threshold 88) promotions as 'hold'.
  const kind: PromotionVerdict = d.verdict;

  // Phase 89 — operator override state. `busy` disables buttons while an
  // override request is in flight; `error` surfaces 409 (stale page) or
  // 400 (at floor/cap) so the operator knows to refresh and retry.
  const [busy, setBusy] = useState<'promote' | 'demote' | 'hold' | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function runAction(
    action: 'promote' | 'demote' | 'hold',
    call: () => Promise<unknown>,
  ) {
    if (busy) return;
    setBusy(action);
    setError(null);
    try {
      await call();
      onActionComplete();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Action failed';
      setError(msg);
    } finally {
      setBusy(null);
    }
  }

  const promote = () => runAction('promote',
    () => forgeApi.promoteAgent(d.agentId, d.fromLevel, d.windowAvg));
  const demote  = () => runAction('demote',
    () => forgeApi.demoteAgent(d.agentId, d.fromLevel, d.windowAvg));
  const hold    = () => runAction('hold',
    () => forgeApi.holdAgent(d.agentId, d.windowAvg));

  return (
    <article className="decision" data-kind={kind}>
      <div className="top">
        <div className="move">
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            {avatarFor(d.agentId, d.name) && (
              <img
                src={avatarFor(d.agentId, d.name)!}
                alt=""
                style={{ width: 26, height: 26, borderRadius: '50%', objectFit: 'cover', border: '1.5px solid var(--border)', flexShrink: 0 }}
              />
            )}
            <div>
              <div className="who">{d.name}</div>
              <div className="id">{d.agentId} · {d.team}</div>
            </div>
          </div>
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

      {error && (
        <div
          role="alert"
          style={{
            marginTop: 8,
            padding: '6px 10px',
            borderRadius: 4,
            background: 'var(--danger-bg)',
            color: 'var(--danger)',
            border: '1px solid var(--danger-border)',
            fontSize: 11.5,
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{error}</span>
          <button
            type="button"
            onClick={onActionComplete}
            style={{ background: 'transparent', border: 0, color: 'inherit', textDecoration: 'underline', cursor: 'pointer', padding: 0 }}
          >
            refresh
          </button>
        </div>
      )}

      <div className="actions">
        {kind === 'promote' && (
          <button
            type="button"
            className="primary"
            onClick={promote}
            disabled={busy !== null}
            aria-busy={busy === 'promote'}
          >
            {busy === 'promote' ? 'Promoting…' : 'Approve & ship'}
          </button>
        )}
        {kind === 'demote' && (
          <button
            type="button"
            className="primary"
            style={{ background: 'var(--danger)', borderColor: 'var(--danger)' }}
            onClick={demote}
            disabled={busy !== null}
            aria-busy={busy === 'demote'}
          >
            {busy === 'demote' ? 'Demoting…' : 'Confirm demotion'}
          </button>
        )}
        {kind === 'hold' && (
          <button
            type="button"
            onClick={hold}
            disabled={busy !== null}
            aria-busy={busy === 'hold'}
            title="Records operator acknowledgement in the level-history audit log. Does not suppress the next nightly job at v0.1."
          >
            {busy === 'hold' ? 'Logging…' : 'Hold for next window'}
          </button>
        )}
        {/* TODO Phase 89+ — wire watch/lead-pending once backend lands.
            Watchlist is a UI-only TODO; lead nomination is Q14.5 follow-up. */}
        {kind === 'watch' && <button type="button" disabled>Add to watchlist</button>}
        {kind === 'lead-pending' && <button type="button" disabled>Open lead nomination</button>}
        <button type="button" disabled>Open file</button>
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
  if (values.length < 2) return null;
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
  if (values.length < 2) return null;
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
// Multi-select dropdown (inline, reusable within this view)
// ════════════════════════════════════════════════════════════════════

function MultiSelectDropdown({ label, options, selected, onChange }: {
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
          {options.length === 0 && (
            <div style={{ padding: '8px 12px', fontSize: 11, color: 'var(--text-tertiary)' }}>
              No options available
            </div>
          )}
        </div>
      )}
    </div>
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
  const spendTodayUsd = cards.reduce((s, c) => s + (c.throughput > 0 ? c.costPerPass * (c.throughput / 20) : 0), 0);
  const activeCards = cards.filter((c) => c.throughput > 0);
  const rosterAvg = activeCards.length > 0 ? activeCards.reduce((s, c) => s + c.avgScore, 0) / activeCards.length : 0;
  const readyToPromote = cards.filter((c) => c.verdict === 'promote').length;
  const atRisk = cards.filter((c) => c.demotionRisk).length;
  const leadVacancies = cards.filter((c) => c.verdict === 'lead-pending').length;
  const activeAgents = cards.length;
  void months;
  return { payrollTodayUsd, spendTodayUsd, rosterAvg, readyToPromote, atRisk, leadVacancies, activeAgents };
}
