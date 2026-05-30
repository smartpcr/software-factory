// Pure-logic lock-in for the accurate story-spawn progress percentage.
//
// Headline contract: the implement/review band tracks real workstream
// completion, so a 50-workstream story with 46 complete reads ~91% — NOT
// the old phase-index "60%".

import { describe, expect, it } from 'vitest';
import {
  EXEC_START_PCT,
  EXEC_END_PCT,
  computeStoryProgressPct,
} from './storyProgress';

describe('computeStoryProgressPct', () => {
  // ── 1. The bug this fixes: implement-phase completion is honoured ──────

  it('reflects workstream completion in the implement phase (46/50 ≈ 91%, not 60%)', () => {
    const pct = computeStoryProgressPct({ phase: 'implement', wsCount: 50, completeCount: 46 });
    // Old phase-index math gave a flat 60%. New value is the 46/50 fill of
    // the [18,97] band: 18 + 0.92 * 79 ≈ 91.
    expect(pct).toBe(91);
    expect(pct).toBeGreaterThan(60);
  });

  it('is monotonic in completeCount within the implement phase', () => {
    const at = (c: number) => computeStoryProgressPct({ phase: 'implement', wsCount: 50, completeCount: c });
    expect(at(0)).toBe(EXEC_START_PCT);
    expect(at(25)).toBeGreaterThan(at(0));
    expect(at(50)).toBe(EXEC_END_PCT);
    expect(at(50)).toBeGreaterThan(at(25));
  });

  // ── 2. Phase bands ────────────────────────────────────────────────────

  it('returns 100 only when done', () => {
    expect(computeStoryProgressPct({ phase: 'done', wsCount: 50, completeCount: 0 })).toBe(100);
    // A fully-complete story still in `review` stays just below 100.
    expect(computeStoryProgressPct({ phase: 'review', wsCount: 50, completeCount: 50 })).toBe(EXEC_END_PCT);
  });

  it('keeps plan/decompose coarse and below the execution band', () => {
    const plan = computeStoryProgressPct({ phase: 'plan', wsCount: 0, completeCount: 0 });
    const decompose = computeStoryProgressPct({ phase: 'decompose', wsCount: 0, completeCount: 0 });
    expect(plan).toBeLessThan(decompose);
    expect(decompose).toBeLessThan(EXEC_START_PCT);
  });

  it('treats the review phase like implement — driven by completion', () => {
    expect(computeStoryProgressPct({ phase: 'review', wsCount: 10, completeCount: 5 }))
      .toBe(computeStoryProgressPct({ phase: 'implement', wsCount: 10, completeCount: 5 }));
  });

  // ── 3. Robustness ──────────────────────────────────────────────────────

  it('returns the band start (no NaN) when workstreams are absent or loading', () => {
    expect(computeStoryProgressPct({ phase: 'implement', wsCount: 0, completeCount: 0 })).toBe(EXEC_START_PCT);
  });

  it('clamps if completeCount somehow exceeds wsCount', () => {
    expect(computeStoryProgressPct({ phase: 'implement', wsCount: 10, completeCount: 99 })).toBe(EXEC_END_PCT);
  });

  it('handles the legacy qa phase via the execution band', () => {
    expect(computeStoryProgressPct({ phase: 'qa', wsCount: 4, completeCount: 2 }))
      .toBe(computeStoryProgressPct({ phase: 'implement', wsCount: 4, completeCount: 2 }));
  });
});
