// Phase 12 — Work Item Tracking. Types mirror the planned Forge.Api/Contracts
// shape but live next to the mockup so the demo runs without backend wiring.
//
// Two parallel ladders (Q12.1 revised):
//   * DEV   — Epic   → Feature → Task | Bug
//   * DESIGN — Phase  → Stage   → Step
// Epic and Phase are both top-level "containers" associable to a product
// feature / functional area via `featureRef`. Story is retained as an alias
// for Feature so backfills from ADO/GitHub don't lose the legacy term.

export type WorkItemTrack = 'dev' | 'design';

export type WorkItemType =
  | 'epic'
  | 'feature'
  | 'task'
  | 'bug'
  | 'phase'
  | 'stage'
  | 'step';

export type WorkItemState = 'proposed' | 'active' | 'in-review' | 'resolved' | 'closed';
export type WorkItemPriority = 'low' | 'med' | 'high';
export type RepoKind = 'ado' | 'github';

export interface WorkItemAssignee {
  /**
   * `agent` = a single roster member (individual). `human` = a real
   * person with a Forge account. `crew` = a specific crew (direct-to-crew,
   * skips the team→lead routing). `team` (Phase 17) = the line-of-business
   * team (Architecture / Engineering / Review / QA). The latter enters the
   * persistent queue with state='awaiting-ack' and routes through the
   * lead → crew → member pipeline.
   */
  kind: 'agent' | 'human' | 'crew' | 'team';
  id: string;
  displayName: string;
  initials: string;
}

export interface WorkItem {
  id: string;
  /** Null when the item isn't yet pinned to a repo (rare; mostly local-only). */
  repoId: string | null;
  provider: RepoKind | 'local';
  /** ADO work item id or GitHub issue number. Null for local-only. */
  providerRef: number | null;
  /** Q12.1 — which ladder this item lives on. */
  track: WorkItemTrack;
  type: WorkItemType;
  state: WorkItemState;
  title: string;
  descriptionMd: string;
  parentId: string | null;
  /**
   * Q12.1 — top-level Epic / Phase items can carry an optional reference to
   * the product feature / functional area they support.
   */
  featureRef?: string | null;
  /** "{repoId}:{providerSprintId}" — namespaced per Q4. */
  iterationRef: string;
  areaPath: string;
  assignedTo: WorkItemAssignee | null;
  points: number;
  priority: WorkItemPriority;
  tags: string[];
  createdAt: string;
  updatedAt: string;
  /** Phase 5.10 — opaque marker advanced on every server write. */
  remoteRevision?: string | null;
  /** Watcher flag for the "Watching" filter chip (UI-only sample-data convenience). */
  watching?: boolean;
  /** Blocked flag matching Phase 1.3 chip (UI-only). */
  blocked?: boolean;
  /** Phase 16.1 — set when LeadAutoDispatcher picked the row up. */
  autoDispatchedAt?: string | null;
  /** Phase 16.5 — for step rows: planning-time file-budget (1..20). */
  expectedFileChanges?: number | null;
  /** Phase 19 — execution status: idle | queued | live | completed | failed | cancelled. */
  executionStatus?: string;
  /** Phase 19 — number of times execution has been attempted. */
  trialCount?: number;
}

export interface IterationRef {
  ref: string;
  /** Display label e.g. "Sprint 14" or "Sprint 15". */
  label: string;
  /** Provider's iteration / milestone id, namespaced by repo. */
  startsAt: string;
  endsAt: string;
}

export interface RepoLite {
  id: string;
  kind: RepoKind;
  displayName: string;
}

export const WORK_ITEM_TYPES: WorkItemType[] = [
  'epic', 'feature', 'task', 'bug',
  'phase', 'stage', 'step',
];
export const WORK_ITEM_STATES: WorkItemState[] = ['proposed', 'active', 'in-review', 'resolved', 'closed'];

/**
 * Track a type belongs to. Cleanest derivation: epic/feature/task/bug → dev;
 * phase/stage/step → design.
 */
export function trackOf(t: WorkItemType): WorkItemTrack {
  return t === 'phase' || t === 'stage' || t === 'step' ? 'design' : 'dev';
}

/**
 * Hierarchy depth within each ladder. Used for tree sort and parent-rank
 * validation: a child's rank must be > parent's rank, and crossing tracks
 * is only allowed at the top level (epic ↔ phase containers can sibling
 * each other under a product feature).
 */
export function typeRank(t: WorkItemType): number {
  switch (t) {
    case 'epic':    return 0;
    case 'phase':   return 0;
    case 'feature': return 1;
    case 'stage':   return 1;
    case 'task':    return 2;
    case 'bug':     return 2;
    case 'step':    return 2;
  }
}

/**
 * Single-letter typographic stamp. E F T B for dev; P G S for design (phase,
 * staGe, step) — chosen so each stamp stays visually distinct at 16px.
 */
export function typeMark(t: WorkItemType): string {
  return {
    epic: 'E', feature: 'F', task: 'T', bug: 'B',
    phase: 'P', stage: 'G', step: 'S',
  }[t];
}

export function typeLabel(t: WorkItemType): string {
  return {
    epic: 'Epic', feature: 'Feature', task: 'Task', bug: 'Bug',
    phase: 'Phase', stage: 'Stage', step: 'Step',
  }[t];
}

export function stateLabel(s: WorkItemState): string {
  return s; // lowercase by design
}

/** Allowed child types for a given parent type (used by reparent validation). */
export function childTypesFor(parent: WorkItemType): WorkItemType[] {
  switch (parent) {
    case 'epic':    return ['feature'];
    case 'feature': return ['task', 'bug'];
    case 'task':
    case 'bug':     return [];
    case 'phase':   return ['stage'];
    case 'stage':   return ['step'];
    case 'step':    return [];
  }
}
