// Verbatim port of the prototype's seed data from
// docs/examples/forge-board.html. Field names + values match exactly.
// Used as the offline fallback when ?seed=local is set on the URL,
// and as the fixture for fidelity screenshot tests.

import type {
  Card,
  Column,
  Epic,
  ForgeState,
  OverviewActivity,
  Pr,
  Workstream,
  WsEvent,
  WsStat,
} from './types';

export const epics: Epic[] = [
  { id: 'EPIC-024', name: 'Multi-tenant authentication', status: 'active',   progress: 38,  crew: ['A1','E4','E7','E9','R3'], cost: 2.18, color: 'epic024' },
  { id: 'EPIC-021', name: 'Observability v2',           status: 'active',   progress: 62,  crew: ['E4','E7','R5','Q2'],     cost: 1.84, color: 'epic021' },
  { id: 'EPIC-022', name: 'Webhook reliability',        status: 'merging',  progress: 95,  crew: ['E7','R3'],                cost: 0.94, color: 'epic022' },
  { id: 'EPIC-026', name: 'Cluster log rotation cap',   status: 'planning', progress: 12,  crew: ['A2','E9'],                cost: 0.41, color: 'epic026' },
  { id: 'EPIC-018', name: 'BITS chunked download',      status: 'merging',  progress: 100, crew: ['E4','R3'],                cost: 1.12, color: 'epic018' },
];

export const cards: Card[] = [
  { id: '#831', epic: 'EPIC-024', col: 'backlog', type: 'story', title: 'Tenant audit log retention policy', tags: ['auth'], pts: 5, priority: 'low', agent: null, cost: 0 },
  { id: '#832', epic: 'EPIC-024', col: 'backlog', type: 'task', title: 'Add tenant ID to OTel span attributes', tags: ['auth','infra'], pts: 2, priority: 'low', agent: null, cost: 0 },
  { id: '#812', epic: 'EPIC-024', col: 'design', type: 'story', title: 'Tenant context middleware', tags: ['auth'], pts: 3, priority: 'high', agent: { id: 'A2', role: 'arch', name: 'Mercer' }, cost: 0.18, autoNext: 'Auto-promote to Build when spec approved', live: true, progress: 72 },
  { id: '#813', epic: 'EPIC-024', col: 'design', type: 'story', title: 'Tenant routing in load balancer', tags: ['auth','infra'], pts: 5, priority: 'med', agent: { id: 'A1', role: 'arch', name: 'Atlas' }, cost: 0.24 },
  { id: '#234', epic: 'EPIC-024', col: 'build', type: 'story', title: 'OIDC token rotation handler with key rollover', tags: ['auth'], pts: 5, priority: 'high', agent: { id: 'E4', role: 'eng', name: 'Forge' }, cost: 0.42, live: true, progress: 64, autoNext: 'Open PR when tests pass', branch: 'ws/oidc-rotation' },
  { id: '#241', epic: 'EPIC-024', col: 'build', type: 'story', title: 'Per-tenant rate limit policy with sliding window', tags: ['auth'], pts: 5, priority: 'med', agent: { id: 'E7', role: 'eng', name: 'Rivet' }, cost: 0.31, live: true, progress: 28, branch: 'ws/rate-limits' },
  { id: '#243', epic: 'EPIC-024', col: 'build', type: 'task', title: 'Tenant admin UI scaffolding', tags: ['ui','auth'], pts: 3, priority: 'low', agent: { id: 'E4', role: 'eng', name: 'Forge' }, cost: 0.08, live: true, progress: 8, branch: 'ws/tenant-admin-ui' },
  { id: 'PR #240', epic: 'EPIC-024', col: 'review', type: 'story', title: 'Add IdP failover to Microsoft Entra', tags: ['security','auth'], pts: 8, priority: 'high', agent: { id: 'XD', role: 'human', name: 'Xiaodong' }, humanGate: true, gateReason: '/security/ path requires human approval', cost: 0.51, branch: 'ws/idp-failover' },
  { id: 'PR #229', epic: 'EPIC-024', col: 'review', type: 'story', title: 'Token store refactor — extract to auth-core', tags: ['auth'], pts: 5, priority: 'med', agent: { id: 'R3', role: 'rev', name: 'Cortex' }, cost: 0.34, autoNext: 'Auto-merge when CI passes' },

  { id: '#821', epic: 'EPIC-021', col: 'backlog', type: 'task', title: 'Document OTel collector deployment', tags: ['infra'], pts: 2, priority: 'low', agent: null, cost: 0 },
  { id: '#820', epic: 'EPIC-021', col: 'design', type: 'story', title: 'OTel span propagation across CSV cluster', tags: ['infra'], pts: 5, priority: 'med', agent: { id: 'A1', role: 'arch', name: 'Atlas' }, cost: 0.27 },
  { id: '#237', epic: 'EPIC-021', col: 'build', type: 'story', title: 'Webhook retry with exponential backoff and jitter', tags: ['infra'], pts: 3, priority: 'med', agent: { id: 'E7', role: 'eng', name: 'Rivet' }, cost: 0.38, live: true, progress: 82, branch: 'ws/retry-jitter', autoNext: 'PR ready in ~3m' },
  { id: 'PR #231', epic: 'EPIC-021', col: 'review', type: 'story', title: 'Session manager cleanup and idle eviction', tags: ['infra'], pts: 3, priority: 'low', agent: { id: 'R5', role: 'rev', name: 'Lattice' }, cost: 0.21 },
  { id: 'PR #228', epic: 'EPIC-021', col: 'done', type: 'story', title: 'Log rotation cap on CSV', tags: ['infra'], pts: 3, priority: 'med', agent: { id: 'E4', role: 'eng', name: 'Forge' }, cost: 0.14, doneAt: '47m ago' },
  { id: 'PR #225', epic: 'EPIC-021', col: 'done', type: 'bug', title: 'Connection pool exhaustion fix', tags: ['bug','infra'], pts: 2, priority: 'high', agent: { id: 'E7', role: 'eng', name: 'Rivet' }, cost: 0.09, doneAt: '3h ago' },

  { id: 'PR #237b', epic: 'EPIC-022', col: 'review', type: 'story', title: 'DLQ routing for failed webhook events', tags: ['infra'], pts: 5, priority: 'med', agent: { id: 'XD', role: 'human', name: 'Xiaodong' }, humanGate: true, gateReason: 'Schema migration in /db/migrations/', cost: 0.41 },
  { id: 'PR #226', epic: 'EPIC-022', col: 'done', type: 'task', title: 'Dashboard metrics card refactor', tags: ['ui'], pts: 2, priority: 'low', agent: { id: 'E7', role: 'eng', name: 'Rivet' }, cost: 0.06, doneAt: '12m ago' },

  { id: '#828', epic: 'EPIC-026', col: 'backlog', type: 'task', title: 'CAU node ordering for 6-node clusters', tags: ['infra'], pts: 5, priority: 'med', agent: null, cost: 0 },
  { id: '#825', epic: 'EPIC-026', col: 'backlog', type: 'bug', title: 'WMI quota violation telemetry', tags: ['bug','infra'], pts: 3, priority: 'low', agent: null, cost: 0 },
  { id: '#822', epic: 'EPIC-026', col: 'design', type: 'story', title: 'Log rotation policy schema', tags: ['infra'], pts: 5, priority: 'med', agent: { id: 'A2', role: 'arch', name: 'Mercer' }, cost: 0.23, live: true, progress: 41 },
  { id: '#809', epic: 'EPIC-026', col: 'build', type: 'bug', title: 'Cluster log rotation patch', tags: ['bug','infra'], pts: 8, priority: 'high', agent: { id: 'E9', role: 'eng', name: 'Anvil' }, cost: 0.18, blocked: true, blockedReason: 'Waiting on schema review' },

  { id: 'PR #218', epic: 'EPIC-018', col: 'done', type: 'story', title: 'Resumable downloads on CSV', tags: ['infra'], pts: 8, priority: 'high', agent: { id: 'E4', role: 'eng', name: 'Forge' }, cost: 0.78, doneAt: '6d ago' },
  { id: 'PR #220', epic: 'EPIC-018', col: 'done', type: 'task', title: 'BITS chunk size telemetry', tags: ['infra'], pts: 2, priority: 'low', agent: { id: 'E7', role: 'eng', name: 'Rivet' }, cost: 0.08, doneAt: '5d ago' },
];

export const columns: Column[] = [
  { id: 'backlog', name: 'Backlog', wipLimit: 20 },
  { id: 'design',  name: 'Design',  wipLimit: 6 },
  { id: 'build',   name: 'Build',   wipLimit: 8 },
  { id: 'review',  name: 'Review',  wipLimit: 6 },
  { id: 'done',    name: 'Done',    wipLimit: null },
];

export const workstreams: Workstream[] = [
  { id: 'ws-oidc',          name: 'oidc-rotation',    branch: 'ws/oidc-rotation',     base: 'main', epic: 'EPIC-024', status: 'active',   worktree: ['C:\\forge\\atlas\\wt', 'oidc-rotation'],    created: -3,   lastActive: -0.01,  agents: ['E4'],            prs: ['#234'],           cards: ['#234'],            commits: 12, ahead: 8,  behind: 0, conflicts: 0, summary: 'Rotates OIDC signing keys on a 24h cadence with 60s grace window. Live build in flight.', barStart: -3,   barEnd: 1.5 },
  { id: 'ws-rate-limits',   name: 'rate-limits',      branch: 'ws/rate-limits',       base: 'main', epic: 'EPIC-024', status: 'active',   worktree: ['C:\\forge\\atlas\\wt', 'rate-limits'],     created: -2,   lastActive: -0.05,  agents: ['E7'],            prs: ['#241'],           cards: ['#241'],            commits: 6,  ahead: 4,  behind: 1, conflicts: 0, summary: 'Per-tenant sliding-window rate limit policy. Implementation 28% — tests not yet written.', barStart: -2,   barEnd: 4 },
  { id: 'ws-idp-failover',  name: 'idp-failover',     branch: 'ws/idp-failover',      base: 'main', epic: 'EPIC-024', status: 'review',   worktree: ['C:\\forge\\atlas\\wt', 'idp-failover'],    created: -8,   lastActive: -0.2,   agents: ['E9','R3'],       prs: ['#240'],           cards: ['PR #240'],         commits: 23, ahead: 18, behind: 0, conflicts: 0, summary: 'Microsoft Entra fail-over path. Awaiting human gate (touches /security/).', barStart: -8,   barEnd: 0.2 },
  { id: 'ws-retry-jitter',  name: 'retry-jitter',     branch: 'ws/retry-jitter',      base: 'main', epic: 'EPIC-021', status: 'active',   worktree: ['C:\\forge\\atlas\\wt', 'retry-jitter'],    created: -1,   lastActive: -0.005, agents: ['E7'],            prs: ['#237'],           cards: ['#237'],            commits: 5,  ahead: 5,  behind: 0, conflicts: 0, summary: 'Webhook retry with exponential backoff and jitter. Tests passing — opening PR shortly.', barStart: -1,   barEnd: 0.5 },
  { id: 'ws-dlq',           name: 'dlq-routing',      branch: 'ws/dlq-routing',       base: 'main', epic: 'EPIC-022', status: 'review',   worktree: ['C:\\forge\\atlas\\wt', 'dlq-routing'],     created: -7,   lastActive: -0.5,   agents: ['E7','R3','XD'],  prs: ['#237b'],          cards: ['PR #237b'],        commits: 19, ahead: 0,  behind: 2, conflicts: 1, summary: 'Dead-letter routing for failed webhook events. Schema migration → human gate. Has merge conflict in /db/migrations/.', barStart: -7,   barEnd: 0 },
  { id: 'ws-tenant-admin',  name: 'tenant-admin-ui',  branch: 'ws/tenant-admin-ui',   base: 'main', epic: 'EPIC-024', status: 'active',   worktree: ['C:\\forge\\atlas\\wt', 'tenant-admin-ui'], created: -0.5, lastActive: -0.02,  agents: ['E4'],            prs: [],                 cards: ['#243'],            commits: 1,  ahead: 1,  behind: 0, conflicts: 0, summary: 'Tenant admin UI scaffold. Just spawned — agent walking the design.', barStart: -0.5, barEnd: 3 },
  { id: 'ws-log-rotation',  name: 'log-rotation-cap', branch: 'ws/log-rotation-cap',  base: 'main', epic: 'EPIC-026', status: 'planning', worktree: ['(not yet created)'],                       created: 0,    lastActive: 0,      agents: ['A2'],            prs: [],                 cards: ['#822','#809'],     commits: 0,  ahead: 0,  behind: 0, conflicts: 0, summary: 'Schema design phase. Worktree spawns when design approved.', barStart: 0.5,  barEnd: 5, planned: true },
  { id: 'ws-bits-cleanup',  name: 'bits-cleanup',     branch: 'ws/bits-cleanup',      base: 'main', epic: 'EPIC-018', status: 'merging',  worktree: ['C:\\forge\\atlas\\wt', 'bits-cleanup'],    created: -6,   lastActive: -1,     agents: ['R3'],            prs: ['#218','#220'],    cards: ['PR #218','PR #220'], commits: 22, ahead: 0, behind: 0, conflicts: 0, summary: 'Final cleanup commits for the BITS epic. Both PRs merged — pruning worktree.', barStart: -6,   barEnd: -0.5 },
];

export const wsEvents: WsEvent[] = [
  { ws: 'ws-oidc',         day: -2,   kind: 'pr',     label: 'PR #234' },
  { ws: 'ws-rate-limits',  day: -1,   kind: 'pr',     label: 'PR #241' },
  { ws: 'ws-idp-failover', day: -3,   kind: 'pr',     label: 'PR #240' },
  { ws: 'ws-idp-failover', day: -0.5, kind: 'review', label: 'Human gate' },
  { ws: 'ws-retry-jitter', day: 0.3,  kind: 'pr',     label: 'PR #237' },
  { ws: 'ws-dlq',          day: -3,   kind: 'pr',     label: 'PR #237b' },
  { ws: 'ws-dlq',          day: -0.6, kind: 'block',  label: 'Conflict' },
  { ws: 'ws-bits-cleanup', day: -1,   kind: 'merge',  label: 'merged' },
];

export const prs: Pr[] = [
  { id: 'PR #234', col: 'build',          epic: 'EPIC-024', epicColor: 'epic024', workstream: 'ws/oidc-rotation',    base: 'main', branch: 'ws/oidc-rotation',     title: 'OIDC token rotation handler with key rollover',    author: { id: 'E4', role: 'eng', name: 'Forge' }, additions: 287, deletions: 41,  files: 8,  commits: 12, age: '3h',     build: { status: 'running', step: 'Tests · 98 / 142',     percent: 68 },  review: null, conflicts: false, humanGate: false },
  { id: 'PR #241', col: 'implementation', epic: 'EPIC-024', epicColor: 'epic024', workstream: 'ws/rate-limits',      base: 'main', branch: 'ws/rate-limits',       title: 'Per-tenant rate limit policy with sliding window', author: { id: 'E7', role: 'eng', name: 'Rivet' }, additions: 102, deletions: 8,   files: 4,  commits: 6,  age: '1h',     build: { status: 'queued', step: 'queued behind 1', percent: 0 }, review: null, conflicts: false, humanGate: false, impl: { phase: 'writing tests', step: 'sliding-window store', percent: 28 } },
  { id: 'PR #243', col: 'implementation', epic: 'EPIC-024', epicColor: 'epic024', workstream: 'ws/tenant-admin-ui',  base: 'main', branch: 'ws/tenant-admin-ui',   title: 'Tenant admin UI scaffold (just spawned)',           author: { id: 'E4', role: 'eng', name: 'Forge' }, additions: 18,  deletions: 0,   files: 2,  commits: 1,  age: '12m',    build: null, review: null, conflicts: false, humanGate: false, impl: { phase: 'reading spec', step: 'route layout', percent: 8 } },
  { id: 'PR #237', col: 'build',          epic: 'EPIC-021', epicColor: 'epic021', workstream: 'ws/retry-jitter',     base: 'main', branch: 'ws/retry-jitter',      title: 'Webhook retry — exponential backoff + jitter',     author: { id: 'E7', role: 'eng', name: 'Rivet' }, additions: 64,  deletions: 22,  files: 3,  commits: 5,  age: '40m',    build: { status: 'running', step: 'Tests · 142 / 142', percent: 92 }, review: { agent: 'R3', name: 'Cortex',  verdict: 'approved', summary: 'Backoff math is sound. Verified jitter caps at 30s. Approved.' }, conflicts: false, humanGate: false },
  { id: 'PR #240', col: 'review',         epic: 'EPIC-024', epicColor: 'epic024', workstream: 'ws/idp-failover',     base: 'main', branch: 'ws/idp-failover',      title: 'Add IdP fail-over to Microsoft Entra',             author: { id: 'E9', role: 'eng', name: 'Anvil' }, additions: 412, deletions: 76,  files: 19, commits: 23, age: '6h',     build: { status: 'passing', step: 'CI green · 3m 12s', percent: 100 }, review: { agent: 'R3', name: 'Cortex',  verdict: 'changes',  summary: 'Token cache TTL needs to handle clock skew. Suggested 30s buffer in <span class="hl">authcache.go:142</span>.' }, conflicts: false, humanGate: true, gateReason: '/security/ path · human approval required' },
  { id: 'PR #229', col: 'review',         epic: 'EPIC-024', epicColor: 'epic024', workstream: 'ws/auth-core',        base: 'main', branch: 'ws/auth-core-extract', title: 'Extract token store to auth-core package',          author: { id: 'E4', role: 'eng', name: 'Forge' }, additions: 198, deletions: 234, files: 12, commits: 7,  age: '2h',     build: { status: 'passing', step: 'CI green · 2m 41s', percent: 100 }, review: { agent: 'R3', name: 'Cortex',  verdict: 'approved', summary: 'Clean refactor. No behavioural changes detected by trace diff. Auto-merge armed.' }, conflicts: false, humanGate: false, autoMerge: true },
  { id: 'PR #237b', col: 'review',        epic: 'EPIC-022', epicColor: 'epic022', workstream: 'ws/dlq-routing',      base: 'main', branch: 'ws/dlq-routing',       title: 'DLQ routing for failed webhook events',            author: { id: 'E7', role: 'eng', name: 'Rivet' }, additions: 156, deletions: 31,  files: 7,  commits: 19, age: '8h',     build: { status: 'passing', step: 'CI green · 4m 02s', percent: 100 }, review: { agent: 'R3', name: 'Cortex',  verdict: 'changes',  summary: 'Schema migration touches live tables. Recommend two-phase migration (add column nullable → backfill → enforce).' }, conflicts: true, conflictPaths: 'db/migrations/2026_03_dlq.sql', humanGate: true, gateReason: 'Schema migration · human gate' },
  { id: 'PR #231', col: 'build',          epic: 'EPIC-021', epicColor: 'epic021', workstream: 'ws/session-cleanup',  base: 'main', branch: 'ws/session-cleanup',   title: 'Session manager cleanup + idle eviction',          author: { id: 'E7', role: 'eng', name: 'Rivet' }, additions: 84,  deletions: 51,  files: 4,  commits: 9,  age: '5h',     build: { status: 'failing', step: 'TestIdleEviction.fail', percent: 60, failingTest: 'TestIdleEviction' }, review: { agent: 'R5', name: 'Lattice', verdict: 'pending', summary: 'Waiting on green build before reviewing.' }, conflicts: false, humanGate: false },
  { id: 'PR #244', col: 'implementation', epic: 'EPIC-026', epicColor: 'epic026', workstream: 'ws/log-rotation-cap', base: 'main', branch: 'ws/log-rotation-cap',  title: 'Cluster log rotation cap implementation',          author: { id: 'E9', role: 'eng', name: 'Anvil' }, additions: 0,   deletions: 0,   files: 0,  commits: 0,  age: 'queued', build: null, review: null, conflicts: false, humanGate: false, blocked: true, impl: { phase: 'blocked', step: 'Waiting on schema review · #822', percent: 0 } },
];

export const wsStats: WsStat[] = [
  { label: 'Active workstreams', val: '5',     unit: 'of 8',        trend: 'up',   trendVal: '+2 this sprint' },
  { label: 'Open PRs',           val: '9',     unit: 'open',        trend: 'flat', trendVal: '2 awaiting approval' },
  { label: 'Merged today',       val: '3',     unit: 'PRs',         trend: 'up',   trendVal: '+1 vs avg' },
  { label: 'Total cost · 24h',   val: '$4.81', unit: '/$7.50 cap',  trend: 'down', trendVal: '64% of budget' },
];

export const overviewActivity: OverviewActivity[] = [
  { time: '12:48', who: 'CI',          msg: '<span class="ref">PR #228</span> merged · log rotation cap shipped' },
  { time: '12:31', who: 'E-04 Forge',  msg: 'pushed 3 commits to <span class="ref">ws/oidc-rotation</span>' },
  { time: '12:18', who: 'R-03 Cortex', msg: 'flagged <span class="ref">PR #240</span> for human review · /security/ path' },
  { time: '11:52', who: 'A-02 Mercer', msg: 'drafted <span class="ref">ADR-021</span> · log rotation policy schema' },
  { time: '11:09', who: 'E-07 Rivet',  msg: 'opened <span class="ref">PR #241</span> · per-tenant rate limit' },
  { time: '10:45', who: 'ADO sync',    msg: 'imported 3 work items from sprint 14 backlog' },
  { time: '10:12', who: 'XD Xiaodong', msg: 'spawned workstream <span class="ref">ws/tenant-admin-ui</span>' },
  { time: '09:38', who: 'E-04 Forge',  msg: 'created branch <span class="ref">ws/tenant-admin-ui</span> off main' },
];

// Agent crew themed after characters from the Ne Zha movie. Image assets
// live under src/assets/crews/ and are imported via Vite in src/crew.ts
// so they're hashed + bundled like any other static asset.
import { crew } from './crew';
export const overviewAgents = crew;

export const sampleState: ForgeState = {
  epics,
  cards,
  columns,
  workstreams,
  wsEvents,
  prs,
  wsStats,
  overviewActivity,
  overviewAgents,
};
