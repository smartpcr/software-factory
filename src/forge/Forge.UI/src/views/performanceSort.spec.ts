// Phase 89 — pure-logic tests for the roster scorecard sort comparator.
// The header rendering is thin and conditional; the ordering decisions
// live in `compareAgentsBySort` (exported from PerformanceView.tsx) so
// they can be locked in here without spinning up a DOM.

import { describe, expect, it } from 'vitest';
import type { AgentScorecard } from '../api/forgeApi';
import { compareAgentsBySort } from './PerformanceView';

function mk(overrides: Partial<AgentScorecard> & { agentId: string }): AgentScorecard {
  return {
    agentId: overrides.agentId,
    name: overrides.name ?? overrides.agentId,
    team: overrides.team ?? 'Engineer',
    crew: overrides.crew ?? 'crew-x',
    crewId: overrides.crewId ?? 'crew-x',
    level: overrides.level ?? 0,
    title: overrides.title ?? 'Trainee',
    hourlyUsd: overrides.hourlyUsd ?? 4,
    avgScore: overrides.avgScore ?? 0,
    passRate: overrides.passRate ?? 0,
    throughput: overrides.throughput ?? 0,
    iterP50: overrides.iterP50 ?? 0,
    iterP90: overrides.iterP90 ?? 0,
    rejectRate: overrides.rejectRate ?? 0,
    disputeRate: overrides.disputeRate ?? 0,
    stalledRate: overrides.stalledRate ?? 0,
    utilization: overrides.utilization ?? 0,
    costPerPass: overrides.costPerPass ?? 0,
    promotionEta: overrides.promotionEta ?? null,
    cooldownDaysRemaining: overrides.cooldownDaysRemaining ?? 0,
    demotionRisk: overrides.demotionRisk ?? false,
    verdict: overrides.verdict ?? 'watch',
    verdictNote: overrides.verdictNote ?? '',
    holdReasons: overrides.holdReasons ?? [],
    scoreSpark: overrides.scoreSpark ?? [],
    monthlyAvg: overrides.monthlyAvg ?? [],
    monthlyMonths: overrides.monthlyMonths ?? [],
    dims: overrides.dims ?? { correctness: 0, quality: 0, tests: 0, scope: 0, risk: 0 },
    products: overrides.products ?? [],
    repos: overrides.repos ?? [],
    lastPromotedAt: overrides.lastPromotedAt ?? null,
    nextEligibleAt: overrides.nextEligibleAt ?? '',
    lastChangeAt: overrides.lastChangeAt ?? null,
    lastChangeKind: overrides.lastChangeKind ?? null,
  };
}

function ids(rows: AgentScorecard[]): string[] {
  return rows.map((r) => r.agentId);
}

function sortBy(
  rows: AgentScorecard[],
  key: Parameters<typeof compareAgentsBySort>[2],
  dir: Parameters<typeof compareAgentsBySort>[3],
): AgentScorecard[] {
  return [...rows].sort((a, b) => compareAgentsBySort(a, b, key, dir));
}

describe('compareAgentsBySort — name', () => {
  it('sorts ascending alphabetically', () => {
    const rows = [
      mk({ agentId: 'z', name: 'Zhi' }),
      mk({ agentId: 'a', name: 'Alpha' }),
      mk({ agentId: 'm', name: 'Mid' }),
    ];
    expect(ids(sortBy(rows, 'name', 'asc'))).toEqual(['a', 'm', 'z']);
  });

  it('flips to descending when dir is desc', () => {
    const rows = [mk({ agentId: 'a', name: 'Alpha' }), mk({ agentId: 'z', name: 'Zhi' })];
    expect(ids(sortBy(rows, 'name', 'desc'))).toEqual(['z', 'a']);
  });
});

describe('compareAgentsBySort — numeric "higher is better" (avgScore, passRate, throughput, level)', () => {
  it('avgScore desc puts the top scorer first', () => {
    const rows = [
      mk({ agentId: 'mid', avgScore: 80 }),
      mk({ agentId: 'high', avgScore: 95 }),
      mk({ agentId: 'low', avgScore: 70 }),
    ];
    expect(ids(sortBy(rows, 'avgScore', 'desc'))).toEqual(['high', 'mid', 'low']);
  });

  it('passRate asc puts the lowest pass rate first', () => {
    const rows = [
      mk({ agentId: 'a', passRate: 0.9 }),
      mk({ agentId: 'b', passRate: 0.5 }),
      mk({ agentId: 'c', passRate: 0.75 }),
    ];
    expect(ids(sortBy(rows, 'passRate', 'asc'))).toEqual(['b', 'c', 'a']);
  });

  it('throughput desc puts the busiest agent first', () => {
    const rows = [
      mk({ agentId: 'idle', throughput: 1 }),
      mk({ agentId: 'busy', throughput: 50 }),
      mk({ agentId: 'med', throughput: 20 }),
    ];
    expect(ids(sortBy(rows, 'throughput', 'desc'))).toEqual(['busy', 'med', 'idle']);
  });

  it('level desc puts the senior agents first; secondary tiebreak on avgScore', () => {
    const rows = [
      mk({ agentId: 'jr', level: 1, avgScore: 99 }),
      mk({ agentId: 'sr-good', level: 6, avgScore: 95 }),
      mk({ agentId: 'sr-meh', level: 6, avgScore: 80 }),
    ];
    // Both L6 agents come before the L1 agent; within L6, the higher
    // avgScore wins because the secondary tiebreaker is also flipped by dir.
    expect(ids(sortBy(rows, 'level', 'desc'))).toEqual(['sr-good', 'sr-meh', 'jr']);
  });
});

describe('compareAgentsBySort — numeric "lower is better" (iter, rejectRate, costPerPass)', () => {
  it('iter asc orders by P50 with P90 tiebreaker', () => {
    const rows = [
      mk({ agentId: 'slow', iterP50: 4, iterP90: 5 }),
      mk({ agentId: 'fast', iterP50: 1, iterP90: 2 }),
      mk({ agentId: 'tie-low', iterP50: 2, iterP90: 3 }),
      mk({ agentId: 'tie-high', iterP50: 2, iterP90: 5 }),
    ];
    expect(ids(sortBy(rows, 'iter', 'asc'))).toEqual(['fast', 'tie-low', 'tie-high', 'slow']);
  });

  it('rejectRate asc puts the cleanest agents first', () => {
    const rows = [
      mk({ agentId: 'dirty', rejectRate: 0.30 }),
      mk({ agentId: 'clean', rejectRate: 0.0 }),
      mk({ agentId: 'ok', rejectRate: 0.05 }),
    ];
    expect(ids(sortBy(rows, 'rejectRate', 'asc'))).toEqual(['clean', 'ok', 'dirty']);
  });

  it('costPerPass asc puts the cheapest agent first', () => {
    const rows = [
      mk({ agentId: 'pricey', costPerPass: 180.0 }),
      mk({ agentId: 'cheap', costPerPass: 5.0 }),
      mk({ agentId: 'mid', costPerPass: 50.0 }),
    ];
    expect(ids(sortBy(rows, 'costPerPass', 'asc'))).toEqual(['cheap', 'mid', 'pricey']);
  });
});

describe('compareAgentsBySort — promotionEta (null handling)', () => {
  it('nulls always sort last when ascending (typical "N/A" placement)', () => {
    const rows = [
      mk({ agentId: 'na', promotionEta: null }),
      mk({ agentId: 'soon', promotionEta: 0 }),
      mk({ agentId: 'far', promotionEta: 12 }),
    ];
    expect(ids(sortBy(rows, 'promotionEta', 'asc'))).toEqual(['soon', 'far', 'na']);
  });

  it('nulls ALSO sort last when descending — "N/A" never floats to the top', () => {
    const rows = [
      mk({ agentId: 'na', promotionEta: null }),
      mk({ agentId: 'far', promotionEta: 12 }),
      mk({ agentId: 'soon', promotionEta: 0 }),
    ];
    expect(ids(sortBy(rows, 'promotionEta', 'desc'))).toEqual(['far', 'soon', 'na']);
  });

  it('two nulls fall back to agentId tiebreaker', () => {
    const rows = [
      mk({ agentId: 'zz', promotionEta: null }),
      mk({ agentId: 'aa', promotionEta: null }),
    ];
    expect(ids(sortBy(rows, 'promotionEta', 'asc'))).toEqual(['aa', 'zz']);
    expect(ids(sortBy(rows, 'promotionEta', 'desc'))).toEqual(['aa', 'zz']);
  });
});

describe('compareAgentsBySort — lastChange (date + null handling)', () => {
  it('desc puts the most recently moved agent first; never-moved (null) sorts last', () => {
    const rows = [
      mk({ agentId: 'never', lastChangeAt: null }),
      mk({ agentId: 'old', lastChangeAt: '2026-04-01' }),
      mk({ agentId: 'recent', lastChangeAt: '2026-05-25' }),
    ];
    expect(ids(sortBy(rows, 'lastChange', 'desc'))).toEqual(['recent', 'old', 'never']);
  });

  it('asc puts the longest-ago move first (overdue candidates), null still last', () => {
    const rows = [
      mk({ agentId: 'never', lastChangeAt: null }),
      mk({ agentId: 'recent', lastChangeAt: '2026-05-25' }),
      mk({ agentId: 'old', lastChangeAt: '2026-04-01' }),
    ];
    expect(ids(sortBy(rows, 'lastChange', 'asc'))).toEqual(['old', 'recent', 'never']);
  });

  it('two never-moved fall back to agentId tiebreaker', () => {
    const rows = [
      mk({ agentId: 'zz', lastChangeAt: null }),
      mk({ agentId: 'aa', lastChangeAt: null }),
    ];
    expect(ids(sortBy(rows, 'lastChange', 'desc'))).toEqual(['aa', 'zz']);
  });
});

describe('compareAgentsBySort — verdict', () => {
  it('sorts verdicts alphabetically when asc', () => {
    const rows = [
      mk({ agentId: 'w', verdict: 'watch' }),
      mk({ agentId: 'p', verdict: 'promote' }),
      mk({ agentId: 'h', verdict: 'hold' }),
      mk({ agentId: 'd', verdict: 'demote' }),
    ];
    expect(ids(sortBy(rows, 'verdict', 'asc'))).toEqual(['d', 'h', 'p', 'w']);
  });
});

describe('compareAgentsBySort — stable tiebreaker', () => {
  it('falls back to agentId asc when the sort key produces a tie', () => {
    const rows = [
      mk({ agentId: 'zebra', avgScore: 90 }),
      mk({ agentId: 'apple', avgScore: 90 }),
      mk({ agentId: 'mango', avgScore: 90 }),
    ];
    // All three tie on avgScore — agentId asc breaks the tie.
    expect(ids(sortBy(rows, 'avgScore', 'desc'))).toEqual(['apple', 'mango', 'zebra']);
    expect(ids(sortBy(rows, 'avgScore', 'asc'))).toEqual(['apple', 'mango', 'zebra']);
  });
});
