// Accurate story-spawn progress percentage.
//
// Backstory: the story index + workstreams story-card both used to derive
// the progress bar purely from the phase index — `(phaseIdx + 1) / 5`. That
// made every story in the `implement` phase read "60%" regardless of how
// much of the work was actually done: a 50-workstream story with 46
// complete (≈92%) and one with 2 complete both showed 60%. The phase index
// is the right signal for the coarse "N/5 phases" label, but it is a poor
// proxy for real completion, because the implement phase contains the bulk
// of the work (every decomposed workstream) yet occupies a single 20-point
// band of a phase-index bar.
//
// This helper keeps the phase as the structural backbone — each phase owns
// a band of the 0–100 bar — but fills the execution band (implement →
// review) proportionally to actual workstream completion, which is the only
// signal that moves while a story sits in `implement` for hours. Plan and
// decompose stay coarse (no workstreams exist yet) but get a small,
// honest band so an early story does not over-report.

import type { StorySpawnPhase } from '@/api/forgeApi';

/** Pre-execution phases are coarse: workstreams don't exist yet, so there
 *  is no finer signal than "we're in this phase". They share a small band
 *  because the heavy lifting all lives in implement/review. */
const PLAN_PCT = 5;
const DECOMPOSE_PCT = 12;

/** The implement→review span. Once decomposition has produced workstreams,
 *  completion fraction (complete / total) is mapped linearly into this band.
 *  It stops short of 100 so only `done` reads 100%. */
export const EXEC_START_PCT = 18;
export const EXEC_END_PCT = 97;

export interface StoryProgressInput {
  /** The spawn's current lifecycle phase. */
  phase: StorySpawnPhase;
  /** Total workstreams in the spawn (0 before decomposition / while the
   *  detail payload is still loading). */
  wsCount: number;
  /** Workstreams whose status is `complete`. */
  completeCount: number;
}

/**
 * Returns an integer 0–100 progress percentage that reflects real
 * completion, not just the phase index.
 *
 * - `done` → 100.
 * - `plan` / `decompose` → small fixed band (no workstreams yet).
 * - `implement` / `review` / legacy `qa` → {@link EXEC_START_PCT}..{@link EXEC_END_PCT}
 *   filled by `completeCount / wsCount`. When `wsCount` is 0 (pre-
 *   decomposition or detail not loaded yet) it returns the band start so it
 *   never divides by zero or jumps backwards.
 * - `paused` / anything unexpected → the band start, so a paused story keeps
 *   the execution floor rather than collapsing to 0.
 */
export function computeStoryProgressPct({ phase, wsCount, completeCount }: StoryProgressInput): number {
  if (phase === 'done') return 100;
  if (phase === 'plan') return PLAN_PCT;
  if (phase === 'decompose') return DECOMPOSE_PCT;

  // implement | review | qa (legacy) | paused — execution band driven by
  // workstream completion.
  if (wsCount <= 0) return EXEC_START_PCT;
  const ratio = Math.max(0, Math.min(1, completeCount / wsCount));
  return Math.round(EXEC_START_PCT + ratio * (EXEC_END_PCT - EXEC_START_PCT));
}
