// Phase 13 — every crew member listed in `agents/allocation.json` resolves
// to a bundled portrait under `src/assets/crews`. We use Vite's `import.meta.glob`
// (eager) so the bundler statically inlines every PNG and we get a O(1)
// lookup keyed by either character id (NZ, AB, …) or display name.
//
// Eight legacy prototype ids (A1, A2, E4, E7, E9, R3, R5, Q2, XD) alias to
// the matching character files so the original Forge cards / PRs that still
// carry those ids still get a face.

import { AGENT_ALLOCATION, type AllocationEntry } from '@/agentAllocation';

const ALLOCATION = AGENT_ALLOCATION;

// Vite glob — all PNGs under crews/. Keys are file paths; values are URLs.
const PNG_URLS = import.meta.glob<{ default: string }>(
  '@/assets/crews/*.png',
  { eager: true },
);

function urlFor(filename: string | null | undefined): string | null {
  if (!filename) return null;
  const match = Object.entries(PNG_URLS).find(([k]) => k.endsWith('/' + filename));
  return match ? match[1].default : null;
}

const BY_ID: Record<string, string> = {};
const BY_NAME: Record<string, string> = {};

for (const e of ALLOCATION) {
  const url = urlFor(e.avatar);
  if (!url) continue;
  BY_ID[e.id] = url;
  BY_NAME[e.name] = url;
}

// Operator portrait — `xiaodong_small.png` lives outside the indexed manifest.
const XD_URL = urlFor('xiaodong_small.png');
if (XD_URL) {
  BY_ID['XD'] = XD_URL;
  BY_NAME['Xiaodong'] = XD_URL;
}

// Legacy prototype ids → character ids (kept in sync with ForgeSeed's
// `prototypeAlias`). Lets old cards/PRs that still carry A1/E4/… render the
// matching face instead of the text chip.
const PROTOTYPE_ALIAS: Record<string, string> = {
  A1: 'YS',
  A2: 'WZ',
  E4: 'NZ',
  E7: 'AB',
  E9: 'ZR',
  R3: 'SG',
  R5: 'YX',
  Q2: 'SF',
};

for (const [proto, charId] of Object.entries(PROTOTYPE_ALIAS)) {
  if (BY_ID[charId] && !BY_ID[proto]) BY_ID[proto] = BY_ID[charId];
}

/** Returns the bundled portrait for an agent, or null if no match. */
export function avatarFor(id: string | null | undefined, name?: string | null): string | null {
  if (id && BY_ID[id]) return BY_ID[id];
  if (name && BY_NAME[name]) return BY_NAME[name];
  return null;
}

// Capability tags + role-derived model — kept inline so a hover tooltip on
// any chip can render `id · role · model · capabilities` without an API call.

const CAPABILITIES_BY_BUCKET: Record<string, string[]> = {
  'leadership-product':   ['roadmap', 'human-gate sign-off', 'crew supervision'],
  'leadership-factory':   ['agent orchestration', 'workflow design', 'reliability'],
  'leadership-principal': ['mentoring', 'review of critical AI output', 'architecture'],
  'leadership-incident':  ['incident command', 'recovery', 'post-mortem'],
  'leadership-security':  ['access control', 'supply chain', 'audit trails'],
  'leadership-devops':    ['CI/CD', 'cost controls', 'observability'],
  'leadership-prompt':    ['prompt systems', 'eval harness', 'failure tuning'],
  'leadership-qa':        ['validation pipelines', 'adversarial tests', 'regression'],

  'plan-architect-generator': ['architecture drafts', 'phase→stage→step skeleton'],
  'plan-red-team':            ['adversarial review', 'attack scenarios'],
  'plan-refinement':          ['merge drafts + findings', 'final spec'],
  'plan-requirements-judge':  ['rubric scoring', 'completeness gate'],
  'plan-design-historian':    ['retired patterns', 'prior incidents'],
  'plan-orchestrator':        ['planning loop', 'budget envelope'],

  'code-generator':          ['feature dev', 'PR-sized step', 'tests-in-diff'],
  'code-breaker':            ['logic flaws', 'race conditions', 'security bypass'],
  'code-unit-test-attacker': ['negative paths', 'boundary tests'],
  'code-refactor-defender':  ['move vs change split', 'churn audit'],
  'code-judge':              ['merge gate', 'maintainability score'],
  'code-memory-agent':       ['bug-pattern index', 'prior-fix surfacing'],

  'ai-requirements-analyst':    ['acceptance criteria', 'spec sharpening'],
  'ai-ux-designer':             ['flows', 'micro-copy', 'states'],
  'ai-systems-architect':       ['ADR backlog', 'deprecation register'],
  'ai-backend-engineer':        ['services', 'persistence', 'observability'],
  'ai-frontend-engineer':       ['React', 'a11y', 'keyboard'],
  'ai-test-engineer':           ['regression suite', 'matrix run'],
  'ai-security-reviewer':       ['threat model', 'auth review'],
  'ai-code-reviewer':           ['readability', 'naming', 'idiomatic style'],
  'ai-release-manager':         ['changelog', 'release branch', 'rollback'],
  'ai-sre':                     ['SLOs', 'alerts', 'runbooks'],
  'ai-customer-support':        ['triage', 'reproduce', 'classify'],
  'ai-root-cause-investigator': ['timelines', 'five whys', 'follow-ups'],
  'ai-doc-writer':              ['runnable examples', 'README', 'runbook prose'],

  'reserve': ['floats between pods', 'cross-team spikes'],
};

const MODEL_BY_ROLE: Record<string, string> = {
  arch:            'claude-opus-4-7',
  eng:             'claude-opus-4-7',
  rev:             'claude-sonnet-4-6',
  qa:              'claude-haiku-4-5',
  'product-owner': 'human',
  human:           'human',
};

function roleFromId(id: string): string {
  switch (id.charAt(0).toUpperCase()) {
    case 'A': return 'arch';
    case 'E': return 'eng';
    case 'R': return 'rev';
    case 'Q': return 'qa';
    default:  return 'human';
  }
}

const ALLOC_BY_ID: Record<string, AllocationEntry> = Object.fromEntries(
  ALLOCATION.map((e) => [e.id, e]),
);
// Prototype ids inherit the aliased entry's bucket for tooltips.
for (const [proto, charId] of Object.entries(PROTOTYPE_ALIAS)) {
  if (!ALLOC_BY_ID[proto] && ALLOC_BY_ID[charId]) ALLOC_BY_ID[proto] = ALLOC_BY_ID[charId];
}

/**
 * Multi-line tooltip for an agent chip: id · name on the first line, role +
 * model on the second, capabilities on the third. Plain `\n` newlines
 * because `<img title>` only renders text.
 */
export function agentTooltip(
  id: string | null | undefined,
  name?: string | null,
  role?: string | null,
  soulPath?: string | null,
): string {
  // Provider mappers can hand us a null id (anonymous reviewers, deactivated
  // accounts, bots without descriptors). Render with name only and 'human'
  // role rather than crashing on roleFromId / ALLOC_BY_ID lookups.
  const safeId = id ?? '';
  const r = role ?? (safeId ? roleFromId(safeId) : 'human');
  const model = MODEL_BY_ROLE[r] ?? 'unknown';
  const entry = safeId ? ALLOC_BY_ID[safeId] : undefined;
  const caps = entry ? CAPABILITIES_BY_BUCKET[entry.role_bucket] : undefined;
  const head = safeId
    ? `${safeId}${name ? ' · ' + name : ''}`
    : (name ?? 'unknown');
  const meta = entry?.role_label
    ? `${entry.role_label} · ${model}`
    : `${r} · ${model}`;
  const lines = [head, meta];
  if (caps && caps.length > 0) lines.push(caps.join(' · '));
  if (soulPath) lines.push(`soul: ${soulPath}`);
  return lines.join('\n');
}

/** Exposed so the new CrewsView can iterate every member. */
export function listAllocation(): AllocationEntry[] {
  return ALLOCATION;
}

/**
 * Resolve an agent id to display name + crew role + default model. Used by
 * tooltips that show "(name) — model" pairs without making an API call —
 * for example the per-rectangle tooltip on the story-delivery strip
 * (`SwarmStoryRow`). Falls back to plausible defaults when the id is
 * unknown (legacy / prototype / deactivated agents). Returns null when no
 * id is supplied at all (caller decides whether to render an "unassigned"
 * placeholder).
 */
export function agentInfo(id: string | null | undefined): {
  id: string;
  name: string;
  role: string;
  model: string;
} | null {
  if (!id) return null;
  const entry = ALLOC_BY_ID[id];
  const role = entry?.crew_role ?? roleFromId(id);
  const model = MODEL_BY_ROLE[role] ?? 'unknown';
  return {
    id,
    name: entry?.name ?? id,
    role,
    model,
  };
}
