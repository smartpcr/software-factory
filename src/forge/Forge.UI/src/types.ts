// Forge UI — shared types. Mirror of Forge.Api/Contracts/ForgeContracts.cs.
// Keep field names + casing in sync; the API serialises with camelCase.

export type Role = 'arch' | 'eng' | 'rev' | 'qa' | 'human' | 'tech-lead' | 'product-owner';
export type CardType = 'story' | 'task' | 'bug';
export type ColumnId = 'backlog' | 'design' | 'build' | 'review' | 'done';
export type Priority = 'low' | 'med' | 'high';
export type EpicStatus = 'active' | 'merging' | 'planning';
export type WorkstreamStatus = 'active' | 'merging' | 'review' | 'planning' | 'idle';

export interface Agent {
  /**
   * Stable identifier (e.g. crew member id like 'E1', GitHub login,
   * ADO descriptor). Provider mappers can surface a null when the
   * upstream payload lacks an identity (anonymous reviewers, deactivated
   * accounts, bots without descriptors); consumers must guard before
   * calling string operations like `startsWith` / `slice`.
   */
  id: string | null;
  role: Role;
  name: string;
  imageUrl?: string | null;
}

export interface Epic {
  id: string;
  name: string;
  status: EpicStatus;
  progress: number;
  crew: string[];
  cost: number;
  color: string;
}

export interface ProductStory {
  id: string;
  productId: string;
  name: string;
  status: string;
  sort: number;
  createdAt: string;
  /** User prompt the architect agent receives when this story is picked up. */
  description: string;
  points: number;
}

export interface Card {
  id: string;
  epic: string;
  col: ColumnId;
  type: CardType;
  title: string;
  tags: string[];
  pts: number;
  priority: Priority;
  agent: Agent | null;
  cost: number;
  autoNext?: string | null;
  live?: boolean;
  progress?: number | null;
  branch?: string | null;
  humanGate?: boolean;
  gateReason?: string | null;
  blocked?: boolean;
  blockedReason?: string | null;
  doneAt?: string | null;
}

export interface Column {
  id: ColumnId;
  name: string;
  wipLimit: number | null;
}

export interface Workstream {
  id: string;
  name: string;
  branch: string;
  base: string;
  epic: string;
  status: WorkstreamStatus;
  worktree: string[];
  created: number;
  lastActive: number;
  agents: string[];
  prs: string[];
  cards: string[];
  commits: number;
  ahead: number;
  behind: number;
  conflicts: number;
  summary: string;
  barStart: number;
  barEnd: number;
  planned?: boolean;
  /** Phase 8.11 — repo this workstream belongs to. Null on legacy seed rows. */
  repoId?: string | null;
}

export interface WsEvent {
  ws: string;
  day: number;
  kind: 'pr' | 'merge' | 'review' | 'block';
  label: string;
}

export type BuildState = 'running' | 'queued' | 'passing' | 'failing';

export interface BuildStatus {
  status: BuildState;
  step: string;
  percent: number;
  failingTest?: string | null;
}

export interface ImplStatus {
  phase: string;
  step: string;
  percent: number;
}

export type ReviewVerdictKind = 'approved' | 'changes' | 'pending';

export interface ReviewVerdict {
  agent: string;
  name: string;
  verdict: ReviewVerdictKind;
  summary: string;
}

export type PrColumn = 'implementation' | 'build' | 'review' | 'merged';

export interface Pr {
  id: string;
  col: PrColumn;
  epic: string;
  epicColor: string;
  workstream: string;
  base: string;
  branch: string;
  title: string;
  author: Agent;
  additions: number;
  deletions: number;
  files: number;
  commits: number;
  /** Phase 28 — number of updates / iterations on the PR (ADO "Updates" tab count). 0/undefined when unknown. */
  updates?: number;
  age: string;
  build: BuildStatus | null;
  review: ReviewVerdict | null;
  conflicts: boolean;
  humanGate: boolean;
  gateReason?: string | null;
  conflictPaths?: string | null;
  autoMerge?: boolean;
  blocked?: boolean;
  impl?: ImplStatus | null;

  // Phase 24 — sync metadata + comment counts surfaced on the card.
  isActive?: boolean;
  /** Total open comments on this PR. */
  openCommentCount?: number;
  /** Subset of openCommentCount that need an agent response. */
  attentionCommentCount?: number;
  /** forge_implementation.md Phase 27 — # of failed-terminal builds with no superseding success. */
  failedBuildCount?: number;
  /** Phase 27 — # of currently-running builds for this PR. */
  runningBuildCount?: number;
  /** Phase 27 — most-recent failed build id; click target for the chip. */
  latestFailedBuildId?: number | null;
  /** ISO-8601 of the most-recent successful sync; null when never synced. */
  lastSyncAt?: string | null;
  /** idle | running | failed. */
  lastSyncStatus?: string;
  /** Provider key (`ado` | `github`); null for prototype/seed PRs. */
  provider?: string | null;
  /** Phase 27 — linked work item ID. */
  workItemId?: string | null;
  /** Phase 27 — PR description body from the provider. */
  description?: string | null;
  /** Phase 27 — clickable web URL to open the PR in ADO/GitHub. */
  webUrl?: string | null;
  /** Phase 27 — PR creation timestamp (ISO-8601). */
  createdAt?: string | null;
  /** Phase 27 — PR last updated timestamp (ISO-8601). */
  updatedAt?: string | null;
  /** Phase 29 — repo this PR belongs to (for filtering). */
  repoId?: string | null;
}

// Phase 24 — comment lifecycle types -----------------------------------------

export type PrCommentState = 'active' | 'resolved' | 'wont-fix' | 'closed' | 'pending';
export type PrCommentAuthorKind = 'human' | 'agent' | 'system';

export interface PrComment {
  id: number;
  prId: string;
  providerCommentId: string;
  threadId: string;
  parentCommentId?: number | null;
  authorId?: string | null;
  authorKind: PrCommentAuthorKind;
  body: string;
  filePath?: string | null;
  line?: number | null;
  state: PrCommentState;
  createdAt: string;
  updatedAt: string;
  resolvedByUserId?: string | null;
  resolvedAt?: string | null;
  linkedCommitSha?: string | null;
  linkedAgentResponseId?: number | null;
  requiresAttention: boolean;
  /** Phase 34 — user reaction: 'thumbsup' | 'thumbsdown' | null. */
  reaction?: string | null;
}

export interface PrCommentHistory {
  id: number;
  commentId: number;
  kind: 'opened' | 'edited' | 'replied' | 'resolved' | 'reopened' | 'linked' | 'unlinked' | 'attention-flipped';
  fromValue?: string | null;
  toValue?: string | null;
  byUserId?: string | null;
  at: string;
  payloadJson?: string | null;
}

export interface WsStat {
  label: string;
  val: string;
  unit: string;
  trend: 'up' | 'down' | 'flat';
  trendVal: string;
}

export interface OverviewActivity {
  time: string;
  who: string;
  msg: string;
}

export interface OverviewAgent {
  id: string;
  name: string;
  role: Role;
  status: 'working' | 'queued' | 'idle';
  task: string;
  /** Optional avatar URL — usually a Vite-imported asset from src/assets/crews. */
  avatar?: string;
  /** Short skill chips shown next to the name. Keep each ≤ 24 chars, ≤ 4 entries. */
  capabilities?: string[];
  /** One-sentence persona / why this agent matches its role. */
  bio?: string;
}

export interface ForgeState {
  epics: Epic[];
  cards: Card[];
  columns: Column[];
  workstreams: Workstream[];
  wsEvents: WsEvent[];
  prs: Pr[];
  wsStats: WsStat[];
  overviewActivity: OverviewActivity[];
  overviewAgents: OverviewAgent[];
}

// --- Timeline (Phase 2) ---------------------------------------------------

export interface TimelineBar {
  workstreamId: string;
  name: string;
  branch: string;
  epic: string;
  epicColor: string;
  status: WorkstreamStatus;
  barStart: number;
  barEnd: number;
  planned: boolean;
  summary: string;
  agents: string[];
  ahead: number;
  behind: number;
  conflicts: number;
  lastActive: number;
}

export interface TimelineMarker {
  workstreamId: string;
  day: number;
  kind: WsEvent['kind'];
  label: string;
}

export interface TimelineResponse {
  from: number;
  to: number;
  bars: TimelineBar[];
  markers: TimelineMarker[];
}

export type TimelineZoom = 'day' | 'week' | 'sprint';
export type TimelineLane = 'workstream' | 'epic';
export type TimelineFilter = 'all' | 'active' | 'merging' | 'planning';

// --- Products & repos (Phase 8) ------------------------------------------

export type RepoKind = 'ado' | 'github';

export interface Product {
  id: string;
  name: string;
  slug: string;
  description: string;
  ownerUserId: string | null;
  createdAt: string;
}

export interface Repo {
  id: string;
  productId: string;
  kind: RepoKind;
  remoteUrl: string;
  displayName: string;
  defaultBranch: string;
  cloneRoot: string;
  adoOrg?: string | null;
  adoProject?: string | null;
  ghOwner?: string | null;
  ghRepo?: string | null;
  /** Phase 23.3 — multi-area-path scope for ADO; OR'd in WIQL. Null/empty = whole project. */
  areaPaths?: string[] | null;
  /** Phase 23.3 — ISO timestamp of the last successful work-item sync run. */
  lastSyncAt?: string | null;
  /** Phase 23.3 — opaque resume cursor written by a partial sync run. */
  lastSyncCursor?: string | null;
  /** Phase 23.3 — `idle | running | failed`. */
  lastSyncStatus?: 'idle' | 'running' | 'failed';
  /** Phase 23.3 — error message from the most recent failed sync. */
  lastSyncError?: string | null;
  /** Phase 23.3 — when a sync run started; used to enforce the 19-minute stale threshold. */
  isRunningSince?: string | null;
  /** Phase 23.3 — per-repo override for the `agent-ready` auto-team-assign target. Null falls back to `Engineering`. */
  agentReadyDefaultTeam?: string | null;
  /** Phase 23.3 — override for the initial sync window (ADO only); null defaults to 365 days. */
  initialSyncWindowDays?: number | null;
  /** Phase 26/29 — init command run as first pipeline step. Null = step skipped. */
  initCommand?: string | null;
  /** Phase 26/29 — restore / dependency-install command. Null = step skipped. */
  restoreCommand?: string | null;
  /** Phase 26 — build command run before PR creation (e.g., `dotnet build Workflow.sln`). Null = gate skipped. */
  buildCommand?: string | null;
  /** Phase 26 — test command run before PR creation (e.g., `dotnet test src/Foo.Tests/Foo.Tests.csproj`). */
  testCommand?: string | null;
  /** Phase 26 — build timeout in seconds. Null defaults to 600. */
  buildTimeoutSec?: number | null;
  /** Phase 26 — test timeout in seconds. Null defaults to 600. */
  testTimeoutSec?: number | null;
  /** Phase 33 — default assignee filter for WI sync. */
  wiSyncAssignedTo?: string | null;
  /** Phase 33 — relative days back for WI sync date window. */
  wiSyncDateFromDays?: number | null;
  /** Phase 33 — absolute start date for WI sync (ISO). */
  wiSyncFromDate?: string | null;
  /** Phase 33 — absolute end date for WI sync (ISO). */
  wiSyncToDate?: string | null;
  /** Phase 33 — default PR author filter. */
  prSyncCreatedBy?: string | null;
  /** Phase 33 — default PR state filter (active/completed/all). */
  prSyncStatus?: string | null;
  /** Phase 33 — pipeline definition IDs to sync builds from. */
  buildPipelineIds?: string[] | null;
  /** Phase 34 — per-repo work-item sync interval in seconds. Null = inherit global (7200s). */
  workItemSyncIntervalSeconds?: number | null;
  /** Phase 34 — per-repo PR sync interval in seconds. Null = inherit global (7200s). */
  prSyncIntervalSeconds?: number | null;
  /** Phase 34 — per-repo build sync interval in seconds. Null = inherit global (7200s). */
  buildSyncIntervalSeconds?: number | null;
  /** Phase 34 — ISO upstream-changedate cursor (max System.ChangedDate / updated_at observed). */
  lastUpstreamChangedDate?: string | null;
}

/** Phase 23.3 — one row in the per-repo sync history. */
export interface WorkItemSyncRun {
  id: string;
  repoId: string;
  kind: 'full' | 'delta' | 'manual';
  startedAt: string;
  finishedAt?: string | null;
  itemsAdded: number;
  itemsUpdated: number;
  itemsConflicted: number;
  errorMessage?: string | null;
  completedSuccessfully: boolean;
}

/** Phase 25 — per-item detail for a sync run. */
export interface SyncRunItem {
  entityKind: 'workitem' | 'pr';
  entityId: string;
  title: string;
  action: 'added' | 'updated' | 'removed';
  providerRef?: number | null;
}

/** Phase 25 follow-up — rich detail returned by /items/{kind}/{id}/detail. */
export interface SyncRunItemDetail {
  entityKind: 'workitem' | 'pr';
  entityId: string;
  providerRef?: number | null;
  title: string;
  action: 'added' | 'updated' | 'removed';
  /** ISO upstream last-update timestamp (System.ChangedDate / updated_at). */
  lastUpdateDate?: string | null;
  /** Human-readable summary line: 'New', 'Removed', or "<state> · N comment(s) · build: <state>". */
  summary: string;
  workItem?: {
    state: string;
    type: string;
    points: number;
    priority: string;
    areaPath: string;
    iterationRef: string;
    assignedTo?: string | null;
    parentId?: string | null;
    tags?: string | null;
  } | null;
  pr?: {
    col: string;
    branch: string;
    base: string;
    additions: number;
    deletions: number;
    files: number;
    workItemId?: string | null;
    isActive: boolean;
    openComments: number;
    totalComments: number;
  } | null;
  latestBuild?: {
    state: string;
    result?: string | null;
    pipelineName?: string | null;
    webUrl?: string | null;
    startedAt?: string | null;
    finishedAt?: string | null;
  } | null;
}

export interface DiscoverableRepo {
  kind: RepoKind;
  displayName: string;
  remoteUrl: string;
  defaultBranch: string;
  adoOrg?: string | null;
  adoProject?: string | null;
  ghOwner?: string | null;
  ghRepo?: string | null;
}

/** Stats roll-up shown on the /products grid card. */
export interface ProductStats {
  productId: string;
  repoCount: number;
  activeWorkstreams: number;
  openPrs: number;
  lastActivity: string | null;
}

// --- Identity (Phase 9) ---------------------------------------------------

export interface CurrentUserProvider {
  connected: boolean;
  account: string | null;
  expiresAt: string | null;
  error: string | null;
}

export interface CurrentUser {
  /** Stable id derived from the first connected provider's account. Empty when no provider is connected. */
  id: string;
  displayName: string;
  email: string | null;
  login: string | null;
  initials: string;
  avatarUrl: string | null;
  role: Role;
  providers: {
    ado: CurrentUserProvider;
    github: CurrentUserProvider;
  };
}

/** Phase 10.1 — per-user preferences. */
export interface UserSettings {
  userId: string;
  defaultAdoOrgUrl: string | null;
  defaultAdoProject: string | null;
  defaultGitHubOwner: string | null;
  defaultProductId: string | null;
  defaultRepoId: string | null;
  browserPreference: string | null;
  theme: string | null;
  notificationsEnabled: boolean;
  /** Phase 9.11 — preferred Microsoft account for ADO sign-in (email). */
  preferredAdoAccount: string | null;
  /** Phase 9.11 — preferred GitHub login (gh username) for sign-in. */
  preferredGitHubAccount: string | null;
  updatedAt: string;
}

/** Phase 13b — one row in a crew's roster (lead is rendered separately). */
export interface CrewMember {
  agentId: string;
  /** "coder" | "reviewer" | "architect" | "qa" | "generator" | "evaluator" | other. */
  role: string;
  addedAt: string;
  addedBy: string | null;
}

/** Phase 10.6 — crew. */
export interface Crew {
  id: string;
  name: string;
  summary: string;
  lead: string | null;
  model: string;
  modelInputPricePerMTok: number;
  modelOutputPricePerMTok: number;
  /** Phase 11.10 — soft daily cap on crew spend. */
  dailyBudgetUsd?: number | null;
  /** Phase 11.10 — hard cap per single invocation. */
  maxTaskBudgetUsd?: number | null;
  // Phase 13.1 additions ──────────────────────────────────────────────
  /** Parent crew id; null = root. */
  parentCrewId?: string | null;
  /** 0=root, 1=team, 2=squad. Persisted for cheap roll-ups. */
  depth?: number;
  /** Phase 11.5b backend: 'copilot' (default) | 'claude'. */
  backend?: string;
  /** Phase 11.5b invoke mode: 'sdk' (default) | 'cli'. */
  invokeMode?: string;
  /** Members of this crew with their per-crew role (lead is rendered separately). */
  roster?: CrewMember[] | null;
  /** Domain-expert content-ids ingested as capabilities. */
  capabilities?: string[];
  /** How many tasks the crew can claim concurrently. */
  maxConcurrent?: number;
  /** Hard ceiling on tasks completed per ISO week. */
  weeklyCap?: number;
  /** Phase 13b — default generator agent for plan / coding tasks. Null = use lead. */
  defaultGeneratorAgentId?: string | null;
  /** Phase 13b — default evaluator agent for the pair. Null = single-agent mode. */
  defaultEvaluatorAgentId?: string | null;
  /**
   * Phase 13b — model the evaluator runs against. Distinct from `model`
   * (which becomes the generator's model) so the pair always runs on two
   * different models. Null = inherit `model` (warns).
   */
  evaluatorModel?: string | null;
  /**
   * Phase 16.2 — pair role: 'standalone' (default), 'generator', or 'evaluator'.
   */
  pairRole?: string;
  /** Phase 16.2 — id of the crew this one is paired with (symmetric). */
  pairedWithCrewId?: string | null;
  /**
   * Phase 18 — schedule band. 'day' = 09:00–21:00 local; 'night' =
   * 21:00–09:00 next morning. Defaults to 'day'.
   */
  shift?: 'day' | 'night';
}

/** Phase 11.3 — agent listing payload (mirrors AgentDetail on the server). */
export interface AgentDetail {
  id: string;
  name: string;
  role: string;
  crewId: string | null;
  soulPath: string | null;
  capabilities: string[];
  state: string;
  /** Phase 13e — per-agent backend override. Null = inherit crew. */
  backend?: string | null;
  /** Phase 13e — per-agent invoke-mode override. Null = inherit crew. */
  invokeMode?: string | null;
  /** Phase 13e — model when this agent runs as the pair's generator. */
  generatorModel?: string | null;
  /** Phase 13e — model when this agent runs as the pair's evaluator. */
  evaluatorModel?: string | null;
  /** Distinct work items this agent has worked on (from agent_usage ledger). */
  workItemCount?: number;
}

/** Phase 13.7 — domain-expert index entry (built-in team caps + sha256 docs). */
export interface DomainExpert {
  contentId: string;
  repoId: string;
  path: string;
  topic: string;
  summary: string;
  indexedAt: string;
}

/** Phase 11.3 — soul.md GET payload. */
export interface SoulPayload {
  agentId: string;
  /** Resolved file-system path. */
  path: string;
  /** Raw markdown contents (empty string when the file doesn't exist yet). */
  contents: string;
  /** Parsed YAML front-matter `capabilities:` list. */
  capabilities: string[];
  /** False when the file hasn't been authored yet — UI should offer to create. */
  exists: boolean;
}

/** Phase 11.2 — model registry entry. */
export interface ModelInfo {
  id: string;
  vendor: string;
  family: string;
  displayName: string;
  contextWindow: number;
  inputPricePerMTok: number;
  outputPricePerMTok: number;
  supportsTools: boolean;
  supportsStreaming: boolean;
  status: string;
}

/** Phase 13.1 — crew_history audit row. */
export interface CrewHistoryEntry {
  id: number;
  at: string;
  crewId: string;
  kind: string;
  fromValue: string | null;
  toValue: string | null;
  byUserId: string | null;
}

/** Phase 11.9 — salary roll-up payload. (Q14.7: same shape, new name `CrewSpend` going forward.) */
export interface CrewSalary {
  crewId: string;
  today: number;
  sprint: number;
  month: number;
  /** 14 daily totals, oldest first → today. */
  sparkline: number[];
}

/**
 * Phase 14.8 — HR payroll snapshot. Distinct from `CrewSalary` /
 * `CrewSpend` (which is token-spend); payroll = `Σ(level → hourly_usd) ×
 * on_shift_hours`. Field shape is intentionally identical so the UI can
 * render both side-by-side.
 */
export interface CrewPayroll {
  crewId: string;
  today: number;
  sprint: number;
  month: number;
  /** 14 daily totals, oldest first → today. */
  sparkline: number[];
}

/** Phase 14 — one row per (work-item × iteration). */
export interface Deliverable {
  id: number;
  workItemId: string;
  crewId: string;
  generatorAgentId: string;
  evaluatorAgentId: string;
  iter: number;
  score: number;
  dims: Record<string, number>;
  verdict: string;
  notes: string;
  genCostUsd: number;
  evalCostUsd: number;
  judgeAgentId: string | null;
  judgeScore: number | null;
  judgeNotes: string | null;
  createdAt: string;
}

/** Phase 14.5 — dispute body (caller supplies the judge result). */
export interface DeliverableDisputeRequest {
  judgeAgentId: string;
  judgeScore: number;
  judgeNotes?: string;
}

/** Phase 14.7 — one decision row in `PromotionRunResponse`. */
export interface PromotionDecision {
  agentId: string;
  oldLevel: number;
  newLevel: number;
  rollingAvg: number;
  reason: string;
  windowSize: number;
}

/** Phase 14.7 — outcome of one `POST /api/admin/promotion/run`. */
export interface PromotionRunResponse {
  agentsScanned: number;
  promoted: number;
  demoted: number;
  at: string;
  decisions: PromotionDecision[];
}

/** Phase 14.7 — one `agent_level_history` row. */
export interface AgentLevelHistoryEntry {
  id: number;
  agentId: string;
  oldLevel: number;
  newLevel: number;
  windowAvg: number;
  reason: string;
  createdAt: string;
}

/** Phase 11.9 — TopBar chip payload. */
export interface SalaryTodaySummary {
  totalUsd: number;
  activeAgents: number;
}

/** Phase 14.11 — TopBar payroll chip aggregator (server-side SUM). */
export interface PayrollTodaySummary {
  totalUsd: number;
  crewCount: number;
}

/**
 * Phase 11 — `/cost` dashboard rollup. Aggregates the last `days` (default 7)
 * of `agent_usage` rows by day × epic × agent so the cost view can render
 * the header stats, daily-stack chart, per-agent burn table, and today-vs-cap
 * gauge from a single request.
 */
export interface CostSummary {
  todayUsd: number;
  week7dUsd: number;
  dailyCapUsd: number;
  todayIndex: number;
  days: CostDailyPoint[];
  epics: CostByEpic[];
  agents: AgentBurn[];
}

export interface CostDailyPoint {
  date: string;
  label: string;
  totalUsd: number;
  byEpic: Record<string, number>;
}

export interface CostByEpic {
  epicId: string;
  total7dUsd: number;
}

export interface AgentBurn {
  id: string;
  name: string;
  role: string;
  cost24hUsd: number;
  cost7dUsd: number;
  tokens: number;
  spark: number[];
  repos?: string[];
  products?: string[];
}

// ─── Phase 15 — sprint & area path management ──────────────────────────────
//
// Iteration / area paths / saved prefixes are bound to a product (Q15.1, Q15.2).
// Mirrors `Forge.Api/Contracts/ForgeContracts.cs` Phase 15 records.

export type ProviderKind = 'ado' | 'github';
export type IterationState = 'past' | 'current' | 'future' | 'provider-orphan';
export type IterationBindingRole = 'current' | 'next' | 'reference';
export type SprintImportKind = 'iterations' | 'areas';

/** Phase 15.5 — one repo binding for a {@link ProductIteration}. */
export interface IterationBinding {
  repoId: string;
  repoName: string;
  role: IterationBindingRole;
}

/**
 * Phase 15.1 — product-scoped iteration imported from a provider.
 * Bound to exactly one product. `providerNodeId` is the stable upstream
 * identity used to resolve renames.
 */
export interface ProductIteration {
  id: string;
  productId: string;
  providerKind: ProviderKind;
  providerOrg: string;
  providerProject: string;
  providerNodeId: string;
  path: string;
  name: string;
  startDate: string | null;
  finishDate: string | null;
  state: IterationState;
  bindings: IterationBinding[];
  pointsTotal: number;
  pointsDone: number;
  importedAt: string;
  refreshedAt: string;
}

/**
 * Phase 15.2 — product-scoped area path. Tree-structured via `parentPath`.
 * ADO-only at v0.1 (Q15.6). Bound to a product.
 */
export interface ProductAreaPath {
  id: string;
  productId: string;
  providerKind: ProviderKind;
  providerOrg: string;
  providerProject: string;
  providerNodeId: string;
  path: string;
  parentPath: string | null;
  parentNodeId?: string | null;
  isDefaultFilter: boolean;
  workItemCount: number;
  importedAt: string;
}

/** Phase 15.10 — diff captured by the most recent refresh of a saved prefix. */
export interface SavedPrefixDelta {
  added: number;
  removed: number;
  renamed: number;
}

/**
 * Phase 15.10 — saved prefix shorthand. Bound to a product so refreshing
 * it scoped-imports under the same product.
 */
export interface SavedPrefix {
  id: string;
  productId: string;
  prefix: string;
  kind: SprintImportKind;
  repoId: string;
  repoName: string;
  addedBy: string;
  lastRefreshedAt: string;
  delta: SavedPrefixDelta;
}

/** Phase 15.2 — provider-specific extras on a {@link DiscoveryNode}. */
export interface DiscoveryNodeAttributes {
  startDate?: string;
  finishDate?: string;
  /** ADO timeFrame mirrored for iterations: 'past' | 'current' | 'future'. */
  timeFrame?: 'past' | 'current' | 'future';
}

/**
 * Phase 15.2 — flat node returned by ADO classification discovery. Operator
 * multi-selects from this list to feed the bulk-import endpoint.
 */
export interface DiscoveryNode {
  providerNodeId: string;
  path: string;
  name: string;
  hasChildren: boolean;
  depth: number;
  attributes: DiscoveryNodeAttributes;
  alreadyImported: boolean;
  workItemCount?: number;
}

/** Phase 15.2 — request body for `POST /api/products/{id}/sprint-discovery`. */
export interface SprintDiscoveryRequest {
  repoId: string;
  kind: SprintImportKind;
  prefix?: string;
}

/** Phase 15.3 — request body for the bulk-import endpoints. */
export interface ImportNodesRequest {
  repoId: string;
  providerNodeIds: string[];
  savedFromPrefix?: string;
}

/** Phase 15.5 — request body for `POST /api/repos/{id}/iteration-binding`. */
export interface IterationBindingRequest {
  productIterationId: string;
  role: IterationBindingRole;
}

/** Phase 15.10 — POST body for saving a new prefix shorthand. */
export interface SavePrefixRequest {
  prefix: string;
  kind: SprintImportKind;
  repoId: string;
}

// ─── Phase 17 — Persistent task queue ──────────────────────────────────────

export type TaskQueueState =
  | 'awaiting-ack'
  | 'awaiting-route'
  | 'queued'
  | 'picked-up'
  | 'completed'
  | 'cancelled'
  | 'reassigned'
  | 'blocked'
  | 'reject'
  | 'stalled'
  | 'stalled-budget';

/**
 * Phase 17 / 24 / 27 — discriminator on every queue row.
 * - `work` — the original Phase 17 work-item-pipeline kind.
 * - `coding` — the implementation-pipeline alias used by Sweep 2.
 * - `comment-response` — Phase 24 PR-comment fix loop.
 * - `build-fix` — Phase 27 CI-failure fix loop.
 */
export type TaskQueueKind =
  | 'work'
  | 'coding'
  | 'comment-response'
  | 'build-fix';

/** Phase 17 — one row in the unified task queue. */
export interface TaskQueueEntry {
  id: number;
  workItemId: string;
  /** "team" | "crew". */
  targetKind: string;
  targetId: string;
  state: TaskQueueState;
  /** Phase 24/27 — queue kind discriminator. */
  kind?: TaskQueueKind;
  /** 0 = high, 1 = med, 2 = low. */
  priorityRank: number;
  enqueuedAt: string;
  ackRequiredBy?: string | null;
  ackedAt?: string | null;
  ackByUserId?: string | null;
  autoAcked: boolean;
  routedToCrewId?: string | null;
  pickedUpAt?: string | null;
  pickedUpByAgent?: string | null;
  completedAt?: string | null;
  lastUpdatedAt: string;
}

/** Phase 27 — canonical state of a CI build run. */
export type CiBuildState =
  | 'queued'
  | 'running'
  | 'not-started'
  | 'succeeded'
  | 'failed'
  | 'partially-succeeded'
  | 'canceled';

/** Phase 27 — one row per upstream pipeline run, mirrors `BuildRow`. */
export interface Build {
  id: number;
  provider: 'ado' | 'github';
  providerBuildId: string;
  prId: string | null;
  repoId: string;
  pipelineId?: string | null;
  pipelineName?: string | null;
  state: CiBuildState;
  result?: string | null;
  triggeringCommit?: string | null;
  branch?: string | null;
  webUrl?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  isActive: boolean;
}

/** Phase 27 — one row per failed leaf stage of a build. */
export interface BuildStage {
  id: number;
  buildId: number;
  providerStageRef: string;
  stageName: string;
  stageOrder: number;
  state: string;
  result?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  logRef?: string | null;
  logTailHash?: string | null;
}

/** Phase 27 — tail-truncated log content for a stage. */
export interface BuildStageLog {
  stageId: number;
  logIndex: number;
  content: string;
  sizeBytes: number;
  truncatedFromBytes: number;
  fetchedAt: string;
}

/** Phase 27 — extracted artifact metadata. */
export interface BuildArtifact {
  id: number;
  buildId: number;
  name: string;
  downloadUrl?: string | null;
  sizeBytes: number;
  extractedPath?: string | null;
  fetchedAt?: string | null;
  expiredAt?: string | null;
}

/** Phase 27 — parsed FailurePoint row from the build failure parser. */
export interface BuildFailurePoint {
  id: number;
  stageId: number;
  kind: string;
  filePath?: string | null;
  line?: number | null;
  column?: number | null;
  message: string;
  sourceLogTailOffset: number;
  severity?: string | null;
  payloadJson?: string | null;
}

/** Phase 17 — per-crew queue stats + strain flag. */
export interface CrewQueueStats {
  crewId: string;
  depth: number;
  etaMinutes: number;
  strained: boolean;
  oldestQueuedAt?: string | null;
  headWorkItemId?: string | null;
}
