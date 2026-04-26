// Forge UI — shared types. Mirror of Forge.Api/Contracts/ForgeContracts.cs.
// Keep field names + casing in sync; the API serialises with camelCase.

export type Role = 'arch' | 'eng' | 'rev' | 'qa' | 'human';
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
