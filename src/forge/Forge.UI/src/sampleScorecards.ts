// Phase 14 — mocked scorecards / KPIs / promotion decisions for the
// PerformanceView. Wholly client-side; no API. The shapes mirror what
// `IAgentKpiService` (spec §14.16) will eventually return so the view
// can swap to live data with a single import change.

export type ReviewWindow = 'trailing20' | 'mtd' | 'lastMonth';
export type PromotionVerdict = 'promote' | 'hold' | 'watch' | 'demote' | 'lead-pending';

export interface ScoreDims {
  correctness: number;  // 0..40
  quality: number;      // 0..25
  tests: number;        // 0..15
  scope: number;        // 0..10
  risk: number;         // 0..10
}

export interface AgentScorecard {
  agentId: string;
  name: string;
  team: 'Architect' | 'Engineer' | 'Review' | 'QA';
  crew: string;
  level: number;        // 0..10
  title: string;        // 'Engineer II', 'Senior I', 'Staff', …
  hourlyUsd: number;
  // KPIs (Q14.9, locked nine + 2 derived flags) ─────────────────────
  throughput: number;
  passRate: number;     // 0..1
  avgScore: number;     // 0..100
  iterP50: number;
  iterP90: number;
  rejectRate: number;   // 0..1
  disputeRate: number;  // 0..1
  stalledRate: number;  // 0..1
  utilization: number;  // 0..1
  costPerPass: number;  // USD
  // Trailing-20 sparkline of avg score, oldest first
  scoreSpark: number[];
  // Five-dim rubric average (0..1 of each dimension's max) for radar
  dims: ScoreDims;
  // Derived flags (Q14.9)
  promotionEta: number | null;  // deliverables until threshold; null = ∞
  demotionRisk: boolean;
  // Promotion decision (advisory; nightly job moves levels)
  verdict: PromotionVerdict;
  verdictNote: string;
  lastPromotedAt: string | null;
  nextEligibleAt: string;       // ISO date
  // Recent monthly avg scores (last 6 months, oldest first)
  monthlyAvg: number[];
  monthlyMonths: string[];      // matching labels e.g. ['Nov', 'Dec', …]
}

export interface PromotionDecision {
  agentId: string;
  name: string;
  team: AgentScorecard['team'];
  fromLevel: number;
  fromTitle: string;
  toLevel: number;
  toTitle: string;
  windowAvg: number;
  windowSize: number;
  rejectsInWindow: number;
  cooldownDaysRemaining: number;
  signedOff: boolean;
  signedOffBy: string | null;
  rationale: string[];
  risks: string[];
  nominatedAt: string;
}

export interface MonthlySnapshot {
  month: string;        // 'Mar 2026'
  agents: number;
  avgScore: number;
  promotions: number;
  demotions: number;
  payrollUsd: number;
  spendUsd: number;
  passRate: number;
  rejectRate: number;
  locked: boolean;
}

// ── Level → title (locked from scoring-and-promotion.md §4) ────────
export const LEVEL_TITLES: Record<number, string> = {
  0: 'Trainee',
  1: 'Engineer I', 2: 'Engineer II', 3: 'Engineer III',
  4: 'Senior I', 5: 'Senior II', 6: 'Senior III',
  7: 'Staff', 8: 'Principal',
  9: 'Distinguished', 10: 'Fellow',
};

// 12 mocked agents across the four teams. Scores are crafted so the
// promotion ladder + decision cards have a story: NZ is a clear promo,
// AB is on the edge, GG is at demotion risk, TY is staff-floor lead.
export const SCORECARDS: AgentScorecard[] = [
  {
    agentId: 'NZ', name: 'Ne Zha', team: 'Engineer', crew: 'Engineering · pod 1',
    level: 5, title: LEVEL_TITLES[5], hourlyUsd: 19,
    throughput: 21, passRate: 0.71, avgScore: 93.2,
    iterP50: 1, iterP90: 2, rejectRate: 0.00, disputeRate: 0.05, stalledRate: 0.00,
    utilization: 0.82, costPerPass: 1.42,
    scoreSpark: [88, 86, 89, 90, 91, 91, 92, 92, 93, 94, 92, 95, 93, 94, 92, 95, 93, 96, 94, 93],
    dims: { correctness: 38, quality: 23, tests: 14, scope: 9, risk: 9 },
    promotionEta: 0,
    demotionRisk: false,
    verdict: 'promote',
    verdictNote: '20-window ≥ 92 · 0 rejects · cooldown clear',
    lastPromotedAt: '2026-01-18',
    nextEligibleAt: '2026-04-26',
    monthlyAvg: [86, 88, 89, 91, 92, 93],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'AB', name: 'Ao Bing', team: 'Engineer', crew: 'Engineering · pod 1',
    level: 4, title: LEVEL_TITLES[4], hourlyUsd: 16,
    throughput: 18, passRate: 0.61, avgScore: 89.4,
    iterP50: 2, iterP90: 3, rejectRate: 0.05, disputeRate: 0.00, stalledRate: 0.05,
    utilization: 0.74, costPerPass: 1.74,
    scoreSpark: [85, 88, 86, 87, 88, 90, 87, 89, 88, 91, 89, 88, 90, 91, 89, 88, 90, 92, 89, 91],
    dims: { correctness: 35, quality: 22, tests: 13, scope: 9, risk: 8 },
    promotionEta: 7,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'avg 89.4 — needs +2.6 to clear 92 floor',
    lastPromotedAt: '2025-11-02',
    nextEligibleAt: '2026-05-12',
    monthlyAvg: [82, 85, 87, 88, 89, 89],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'ZR', name: 'Zhu Rong', team: 'Engineer', crew: 'Engineering · pod 2',
    level: 3, title: LEVEL_TITLES[3], hourlyUsd: 13,
    throughput: 22, passRate: 0.68, avgScore: 90.8,
    iterP50: 1, iterP90: 2, rejectRate: 0.05, disputeRate: 0.00, stalledRate: 0.00,
    utilization: 0.79, costPerPass: 0.98,
    scoreSpark: [83, 85, 86, 88, 87, 89, 90, 88, 91, 90, 92, 91, 90, 92, 93, 90, 92, 91, 93, 92],
    dims: { correctness: 36, quality: 22, tests: 14, scope: 9, risk: 9 },
    promotionEta: 4,
    demotionRisk: false,
    verdict: 'watch',
    verdictNote: 'fast-tracked · 4 deliverables to threshold',
    lastPromotedAt: '2026-02-09',
    nextEligibleAt: '2026-05-11',
    monthlyAvg: [84, 86, 88, 89, 90, 91],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'TY', name: 'Taiji Zhenren', team: 'Architect', crew: 'Architecture · lead',
    level: 7, title: LEVEL_TITLES[7], hourlyUsd: 25,
    throughput: 9, passRate: 0.78, avgScore: 94.8,
    iterP50: 1, iterP90: 1, rejectRate: 0.00, disputeRate: 0.11, stalledRate: 0.00,
    utilization: 0.66, costPerPass: 3.41,
    scoreSpark: [92, 91, 93, 94, 92, 95, 94, 96, 95, 94, 95, 96, 94, 95, 95, 96, 94, 95, 96, 95],
    dims: { correctness: 39, quality: 24, tests: 13, scope: 10, risk: 10 },
    promotionEta: 18,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'cooldown · 14 days remain since last promotion',
    lastPromotedAt: '2026-04-12',
    nextEligibleAt: '2026-05-12',
    monthlyAvg: [92, 93, 94, 94, 95, 95],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'WZ', name: 'Wen Zhong', team: 'Architect', crew: 'Architecture · pod 1',
    level: 6, title: LEVEL_TITLES[6], hourlyUsd: 22,
    throughput: 11, passRate: 0.64, avgScore: 91.1,
    iterP50: 2, iterP90: 3, rejectRate: 0.00, disputeRate: 0.09, stalledRate: 0.00,
    utilization: 0.71, costPerPass: 2.18,
    scoreSpark: [86, 88, 87, 89, 88, 90, 91, 89, 90, 91, 92, 90, 91, 92, 91, 92, 93, 91, 92, 91],
    dims: { correctness: 37, quality: 23, tests: 13, scope: 9, risk: 9 },
    promotionEta: 12,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'on track — 12 deliverables to lead floor',
    lastPromotedAt: '2025-09-21',
    nextEligibleAt: '2026-05-21',
    monthlyAvg: [88, 89, 90, 90, 91, 91],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'SG', name: 'Shen Gongbao', team: 'Review', crew: 'Review · evaluator',
    level: 4, title: LEVEL_TITLES[4], hourlyUsd: 16,
    throughput: 26, passRate: 0.69, avgScore: 90.4,
    iterP50: 1, iterP90: 2, rejectRate: 0.04, disputeRate: 0.08, stalledRate: 0.00,
    utilization: 0.85, costPerPass: 0.84,
    scoreSpark: [86, 87, 88, 90, 89, 91, 90, 89, 91, 92, 90, 91, 90, 92, 91, 90, 91, 92, 91, 92],
    dims: { correctness: 36, quality: 23, tests: 14, scope: 9, risk: 8 },
    promotionEta: 6,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'high dispute rate flag — judge audits clean',
    lastPromotedAt: '2025-12-04',
    nextEligibleAt: '2026-05-11',
    monthlyAvg: [85, 87, 88, 89, 90, 90],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'YX', name: 'Yun Xiao', team: 'Review', crew: 'Review · pair',
    level: 3, title: LEVEL_TITLES[3], hourlyUsd: 13,
    throughput: 19, passRate: 0.74, avgScore: 92.6,
    iterP50: 1, iterP90: 1, rejectRate: 0.00, disputeRate: 0.05, stalledRate: 0.00,
    utilization: 0.78, costPerPass: 0.92,
    scoreSpark: [88, 89, 90, 91, 92, 93, 91, 92, 93, 94, 92, 93, 94, 93, 92, 93, 94, 93, 94, 93],
    dims: { correctness: 37, quality: 24, tests: 14, scope: 9, risk: 9 },
    promotionEta: 0,
    demotionRisk: false,
    verdict: 'promote',
    verdictNote: 'avg 92.6 · 0 rejects · cooldown clear',
    lastPromotedAt: '2026-01-04',
    nextEligibleAt: '2026-04-21',
    monthlyAvg: [86, 88, 90, 91, 92, 93],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'SF', name: "Shun Feng'er", team: 'QA', crew: 'QA · pod 1',
    level: 2, title: LEVEL_TITLES[2], hourlyUsd: 10,
    throughput: 24, passRate: 0.71, avgScore: 88.9,
    iterP50: 1, iterP90: 2, rejectRate: 0.04, disputeRate: 0.00, stalledRate: 0.04,
    utilization: 0.81, costPerPass: 0.61,
    scoreSpark: [82, 84, 86, 87, 88, 87, 89, 90, 88, 89, 90, 88, 91, 89, 90, 88, 89, 90, 88, 89],
    dims: { correctness: 35, quality: 22, tests: 14, scope: 9, risk: 8 },
    promotionEta: 11,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'climb steady — 11 deliverables to senior',
    lastPromotedAt: '2025-10-12',
    nextEligibleAt: '2026-06-02',
    monthlyAvg: [82, 84, 86, 87, 88, 89],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'ZZ', name: 'Zhi Zhi', team: 'QA', crew: 'QA · pod 2',
    level: 1, title: LEVEL_TITLES[1], hourlyUsd: 7,
    throughput: 14, passRate: 0.57, avgScore: 84.1,
    iterP50: 2, iterP90: 4, rejectRate: 0.07, disputeRate: 0.00, stalledRate: 0.07,
    utilization: 0.62, costPerPass: 0.71,
    scoreSpark: [78, 80, 82, 81, 83, 82, 84, 83, 85, 82, 84, 85, 83, 84, 85, 83, 84, 85, 84, 85],
    dims: { correctness: 32, quality: 21, tests: 13, scope: 9, risk: 9 },
    promotionEta: null,
    demotionRisk: false,
    verdict: 'watch',
    verdictNote: 'soul.md hot-reload pending; expect lift in 2 weeks',
    lastPromotedAt: '2025-08-19',
    nextEligibleAt: '2026-07-04',
    monthlyAvg: [78, 80, 82, 83, 84, 84],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'GG', name: 'Gong Gong', team: 'Engineer', crew: 'Engineering · pod 2',
    level: 4, title: LEVEL_TITLES[4], hourlyUsd: 16,
    throughput: 17, passRate: 0.41, avgScore: 76.3,
    iterP50: 3, iterP90: 5, rejectRate: 0.18, disputeRate: 0.06, stalledRate: 0.12,
    utilization: 0.58, costPerPass: 2.84,
    scoreSpark: [82, 80, 78, 79, 77, 76, 75, 78, 76, 74, 75, 73, 76, 74, 76, 75, 78, 76, 75, 76],
    dims: { correctness: 30, quality: 19, tests: 11, scope: 8, risk: 8 },
    promotionEta: null,
    demotionRisk: true,
    verdict: 'demote',
    verdictNote: 'avg 76.3 < 75 floor with 18 % rejects — demote review',
    lastPromotedAt: '2025-07-22',
    nextEligibleAt: '—',
    monthlyAvg: [85, 83, 81, 79, 77, 76],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'MB', name: 'Mu Bao', team: 'Engineer', crew: 'Engineering · pod 1',
    level: 2, title: LEVEL_TITLES[2], hourlyUsd: 10,
    throughput: 13, passRate: 0.62, avgScore: 87.5,
    iterP50: 2, iterP90: 3, rejectRate: 0.08, disputeRate: 0.00, stalledRate: 0.00,
    utilization: 0.68, costPerPass: 1.12,
    scoreSpark: [80, 82, 84, 83, 85, 86, 85, 87, 86, 88, 87, 86, 88, 87, 88, 87, 88, 87, 88, 88],
    dims: { correctness: 34, quality: 22, tests: 13, scope: 9, risk: 8 },
    promotionEta: 14,
    demotionRisk: false,
    verdict: 'hold',
    verdictNote: 'consistent — needs throughput, not score',
    lastPromotedAt: '2025-11-19',
    nextEligibleAt: '2026-06-14',
    monthlyAvg: [80, 82, 84, 86, 87, 87],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
  {
    agentId: 'JD', name: 'Jin Dou', team: 'Architect', crew: 'Architecture · pod 2',
    level: 6, title: LEVEL_TITLES[6], hourlyUsd: 22,
    throughput: 8, passRate: 0.50, avgScore: 73.8,
    iterP50: 3, iterP90: 5, rejectRate: 0.13, disputeRate: 0.13, stalledRate: 0.13,
    utilization: 0.51, costPerPass: 4.18,
    scoreSpark: [78, 76, 74, 75, 72, 73, 71, 74, 72, 73, 71, 72, 74, 73, 75, 74, 73, 75, 74, 73],
    dims: { correctness: 28, quality: 18, tests: 11, scope: 8, risk: 8 },
    promotionEta: null,
    demotionRisk: true,
    verdict: 'lead-pending',
    verdictNote: 'lead floor at L7 — vacancy, 9 days to nominate',
    lastPromotedAt: '2025-06-04',
    nextEligibleAt: '—',
    monthlyAvg: [82, 80, 78, 76, 74, 74],
    monthlyMonths: ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'],
  },
];

// Promotion-decision cards — derived from SCORECARDS but with extra
// rationale + risks fields the verdict pill alone can't carry.
export const PROMOTION_DECISIONS: PromotionDecision[] = [
  {
    agentId: 'NZ', name: 'Ne Zha', team: 'Engineer',
    fromLevel: 5, fromTitle: LEVEL_TITLES[5], toLevel: 6, toTitle: LEVEL_TITLES[6],
    windowAvg: 93.2, windowSize: 20, rejectsInWindow: 0,
    cooldownDaysRemaining: 0, signedOff: true, signedOffBy: 'TY · Taiji Zhenren',
    rationale: [
      'Trailing-20 average 93.2 ≥ 92 threshold (§4)',
      '0 rejects in window; 1 dispute resolved by judge',
      'Cooldown cleared 2026-04-19 (90 d since L5)',
      'Hourly will move from $19 → $22 ($228 → $264 / 12h day)',
    ],
    risks: ['Crew is at 5/6 senior cap — promo pushes 1 below floor'],
    nominatedAt: '2026-04-25',
  },
  {
    agentId: 'YX', name: 'Yun Xiao', team: 'Review',
    fromLevel: 3, fromTitle: LEVEL_TITLES[3], toLevel: 4, toTitle: LEVEL_TITLES[4],
    windowAvg: 92.6, windowSize: 20, rejectsInWindow: 0,
    cooldownDaysRemaining: 0, signedOff: false, signedOffBy: null,
    rationale: [
      'Trailing-20 average 92.6 ≥ 92 threshold',
      '0 rejects, 1 dispute (judge confirmed evaluator)',
      'P90 iteration depth = 1 — first-shot wins dominate',
    ],
    risks: [
      'Pair partner SG is at L4 — re-pairing may be needed',
      'No cross-product reviews in window — narrow exposure',
    ],
    nominatedAt: '2026-04-26',
  },
  {
    agentId: 'AB', name: 'Ao Bing', team: 'Engineer',
    fromLevel: 4, fromTitle: LEVEL_TITLES[4], toLevel: 5, toTitle: LEVEL_TITLES[5],
    windowAvg: 89.4, windowSize: 20, rejectsInWindow: 1,
    cooldownDaysRemaining: 0, signedOff: false, signedOffBy: null,
    rationale: [
      'Strong throughput (18 deliverables in window)',
      'Pass-rate 0.61 — first-shot wins trending up since March',
    ],
    risks: [
      'Average 89.4 below 92 floor by 2.6 points',
      '1 reject in window blocks the auto-promote rule',
      'Lead-track readiness unclear — no plan-task contributions',
    ],
    nominatedAt: '2026-04-26',
  },
  {
    agentId: 'GG', name: 'Gong Gong', team: 'Engineer',
    fromLevel: 4, fromTitle: LEVEL_TITLES[4], toLevel: 3, toTitle: LEVEL_TITLES[3],
    windowAvg: 76.3, windowSize: 20, rejectsInWindow: 4,
    cooldownDaysRemaining: 0, signedOff: false, signedOffBy: null,
    rationale: [
      'Trailing-20 average 76.3 — 6-month decline, now < 78 risk floor',
      '4 rejects in window vs 0 cap for promotion eligibility',
      'Stalled-rate 12 % indicates iteration-cap fatigue',
    ],
    risks: [
      'Demotion drops hourly from $16 → $13 (− $36 / day)',
      'Crew may need a soul.md retrain before L3 placement sticks',
    ],
    nominatedAt: '2026-04-26',
  },
  {
    agentId: 'JD', name: 'Jin Dou', team: 'Architect',
    fromLevel: 6, fromTitle: LEVEL_TITLES[6], toLevel: 7, toTitle: LEVEL_TITLES[7],
    windowAvg: 73.8, windowSize: 20, rejectsInWindow: 3,
    cooldownDaysRemaining: 0, signedOff: false, signedOffBy: null,
    rationale: [
      'Crew is in lead-pending state — needs L7 lead by 2026-05-05',
    ],
    risks: [
      'Average 73.8 below lead floor (Q14.5) — refuse 400 + crew.lead.level_too_low',
      'Demotion risk active; L6 itself is not stable',
      'Recommend nominating from outside crew or extending pending window',
    ],
    nominatedAt: '2026-04-26',
  },
];

// Monthly review snapshot history for the right rail. Most recent is the
// in-progress month (locked = false, retro_notes still editable).
export const MONTHLY_SNAPSHOTS: MonthlySnapshot[] = [
  { month: 'Nov 2025', agents: 11, avgScore: 84.2, promotions: 1, demotions: 0, payrollUsd: 21_400, spendUsd:  9_280, passRate: 0.58, rejectRate: 0.08, locked: true },
  { month: 'Dec 2025', agents: 11, avgScore: 86.6, promotions: 0, demotions: 0, payrollUsd: 22_140, spendUsd: 10_120, passRate: 0.61, rejectRate: 0.06, locked: true },
  { month: 'Jan 2026', agents: 12, avgScore: 87.9, promotions: 2, demotions: 0, payrollUsd: 24_780, spendUsd: 11_840, passRate: 0.65, rejectRate: 0.05, locked: true },
  { month: 'Feb 2026', agents: 12, avgScore: 88.7, promotions: 1, demotions: 0, payrollUsd: 25_220, spendUsd: 12_180, passRate: 0.67, rejectRate: 0.05, locked: true },
  { month: 'Mar 2026', agents: 12, avgScore: 90.1, promotions: 1, demotions: 0, payrollUsd: 25_960, spendUsd: 12_640, passRate: 0.69, rejectRate: 0.04, locked: true },
  { month: 'Apr 2026', agents: 12, avgScore: 90.7, promotions: 0, demotions: 0, payrollUsd: 22_180, spendUsd: 10_920, passRate: 0.70, rejectRate: 0.03, locked: false },
];

export const RUBRIC_DIMS = [
  { key: 'correctness', label: 'Correctness', max: 40 },
  { key: 'quality',     label: 'Quality',     max: 25 },
  { key: 'tests',       label: 'Tests',       max: 15 },
  { key: 'scope',       label: 'Scope',       max: 10 },
  { key: 'risk',        label: 'Risk',        max: 10 },
] as const;
