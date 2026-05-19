// docs/forge/e2e.md Stage 8.3 — single source of truth for the
// story-spawn phase badge taxonomy.
//
// Backstory: Phase 28's initial rollout shipped a 6-phase lifecycle
// (plan → decompose → implement → review → qa → done) with a
// trailing "QA" phase where every workstream's qa-e2e tests fired as
// one bundled gate. Stage 8 of this plan refactored qa to per-stage
// trailing workstreams (Stage 5.1 routing), so new spawns now flow
// plan → decompose → implement → review → done — the `qa` phase is
// drained by the legacy-spawn guard in `StorySpawnOrchestrator.
// DriveQaAsync` (Stage 5.3) but never entered by new spawns.
//
// The UI's job for this stage: stop teaching the operator that `qa`
// is a normal step in the rotation, while keeping the badge visible
// (muted + tooltip) for legacy spawns still parked at `phase='qa'`
// so the operator can see them and trigger the drain.

import type { StorySpawnPhase } from '@/api/forgeApi';

/** docs/forge/e2e.md Stage 8.3 — canonical phase order for a NEW
 *  spawn. `qa` is deliberately absent; `paused` is also absent because
 *  it's a transient operator-driven state, not a step in the
 *  rotation. */
export const CANONICAL_PHASES: ReadonlyArray<StorySpawnPhase> = [
  'plan',
  'decompose',
  'implement',
  'review',
  'done',
];

/** docs/forge/e2e.md Stage 8.3 — phases that COULD appear on a
 *  StorySpawnRow but should be rendered as muted "legacy" badges
 *  with an explanatory tooltip. Only `qa` is legacy in the Stage 8
 *  cut; future drains can extend this set without touching every
 *  consumer. */
export const LEGACY_PHASES: ReadonlyArray<StorySpawnPhase> = ['qa'];

/** docs/forge/e2e.md Stage 8.3 — predicate. Returns true when the
 *  phase is part of the canonical NEW-spawn rotation. */
export function isCanonicalPhase(phase: StorySpawnPhase): boolean {
  return CANONICAL_PHASES.includes(phase);
}

/** docs/forge/e2e.md Stage 8.3 — predicate. Returns true when the
 *  phase is a drained-but-still-renderable legacy state. */
export function isLegacyPhase(phase: StorySpawnPhase): boolean {
  return LEGACY_PHASES.includes(phase);
}

/** docs/forge/e2e.md Stage 8.3 — shape returned by {@link phaseBadgeProps}.
 *  `className` is what the consumer adds to its `.phase-badge`
 *  element; for legacy phases it includes the `phase-badge--legacy`
 *  modifier so the CSS mutes the colour. `title` is the operator-
 *  facing tooltip — only populated for legacy phases. */
export interface PhaseBadgeProps {
  /** Display label (e.g. `"PLAN"`, `"QA"`). */
  label: string;
  /** Discriminator class for the badge's runtime style. */
  className: string;
  /** Tooltip shown on hover; only set for legacy phases. */
  title?: string;
  /** True when the phase is a legacy/drained state. Useful for
   *  consumers that want to surface it via JSX (e.g. data-attr)
   *  alongside the class name. */
  isLegacy: boolean;
}

/** docs/forge/e2e.md Stage 8.3 — operator-facing tooltip text for
 *  legacy phases. Exported so the vitest lock-in + the Playwright
 *  spec can both pin the exact string a future contributor would
 *  break by accident. */
export const LEGACY_PHASE_TOOLTIP: Record<StorySpawnPhase, string> = {
  qa: 'Legacy phase — `qa` no longer enters the canonical rotation. '
    + 'This spawn is still parked here from before docs/forge/e2e.md Stage 5 '
    + "split QA into per-stage workstreams; it'll drain through the legacy guard in DriveQaAsync.",
  // The other phases never enter this map at runtime — typed as
  // partial so a future legacy phase only needs an entry added here.
  plan: '', decompose: '', implement: '', review: '', done: '', paused: '',
};

/** docs/forge/e2e.md Stage 8.3 — derive badge render props from a
 *  raw phase value. The label is upper-cased to match the existing
 *  story-card / story-row badge convention. */
export function phaseBadgeProps(phase: StorySpawnPhase): PhaseBadgeProps {
  const legacy = isLegacyPhase(phase);
  return {
    label: phase.toUpperCase(),
    className: legacy ? `${phase} phase-badge--legacy` : phase,
    title: legacy ? LEGACY_PHASE_TOOLTIP[phase] : undefined,
    isLegacy: legacy,
  };
}
