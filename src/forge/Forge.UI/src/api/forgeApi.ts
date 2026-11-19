import { operatorFetch } from '@/api/operatorSession';
import type {
  AgentDetail,
  AgentLevelHistoryEntry,
  Card,
  ColumnId,
  CostSummary,
  Crew,
  CrewConcurrencySummary,
  CrewHistoryEntry,
  CrewPayroll,
  CrewSalary,
  CurrentUser,
  Deliverable,
  DeliverableDisputeRequest,
  DiscoverableRepo,
  DiscoveryNode,
  DomainExpert,
  Epic,
  ForgeState,
  ImportNodesRequest,
  IterationBindingRequest,
  ModelInfo,
  Pr,
  Product,
  ProductAreaPath,
  ProductIteration,
  ProductStats,
  ProductStory,
  StoryAttachment,
  PayrollTodaySummary,
  PromotionRunResponse,
  Repo,
  SalaryTodaySummary,
  SavePrefixRequest,
  SavedPrefix,
  SoulPayload,
  SprintDiscoveryRequest,
  SyncRunItem,
  SyncRunItemDetail,
  TaskQueueEntry,
  CrewQueueStats,
  TimelineResponse,
  UserSettings,
  AgentProviderSettings,
  Workstream,
  WorkItemSyncRun,
} from '@/types';
import { sampleState } from '@/sampleData';
import type {
  WorkItem,
  WorkItemAssignee,
  WorkItemPriority,
  WorkItemState,
  WorkItemTrack,
  WorkItemType,
} from '@/workitems/workItemTypes';

// Phase 91 — operator-facing rationale for an open question, shown in the
// Questions-tab "Explain" popup. Agent-authored (parsed from the open-questions
// block) or generated on demand. All fields optional — older / partial sources
// may carry only some.
export interface QuestionChoiceAnalysis {
  /** The option text (verbatim). */
  choice: string;
  /** Concise reasons for picking this option. */
  pros: string[];
  /** Concise reasons against picking this option. */
  cons: string[];
}

export interface QuestionExplanation {
  /** Plain-language explanation of the question + any domain terms. */
  explanation?: string;
  /** Preferred per-choice pros/cons (rendered as a table). */
  choiceAnalysis?: QuestionChoiceAnalysis[];
  /** Legacy per-choice logic, keyed by the choice text (back-compat). */
  choiceExplanations?: Record<string, string>;
  /** The agent's recommended choice (verbatim) or suggested free-form answer. */
  recommendation?: string;
  /** Why the recommendation. */
  recommendationRationale?: string;
  /** True when produced by on-demand generation rather than the authoring agent. */
  generated?: boolean;
}

// forge_implementation.md Phase 28 — story spawn types.
export type StorySpawnPhase = 'plan' | 'decompose' | 'implement' | 'review' | 'qa' | 'done' | 'paused';

// Phase 92 — human operator steering prompt for a workstream.
export interface OperatorPrompt {
  id: string;
  workstreamId: string;
  body: string;
  createdAt: string;
  createdBy: string | null;
  dismissedAt: string | null;
}

export interface StorySpawn {
  id: string;
  /** Explicit ownership from the list response, including the all-products view. */
  productId?: string | null;
  chatSummary?: import('../swarm/plugins/chatApi').ChatSummary;
  storyId: string;
  storyTitle: string;
  repoId: string | null;
  phase: StorySpawnPhase;
  /** Phase 45 — operator-chosen base branch for every PR this spawn opens
   *  (plan, workstream, QA, step-def). Defaults to "main". When non-default
   *  the orchestrator branches it from main on first drive. */
  baseBranch: string;
  /** Phase 46 — when true, agents auto-merge PRs against the base branch.
   *  Honoured for any base (including "main"); operator-armed. */
  autoMerge?: boolean;
  planBranch: string | null;
  planPrId: string | null;
  decompositionJson: string | null;
  decompositionApproved: boolean;
  qaBranch: string | null;
  qaPrId: string | null;
  createdAt: string;
  updatedAt: string;
  /** Phase 37 — operator-visible failure surfaced on the workstream banner.
   *  Cleared by the orchestrator on the next successful step or by an
   *  explicit POST /api/stories/{storyId}/spawn/retry. */
  lastError?: string | null;
  lastErrorAt?: string | null;
  /** Phase 43 — operator-uploaded reference documents. */
  attachments?: StoryAttachment[];
  /** Phase 53 — per-doc PRs for sequential plan-doc spawns.
   *  Null on legacy (single-PR) spawns. UI prefers this over planPrId. */
  planDocPrs?: { key: string; branch: string | null; prId: string | null }[] | null;
  /** Phase 63 — the phase the spawn was in before the operator paused it.
   *  Null unless phase === 'paused'. */
  pausedFromPhase?: string | null;
}

export interface StorySpawnWorkstreamSummary {
  id: string;
  name: string;
  branch: string;
  status: string;
  cards: string[];
  prs: string[];
  deps: string[];
  /**
   * Phase 49 — `phase-{slug}` prefix of the originating Stage's PlanAnchor.
   * The swarm DAG view groups stage cards into collapsible phase containers
   * keyed on this. Null / undefined on legacy rows that predate Phase 47
   * (workstreams.PhaseAnchor column).
   */
  phaseAnchor?: string | null;
  /**
   * Phase 49 — full `phase-{slug}/stage-{slug}` plan anchor. Used by the UI
   * to derive the stage's display label without re-parsing the plan.
   */
  planAnchor?: string | null;
  /** Phase 28+ — live runtime fields surfaced from the deliverables /
   *  agent_usage tables. All optional so older clients keep working and
   *  workstreams that haven't claimed yet (planning) report zeroes. */
  agents?: string[];
  iter?: number;
  latestScore?: number | null;
  latestVerdict?: string | null;
  latestGenAgentId?: string | null;
  latestEvalAgentId?: string | null;
  // Full gen/eval pair history (oldest first, consecutive-deduped). >1 entry
  // means the workstream was reassigned mid-run — the swarm card shows the
  // prior pairs faded before the current one so the switches are visible.
  agentPairs?: Array<{ gen: string | null; eval: string | null }>;
  lastActiveAt?: string | null;
  /** Phase 28+ — ISO timestamp of the workstream's first deliverable (when the
   *  gen↔eval loop started). Null until the first iteration runs. Surfaced in
   *  the swarm phase-board metrics row alongside iter / score / cost. */
  startedAt?: string | null;
  costUsd?: number;
  /** Local per-iteration build / test gate outcomes (`passed` | `failed` |
   *  `skipped` | `resolving` | `resolved`) from the workstream's most recent
   *  gate events — the same gate the Conversation Build tab shows. Null/absent
   *  until the gate runs. Drive the BUILD / TEST badges in the metrics row. */
  buildGate?: string | null;
  testGate?: string | null;
  commits?: number;
  ahead?: number;
  behind?: number;
  /** Unanswered open questions blocking pass — 0 when no questions exist. */
  unansweredQuestionCount?: number;
  /** Total questions raised on the workstream (answered + unanswered), deduped
   *  by ExternalId. Drives the neutral questions badge; 0 when none raised. */
  questionCount?: number;
  /**
   * Phase 49 — when {@link status} is `'stalled'`, the operator-facing
   * description of the failure that landed the row in triage (build
   * gate, no-progress, non-pass verdict). Null on every other status.
   * Surfaced as a tooltip on the stalled badge.
   */
  stallReason?: string | null;
  /**
   * Phase 50 — short worktree folder name under `{cloneRoot}/.worktree/`
   * (e.g. `stage-1.1-cargo-workspace-and-crate-layout` or legacy
   * `spawn-{branch-slug}`). Computed server-side via
   * `WorkstreamWorktreeSlug.Compute`. Surfaced in the story-delivery
   * strip tooltip so the operator can locate the directory the engineer
   * crew is editing.
   */
  worktreeFolder?: string | null;
  /**
   * Phase 50 — step-definition PR (`ws/{storyId}/{slug}-steps`). Opened
   * by the orchestrator AFTER the QA PR merges, separate from the impl
   * PR. Null until the orchestrator creates it. `stepDefMerged` is true
   * once the PR's `Col` reaches `"merged"` — `status === 'complete'`
   * alone is not a faithful signal because workstreams flip to
   * `complete` when the IMPL PR merges, before step-defs exist.
   */
  stepDefPrId?: string | null;
  stepDefBranch?: string | null;
  stepDefMerged?: boolean;
  /**
   * docs/forge/e2e.md Stage 8.1 — workstream category discriminator
   * (`"impl"` | `"qa-e2e"`). Drives the phase-board's stacked-row
   * rendering: `qa-e2e` rows are stacked beneath their impl sibling
   * (matched by stripping the `/e2e` suffix from `planAnchor`) instead
   * of rendering as standalone phase cards. Backend default for
   * legacy rows that predate Phase 79Migration is `"impl"`.
   */
  category?: string | null;
}

export interface StorySpawnDetail {
  spawn: StorySpawn;
  workstreams: StorySpawnWorkstreamSummary[];
  /**
   * Phase 50 — true when {@link StorySpawn.qaPrId} is set AND its PR row
   * reached the terminal `"merged"` state. Surfaced as a top-level
   * sibling (not inside `spawn`) because the PR lookup is only done by
   * the spawn-detail endpoint, not every place that produces a
   * StorySpawn contract.
   */
  qaPrMerged?: boolean;
}

export interface StorySpawnDecomposeWorkstream {
  title: string;
  description?: string;
  acceptanceCriteria?: string[];
  deps?: string[];
  deliverables?: string[];
  timeline?: string;
  estimatedEffort?: string;
  /**
   * Phase 46 — cached `phase-{slug}` prefix of the originating Stage's
   * PlanAnchor. Used by the swarm DAG view to group stage cards into
   * collapsible phase containers without re-parsing the plan. Null /
   * undefined on legacy LLM-only fallback rows; in that case the UI
   * groups every card under a single "Workstreams" container.
   */
  phaseAnchor?: string;
  /**
   * Phase 46 — anchor refs from the plan that didn't resolve to any
   * known phase / stage. Surfaced as a yellow "unresolved" banner on
   * the offending card.
   */
  unresolvedDeps?: string[];
}

export interface StorySpawnArtifacts {
  plan: {
    branch: string | null;
    pr: string | null;
    /** Phase 53 — per-doc PRs. Null on legacy spawns. */
    docs?: { key: string; branch: string | null; prId: string | null }[] | null;
    files: string[];
  };
  decompose: { approved: boolean; json: string | null };
  implement: { prs: string[]; workstreams: string[] };
  review: { prs: string[] };
  qa: { branch: string | null; pr: string | null; files: string[] };
}

export interface WorkItemListFilters {
  productId?: string;
  repoId?: string;
  iterationRef?: string;
  track?: WorkItemTrack;
  parentId?: string;
  assignedTo?: string;
  tag?: string;
  q?: string;
  states?: WorkItemState[];
  types?: WorkItemType[];
  page?: number;
  pageSize?: number;
}

export interface WorkItemCreateBody {
  id: string;
  track: WorkItemTrack;
  type: WorkItemType;
  title: string;
  repoId?: string | null;
  provider?: 'ado' | 'github' | 'local';
  providerRef?: number | null;
  state?: WorkItemState;
  descriptionMd?: string;
  parentId?: string | null;
  featureRef?: string | null;
  iterationRef?: string;
  areaPath?: string;
  assignedTo?: WorkItemAssignee | null;
  points?: number;
  priority?: WorkItemPriority;
  tags?: string[];
}

export interface WorkItemPatchBody {
  title?: string;
  descriptionMd?: string;
  state?: WorkItemState;
  iterationRef?: string;
  areaPath?: string;
  assignedTo?: WorkItemAssignee | null;
  clearAssignedTo?: boolean;
  points?: number;
  priority?: WorkItemPriority;
  tags?: string[];
  featureRef?: string | null;
  clearFeatureRef?: boolean;
  /** Phase 14 follow-up — re-attach to a different repo. */
  repoId?: string | null;
  clearRepoId?: boolean;
  /** Phase 14 follow-up — server auto-flips track per trackOf(). */
  type?: WorkItemType;
}

export interface WorkItemSyncBody {
  areaPath?: string | null;
  types?: WorkItemType[];
  assignedTo?: string | null;
  updatedFrom?: string | null;
  updatedTo?: string | null;
  ignoreThrottle?: boolean;
}

export interface WorkItemSyncResult {
  repoId: string;
  pulled: number;
  inserted: number;
  updated: number;
  skipped: number;
  error: string | null;
}

export interface ProviderHealth {
  kind: string;
  ok: boolean;
  message: string | null;
  lastSync: string | null;
  lastErrorCount: number;
  rateLimitRemaining: number | null;
  rateLimitLimit: number | null;
}

/** Phase 9 — interactive sign-in state for a provider. */
export interface AuthStatus {
  kind: string;
  connected: boolean;
  account: string | null;
  userName: string | null;
  expiresAt: string | null;
  error: string | null;
}

export interface AuditEntry {
  id: number;
  at: string;
  user: string;
  method: string;
  path: string;
  status: number;
  idempotencyKey: string | null;
  requestBodySnippet: string | null;
}

export interface HumanGateRules {
  branchSubstrings: string[];
  titleSubstrings: string[];
  labels: string[];
  pathGlobs: string[];
}

const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';

interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  // Allow server-side `extensions` to flow through (e.g. {conflict: true, hint}).
  [key: string]: unknown;
}

export class ForgeApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: ProblemDetails | null,
    message: string,
  ) {
    super(message);
    this.name = 'ForgeApiError';
  }
}

function useLocalSeed(): boolean {
  if (typeof window === 'undefined') return false;
  return new URLSearchParams(window.location.search).get('seed') === 'local';
}

/**
 * Identity used for the `X-Forge-User` header on every write. The server
 * looks the value up against `users.email | login | displayName | id`
 * (RequireRole.ResolveRole), so we must send something the DB row recognises
 * — otherwise role gates fall back to "human" and writes 403.
 *
 * Resolution order:
 *  1. Cached identity written by `setWriterIdentity` after `/api/me` resolves
 *     (the Phase 9 sign-in flow). This is the steady-state path.
 *  2. Manual `forge.user` localStorage override (handy for multi-user dev).
 *  3. The legacy `'forge-ui'` placeholder used pre-sign-in. The server only
 *     accepts this when the users table is empty (single-user bootstrap).
 */
let cachedWriterIdentity: string | null = null;

export function setWriterIdentity(identity: string | null): void {
  cachedWriterIdentity = identity && identity.trim().length > 0 ? identity.trim() : null;
}

function writerHeader(): string {
  if (cachedWriterIdentity) return cachedWriterIdentity;
  if (typeof window === 'undefined') return 'forge-ui';
  return window.localStorage.getItem('forge.user') ?? 'forge-ui';
}

/** Phase 8.8 — active product the UI is scoped to (TopBar switcher). */
const ACTIVE_PRODUCT_KEY = 'forge.activeProductId';

export function getActiveProductId(): string | null {
  if (typeof window === 'undefined') return null;
  return window.localStorage.getItem(ACTIVE_PRODUCT_KEY);
}

export function setActiveProductId(id: string | null): void {
  if (typeof window === 'undefined') return;
  if (id === null) window.localStorage.removeItem(ACTIVE_PRODUCT_KEY);
  else window.localStorage.setItem(ACTIVE_PRODUCT_KEY, id);
}

/** Phase 80.G — active repo the UI is scoped to (TopBar repo switcher).
 *  Sent as `X-Forge-Repo-Id`; the server narrows product-scoped reads to
 *  this repo and binds new stories/spawns to it. Null/empty → product-wide. */
const ACTIVE_REPO_KEY = 'forge.activeRepoId';

export function getActiveRepoId(): string | null {
  if (typeof window === 'undefined') return null;
  return window.localStorage.getItem(ACTIVE_REPO_KEY);
}

export function setActiveRepoId(id: string | null): void {
  if (typeof window === 'undefined') return;
  if (id === null) window.localStorage.removeItem(ACTIVE_REPO_KEY);
  else window.localStorage.setItem(ACTIVE_REPO_KEY, id);
}

/** Phase 80.G — pure builder for the scope headers attached to every request.
 *  Emits `X-Forge-Product-Id` / `X-Forge-Repo-Id` only when each id is set
 *  (empty/null is omitted so the server treats it as product-wide / all-repos).
 *  Exported so the header logic can be unit-tested without a DOM. */
export function buildScopeHeaders(
  productId: string | null,
  repoId: string | null,
): Record<string, string> {
  const headers: Record<string, string> = {};
  if (productId) headers['X-Forge-Product-Id'] = productId;
  if (repoId) headers['X-Forge-Repo-Id'] = repoId;
  return headers;
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const { headers: extraHeaders, ...rest } = init ?? {};
  // Phase 8.9 / 80.G — automatically scope every read to the active product
  // and (when selected) the active repo.
  const res = await operatorFetch(`${baseUrl}${path}`, {
    ...rest,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...buildScopeHeaders(getActiveProductId(), getActiveRepoId()),
      ...(extraHeaders ?? {}),
    },
  });
  if (!res.ok) {
    let problem: ProblemDetails | null = null;
    const ct = res.headers.get('content-type') ?? '';
    if (ct.includes('json')) {
      try {
        problem = (await res.json()) as ProblemDetails;
      } catch {
        // fall through
      }
    }
    const detail = problem?.detail ?? problem?.title ?? res.statusText;
    throw new ForgeApiError(res.status, problem, `${path}: ${res.status} ${detail}`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export interface MoveCardResponse {
  card: Card;
  policy: {
    allow: boolean;
    toast?: string | null;
    title?: string | null;
    text?: string | null;
    warn?: string | null;
    confirm?: boolean;
  };
}

export interface PrDetailsResponse {
  pr: Pr;
  description: string;
  buildLogTail: string[];
  reviewers: string[];
}

/** Phase 26 — result of POST /api/repos/{id}/detect-build. */
export interface DetectBuildResult {
  ecosystem: string;
  initCommand?: string | null;
  restoreCommand?: string | null;
  buildCommand?: string | null;
  testCommand?: string | null;
  ambiguous: boolean;
  ambiguityDetail?: string | null;
  current: {
    initCommand?: string | null;
    restoreCommand?: string | null;
    buildCommand?: string | null;
    testCommand?: string | null;
    buildTimeoutSec?: number | null;
    testTimeoutSec?: number | null;
  };
}

/** Phase 26/29 — result of POST /api/repos/{id}/validate-build (async). */
export interface ValidateBuildAccepted {
  runId: string;
  status: string;
}

/** Phase 29 — step result within a validation run. */
export interface ValidateStep {
  step: string;
  command: string;
  passed: boolean;
  exitCode: number;
  timedOut: boolean;
  stdout: string;
  stderr: string;
}

/** Phase 26 — validation result (used for display). */
export interface ValidateBuildResult {
  allPassed: boolean;
  ecosystem: string;
  steps: ValidateStep[];
}

/** Phase 59 Stage A — dry-run of the impacted-test resolver against HEAD of cloneRoot. */
export interface ImpactedTestPreview {
  repoId: string;
  cloneRoot: string;
  baseRef: string;
  testCommand: string;
  changedFileCount: number;
  rewritten: boolean;
  skipped: boolean;
  wholeSuiteFallback: boolean;
  command: string;
  impactedProjects: string[];
  allTestProjectCount: number;
  reason: string;
}

/** Phase 29 — persistent build validation run record. */
export interface BuildValidationRun {
  id: string;
  repoId: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  startedAt: string;
  finishedAt?: string | null;
  allPassed: boolean;
  ecosystem?: string | null;
  stepsJson?: string | null;
  triggeredBy?: string | null;
  errorMessage?: string | null;
}

// ── Performance scorecard types ──────────────────────────────────────

export type ReviewWindow = 'trailing20' | 'mtd' | 'lastMonth';
export type PromotionVerdict = 'promote' | 'hold' | 'watch' | 'demote' | 'lead-pending';

export interface ScoreDims {
  correctness: number;
  quality: number;
  tests: number;
  scope: number;
  risk: number;
}

export interface AgentScorecard {
  agentId: string;
  name: string;
  team: 'Architect' | 'Engineer' | 'Review' | 'QA';
  crew: string;
  crewId: string;
  level: number;
  title: string;
  hourlyUsd: number;
  throughput: number;
  passRate: number;
  avgScore: number;
  iterP50: number;
  iterP90: number;
  rejectRate: number;
  disputeRate: number;
  stalledRate: number;
  utilization: number;
  costPerPass: number;
  scoreSpark: number[];
  dims: ScoreDims;
  promotionEta: number | null;
  /**
   * Phase 89 — when verdict is `hold` and the only thing blocking promotion
   * is the tier cooldown, this is the number of days until the agent is
   * eligible again. 0 means cooldown is clear (or the agent has never been
   * promoted). The roster scorecard's Promo ETA column renders this as
   * "+{n} d" so operators stop wondering why a 100%-pass agent reads HOLD.
   */
  cooldownDaysRemaining: number;
  demotionRisk: boolean;
  verdict: PromotionVerdict;
  /**
   * Phase 89 — human-readable rationale for the verdict. For `hold`, this
   * lists every failing gate (cooldown, window-avg below threshold, rejects
   * in window) joined with ` · `. Used as the verdict pill's tooltip.
   */
  verdictNote: string;
  /**
   * Phase 89 — itemised hold reasons (same data as verdictNote, split for
   * rich rendering). Empty for non-hold verdicts.
   */
  holdReasons: string[];
  lastPromotedAt: string | null;
  nextEligibleAt: string;
  /** Phase 89 — date (yyyy-MM-dd) of the last ACTUAL level change
   *  (promotion or demotion, algorithmic or operator), or null if the
   *  agent has never moved. Drives the roster "Last change" column. */
  lastChangeAt: string | null;
  /** Direction of {@link lastChangeAt}: `'promote'` ▲ / `'demote'` ▼, or
   *  null when there has been no level change. */
  lastChangeKind: 'promote' | 'demote' | null;
  monthlyAvg: number[];
  monthlyMonths: string[];
  repos: string[];
  products: string[];
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
  /**
   * Phase 89 — server-derived verdict (tier-aware). The UI must trust this
   * instead of re-deriving from `windowAvg`, because the promotion threshold
   * varies by current level (88 for L0-L2 easy tier, 92 for L3-L6 standard,
   * higher for L7+ hard tier). Set by PerformanceEndpoints.cs.
   */
  verdict: PromotionVerdict;
  nominatedAt: string;
}

export interface MonthlySnapshot {
  month: string;
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

export interface PerformanceFilterOptions {
  products: Array<{ id: string; name: string }>;
  repos: Array<{ id: string; productId: string }>;
  teams: string[];
}

export interface PerformanceScorecardsResponse {
  window: string;
  scorecards: AgentScorecard[];
  promotionDecisions: PromotionDecision[];
  monthlySnapshots: MonthlySnapshot[];
  filterOptions: PerformanceFilterOptions;
}

/**
 * Phase 89 — response from the per-agent PO override endpoints
 * (`POST /api/agents/{id}/promote|demote|hold`). Also the payload pushed
 * over SignalR on `agent.level.changed` (promote/demote) and
 * `agent.promotion.acknowledged` (hold). For hold, `oldLevel === newLevel`.
 */
export interface OperatorDecisionResult {
  agentId: string;
  oldLevel: number;
  newLevel: number;
  hourlyUsd: number;
  reason: 'operator-promote' | 'operator-demote' | 'operator-hold';
  actor: string | null;
  at: string;
}

// Phase 82.1 — persisted iteration prompt audit row. Field order
// mirrors `IterationPromptDto` in `Forge.Api/Contracts/ForgeContracts.cs`
// (the System.Text.Json default emits property names in the record's
// declaration order). UserPrompt is the FULL composed prompt the agent
// saw (pins + prior notes + gate + evaluator chain + brief); these
// payloads can be multi-100KB. SystemPrompt may be null when the
// backend doesn't use one.
export interface IterationPromptDto {
  role: 'generator' | 'evaluator' | string;
  agentId: string;
  crewId: string;
  attempt: number;
  systemPrompt: string | null;
  userPrompt: string;
  model: string | null;
  backend: string | null;
  invokeMode: string | null;
  createdAt: string;
}

export const forgeApi = {
  async getState(): Promise<ForgeState> {
    if (useLocalSeed()) {
      return sampleState;
    }
    try {
      return await fetchJson<ForgeState>('/api/state');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/state failed, using local sample data', err);
        return sampleState;
      }
      throw err;
    }
  },

  /** POST /api/cards. Phase 6.1: optional `postTo` posts to ADO/GitHub. */
  async createCard(card: Card, postTo?: 'ado' | 'github', description?: string): Promise<Card> {
    return fetchJson<Card>('/api/cards', {
      method: 'POST',
      body: JSON.stringify({ card, postTo, description }),
      headers: postTo ? { 'X-Forge-User': writerHeader() } : {},
    });
  },

  /** POST /api/epics — create a new story/epic (legacy, global). */
  async createEpic(req: { name: string; id?: string; status?: string }): Promise<Epic> {
    return fetchJson<Epic>('/api/epics', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/epics — list all stories/epics (legacy, global). */
  async listEpics(): Promise<Epic[]> {
    return fetchJson<Epic[]>('/api/epics');
  },

  /** GET /api/products/{productId}/stories — list stories for a product. */
  async listProductStories(productId: string): Promise<ProductStory[]> {
    return fetchJson<ProductStory[]>(`/api/products/${encodeURIComponent(productId)}/stories`);
  },

  /** POST /api/products/{productId}/stories — create a story under a product.
   *  Phase 45: server returns either a bare ProductStory or `{ story, branchWarnings }`
   *  when a non-default base branch is set. The client normalises both shapes. */
  async createProductStory(
    productId: string,
    req: { name: string; id?: string; status?: string; description?: string; points?: number; uploadIds?: string[]; baseBranch?: string; autoMerge?: boolean; repoId?: string | null },
  ): Promise<{ story: ProductStory; branchWarnings?: string[] }> {
    const resp = await fetchJson<ProductStory | { story: ProductStory; branchWarnings: string[] }>(
      `/api/products/${encodeURIComponent(productId)}/stories`,
      {
        method: 'POST',
        body: JSON.stringify(req),
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
    return 'story' in resp
      ? { story: resp.story, branchWarnings: resp.branchWarnings }
      : { story: resp };
  },

  /** PATCH /api/products/{productId}/stories/{storyId} — edit a story's name / desc / points / status / base branch.
   *  Phase 45 / 80.F: changing baseBranch to a non-"main" value triggers an immediate
   *  `git branch` creation in the story's bound repo AND, when a spawn
   *  exists for this story, the full rebase + replay-merged-PRs + retarget-open-PRs
   *  flow. The optional fields below are populated only when a base-branch
   *  change actually occurred and a spawn was present. */
  async updateProductStory(
    productId: string,
    storyId: string,
    body: { name?: string; status?: string; description?: string; points?: number; uploadIds?: string[]; removeFileNames?: string[]; baseBranch?: string; autoMerge?: boolean; repoId?: string | null },
  ): Promise<{
    story: ProductStory;
    branchWarnings?: string[];
    replayedBranches?: string[];
    retargetedPrs?: string[];
    replayFailures?: string[];
  }> {
    const resp = await fetchJson<ProductStory | {
      story: ProductStory;
      branchWarnings?: string[] | null;
      replayedBranches?: string[] | null;
      retargetedPrs?: string[] | null;
      replayFailures?: string[] | null;
    }>(
      `/api/products/${encodeURIComponent(productId)}/stories/${encodeURIComponent(storyId)}`,
      {
        method: 'PATCH',
        body: JSON.stringify(body),
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
    return 'story' in resp
      ? {
          story: resp.story,
          branchWarnings: resp.branchWarnings ?? undefined,
          replayedBranches: resp.replayedBranches ?? undefined,
          retargetedPrs: resp.retargetedPrs ?? undefined,
          replayFailures: resp.replayFailures ?? undefined,
        }
      : { story: resp };
  },

  /** DELETE /api/products/{productId}/stories/{storyId} — delete a story. */
  async deleteProductStory(productId: string, storyId: string): Promise<void> {
    await fetchJson<unknown>(
      `/api/products/${encodeURIComponent(productId)}/stories/${encodeURIComponent(storyId)}`,
      {
        method: 'DELETE',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** POST /api/cards/{id}/comment. */
  async commentOnCard(id: string, body: string, provider: 'ado' | 'github' = 'github'): Promise<void> {
    await fetchJson<unknown>(`/api/cards/${encodeURIComponent(id)}/comment`, {
      method: 'POST',
      body: JSON.stringify({ body, provider }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/approve. */
  async approvePr(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/approve`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/changes. */
  async requestChangesOnPr(id: string, body: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/changes`, {
      method: 'POST',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/comment. */
  async commentOnPr(id: string, body: string): Promise<void> {
    await fetchJson<unknown>(`/api/prs/${encodeURIComponent(id)}/comment`, {
      method: 'POST',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Phase 24 — comment lifecycle endpoints -------------------------------

  /** GET /api/prs/{prId}/comments — list comments with optional filters. */
  async listPrComments(
    prId: string,
    opts?: { state?: string; requiresAttention?: boolean; skip?: number; take?: number },
  ): Promise<{ total: number; items: import('@/types').PrComment[] }> {
    const q = new URLSearchParams();
    if (opts?.state) q.set('state', opts.state);
    if (opts?.requiresAttention !== undefined) q.set('requiresAttention', String(opts.requiresAttention));
    if (opts?.skip !== undefined) q.set('skip', String(opts.skip));
    if (opts?.take !== undefined) q.set('take', String(opts.take));
    const qs = q.toString();
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments${qs ? `?${qs}` : ''}`);
  },

  /** GET /api/prs/{prId}/comments/{id} — single comment + history. */
  async getPrComment(prId: string, commentId: number): Promise<{
    comment: import('@/types').PrComment;
    history: import('@/types').PrCommentHistory[];
  }> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments/${commentId}`);
  },

  /** POST /api/prs/{prId}/comments — write a new comment / reply. */
  async createPrComment(
    prId: string,
    body: { body: string; parentCommentId?: number; filePath?: string; line?: number },
  ): Promise<import('@/types').PrComment> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments`, {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/prs/{prId}/comments/{id} — edit body. */
  async editPrComment(prId: string, commentId: number, body: string): Promise<import('@/types').PrComment> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments/${commentId}`, {
      method: 'PATCH',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{prId}/comments/{id}/resolve — humans only. */
  async resolvePrComment(prId: string, commentId: number, resolution: 'resolved' | 'wont-fix' = 'resolved'):
    Promise<import('@/types').PrComment> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments/${commentId}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ resolution }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{prId}/comments/{id}/reopen — humans only. */
  async reopenPrComment(prId: string, commentId: number): Promise<import('@/types').PrComment> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments/${commentId}/reopen`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{prId}/comments/{id}/react — toggle thumbsup/thumbsdown. */
  async reactToPrComment(prId: string, commentId: number, reaction: 'thumbsup' | 'thumbsdown'):
    Promise<import('@/types').PrComment> {
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/comments/${commentId}/react`, {
      method: 'POST',
      body: JSON.stringify({ reaction }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Phase 27 — build sync endpoints --------------------------------------

  /** GET /api/prs/{prId}/builds — list builds for a PR newest-first. */
  async listPrBuilds(
    prId: string,
    opts?: { limit?: number },
  ): Promise<{ total: number; items: import('@/types').Build[] }> {
    const q = new URLSearchParams();
    if (opts?.limit !== undefined) q.set('limit', String(opts.limit));
    const qs = q.toString();
    return fetchJson(`/api/prs/${encodeURIComponent(prId)}/builds${qs ? `?${qs}` : ''}`);
  },

  /** GET /api/builds/{id} — build detail with stages + failure points joined. */
  async getBuild(id: number): Promise<{
    build: import('@/types').Build;
    stages: import('@/types').BuildStage[];
    failurePoints: import('@/types').BuildFailurePoint[];
  }> {
    return fetchJson(`/api/builds/${id}`);
  },

  /** GET /api/builds/{id}/stages/{stageId}/logs — paginated log tail viewer. */
  async getBuildStageLogs(
    id: number,
    stageId: number,
    opts?: { from?: number; limit?: number },
  ): Promise<{
    stage: import('@/types').BuildStage;
    lines: string[];
    totalLines: number;
    truncatedFromBytes?: number;
  }> {
    const q = new URLSearchParams();
    if (opts?.from !== undefined) q.set('from', String(opts.from));
    if (opts?.limit !== undefined) q.set('limit', String(opts.limit));
    const qs = q.toString();
    return fetchJson(`/api/builds/${id}/stages/${stageId}/logs${qs ? `?${qs}` : ''}`);
  },

  /** GET /api/builds/{id}/artifacts — list artifact rows. */
  async listBuildArtifacts(id: number): Promise<{ items: import('@/types').BuildArtifact[] }> {
    return fetchJson(`/api/builds/${id}/artifacts`);
  },

  /** POST /api/builds/{id}/sync — manual trigger (PO/lead, ?force=true bypasses 19-min stale rule). */
  async syncBuild(id: number, force = false): Promise<void> {
    const url = `/api/builds/${id}/sync${force ? '?force=true' : ''}`;
    await fetchJson(url, { method: 'POST', headers: { 'X-Forge-User': writerHeader() } });
  },

  /** POST /api/builds/{id}/annotate — operator triage; PO/lead only. */
  async annotateBuild(id: number, body: {
    rootCause?: string;
    suggestedFiles?: string[];
    category: 'infra' | 'flaky-test' | 'env' | 'code';
  }): Promise<void> {
    await fetchJson(`/api/builds/${id}/annotate`, {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/queue/build-fix-pending/{id}/promote-now — operator override. */
  async promoteBuildFixNow(pendingId: number): Promise<{ queueId: number }> {
    return fetchJson(`/api/queue/build-fix-pending/${pendingId}/promote-now`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * POST /api/prs/{id}/merge.
   * On a real merge conflict the server returns 409 with
   * `{conflict: true, resolution, hint}`. The `resolution` field is one of:
   *   - "force-available": force has not yet been attempted; the UI may
   *     offer the destructive `-X ours` auto-resolve.
   *   - "manual-required": Forge already attempted the destructive
   *     auto-resolve (either via inherited AutoMerge or `?force=true`) and
   *     the provider still rejects the merge. The operator must resolve in
   *     the provider UI; the UI should redirect, not re-prompt for force.
   * Pass `force: true` to opt into the destructive `-X ours` auto-resolve path
   * (keeps source-branch changes, discards upstream-side hunks).
   * Returns `{pr, queued}`. When `queued=true` ADO accepted the merge but is
   * waiting on branch policies / required validations — Col stays on review
   * locally and PrSyncJob reconciles it once ADO completes the squash.
   */
  async mergePr(id: string, opts?: { force?: boolean }): Promise<{ pr: Pr; queued: boolean; detail?: string }> {
    const qs = opts?.force ? '?force=true' : '';
    const url = `${baseUrl}/api/prs/${encodeURIComponent(id)}/merge${qs}`;
    const res = await operatorFetch(url, {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        'X-Forge-User': writerHeader(),
        ...buildScopeHeaders(getActiveProductId(), getActiveRepoId()),
      },
    });
    if (!res.ok) {
      let problem: ProblemDetails | null = null;
      const ct = res.headers.get('content-type') ?? '';
      if (ct.includes('json')) {
        try {
          problem = (await res.json()) as ProblemDetails;
        } catch {
          // fall through
        }
      }
      const detail = problem?.detail ?? problem?.title ?? res.statusText;
      throw new ForgeApiError(res.status, problem, `merge ${id}: ${res.status} ${detail}`);
    }
    if (res.status === 202) {
      const body = (await res.json()) as { queued: boolean; pr: Pr; detail?: string };
      return { pr: body.pr, queued: true, detail: body.detail };
    }
    const pr = (await res.json()) as Pr;
    return { pr, queued: false };
  },

  /** POST /api/workstreams/{id}/pr — Phase 6.4. */
  async openWorkstreamPr(
    id: string,
    req: { title: string; body: string; provider?: 'ado' | 'github'; draft?: boolean },
  ): Promise<Pr> {
    return fetchJson<Pr>(`/api/workstreams/${encodeURIComponent(id)}/pr`, {
      method: 'POST',
      body: JSON.stringify({ provider: 'github', ...req }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/workstreams/{id}/restart — nuke and restart from scratch. */
  async restartWorkstream(id: string): Promise<{
    id: string;
    status: string;
    deliverablesCleaned: number;
    eventsCleaned: number;
    iterationPromptsCleaned: number;
    memoryRowsCleaned: number;
    gateLogsPruned: boolean;
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/restart`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * POST /api/workstreams/{id}/retry — soft retry. Demotes the last
   * deliverable's verdict to "iterate" so the orchestrator re-enters
   * the gen/eval loop at iter+1. Preserves worktree, deliverables,
   * transcript, agent_usage, and PRs. Use when the workstream stalled
   * at a high score and most of the diff is salvageable — vs restart,
   * which wipes everything.
   */
  async retryWorkstream(id: string): Promise<{
    workstreamId: string;
    priorVerdict: string;
    newVerdict: string;
    demoted: boolean;
    priorIter: number;
    priorScore: number;
    status: string;
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/retry`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/workstreams/{id}/cancel — stop the running gen/eval loop. */
  async cancelWorkstream(id: string): Promise<{ id: string; status: string; queueRowsCancelled: number }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/cancel`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * POST /api/workstreams/{id}/accept — Phase 70 operator override.
   * Flips the latest deliverable's verdict to `pass` so the orchestrator
   * commits the worktree files + opens the PR on its next tick.
   * Mirrors the plan-doc `acceptArtifact` shape. Use to bypass
   * false-positive demotions from the Phase 50 checkbox gate, Phase 56
   * post-pass gate suite, or Phase 66 stalled-questions gate when the
   * operator has independently verified the diff is good.
   * Refused with 409 on terminal status (cancelled / complete / merging).
   */
  async acceptWorkstream(id: string): Promise<{
    workstreamId: string;
    priorVerdict: string;
    newVerdict: string;
    alreadyPassed: boolean;
    iter: number;
    score: number;
    status: string;
    queueRowsCancelled: number;
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/accept`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * GET /api/workstreams/{id}/iter-notes — Insight I.2
   * (docs/forge/workstream-insights.md). Returns the agent's
   * `.forge/notes/iter-*.md` archive for the workstream, sorted
   * ascending. Content capped at ~16 KB per iter on the server side
   * (`truncated: true` when the cap clipped output).
   */
  async fetchWorkstreamIterNotes(id: string): Promise<{
    workItemId: string;
    iters: { iter: number; content: string; truncated: boolean; lengthBytes: number }[];
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/iter-notes`);
  },

  /**
   * GET /api/workstreams/{id}/iter-kpis — Insight I.3
   * (docs/forge/workstream-insights.md). Returns per-iter rollup of
   * score / token usage / cost / files-changed / duration so the
   * Conversation tab can render the IterKpiStrip + sparklines.
   * Iter window mapping is computed server-side from deliverable
   * timestamps so the client doesn't have to re-derive.
   */
  async fetchWorkstreamIterKpis(id: string): Promise<{
    workItemId: string;
    // Durable timing anchors for the swarm header uptime readouts. Both are
    // ISO-8601 strings or null. `startedAt` = when the agent pair began this
    // task (earliest agent_usage row → survives transcript eviction); drives
    // the WORKSTREAM UPTIME stat. `currentIterStartedAt` = the running
    // iteration's start (null when not currently iterating); drives the live
    // ITER uptime. Reported even before the first deliverable exists so an
    // in-flight iter-1 workstream still shows a live uptime.
    startedAt?: string | null;
    currentIterStartedAt?: string | null;
    iters: {
      iter: number;
      score: number;
      verdict: string;
      inputTokens: number;
      outputTokens: number;
      costUsd: number;
      durationMs: number | null;
      filesChanged: number;
      createdAt: string;
      // Per-iter generator/evaluator agent ids (backend already projects these
      // from the deliverable rows). Drive the iteration-strip agent label +
      // the amber "agent switched" divider so a mid-run agent rotation —
      // e.g. a transport-death recovery that re-picks a fresh agent — is visible
      // on the iteration it happened, not silent.
      generatorAgentId?: string | null;
      evaluatorAgentId?: string | null;
      // 2026-05-16 Phase 72 — cache / reasoning / conversation token
      // telemetry. Null when the iter ran on a backend (Claude SDK,
      // Copilot CLI) or pre-Phase-72 row that doesn't report them.
      cacheReadTokens?: number | null;
      cacheWriteTokens?: number | null;
      reasoningTokens?: number | null;
      conversationTokens?: number | null;
      // 2026-05-26 — impacted-test resolution wall-clock (ms) for this iter
      // (build-gate-pass → test-command-start). Surfaces a slow/hung resolver
      // in the insights sparkline. Null when resolution didn't run / legacy row.
      resolveMs?: number | null;
    }[];
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/iter-kpis`);
  },

  /**
   * GET /api/workstreams/{id}/memory — Phase 55 / Phase G with Phase
   * F effectiveThreshold added by Insight I.4. Returns the per-role
   * agent_memory rows + their effective compactor threshold so the
   * UI can render the ContextUsageBar.
   */
  async fetchWorkstreamMemory(id: string): Promise<{
    workItemId: string;
    roles: Record<string, {
      agentId: string;
      backend: string;
      invokeMode: string;
      model: string | null;
      sessionId: string | null;
      hasMessagesHistory: boolean;
      turnCount: number | null;
      approxInputTokens: number;
      effectiveThreshold: number;
      lastIter: number;
      createdAt: string;
      updatedAt: string;
    } | undefined>;
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}/memory`);
  },

  /**
   * Phase 92 — human operator steering. GET lists all operator prompts for a
   * workstream (active + dismissed), newest first. POST adds an active prompt
   * that is injected into BOTH the generator and the evaluator each iteration
   * until dismissed. POST .../dismiss deactivates one.
   */
  async fetchOperatorPrompts(id: string): Promise<OperatorPrompt[]> {
    return fetchJson<OperatorPrompt[]>(`/api/workstreams/${encodeURIComponent(id)}/operator-prompts`);
  },

  async addOperatorPrompt(id: string, body: string): Promise<OperatorPrompt> {
    return fetchJson<OperatorPrompt>(`/api/workstreams/${encodeURIComponent(id)}/operator-prompts`, {
      method: 'POST',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async dismissOperatorPrompt(id: string, promptId: string): Promise<OperatorPrompt> {
    return fetchJson<OperatorPrompt>(
      `/api/workstreams/${encodeURIComponent(id)}/operator-prompts/${encodeURIComponent(promptId)}/dismiss`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      });
  },

  /** GET /api/audit. */
  async listAudit(take = 200): Promise<AuditEntry[]> {
    return fetchJson<AuditEntry[]>(`/api/audit?take=${take}`);
  },

  /** GET /api/human-gate/rules. */
  async getHumanGateRules(): Promise<HumanGateRules> {
    return fetchJson<HumanGateRules>('/api/human-gate/rules');
  },

  /** PATCH /api/cards/{id}. */
  async patchCard(id: string, patch: Partial<Card>): Promise<Card> {
    return fetchJson<Card>(`/api/cards/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(patch),
    });
  },

  /** DELETE /api/cards/{id}. */
  async deleteCard(id: string): Promise<void> {
    await fetchJson(`/api/cards/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/cards/{id}/move. */
  async moveCard(id: string, toCol: ColumnId, force = false): Promise<MoveCardResponse> {
    return fetchJson<MoveCardResponse>(`/api/cards/${encodeURIComponent(id)}/move`, {
      method: 'POST',
      body: JSON.stringify({ toCol, force }),
    });
  },

  /** GET /api/workstreams. Scoped to the active product via the
   *  `X-Forge-Product-Id` header. Returns the API result verbatim — no
   *  sample-data fallback so failures surface to the operator instead of
   *  being papered over with stale demo rows. The opt-in `?seed=local`
   *  query param still exposes sample data for screenshots / demos. */
  async getWorkstreams(): Promise<Workstream[]> {
    if (useLocalSeed()) return sampleState.workstreams;
    return fetchJson<Workstream[]>('/api/workstreams');
  },

  /** POST /api/workstreams. */
  async createWorkstream(req: {
    id: string;
    name: string;
    branch: string;
    base: string;
    epic: string;
    worktreePath?: string | null;
  }): Promise<Workstream> {
    return fetchJson<Workstream>('/api/workstreams', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/workstreams/{id}/sync. */
  async syncWorkstream(id: string): Promise<Workstream> {
    return fetchJson<Workstream>(`/api/workstreams/${encodeURIComponent(id)}/sync`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * DELETE /api/workstreams/{id}?force=true — cascading delete. Removes the
   * workstream row, the synthesized work-item, all deliverables /
   * transcripts / queue rows / dep edges / agent assignments / board card,
   * plus the on-disk gen↔eval workspace, the worktree mount, and the local
   * git branch. Returns `{ id, status, counts }` so the caller can surface
   * what got removed. The server returns 422 if the workstream has open
   * PRs and `force` is false.
   */
  async deleteWorkstream(id: string, force = false): Promise<{
    id: string;
    status: string;
    counts: Record<string, number>;
  }> {
    return fetchJson(`/api/workstreams/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // ---------------------------------------------------------------------
  // forge_implementation.md Phase 28 — story-driven spawn endpoints.
  // ---------------------------------------------------------------------

  /** POST /api/stories/{storyId}/spawn — start the multi-team delivery flow. */
  async spawnStory(
    storyId: string,
    repoId?: string | null,
    uploadIds?: string[],
    baseBranch?: string,
    autoMerge?: boolean,
  ): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(`/api/stories/${encodeURIComponent(storyId)}/spawn`, {
      method: 'POST',
      body: JSON.stringify({
        repoId: repoId ?? null,
        uploadIds: uploadIds?.length ? uploadIds : undefined,
        // Phase 45 — operator-chosen target branch. Server treats null /
        // missing as "main" so existing callers stay compatible.
        baseBranch: baseBranch ?? undefined,
        // Phase 46 — auto-merge PRs. Honoured for any base, including main.
        autoMerge: autoMerge ?? undefined,
      }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/stories/{storyId}/spawn/base-branch — switch the spawn's base branch.
   *  The server rebases the new base on top of main, replays every already-merged
   *  spawn PR onto it, and retargets every still-open PR. Returns the updated
   *  spawn plus a list of replayed branches and retargeted PR ids. */
  async changeStoryBaseBranch(storyId: string, baseBranch: string): Promise<{
    spawn: StorySpawn;
    replayedBranches: string[];
    retargetedPrs: string[];
    replayFailures: string[];
  }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/base-branch`, {
      method: 'PATCH',
      body: JSON.stringify({ baseBranch }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/attachments/upload — upload reference files before spawning. */
  async uploadStoryAttachments(files: File[]): Promise<{ uploadId: string; files: StoryAttachment[] }> {
    const form = new FormData();
    for (const f of files) form.append('files', f);
    const res = await operatorFetch(`${baseUrl}/api/stories/attachments/upload`, {
      method: 'POST',
      body: form,
      headers: {
        ...buildScopeHeaders(getActiveProductId(), getActiveRepoId()),
        'X-Forge-User': writerHeader(),
      },
    });
    if (!res.ok) {
      const ct = res.headers.get('content-type') ?? '';
      let detail = res.statusText;
      if (ct.includes('json')) {
        try { const p = await res.json(); detail = p.detail ?? p.title ?? detail; } catch { /* */ }
      }
      throw new ForgeApiError(res.status, null, `upload: ${res.status} ${detail}`);
    }
    return res.json();
  },

  /** GET /api/stories/spawns — list all story spawns (optionally filtered by phase). */
  async listStorySpawns(phase?: string): Promise<StorySpawn[]> {
    const qs = phase ? `?phase=${encodeURIComponent(phase)}` : '';
    return fetchJson<StorySpawn[]>(`/api/stories/spawns${qs}`);
  },

  /** GET /api/stories/{storyId}/spawn — full spawn state + child workstreams. */
  async getStorySpawn(storyId: string, includeChatSummary = true): Promise<StorySpawnDetail> {
    return fetchJson<StorySpawnDetail>(`/api/stories/${encodeURIComponent(storyId)}/spawn${includeChatSummary ? "" : "?includeChatSummary=false"}`);
  },

  /**
   * Phase 50 — DELETE /api/stories/{storyId}/spawn?deleteBaseBranch=… —
   * wipe a story spawn and every artifact it produced (workstreams,
   * worktrees, local + remote branches, deliverables, transcripts,
   * queue rows, agent claims, PRs both upstream-abandoned and
   * locally-removed, on-disk workspaces and attachments). Used by the
   * delete button on each row in the swarm story-index.
   *
   * <p>When the spawn was opened against a non-`main` base branch and
   * <code>deleteBaseBranch</code> is true, the base branch is also
   * deleted (local + remote) — unless another spawn shares it, in
   * which case the server returns 409 with a human-readable detail.
   * The caller is expected to fall back to a re-call without the flag
   * when that happens.</p>
   */
  async deleteStorySpawn(
    storyId: string,
    deleteBaseBranch: boolean = false,
  ): Promise<{ storyId: string; status: string; baseBranchDeleted: boolean; counts: Record<string, number>; remoteDeleteFailures?: string[] }> {
    const qs = deleteBaseBranch ? '?deleteBaseBranch=true' : '';
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn${qs}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/stories/{storyId}/spawn/phase — advance the phase if the gate passes. */
  async advanceStoryPhase(storyId: string, targetPhase: StorySpawnPhase): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(`/api/stories/${encodeURIComponent(storyId)}/spawn/phase`, {
      method: 'PATCH',
      body: JSON.stringify({ targetPhase }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/retry — clear the persisted spawn LastError
   *  and re-drive the orchestrator immediately. Operator clicks this from the
   *  workstream-view error banner after resolving the underlying cause
   *  (rebased plan branch, fixed branch protection, etc.). */
  async retryStorySpawn(storyId: string): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(`/api/stories/${encodeURIComponent(storyId)}/spawn/retry`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async dismissStorySpawnError(storyId: string): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(`/api/stories/${encodeURIComponent(storyId)}/spawn/dismiss-error`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** Phase 63 — POST /api/stories/{storyId}/spawn/pause — hard-pause the
   *  entire spawn, killing all in-flight agents and pausing workstreams. */
  async pauseStorySpawn(storyId: string): Promise<{ id: string; status: string; pausedFromPhase: string; workstreamsPaused: number }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/pause`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** Phase 63 — POST /api/stories/{storyId}/spawn/resume — resume a paused
   *  spawn, restoring the prior phase and workstream statuses. */
  async resumeStorySpawn(storyId: string): Promise<{ id: string; status: string; phase: string; workstreamsResumed: number }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/resume`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * Phase 50 — operator decision pins. Pin a binding directive that is
   * prepended to every future iter prompt for every plan-doc / workstream
   * under this spawn. Used to break cross-doc convergence loops (the
   * agent re-litigates the same decision because sibling docs change
   * between iters; see qq:TELEGRAM-MESSENGER-S architecture.md 51 iters
   * with DefaultAction re-flagged in 41/51).
   *
   * Pass `workItemId` to scope the pin to one doc / workstream; omit it
   * to apply the pin globally across the spawn.
   */
  async listStoryDecisionPins(storyId: string): Promise<Array<{
    id: string;
    text: string;
    workItemId: string | null;
    createdAt: string;
    createdBy: string;
  }>> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/pins`);
  },

  async addStoryDecisionPin(
    storyId: string,
    text: string,
    workItemId?: string,
  ): Promise<{ id: string; text: string; workItemId: string | null; createdAt: string; createdBy: string }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/pins`, {
      method: 'POST',
      body: JSON.stringify({ text, workItemId: workItemId ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async removeStoryDecisionPin(storyId: string, pinId: string): Promise<{ removed: number }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/pins/${encodeURIComponent(pinId)}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/artifacts/{key}/resume — resume a single doc's gen↔eval loop. */
  async resumeStoryArtifact(storyId: string, key: string): Promise<{ storyId: string; key: string; status: string }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/resume`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * POST /api/stories/{storyId}/spawn/artifacts/{key}/retry — soft retry
   * for a plan-doc that hit a terminal verdict (sloppy at iter-cap,
   * stalled, or recovery-exhausted pass). Demotes the verdict to
   * "iterate" AND bumps the iter cap by +5 so DeliverableLoop seeding
   * continues from iter+1 instead of terminal-replaying. Preserves
   * doc, deliverables, transcript, costs.
   */
  async retryStoryArtifact(storyId: string, key: string): Promise<{
    storyId: string;
    key: string;
    priorVerdict: string;
    newVerdict: string;
    demoted: boolean;
    priorIter: number;
    priorScore: number;
    priorMaxIter: number;
    newMaxIter: number;
    status: string;
  }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/retry`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/artifacts/{key}/cancel — stop the running gen↔eval loop for a single doc. */
  async cancelStoryArtifact(storyId: string, key: string): Promise<{
    storyId: string;
    key: string;
    status: string;
    queueRowsCancelled: number;
    invocationsCancelled: number;
  }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/cancel`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/artifacts/{key}/accept — manually accept a doc artifact despite score < 90. */
  async acceptStoryArtifact(storyId: string, key: string): Promise<{
    storyId: string;
    key: string;
    status: string;
    previousVerdict: string;
    previousScore: number;
    queueRowsCancelled: number;
    invocationsCancelled: number;
  }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/accept`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/artifacts/{key}/restart — wipe iterations + transcript and re-enter the loop at iter 1. */
  async restartStoryArtifact(storyId: string, key: string): Promise<{
    storyId: string;
    key: string;
    status: string;
    deliverablesCleaned: number;
    eventsCleaned: number;
    queueRowsCancelled: number;
  }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/restart`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/decompose — submit workstream definitions. */
  async submitStoryDecomposition(
    storyId: string,
    workstreams: StorySpawnDecomposeWorkstream[],
  ): Promise<{ spawn: StorySpawn; workstreams: string[] }> {
    return fetchJson(`/api/stories/${encodeURIComponent(storyId)}/spawn/decompose`, {
      method: 'POST',
      body: JSON.stringify({ workstreams }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/stories/{storyId}/spawn/decompose/approve — operator approves. */
  async approveStoryDecomposition(storyId: string): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/decompose/approve`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      });
  },

  /** GET /api/stories/{storyId}/spawn/artifacts — listings grouped by phase. */
  async getStoryArtifacts(storyId: string): Promise<StorySpawnArtifacts> {
    return fetchJson<StorySpawnArtifacts>(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts`);
  },

  /** GET /api/stories/{storyId}/spawn/artifacts/{key} — markdown body for one
   *  plan artifact (architecture | tech-spec | implementation-plan | e2e-scenarios)
   *  or the raw decomposition JSON. Falls back to the plan branch via git when
   *  the file isn't on main yet. */
  async getStoryArtifactContent(storyId: string, key: string): Promise<{
    key: string;
    path: string | null;
    content: string;
    lastModifiedAt: string | null;
    exists: boolean;
    source?: string | null;
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}`);
  },

  /** GET /api/stories/{storyId}/spawn/artifacts/{key}/iterations — per-iteration
   *  generator + evaluator history for one plan artifact. Empty for
   *  `decomposition`. */
  async getStoryArtifactIterations(storyId: string, key: string): Promise<{
    key: string;
    workItemId: string | null;
    /** Derived lifecycle: live (events still flowing) → iterating (last
     *  verdict !pass) → awaiting-pr-review (verdict=pass but plan PR not
     *  merged yet) → completed (PR merged) | pending (no iterations yet).
     *  `stalled` wins over `live`/`iterating` when there are open
     *  questions blocking the pass gate — see DeliverableLoop.cs which
     *  demotes verdict=pass → iterate while unanswered questions exist. */
    status: 'live' | 'iterating' | 'stalled' | 'awaiting-pr-review' | 'completed' | 'pending';
    /** Reason the doc is stalled — e.g. the last deliverable verdict such
     *  as "reject", "stalled", "sloppy". Only set when status is "stalled". */
    stallReason: string | null;
    maxIterations: number;
    budgetSeconds: number;
    /** Count of unanswered operator questions for this work item. The
     *  pass gate is hard-blocked while > 0; the UI surfaces this count
     *  next to the stalled badge so the operator knows how many answers
     *  the agent is still waiting on. */
    unansweredQuestions: number;
    /** Latest generator agent name + model, null until first iteration lands. */
    generator: { agentId: string; name: string; model: string | null } | null;
    /** Latest evaluator agent name + model, null until first iteration lands. */
    evaluator: { agentId: string; name: string; model: string | null } | null;
    iterations: Array<{
      id: number;
      iter: number;
      score: number;
      verdict: string;
      generatorAgentId: string;
      evaluatorAgentId: string;
      generatorSummary: string;
      reasoningText?: string | null;
      notes: string;
      genCostUsd: number;
      evalCostUsd: number;
      judgeAgentId: string | null;
      judgeScore: number | null;
      judgeNotes: string | null;
      createdAt: string;
    }>;
    totalCostUsd: number;
    /** Phase 52 — current pair attempt (1 = original, 2+ = reassigned).
     *  UI surfaces an "ATTEMPT n/m" badge when this exceeds 1. */
    pairAttemptCount: number;
    /** Phase 52 — max allowed pair attempts before terminal failure
     *  (mirrors `ScoringConstants.MaxPairAttempts`, currently 3). */
    maxPairAttempts: number;
    /** Phase 52 — generator-side input+output tokens accrued since
     *  `WorkItemRow.BudgetWindowStartUtc`. Matches the meter the sloppy
     *  detector gates against. */
    generatorTokensSinceWindow: number;
    /** Phase 52 — generator-side token cap. Matches
     *  `ScoringConstants.SloppyGeneratorTokenCap` (currently 500,000). */
    generatorTokenCap: number;
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/iterations`);
  },

  /**
   * GET /api/stories/{storyId}/spawn/artifacts/{key}/iter-kpis — Insight
   * I.3 for plan-doc tasks. Same shape as
   * {@link fetchWorkstreamIterKpis} but keyed on the synthetic
   * `plan-doc:{spawnId}:{filename}` workItemId, so the architect crew's
   * iterations populate the doc Conversation tab's KPI strip +
   * sparklines identically to engineer workstreams.
   */
  async fetchStoryArtifactIterKpis(storyId: string, key: string): Promise<{
    workItemId: string | null;
    iters: {
      iter: number;
      score: number;
      verdict: string;
      inputTokens: number;
      outputTokens: number;
      costUsd: number;
      durationMs: number | null;
      filesChanged: number;
      createdAt: string;
      // Per-iter generator/evaluator agent ids (backend already projects these
      // from the deliverable rows). Drive the iteration-strip agent label +
      // the amber "agent switched" divider so a mid-run agent rotation —
      // e.g. a transport-death recovery that re-picks a fresh agent — is visible
      // on the iteration it happened, not silent.
      generatorAgentId?: string | null;
      evaluatorAgentId?: string | null;
      // 2026-05-16 Phase 72 — see fetchWorkstreamIterKpis above.
      cacheReadTokens?: number | null;
      cacheWriteTokens?: number | null;
      reasoningTokens?: number | null;
      conversationTokens?: number | null;
    }[];
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/iter-kpis`);
  },

  /**
   * GET /api/stories/{storyId}/spawn/artifacts/{key}/iter-notes — Insight
   * I.2 for plan-doc tasks. Reads the architect's per-iter working notes
   * (`.forge/notes/iter-{N}.md`) under the per-doc worktree
   * (`.worktree/plan-{key}` — Phase 53 slug).
   */
  async fetchStoryArtifactIterNotes(storyId: string, key: string): Promise<{
    workItemId: string | null;
    iters: { iter: number; content: string; truncated: boolean; lengthBytes: number }[];
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/iter-notes`);
  },

  /**
   * GET /api/stories/{storyId}/spawn/artifacts/{key}/memory — Insight I.4
   * / Phase G for plan-doc tasks. Returns the per-role agent_memory rows
   * + their effective compactor threshold so the doc Conversation tab
   * can render the ContextUsageBar.
   */
  async fetchStoryArtifactMemory(storyId: string, key: string): Promise<{
    workItemId: string | null;
    roles: Record<string, {
      agentId: string;
      backend: string;
      invokeMode: string;
      model: string | null;
      sessionId: string | null;
      hasMessagesHistory: boolean;
      turnCount: number | null;
      approxInputTokens: number;
      effectiveThreshold: number;
      lastIter: number;
      createdAt: string;
      updatedAt: string;
    } | undefined>;
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/memory`);
  },

  /** GET /api/prs. */
  async getPrs(): Promise<Pr[]> {
    if (useLocalSeed()) return sampleState.prs;
    try {
      return await fetchJson<Pr[]>('/api/prs');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/prs failed, using local sample data', err);
        return sampleState.prs;
      }
      throw err;
    }
  },

  /** GET /api/prs/{id}/details. */
  async getPrDetails(id: string): Promise<PrDetailsResponse> {
    return fetchJson<PrDetailsResponse>(`/api/prs/${encodeURIComponent(id)}/details`);
  },

  /** POST /api/prs/{id}/auto-merge. */
  async setAutoMerge(id: string, autoMerge: boolean): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/auto-merge`, {
      method: 'POST',
      body: JSON.stringify({ autoMerge }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/build/rerun. */
  async rerunBuild(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/build/rerun`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/build/cancel. */
  async cancelBuild(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/build/cancel`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/sync — pull PRs from provider into local cache. */
  async syncPrs(body: {
    repoId: string;
    provider: string;
    createdBy?: string | null;
    status?: string | null;
    fromDate?: string | null;
    toDate?: string | null;
  }): Promise<{ pulled: number; inserted: number; updated: number }> {
    return fetchJson('/api/prs/sync', {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/providers — last-known health snapshot for every provider. */
  async listProviders(): Promise<ProviderHealth[]> {
    if (useLocalSeed()) return [];
    try {
      return await fetchJson<ProviderHealth[]>('/api/providers');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/providers failed', err);
        return [];
      }
      throw err;
    }
  },

  /** GET /api/providers/{kind}/test — live probe. */
  async testProvider(kind: string): Promise<ProviderHealth> {
    return fetchJson<ProviderHealth>(`/api/providers/${encodeURIComponent(kind)}/test`);
  },

  /** POST /api/providers/{kind}/sync — manual upsert run. Returns refreshed health. */
  async syncProviderNow(kind: string): Promise<ProviderHealth> {
    return fetchJson<ProviderHealth>(`/api/providers/${encodeURIComponent(kind)}/sync`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Phase 9 — interactive sign-in --------------------------------------

  /** GET /api/auth/{kind}/status. */
  async authStatus(kind: 'ado' | 'github'): Promise<AuthStatus> {
    return fetchJson<AuthStatus>(`/api/auth/${kind}/status`);
  },

  /** POST /api/auth/{kind}/sign-in — opens WAM/browser popup, blocks until done. */
  async signIn(kind: 'ado' | 'github'): Promise<AuthStatus> {
    return fetchJson<AuthStatus>(`/api/auth/${kind}/sign-in`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/auth/{kind}/sign-out — drops the cached account. */
  async signOut(kind: 'ado' | 'github'): Promise<void> {
    await fetchJson<void>(`/api/auth/${kind}/sign-out`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/me — aggregated identity + per-provider auth state. */
  async getMe(): Promise<CurrentUser> {
    return fetchJson<CurrentUser>('/api/me');
  },

  /**
   * PUT /api/me/role — set my role for a product (or globally when
   * productId is null/empty). Server validates the role against the
   * known list (human, eng, rev, qa, arch, tech-lead, product-owner).
   */
  async setMyRole(role: string, productId: string | null = null): Promise<void> {
    await fetchJson<unknown>('/api/me/role', {
      method: 'PUT',
      body: JSON.stringify({ productId: productId ?? '', role }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PUT /api/me/avatar — persist chosen avatar filename to user row. */
  async setMyAvatar(avatar: string): Promise<void> {
    await fetchJson<unknown>('/api/me/avatar', {
      method: 'PUT',
      body: JSON.stringify({ avatar }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PUT /api/me/display-name — update operator display name. */
  async setMyDisplayName(displayName: string): Promise<void> {
    await fetchJson<unknown>('/api/me/display-name', {
      method: 'PUT',
      body: JSON.stringify({ displayName }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Phase 10 — preferences + crews ------------------------------------

  /** GET /api/me/settings. */
  async getMySettings(): Promise<UserSettings> {
    return fetchJson<UserSettings>('/api/me/settings');
  },

  /** GET /api/settings/agent-providers — which credentials each agent runtime uses. */
  async getAgentProviders(): Promise<AgentProviderSettings> {
    return fetchJson<AgentProviderSettings>('/api/settings/agent-providers');
  },

  /** PUT /api/settings/agent-providers/copilot — host admin; null = follow the GitHub provider's account. */
  async setCopilotAccount(account: string | null): Promise<AgentProviderSettings> {
    return fetchJson<AgentProviderSettings>('/api/settings/agent-providers/copilot', {
      method: 'PUT',
      body: JSON.stringify({ account }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/me/settings. */
  async patchMySettings(req: Partial<UserSettings>): Promise<UserSettings> {
    return fetchJson<UserSettings>('/api/me/settings', {
      method: 'PATCH',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/repos/{id} — per-repo override editor. */
  async patchRepo(id: string, req: {
    displayName?: string;
    cloneRoot?: string;
    defaultBranch?: string;
    adoOrg?: string | null;
    adoProject?: string | null;
    ghOwner?: string | null;
    ghRepo?: string | null;
    // Phase 23 — sync settings.
    areaPaths?: string[];
    agentReadyDefaultTeam?: string | null;
    // Phase 26/29 — build pipeline gate.
    initCommand?: string;
    restoreCommand?: string;
    buildCommand?: string;
    testCommand?: string;
    buildTimeoutSec?: number;
    testTimeoutSec?: number;
    // Phase 59 — per-repo override for impacted-test selection.
    // null = inherit global Forge:Test:ImpactedOnly (defaults to true).
    testImpactedOnly?: boolean | null;
    // Phase 83 — plan-doc base dir. Empty string clears the override
    // (inherit "docs/stories"); any other value is pinned + normalized.
    planDocBaseDir?: string | null;
    // Phase 84 — per-repo build/test elevation requirement (Administrator).
    runTestsElevated?: boolean | null;
    // Phase 94 — per-repo qa-e2e test-style override. null = inherit global
    // Forge:Stories:E2eUseBddPattern (true = BDD/Gherkin); true forces
    // BDD/Gherkin; false = plain idiomatic test methods in the repo's framework.
    e2eUseBddPattern?: boolean | null;
    // Phase 33 — persisted sync settings.
    wiSyncAssignedTo?: string;
    wiSyncDateFromDays?: number | null;
    wiSyncFromDate?: string;
    wiSyncToDate?: string;
    prSyncCreatedBy?: string;
    prSyncStatus?: string;
    buildPipelineIds?: string[];
    // Phase 34 — per-repo sync cadence overrides (seconds; 0 clears the override).
    workItemSyncIntervalSeconds?: number;
    prSyncIntervalSeconds?: number;
    buildSyncIntervalSeconds?: number;
  }): Promise<Repo> {
    return fetchJson<Repo>(`/api/repos/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/repos/{id}/clone — ensure the repo's cloneRoot exists, clone if missing. */
  async cloneRepo(id: string): Promise<{ outcome: 'cloned' | 'skipped'; message?: string }> {
    return fetchJson<{ outcome: 'cloned' | 'skipped'; message?: string }>(
      `/api/repos/${encodeURIComponent(id)}/clone`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** Phase 80 — POST /api/repos/{id}/clear-sync-error — acknowledge a failed
   *  sync (reset status → idle, clear error) without running a sync. */
  async clearRepoSyncError(id: string): Promise<Repo> {
    return fetchJson<Repo>(
      `/api/repos/${encodeURIComponent(id)}/clear-sync-error`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** Phase 59 Stage A — GET /api/repos/{id}/test-impacted-preview — dry-run resolver. */
  async previewImpactedTests(id: string): Promise<ImpactedTestPreview> {
    return fetchJson<ImpactedTestPreview>(
      `/api/repos/${encodeURIComponent(id)}/test-impacted-preview`,
    );
  },

  /** Phase 26 — POST /api/repos/{id}/detect-build — scan repo for build markers. */
  async detectBuild(id: string): Promise<DetectBuildResult> {
    return fetchJson<DetectBuildResult>(
      `/api/repos/${encodeURIComponent(id)}/detect-build`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** Phase 26/29 — POST /api/repos/{id}/validate-build — enqueue async validation. */
  async validateBuild(id: string): Promise<ValidateBuildAccepted> {
    return fetchJson<ValidateBuildAccepted>(
      `/api/repos/${encodeURIComponent(id)}/validate-build`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** Phase 29 — GET /api/repos/{id}/validation-runs — fetch persistent history. */
  async getValidationRuns(id: string): Promise<BuildValidationRun[]> {
    return fetchJson<BuildValidationRun[]>(
      `/api/repos/${encodeURIComponent(id)}/validation-runs`,
    );
  },

  /** Phase 29 — POST /api/repos/{id}/validation-runs/{runId}/cancel — cancel a running validation. */
  async cancelValidationRun(repoId: string, runId: string): Promise<{ runId: string; status: string }> {
    return fetchJson<{ runId: string; status: string }>(
      `/api/repos/${encodeURIComponent(repoId)}/validation-runs/${encodeURIComponent(runId)}/cancel`,
      {
        method: 'POST',
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  /** Phase 29 — DELETE /api/repos/{id}/validation-runs — clear completed validation history. */
  async clearValidationRuns(repoId: string): Promise<{ deleted: number }> {
    return fetchJson<{ deleted: number }>(
      `/api/repos/${encodeURIComponent(repoId)}/validation-runs`,
      { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } },
    );
  },

  /** Phase 29 — DELETE /api/repos/{id}/sync-work-items/runs — clear sync history. */
  async clearSyncRuns(repoId: string): Promise<{ deleted: number }> {
    return fetchJson<{ deleted: number }>(
      `/api/repos/${encodeURIComponent(repoId)}/sync-work-items/runs`,
      { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } },
    );
  },

  /**
   * GET /api/repos/{id}/sync-work-items/runs — paginated sync-history.
   * Server caps `limit` at 50 (default 50). `target` filters to work-item
   * (`wi`) or PR (`pr`) runs; omit for all kinds.
   *
   * `signal` lets callers cancel the request from a useEffect cleanup so
   * unmount/re-render doesn't leave a stale fetch in flight (which can
   * surface as a `TypeError: Failed to fetch` in the browser when the
   * dev-server proxy / page-context goes away mid-request).
   */
  async listSyncRuns(
    repoId: string,
    opts: { limit?: number; target?: 'wi' | 'pr'; signal?: AbortSignal } = {},
  ): Promise<WorkItemSyncRun[]> {
    const params = new URLSearchParams();
    params.set('limit', String(Math.max(1, Math.min(50, opts.limit ?? 50))));
    if (opts.target) params.set('target', opts.target);
    return fetchJson<WorkItemSyncRun[]>(
      `/api/repos/${encodeURIComponent(repoId)}/sync-work-items/runs?${params.toString()}`,
      { signal: opts.signal },
    );
  },

  /**
   * GET /api/repos/{id}/sync-work-items/runs/{runId}/items — per-item
   * detail rows for one sync run (Phase 25). `signal` supports clean
   * cancellation from a useEffect cleanup.
   */
  async listSyncRunItems(
    repoId: string,
    runId: string,
    opts: { signal?: AbortSignal } = {},
  ): Promise<SyncRunItem[]> {
    return fetchJson<SyncRunItem[]>(
      `/api/repos/${encodeURIComponent(repoId)}/sync-work-items/runs/${encodeURIComponent(runId)}/items`,
      { signal: opts.signal },
    );
  },

  /**
   * GET /api/repos/{id}/sync-work-items/runs/{runId}/items/{kind}/{entityId}/detail —
   * Phase 25 follow-up; rich detail payload for the click-through popup.
   */
  async getSyncRunItemDetail(
    repoId: string,
    runId: string,
    entityKind: string,
    entityId: string,
    opts: { signal?: AbortSignal } = {},
  ): Promise<SyncRunItemDetail> {
    return fetchJson<SyncRunItemDetail>(
      `/api/repos/${encodeURIComponent(repoId)}/sync-work-items/runs/${encodeURIComponent(runId)}/items/${encodeURIComponent(entityKind)}/${encodeURIComponent(entityId)}/detail`,
      { signal: opts.signal },
    );
  },

  /** GET /api/crews. */
  async listCrews(): Promise<Crew[]> {
    return fetchJson<Crew[]>('/api/crews');
  },

  /** GET /api/crews/{id}/salary — Phase 11.9 salary roll-up (Q14.7: legacy alias). */
  async getCrewSalary(id: string): Promise<CrewSalary> {
    return fetchJson<CrewSalary>(`/api/crews/${encodeURIComponent(id)}/salary`);
  },

  /** GET /api/crews/{id}/spend — Phase 14.8 token-cost rollup (Q14.7 canonical name). */
  async getCrewSpend(id: string): Promise<CrewSalary> {
    return fetchJson<CrewSalary>(`/api/crews/${encodeURIComponent(id)}/spend`);
  },

  /** GET /api/crews/{id}/payroll — Phase 14.8 HR salary rollup. */
  async getCrewPayroll(id: string): Promise<CrewPayroll> {
    return fetchJson<CrewPayroll>(`/api/crews/${encodeURIComponent(id)}/payroll`);
  },

  /** GET /api/crews/payroll/today — Phase 14.11 server-side payroll SUM. */
  async getPayrollTodayTotal(): Promise<PayrollTodaySummary> {
    return fetchJson<PayrollTodaySummary>('/api/crews/payroll/today');
  },

  /** GET /api/deliverables/{id} — Phase 14.5. */
  async getDeliverable(id: number): Promise<Deliverable> {
    return fetchJson<Deliverable>(`/api/deliverables/${id}`);
  },

  /** GET /api/work-items/{workItemId}/deliverables — Phase 14.5 list iterations. */
  async listDeliverablesForWorkItem(workItemId: string): Promise<Deliverable[]> {
    return fetchJson<Deliverable[]>(`/api/work-items/${encodeURIComponent(workItemId)}/deliverables`);
  },

  /** POST /api/deliverables/{id}/dispute — Phase 14.5 judge re-score. */
  async disputeDeliverable(id: number, body: DeliverableDisputeRequest): Promise<Deliverable> {
    return fetchJson<Deliverable>(`/api/deliverables/${id}/dispute`, {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/agents/{id}/level-history — Phase 14.7 promotion timeline. */
  async getAgentLevelHistory(agentId: string): Promise<AgentLevelHistoryEntry[]> {
    return fetchJson<AgentLevelHistoryEntry[]>(`/api/agents/${encodeURIComponent(agentId)}/level-history`);
  },

  /** POST /api/admin/promotion/run — Phase 14.7 manual trigger. */
  async runPromotion(): Promise<PromotionRunResponse> {
    return fetchJson<PromotionRunResponse>(`/api/admin/promotion/run`, {
      method: 'POST',
      body: JSON.stringify({}),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * Phase 89 — per-agent PO override: bump level by +1. Pass the level the UI
   * currently displays for the agent; the server returns 409 if it no longer
   * matches (stale page). Optional `windowAvg` is recorded into the audit row.
   */
  async promoteAgent(agentId: string, expectedFromLevel: number, windowAvg?: number): Promise<OperatorDecisionResult> {
    return fetchJson<OperatorDecisionResult>(`/api/agents/${encodeURIComponent(agentId)}/promote`, {
      method: 'POST',
      body: JSON.stringify({ expectedFromLevel, windowAvg: windowAvg ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * Phase 89 — per-agent PO override: drop level by 1. Refuses with
   * `crew.lead.level_too_low` (409) if the agent leads a crew and would
   * fall below the lead floor (L7).
   */
  async demoteAgent(agentId: string, expectedFromLevel: number, windowAvg?: number): Promise<OperatorDecisionResult> {
    return fetchJson<OperatorDecisionResult>(`/api/agents/${encodeURIComponent(agentId)}/demote`, {
      method: 'POST',
      body: JSON.stringify({ expectedFromLevel, windowAvg: windowAvg ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * Phase 89 — per-agent PO override: <b>audit-only</b> hold. Records the
   * operator's acknowledgement in the level history without changing the
   * agent's level. Does NOT suppress the next nightly promotion tick at v0.1.
   */
  async holdAgent(agentId: string, windowAvg?: number): Promise<OperatorDecisionResult> {
    return fetchJson<OperatorDecisionResult>(`/api/agents/${encodeURIComponent(agentId)}/hold`, {
      method: 'POST',
      body: JSON.stringify({ expectedFromLevel: 0, windowAvg: windowAvg ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // ----- Phase 13 — crew management & task orchestration ----------------

  /** PATCH /api/crews/{id} — covers backend, invokeMode, parent, quotas, etc. */
  async patchCrew(id: string, patch: Partial<Crew> & { clearParentCrewId?: boolean }): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(patch),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/crews/{id}/assign — Phase 13.3 lead-routing. */
  async assignCrewTask(id: string, req: { workItemId: string; memberAgentId: string; force?: boolean }): Promise<unknown> {
    return fetchJson<unknown>(`/api/crews/${encodeURIComponent(id)}/assign`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/crews/{id}/capabilities — Phase 13.8 capability ingestion. */
  async addCrewCapability(id: string, contentId: string): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(id)}/capabilities`, {
      method: 'POST',
      body: JSON.stringify({ contentId }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/crews/{id}/capabilities — Phase 13.8 capability removal. */
  async removeCrewCapability(id: string, contentId: string): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(id)}/capabilities`, {
      method: 'DELETE',
      body: JSON.stringify({ contentId }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/crews/{id}/history — Phase 13.1 history readout. */
  async listCrewHistory(id: string): Promise<CrewHistoryEntry[]> {
    return fetchJson<CrewHistoryEntry[]>(`/api/crews/${encodeURIComponent(id)}/history`);
  },

  /** POST /api/crews/{id}/members — Phase 13b roster CRUD. */
  async addCrewMember(crewId: string, agentId: string, role = 'coder'): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(crewId)}/members`, {
      method: 'POST',
      body: JSON.stringify({ agentId, role }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/crews/{id}/members/{agentId} — change a member's per-crew role. */
  async setCrewMemberRole(crewId: string, agentId: string, role: string): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(crewId)}/members/${encodeURIComponent(agentId)}`, {
      method: 'PATCH',
      body: JSON.stringify({ role }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/crews/{id}/members/{agentId} — remove from crew. */
  async removeCrewMember(crewId: string, agentId: string): Promise<Crew> {
    return fetchJson<Crew>(`/api/crews/${encodeURIComponent(crewId)}/members/${encodeURIComponent(agentId)}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/agents — Phase 11.3 listing. */
  async listAgents(): Promise<AgentDetail[]> {
    return fetchJson<AgentDetail[]>('/api/agents');
  },

  /** GET /api/agents/{id} — Phase 11.3 single-agent fetch. */
  async getAgent(agentId: string): Promise<AgentDetail> {
    return fetchJson<AgentDetail>(`/api/agents/${encodeURIComponent(agentId)}`);
  },

  /** GET /api/models — Phase 11.2 model registry. */
  async listModels(): Promise<ModelInfo[]> {
    return fetchJson<ModelInfo[]>('/api/models');
  },

  /**
   * POST /api/models/refresh — force a model-catalog enrichment cycle against
   * the live Copilot catalog (auto-add new models, retire dropped ones, persist
   * to models.json) and return the refreshed catalog. Slower than
   * {@link listModels} because it round-trips the Copilot server.
   */
  async refreshModels(): Promise<ModelInfo[]> {
    const res = await fetchJson<{ models: ModelInfo[] }>('/api/models/refresh', { method: 'POST' });
    return res.models ?? [];
  },

  /** GET /api/agents/{id}/soul — Phase 11.3 soul.md fetch. */
  async getAgentSoul(agentId: string): Promise<SoulPayload> {
    return fetchJson<SoulPayload>(`/api/agents/${encodeURIComponent(agentId)}/soul`);
  },

  /** PUT /api/agents/{id}/soul — Phase 11.3 soul.md save. */
  async putAgentSoul(agentId: string, contents: string): Promise<SoulPayload> {
    return fetchJson<SoulPayload>(`/api/agents/${encodeURIComponent(agentId)}/soul`, {
      method: 'PUT',
      body: JSON.stringify({ contents }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * PATCH /api/agents/{id} — Phase 13b/e agent edit. Empty-string clears
   * an override field (reverts to crew inheritance); null leaves it alone.
   */
  /** Phase 80 — POST /api/agents — create a new agent assigned to a crew. */
  async createAgent(req: {
    name: string;
    crewId: string;
    id?: string;
    role?: string;
    level?: number;
    backend?: string;
    invokeMode?: string;
    generatorModel?: string;
    evaluatorModel?: string;
    avatar?: string;
  }): Promise<AgentDetail> {
    return fetchJson<AgentDetail>('/api/agents', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** Phase 80 — DELETE /api/agents/{id} — remove an agent + its crew memberships. */
  async deleteAgent(agentId: string): Promise<void> {
    await fetchJson<void>(`/api/agents/${encodeURIComponent(agentId)}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async patchAgent(agentId: string, patch: {
    name?: string;
    role?: string;
    soulPath?: string;
    backend?: string;
    invokeMode?: string;
    generatorModel?: string;
    evaluatorModel?: string;
    avatar?: string;
  }): Promise<AgentDetail> {
    return fetchJson<AgentDetail>(`/api/agents/${encodeURIComponent(agentId)}`, {
      method: 'PATCH',
      body: JSON.stringify(patch),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/avatars — list available avatar filenames from assets/crews/. */
  async listAvatars(): Promise<string[]> {
    return fetchJson<string[]>('/api/avatars');
  },

  /**
   * POST /api/crews/auto-assign — Phase 13c reconcile pass.
   * Adds every unassigned agent to its team's crew, alternating
   * generator / evaluator so each crew ends up balanced. Idempotent.
   */
  async autoAssignAgents(): Promise<{
    inspected: number;
    added: number;
    actions: Array<{ agentId: string; crewId: string; role: string }>;
  }> {
    return fetchJson(`/api/crews/auto-assign`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/domain-experts — Phase 13d capability catalogue. */
  async listDomainExperts(prefix?: string): Promise<DomainExpert[]> {
    const qs = prefix ? `?prefix=${encodeURIComponent(prefix)}` : '';
    return fetchJson<DomainExpert[]>(`/api/domain-experts${qs}`);
  },

  /** GET /api/crews/salary/today — Phase 11.9 TopBar chip total. */
  async getSalaryTodayTotal(): Promise<SalaryTodaySummary> {
    return fetchJson<SalaryTodaySummary>('/api/crews/salary/today');
  },

  /**
   * GET /api/crews/concurrency — TopBar "ACTIVE n/cap" chip payload.
   * Counts open agent_claims rows joined with agents.CrewId so the
   * chip reflects story-spawn-driven activity, not just the legacy
   * task_queue picked-up state.
   */
  async getCrewConcurrency(): Promise<CrewConcurrencySummary> {
    return fetchJson<CrewConcurrencySummary>('/api/crews/concurrency');
  },

  /** GET /api/cost/summary — Phase 11 `/cost` dashboard rollup. */
  async getCostSummary(days?: number): Promise<CostSummary> {
    const qs = days ? `?days=${days}` : '';
    return fetchJson<CostSummary>(`/api/cost/summary${qs}`);
  },

  // ----- Phase 8 — products & repos --------------------------------------

  /** GET /api/products. */
  async listProducts(): Promise<Product[]> {
    return fetchJson<Product[]>('/api/products');
  },

  /** GET /api/products/{id}. */
  async getProduct(id: string): Promise<Product> {
    return fetchJson<Product>(`/api/products/${encodeURIComponent(id)}`);
  },

  /** GET /api/products/{id}/repos. */
  async listProductRepos(id: string): Promise<Repo[]> {
    return fetchJson<Repo[]>(`/api/products/${encodeURIComponent(id)}/repos`);
  },

  /** GET /api/products/{id}/stats — repo count + activity roll-up. */
  async getProductStats(id: string): Promise<ProductStats> {
    return fetchJson<ProductStats>(`/api/products/${encodeURIComponent(id)}/stats`);
  },

  /** POST /api/products. */
  async createProduct(req: {
    id: string;
    name: string;
    slug?: string;
    description?: string;
    ownerUserId?: string | null;
  }): Promise<Product> {
    return fetchJson<Product>('/api/products', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/products/{id}. */
  async patchProduct(id: string, req: {
    name?: string;
    description?: string;
    ownerUserId?: string | null;
    /** Phase 15.6 — empty string clears the override; null leaves it unchanged. */
    activeIterationId?: string | null;
  }): Promise<Product> {
    return fetchJson<Product>(`/api/products/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/products/{id}?force=true to also detach attached repos. */
  async deleteProduct(id: string, force = false): Promise<void> {
    await fetchJson<void>(`/api/products/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/products/{id}/repos. */
  async attachRepo(productId: string, req: {
    id: string;
    kind: 'ado' | 'github';
    remoteUrl: string;
    displayName: string;
    cloneRoot: string;
    defaultBranch?: string;
    adoOrg?: string;
    adoProject?: string;
    ghOwner?: string;
    ghRepo?: string;
  }): Promise<{ repo: Repo; cloneOutcome: string | null; cloneError: string | null }> {
    return fetchJson<{ repo: Repo; cloneOutcome: string | null; cloneError: string | null }>(`/api/products/${encodeURIComponent(productId)}/repos`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/repos[?productId=…]. */
  async listRepos(productId?: string): Promise<Repo[]> {
    const qs = productId ? `?productId=${encodeURIComponent(productId)}` : '';
    return fetchJson<Repo[]>(`/api/repos${qs}`);
  },

  /** DELETE /api/repos/{id}?force=true. */
  async deleteRepo(id: string, force = false): Promise<void> {
    await fetchJson<void>(`/api/repos/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** Phase 23.7 — POST /api/repos/{id}/sync-work-items (manual trigger). */
  async syncRepoWorkItems(id: string, body: { kind?: 'full' | 'delta' | 'auto'; ignoreThrottle?: boolean } = {}): Promise<void> {
    await fetchJson<void>(`/api/repos/${encodeURIComponent(id)}/sync-work-items`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader(), 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  },

  /** GET /api/providers/{kind}/repos — Phase 8.5 server-driven add-repo picker. */
  async discoverProviderRepos(kind: 'ado' | 'github'): Promise<DiscoverableRepo[]> {
    return fetchJson<DiscoverableRepo[]>(`/api/providers/${encodeURIComponent(kind)}/repos`);
  },

  // ----- Phase 12 — Work Item Tracking ----------------------------------

  /** GET /api/work-items with optional filters. */
  async listWorkItems(filters: WorkItemListFilters = {}): Promise<WorkItem[]> {
    const qs = new URLSearchParams();
    if (filters.productId)    qs.set('productId', filters.productId);
    if (filters.repoId)       qs.set('repoId', filters.repoId);
    if (filters.iterationRef) qs.set('iterationRef', filters.iterationRef);
    if (filters.track)        qs.set('track', filters.track);
    if (filters.parentId)     qs.set('parentId', filters.parentId);
    if (filters.assignedTo)   qs.set('assignedTo', filters.assignedTo);
    if (filters.tag)          qs.set('tag', filters.tag);
    if (filters.q)            qs.set('q', filters.q);
    if (filters.states?.length) qs.set('state', filters.states.join(','));
    if (filters.types?.length)  qs.set('type', filters.types.join(','));
    if (filters.pageSize)     qs.set('pageSize', String(filters.pageSize));
    if (filters.page)         qs.set('page', String(filters.page));
    const url = qs.toString() ? `/api/work-items?${qs}` : '/api/work-items?pageSize=500';
    return fetchJson<WorkItem[]>(url);
  },

  /** GET /api/work-items/{id}. */
  async getWorkItem(id: string): Promise<WorkItem> {
    return fetchJson<WorkItem>(`/api/work-items/${encodeURIComponent(id)}`);
  },

  /** POST /api/work-items — caller supplies the id. */
  async createWorkItem(body: WorkItemCreateBody): Promise<WorkItem> {
    return fetchJson<WorkItem>('/api/work-items', {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * GET /api/work-items/{id}/workstream-link — returns null when the work
   * item isn't tied to a story spawn (ad-hoc / external item).
   */
  async getWorkItemWorkstreamLink(id: string): Promise<{ storyId: string; workstreamId: string; spawnId: string } | null> {
    try {
      return await fetchJson(`/api/work-items/${encodeURIComponent(id)}/workstream-link`);
    } catch (e) {
      if (e instanceof ForgeApiError && e.status === 404) return null;
      throw e;
    }
  },

  /** PATCH /api/work-items/{id} — opt-in If-Match for the conflict policy. */
  async patchWorkItem(id: string, body: WorkItemPatchBody, ifMatch?: string | null): Promise<WorkItem> {
    const headers: Record<string, string> = { 'X-Forge-User': writerHeader() };
    if (ifMatch) headers['If-Match'] = ifMatch;
    return fetchJson<WorkItem>(`/api/work-items/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(body),
      headers,
    });
  },

  /** DELETE /api/work-items/{id} (soft) or ?force=true (hard, PO-only). */
  async deleteWorkItem(id: string, force = false): Promise<WorkItem | null> {
    const url = `/api/work-items/${encodeURIComponent(id)}${force ? '?force=true' : ''}`;
    if (force) {
      await fetchJson<void>(url, { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } });
      return null;
    }
    return fetchJson<WorkItem>(url, { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } });
  },

  /** POST /api/work-items/{id}/move with state and/or iterationRef. */
  async moveWorkItem(id: string, body: { state?: WorkItem['state']; iterationRef?: string }): Promise<WorkItem> {
    return fetchJson<WorkItem>(`/api/work-items/${encodeURIComponent(id)}/move`, {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/work-items/{id}/parent — null parent unsets the link. */
  async reparentWorkItem(id: string, parentId: string | null): Promise<WorkItem> {
    return fetchJson<WorkItem>(`/api/work-items/${encodeURIComponent(id)}/parent`, {
      method: 'POST',
      body: JSON.stringify({ parentId }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/work-items/sync/{repoId} — pulls remote into the local cache. */
  /**
   * POST /api/work-items/{id}/resync — Phase 14 follow-up. Force-refresh
   * a single WI from its provider (bypasses the WIQL change filter and
   * the revision-skip in WorkItemSyncService). Returns the updated WI.
   */
  async resyncWorkItem(id: string): Promise<WorkItem> {
    return fetchJson<WorkItem>(`/api/work-items/${encodeURIComponent(id)}/resync`, {
      method: 'POST',
      body: JSON.stringify({}),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * POST /api/prs/{id}/resync — force-refresh a single PR's state + comments
   * + commit/file stats from the provider. Hits PrSyncJob.RunSyncForAsync
   * directly so the row catches up immediately without waiting for the
   * 2 hr cadence.
   */
  async resyncPr(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/resync`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async syncWorkItems(repoId: string, body: WorkItemSyncBody): Promise<WorkItemSyncResult> {
    return fetchJson<WorkItemSyncResult>(`/api/work-items/sync/${encodeURIComponent(repoId)}`, {
      method: 'POST',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/timeline?from=…&to=… . */
  async getTimeline(from = -10, to = 5): Promise<TimelineResponse> {
    if (useLocalSeed()) {
      return buildLocalTimeline(from, to);
    }
    try {
      return await fetchJson<TimelineResponse>(`/api/timeline?from=${from}&to=${to}`);
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/timeline failed, using local sample data', err);
        return buildLocalTimeline(from, to);
      }
      throw err;
    }
  },

  // ----- Phase 15 — sprint & area path management -----------------------

  /** GET /api/products/{id}/iterations. */
  async listIterations(productId: string): Promise<ProductIteration[]> {
    return fetchJson<ProductIteration[]>(`/api/products/${encodeURIComponent(productId)}/iterations`);
  },

  /** GET /api/products/{id}/area-paths. */
  async listAreaPaths(productId: string): Promise<ProductAreaPath[]> {
    return fetchJson<ProductAreaPath[]>(`/api/products/${encodeURIComponent(productId)}/area-paths`);
  },

  /** GET /api/products/{id}/saved-prefixes. */
  async listSavedPrefixes(productId: string): Promise<SavedPrefix[]> {
    return fetchJson<SavedPrefix[]>(`/api/products/${encodeURIComponent(productId)}/saved-prefixes`);
  },

  /** POST /api/products/{id}/sprint-discovery. */
  async discoverSprintNodes(
    productId: string,
    req: SprintDiscoveryRequest,
  ): Promise<DiscoveryNode[]> {
    return fetchJson<DiscoveryNode[]>(`/api/products/${encodeURIComponent(productId)}/sprint-discovery`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/products/{id}/iterations/import — bulk upsert. */
  async importIterations(productId: string, req: ImportNodesRequest): Promise<ProductIteration[]> {
    return fetchJson<ProductIteration[]>(`/api/products/${encodeURIComponent(productId)}/iterations/import`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/products/{id}/area-paths/import — bulk upsert. */
  async importAreaPaths(productId: string, req: ImportNodesRequest): Promise<ProductAreaPath[]> {
    return fetchJson<ProductAreaPath[]>(`/api/products/${encodeURIComponent(productId)}/area-paths/import`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/products/{id}/area-paths/{areaPathId}/default — toggle. */
  async toggleDefaultAreaPath(productId: string, areaPathId: string): Promise<ProductAreaPath[]> {
    return fetchJson<ProductAreaPath[]>(
      `/api/products/${encodeURIComponent(productId)}/area-paths/${encodeURIComponent(areaPathId)}/default`,
      { method: 'PATCH', headers: { 'X-Forge-User': writerHeader() } });
  },

  /** POST /api/products/{id}/saved-prefixes. */
  async savePrefix(productId: string, req: SavePrefixRequest): Promise<SavedPrefix> {
    return fetchJson<SavedPrefix>(`/api/products/${encodeURIComponent(productId)}/saved-prefixes`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/products/{id}/saved-prefixes/{prefixId}. */
  async deleteSavedPrefix(productId: string, prefixId: string): Promise<void> {
    await fetchJson<void>(
      `/api/products/${encodeURIComponent(productId)}/saved-prefixes/${encodeURIComponent(prefixId)}`,
      { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } });
  },

  /** POST /api/products/{id}/saved-prefixes/{prefixId}/refresh. */
  async refreshSavedPrefix(productId: string, prefixId: string): Promise<SavedPrefix> {
    return fetchJson<SavedPrefix>(
      `/api/products/${encodeURIComponent(productId)}/saved-prefixes/${encodeURIComponent(prefixId)}/refresh`,
      { method: 'POST', headers: { 'X-Forge-User': writerHeader() } });
  },

  /** POST /api/repos/{id}/iteration-binding. */
  async addIterationBinding(repoId: string, req: IterationBindingRequest): Promise<unknown> {
    return fetchJson<unknown>(`/api/repos/${encodeURIComponent(repoId)}/iteration-binding`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/repos/{id}/iteration-binding/{bindingId}. */
  async removeIterationBinding(repoId: string, bindingId: string | number): Promise<void> {
    await fetchJson<void>(
      `/api/repos/${encodeURIComponent(repoId)}/iteration-binding/${encodeURIComponent(String(bindingId))}`,
      { method: 'DELETE', headers: { 'X-Forge-User': writerHeader() } });
  },

  // ─── Phase 17 — Persistent task queue ──────────────────────────────────

  async listQueue(opts?: { targetKind?: string; targetId?: string; state?: string }): Promise<TaskQueueEntry[]> {
    const qs = new URLSearchParams();
    if (opts?.targetKind) qs.set('targetKind', opts.targetKind);
    if (opts?.targetId)   qs.set('targetId', opts.targetId);
    if (opts?.state)      qs.set('state', opts.state);
    const tail = qs.toString();
    return fetchJson<TaskQueueEntry[]>(`/api/queue${tail ? `?${tail}` : ''}`);
  },

  async listTeamQueue(teamId: string): Promise<TaskQueueEntry[]> {
    return fetchJson<TaskQueueEntry[]>(`/api/teams/${encodeURIComponent(teamId)}/queue`);
  },

  async listCrewQueue(crewId: string): Promise<TaskQueueEntry[]> {
    return fetchJson<TaskQueueEntry[]>(`/api/crews/${encodeURIComponent(crewId)}/queue`);
  },

  async getCrewQueueStats(crewId: string): Promise<CrewQueueStats> {
    return fetchJson<CrewQueueStats>(`/api/crews/${encodeURIComponent(crewId)}/queue-stats`);
  },

  async ackQueue(queueId: number): Promise<TaskQueueEntry> {
    return fetchJson<TaskQueueEntry>(`/api/queue/${queueId}/ack`, {
      method: 'POST', body: '{}', headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async routeQueue(queueId: number, crewId: string): Promise<TaskQueueEntry> {
    return fetchJson<TaskQueueEntry>(`/api/queue/${queueId}/route`, {
      method: 'POST', body: JSON.stringify({ crewId }), headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async reassignQueue(queueId: number, newCrewId: string): Promise<TaskQueueEntry> {
    return fetchJson<TaskQueueEntry>(`/api/queue/${queueId}/reassign`, {
      method: 'POST', body: JSON.stringify({ newCrewId }), headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async cancelQueue(queueId: number, reason?: string): Promise<TaskQueueEntry> {
    return fetchJson<TaskQueueEntry>(`/api/queue/${queueId}/cancel`, {
      method: 'POST', body: JSON.stringify({ reason: reason ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async confirmStrain(queueId: number): Promise<TaskQueueEntry> {
    return fetchJson<TaskQueueEntry>(`/api/queue/${queueId}/strain-confirm`, {
      method: 'POST', body: '{}', headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // ── Live Execution (Phase 16.9) ──────────────────────────────────────

  async getWorkItemExecution(workItemId: string): Promise<unknown> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/execution`);
  },

  async getWorkItemTranscript(workItemId: string, opts?: { from?: number; before?: number; limit?: number; order?: string }): Promise<{ events: unknown[]; totalCount: number }> {
    const params = new URLSearchParams();
    if (opts?.from != null) params.set('from', String(opts.from));
    if (opts?.before != null) params.set('before', String(opts.before));
    if (opts?.limit != null) params.set('limit', String(opts.limit));
    if (opts?.order) params.set('order', opts.order);
    const qs = params.toString();
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/transcript${qs ? `?${qs}` : ''}`);
  },

  /**
   * GET /api/work-items/{id}/transcript/{eventId} — full untruncated event
   * payload. The bulk transcript stream truncates large string fields
   * (systemPrompt / userPrompt / tool results) to 320 chars per field; this
   * endpoint backs the "Show full prompt" affordance the UI surfaces when
   * a truncation marker appears in the inline preview.
   */
  async getWorkItemTranscriptEvent(workItemId: string, eventId: number | string): Promise<{
    id: number;
    workItemId: string;
    kind: string;
    agentId: string | null;
    crewId: string | null;
    payload: Record<string, unknown>;
    at: string;
  }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/transcript/${encodeURIComponent(String(eventId))}`);
  },

  /**
   * Phase 82.1 — GET /api/work-items/{id}/iteration-prompts.
   * Lists every persisted iteration_prompt pair (generator + evaluator)
   * for one work item. Payload is FULL untruncated — unlike the
   * /transcript stream which caps systemPrompt + userPrompt at 320
   * bytes, this endpoint returns the raw bytes the agent saw. Sorted
   * ascending by iter. Backs the in-tab pager on the Prompt tab.
   */
  async getWorkItemIterationPrompts(workItemId: string): Promise<{
    workItemId: string;
    iters: {
      iter: number;
      generator: IterationPromptDto | null;
      evaluator: IterationPromptDto | null;
      generatorAttempts: number;
      evaluatorAttempts: number;
    }[];
  }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/iteration-prompts`);
  },

  async clearWorkItemTranscript(workItemId: string): Promise<{ deleted: number; deletedDeliverables?: number; deletedPrompts?: number }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/transcript`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async requeueWorkItem(crewId: string, workItemId: string, opts?: { restart?: boolean }): Promise<unknown> {
    return fetchJson(`/api/crews/${encodeURIComponent(crewId)}/plan`, {
      method: 'POST',
      body: JSON.stringify({ workItemId, restart: opts?.restart ?? false }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async patchExecution(workItemId: string, body: { maxIterations?: number }): Promise<void> {
    await fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/execution`, {
      method: 'PATCH',
      body: JSON.stringify(body),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /**
   * Drag-to-reconcile on the Exec board. POSTs the operator's desired target
   * execution status; the server validates against an inspector that computes
   * the *authoritative* status from runner + queue + workstream + deliverable.
   *
   * On match → 200 with `{ previous, current, reason }`.
   * On mismatch → throws `ForgeApiError` with `status === 409` and
   *   `problem = { error: 'execution_status_mismatch', requested,
   *   authoritative, reason, current }` so the caller can show a toast.
   * On invalid target → throws with `status === 400`.
   * On missing WI → throws with `status === 404`.
   *
   * This is drift-correction, NOT a runtime action — does not cancel agents,
   * enqueue work, or open PRs.
   */
  async reconcileExecutionStatus(
    workItemId: string,
    targetStatus: string,
  ): Promise<{ id: string; previous: string; current: string; reason: string }> {
    return fetchJson<{ id: string; previous: string; current: string; reason: string }>(
      `/api/work-items/${encodeURIComponent(workItemId)}/execution-status`,
      {
        method: 'POST',
        body: JSON.stringify({ targetStatus }),
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
  },

  // Authoritative commit list for a work item's worktree branch — runs
  // `git log --name-status origin/{base}..HEAD` server-side. Replaces the
  // worktree-tick reconstruction the live-exec view used to do, which
  // silently dropped commits faster than the tick interval.
  async getWorkItemCommits(workItemId: string): Promise<{
    worktreePath: string | null;
    branch: string;
    baseBranch: string;
    ahead: number;
    behind: number;
    headSha: string | null;
    commits: Array<{
      sha: string;
      message: string;
      author: string;
      at: string;
      added: string[];
      modified: string[];
      deleted: string[];
      other: Array<{ status: string; path: string; oldPath?: string | null }>;
    }>;
    uncommitted: { added: string[]; modified: string[]; deleted: string[] };
    /**
     * Phase 49 follow-up — cumulative diff between the worktree's working
     * tree (committed + uncommitted) and `origin/{baseBranch}`. Same
     * scope a reviewer sees in the PR's file list. Use this for the
     * `+N ~N -N` counters; fall back to `uncommitted` only when
     * `branchTotals` is missing (older server build).
     */
    branchTotals?: { added: string[]; modified: string[]; deleted: string[] };
  }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/commits`, {
      method: 'GET',
    });
  },

  // Single Resume — branches on the last deliverable verdict:
  //   pass     → re-run delivery only (build gate + push + PR), skip the loop
  //   iterate  → continue iterating (DeliverableLoop seeds the prior chain)
  //   stalled  → ditto
  //   reject / no deliverables → 400
  // The optional `note` is injected into the next iter's user prompt; on
  // the pass path it's silently ignored (no agent runs to read it).
  async resume(workItemId: string, note?: string): Promise<{
    workItemId: string;
    queueId: number;
    crewId: string;
    mode: 'delivery-replay' | 'continue-iter';
    priorIter: number;
    priorScore: number;
    priorVerdict: string;
    noteApplied: boolean;
  }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/resume`, {
      method: 'POST',
      body: JSON.stringify({ note: note ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/work-items/{id}/questions — list deduped questions for the
   *  conversation-tab open-questions wizard. Phase 39 (Layer C). */
  async listQuestions(workItemId: string): Promise<{
    workItemId: string;
    items: Array<{
      id: number;
      externalId: string;
      iter: number;
      text: string;
      kind: 'choice' | 'open';
      choices: string[];
      answer: string | null;
      answeredAt: string | null;
      answerRevision?: string | null;
      answeredBy: string | null;
      /** Operator-facing rationale (terms / per-choice logic / recommendation);
       *  null until agent-authored or generated via explainQuestion. */
      explanation: QuestionExplanation | null;
    }>;
    unansweredCount: number;
    answeredCount: number;
    totalCount: number;
  }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/questions`);
  },

  /** POST /api/work-items/{id}/questions/{externalId}/explain — return the
   *  question's explanation, generating + caching one (LLM) when none exists.
   *  Idempotent: agent-authored / previously-generated explanations win and
   *  cost no model call (cached=true). Powers the Questions-tab Explain popup. */
  async explainQuestion(
    workItemId: string,
    externalId: string,
    force = false,
  ): Promise<{ workItemId: string; externalId: string; explanation: QuestionExplanation; cached: boolean }> {
    const qs = force ? '?force=true' : '';
    return fetchJson(
      `/api/work-items/${encodeURIComponent(workItemId)}/questions/${encodeURIComponent(externalId)}/explain${qs}`,
      { method: 'POST', headers: { 'X-Forge-User': writerHeader() } },
    );
  },

  /** POST /api/work-items/{id}/questions/answer — submit operator answers.
   *  Server records them, the next iteration's prompt prepends a `## Operator
   *  answers` block, and the verdict-gate releases pass when none remain. */
  async answerQuestions(
    workItemId: string,
    answers: Array<{ externalId: string; answer: string; expectedRevision?: number; editExisting?: boolean; expectedAnswerRevision?: string | null }>,
  ): Promise<{ workItemId: string; unansweredCount: number }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/questions/answer`, {
      method: 'POST',
      body: JSON.stringify({ answers }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/work-items/{id}/questions/dismiss — mark questions as
   *  "(dismissed by operator)" so they stop blocking the verdict gate
   *  without forcing the operator to write a real answer. Used for legacy
   *  prose questions that aren't usefully answerable. */
  async dismissQuestions(
    workItemId: string,
    externalIds: string[],
  ): Promise<{ workItemId: string; unansweredCount: number }> {
    return fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/questions/dismiss`, {
      method: 'POST',
      body: JSON.stringify({ externalIds }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  async clearAgentAssignment(workItemId: string, reason?: string): Promise<void> {
    await fetchJson(`/api/work-items/${encodeURIComponent(workItemId)}/assigned-agent`, {
      method: 'DELETE',
      body: reason ? JSON.stringify({ reason }) : undefined,
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Performance scorecards -------------------------------------------

  async getPerformanceScorecards(window: ReviewWindow = 'trailing20'): Promise<PerformanceScorecardsResponse> {
    return fetchJson<PerformanceScorecardsResponse>(`/api/performance/scorecards?window=${window}`);
  },

  // -- Admin / maintenance ----------------------------------------------

  /** 2026-05-07 — DB stats for the Settings → Maintenance panel. */
  async getDbStats(): Promise<DbStats> {
    return fetchJson<DbStats>(`/api/admin/db/stats`);
  },

  /** 2026-05-07 — manually trigger a transcript prune. `dryRun` returns
   *  the counts without modifying the DB; `vacuum` runs SQLite VACUUM
   *  after a successful prune to reclaim file-system space. */
  async pruneTranscripts(opts: { dryRun?: boolean; vacuum?: boolean } = {}): Promise<PruneStats> {
    const params = new URLSearchParams();
    if (opts.dryRun) params.set('dryRun', 'true');
    if (opts.vacuum) params.set('vacuum', 'true');
    const qs = params.toString();
    return fetchJson<PruneStats>(`/api/admin/transcripts/prune${qs ? `?${qs}` : ''}`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- DB Split -----------------------------------------------------------

  /** GET /api/admin/db/split-status — check if split is possible/applied. */
  async getDbSplitStatus(): Promise<DbSplitStatus> {
    return fetchJson<DbSplitStatus>('/api/admin/db/split-status');
  },

  /** POST /api/admin/db/split-dry-run — analyze partition without modifying DB. */
  async splitDryRun(): Promise<SplitDryRunReport> {
    return fetchJson<SplitDryRunReport>('/api/admin/db/split-dry-run', {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/admin/db/split-apply — actually partition forge.db into per-product DBs. */
  async splitApply(): Promise<SplitApplyResult> {
    return fetchJson<SplitApplyResult>('/api/admin/db/split-apply', {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },
};

export interface DbStats {
  dbPath: string;
  fileBytes: number;
  modifiedAt: string | null;
  transcriptRows: number;
  transcriptPayloadBytes: number;
  deliverableRows: number;
}

export interface PruneStats {
  dryRun: boolean;
  workItemsScanned: number;
  workItemsPruned: number;
  rowsDeleted: number;
  vacuumRan: boolean;
  elapsedMs: number;
}

export interface DbSplitStatus {
  alreadyApplied: boolean;
  splitEnabled: boolean;
  productCount: number;
  controlHasProductTables: boolean;
  canSplit: boolean;
}

export interface SplitDryRunReport {
  generatedAt: string;
  controlDbPath: string;
  controlDbSizeBytes: number;
  totalRows: number;
  controlRows: number;
  productRows: number;
  orphanRows: number;
  unknownRows: number;
  tables: Array<{
    tableName: string;
    kind: string;
    strategy: string;
    partitionColumn: string | null;
    totalRows: number;
    classifiedRows: number;
    orphanRows: number;
  }>;
  products: Array<{
    productId: string;
    productSlug: string;
    totalRows: number;
    rowsPerTable: Record<string, number>;
  }>;
  orphans: Array<{
    tableName: string;
    kind: string;
    count: number;
    detail: string;
  }>;
  unknownTables: Array<{ tableName: string; rowCount: number }>;
}

export interface SplitApplyResult {
  alreadyApplied: boolean;
  backupPath: string | null;
  productDbs: string[];
  rowsCopied: number;
  errors: string[];
  success: boolean;
}

function buildLocalTimeline(from: number, to: number): TimelineResponse {
  const epicColor = new Map(sampleState.epics.map((e) => [e.id, e.color]));
  const bars = sampleState.workstreams
    .filter((w) => !(w.barEnd < from || w.barStart > to))
    .map((w) => ({
      workstreamId: w.id,
      name: w.name,
      branch: w.branch,
      epic: w.epic,
      epicColor: epicColor.get(w.epic) ?? w.epic,
      status: w.status,
      barStart: w.barStart,
      barEnd: w.barEnd,
      planned: !!w.planned,
      summary: w.summary,
      agents: w.agents,
      ahead: w.ahead,
      behind: w.behind,
      conflicts: w.conflicts,
      lastActive: w.lastActive,
    }));
  const markers = sampleState.wsEvents
    .filter((e) => e.day >= from && e.day <= to)
    .map((e) => ({ workstreamId: e.ws, day: e.day, kind: e.kind, label: e.label }));
  return { from, to, bars, markers };
}
