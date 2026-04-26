// Forge UI — shared types. Mirror of Forge.Api/Contracts/ForgeContracts.cs.
// Keep field names + casing in sync; the API serialises with camelCase.

export type Role = 'arch' | 'eng' | 'rev' | 'qa' | 'human' | 'tech-lead' | 'product-owner';
export type CardType = 'story' | 'task' | 'bug';
export type ColumnId = 'backlog' | 'design' | 'build' | 'review' | 'done';
export type Priority = 'low' | 'med' | 'high';
export type EpicStatus = 'active' | 'merging' | 'planning';
export type WorkstreamStatus = 'active' | 'merging' | 'review' | 'planning';

export interface Agent {
  id: string;
  role: Role;
  name: string;
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

/** Phase 10.6 — crew. */
export interface Crew {
  id: string;
  name: string;
  productId: string | null;
  summary: string;
  lead: string | null;
  model: string;
  modelInputPricePerMTok: number;
  modelOutputPricePerMTok: number;
  /** Phase 11.10 — soft daily cap on crew spend. */
  dailyBudgetUsd?: number | null;
  /** Phase 11.10 — hard cap per single invocation. */
  maxTaskBudgetUsd?: number | null;
}

/** Phase 11.9 — salary roll-up payload. */
export interface CrewSalary {
  crewId: string;
  today: number;
  sprint: number;
  month: number;
  /** 14 daily totals, oldest first → today. */
  sparkline: number[];
}

/** Phase 11.9 — TopBar chip payload. */
export interface SalaryTodaySummary {
  totalUsd: number;
  activeAgents: number;
}
