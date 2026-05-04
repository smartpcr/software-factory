import type {
  AgentDetail,
  AgentLevelHistoryEntry,
  Card,
  ColumnId,
  CostSummary,
  Crew,
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
  PayrollTodaySummary,
  PromotionRunResponse,
  Repo,
  SalaryTodaySummary,
  SavePrefixRequest,
  SavedPrefix,
  SoulPayload,
  SprintDiscoveryRequest,
  TaskQueueEntry,
  CrewQueueStats,
  TimelineResponse,
  UserSettings,
  Workstream,
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

// forge_implementation.md Phase 28 — story spawn types.
export type StorySpawnPhase = 'plan' | 'decompose' | 'implement' | 'review' | 'qa' | 'done';

export interface StorySpawn {
  id: string;
  storyId: string;
  storyTitle: string;
  repoId: string | null;
  phase: StorySpawnPhase;
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
}

export interface StorySpawnWorkstreamSummary {
  id: string;
  name: string;
  branch: string;
  status: string;
  cards: string[];
  prs: string[];
  deps: string[];
  /** Phase 28+ — live runtime fields surfaced from the deliverables /
   *  agent_usage tables. All optional so older clients keep working and
   *  workstreams that haven't claimed yet (planning) report zeroes. */
  agents?: string[];
  iter?: number;
  latestScore?: number | null;
  latestVerdict?: string | null;
  latestGenAgentId?: string | null;
  latestEvalAgentId?: string | null;
  lastActiveAt?: string | null;
  costUsd?: number;
  commits?: number;
  ahead?: number;
  behind?: number;
}

export interface StorySpawnDetail {
  spawn: StorySpawn;
  workstreams: StorySpawnWorkstreamSummary[];
}

export interface StorySpawnDecomposeWorkstream {
  title: string;
  description?: string;
  acceptanceCriteria?: string[];
  deps?: string[];
  deliverables?: string[];
  timeline?: string;
  estimatedEffort?: string;
}

export interface StorySpawnArtifacts {
  plan: { branch: string | null; pr: string | null; files: string[] };
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

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const { headers: extraHeaders, ...rest } = init ?? {};
  // Phase 8.9 — automatically scope every read to the active product when set.
  const productId = getActiveProductId();
  const res = await fetch(`${baseUrl}${path}`, {
    ...rest,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(productId ? { 'X-Forge-Product-Id': productId } : {}),
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
  demotionRisk: boolean;
  verdict: PromotionVerdict;
  verdictNote: string;
  lastPromotedAt: string | null;
  nextEligibleAt: string;
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

  /** POST /api/products/{productId}/stories — create a story under a product. */
  async createProductStory(
    productId: string,
    req: { name: string; id?: string; status?: string; description?: string; points?: number },
  ): Promise<ProductStory> {
    return fetchJson<ProductStory>(`/api/products/${encodeURIComponent(productId)}/stories`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/products/{productId}/stories/{storyId} — edit a story's name / desc / points / status. */
  async updateProductStory(
    productId: string,
    storyId: string,
    body: { name?: string; status?: string; description?: string; points?: number },
  ): Promise<ProductStory> {
    return fetchJson<ProductStory>(
      `/api/products/${encodeURIComponent(productId)}/stories/${encodeURIComponent(storyId)}`,
      {
        method: 'PATCH',
        body: JSON.stringify(body),
        headers: { 'X-Forge-User': writerHeader() },
      },
    );
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

  /** POST /api/prs/{id}/merge. */
  async mergePr(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/merge`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
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

  /** GET /api/workstreams. */
  async getWorkstreams(): Promise<Workstream[]> {
    if (useLocalSeed()) return sampleState.workstreams;
    try {
      return await fetchJson<Workstream[]>('/api/workstreams');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/workstreams failed, using local sample data', err);
        return sampleState.workstreams;
      }
      throw err;
    }
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

  /** DELETE /api/workstreams/{id}?force=true. */
  async deleteWorkstream(id: string, force = false): Promise<void> {
    await fetchJson<void>(`/api/workstreams/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // ---------------------------------------------------------------------
  // forge_implementation.md Phase 28 — story-driven spawn endpoints.
  // ---------------------------------------------------------------------

  /** POST /api/stories/{storyId}/spawn — start the multi-team delivery flow. */
  async spawnStory(storyId: string, repoId?: string | null): Promise<StorySpawn> {
    return fetchJson<StorySpawn>(`/api/stories/${encodeURIComponent(storyId)}/spawn`, {
      method: 'POST',
      body: JSON.stringify({ repoId: repoId ?? null }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/stories/spawns — list all story spawns (optionally filtered by phase). */
  async listStorySpawns(phase?: string): Promise<StorySpawn[]> {
    const qs = phase ? `?phase=${encodeURIComponent(phase)}` : '';
    return fetchJson<StorySpawn[]>(`/api/stories/spawns${qs}`);
  },

  /** GET /api/stories/{storyId}/spawn — full spawn state + child workstreams. */
  async getStorySpawn(storyId: string): Promise<StorySpawnDetail> {
    return fetchJson<StorySpawnDetail>(`/api/stories/${encodeURIComponent(storyId)}/spawn`);
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
     *  merged yet) → completed (PR merged) | pending (no iterations yet). */
    status: 'live' | 'iterating' | 'awaiting-pr-review' | 'completed' | 'pending';
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
      notes: string;
      genCostUsd: number;
      evalCostUsd: number;
      judgeAgentId: string | null;
      judgeScore: number | null;
      judgeNotes: string | null;
      createdAt: string;
    }>;
    totalCostUsd: number;
  }> {
    return fetchJson(
      `/api/stories/${encodeURIComponent(storyId)}/spawn/artifacts/${encodeURIComponent(key)}/iterations`);
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

  // -- Phase 10 — preferences + crews ------------------------------------

  /** GET /api/me/settings. */
  async getMySettings(): Promise<UserSettings> {
    return fetchJson<UserSettings>('/api/me/settings');
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
  async patchAgent(agentId: string, patch: {
    name?: string;
    role?: string;
    soulPath?: string;
    backend?: string;
    invokeMode?: string;
    generatorModel?: string;
    evaluatorModel?: string;
  }): Promise<AgentDetail> {
    return fetchJson<AgentDetail>(`/api/agents/${encodeURIComponent(agentId)}`, {
      method: 'PATCH',
      body: JSON.stringify(patch),
      headers: { 'X-Forge-User': writerHeader() },
    });
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

  async clearWorkItemTranscript(workItemId: string): Promise<{ deleted: number }> {
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
};

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
