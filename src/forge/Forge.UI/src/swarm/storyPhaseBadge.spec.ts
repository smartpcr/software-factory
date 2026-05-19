// docs/forge/e2e.md Stage 8.3 — pure-logic lock-in for the story-card
// phase badge taxonomy.
//
// Headline contracts:
//   • `qa` is NOT in the canonical NEW-spawn rotation
//     (plan → decompose → implement → review → done).
//   • A legacy spawn at `phase==='qa'` resolves to a badge with the
//     `phase-badge--legacy` modifier class + the operator-facing
//     drain-explanation tooltip.

import { describe, expect, it } from 'vitest';
import type { StorySpawnPhase } from '../api/forgeApi';
import {
  CANONICAL_PHASES,
  LEGACY_PHASES,
  LEGACY_PHASE_TOOLTIP,
  isCanonicalPhase,
  isLegacyPhase,
  phaseBadgeProps,
} from './storyPhaseBadge';

describe('CANONICAL_PHASES', () => {
  // ── 1. [ui-unit] New spawn rotation NEVER includes `qa` ─────────────

  it('contains the 5 canonical lifecycle phases in plan→done order', () => {
    expect(CANONICAL_PHASES).toEqual([
      'plan',
      'decompose',
      'implement',
      'review',
      'done',
    ]);
  });

  it('omits `qa` — confirmed both directly and via the predicate', () => {
    expect(CANONICAL_PHASES).not.toContain('qa');
    expect(isCanonicalPhase('qa' as StorySpawnPhase)).toBe(false);
    // Each canonical phase passes the predicate.
    for (const p of CANONICAL_PHASES) {
      expect(isCanonicalPhase(p)).toBe(true);
    }
  });

  it('omits `paused` — paused is a transient operator state, not a step in the rotation', () => {
    // paused was never a step in the canonical rotation; pinning here
    // catches a future refactor accidentally adding it back to the
    // progress sequence (which would push the progress-pct math
    // sideways for every paused spawn).
    expect(CANONICAL_PHASES).not.toContain('paused');
  });
});

describe('LEGACY_PHASES + isLegacyPhase', () => {
  // ── 2. [ui-unit] Legacy spawn at phase==='qa' renders muted ─────────

  it('lists `qa` as the (only, currently) legacy phase', () => {
    expect(LEGACY_PHASES).toEqual(['qa']);
    expect(isLegacyPhase('qa' as StorySpawnPhase)).toBe(true);
  });

  it('does NOT flag canonical phases as legacy', () => {
    for (const p of CANONICAL_PHASES) {
      expect(isLegacyPhase(p)).toBe(false);
    }
    expect(isLegacyPhase('paused' as StorySpawnPhase)).toBe(false);
  });

  it('carries an operator-facing drain tooltip for every legacy phase', () => {
    for (const p of LEGACY_PHASES) {
      const tip = LEGACY_PHASE_TOOLTIP[p];
      expect(typeof tip).toBe('string');
      expect(tip.length).toBeGreaterThan(0);
      // Operator-facing — should reference the drain mechanism so
      // they know the spawn will eventually clear without manual
      // intervention.
      expect(tip).toMatch(/legacy|drain|stage 5/i);
    }
  });
});

describe('phaseBadgeProps', () => {
  it('returns a clean (non-legacy) shape for every canonical phase', () => {
    for (const p of CANONICAL_PHASES) {
      const props = phaseBadgeProps(p);
      expect(props).toMatchObject({
        label: p.toUpperCase(),
        className: p,
        title: undefined,
        isLegacy: false,
      });
    }
  });

  it('returns the legacy shape for `qa` with the tooltip + modifier class', () => {
    const props = phaseBadgeProps('qa' as StorySpawnPhase);
    expect(props.isLegacy).toBe(true);
    expect(props.label).toBe('QA');
    // className keeps the original phase token (so existing per-phase
    // colour rules still apply as a base) PLUS the legacy modifier
    // (which mutes everything via !important overrides).
    expect(props.className).toContain('qa');
    expect(props.className).toContain('phase-badge--legacy');
    expect(props.title).toBe(LEGACY_PHASE_TOOLTIP.qa);
  });

  it('label-cases every phase, including `paused` (non-canonical, non-legacy)', () => {
    // paused isn't in CANONICAL_PHASES nor LEGACY_PHASES — it should
    // render as a normal (non-muted) badge with its phase token as
    // the className. This keeps the existing "spawn paused" pill
    // working untouched by Stage 8.3.
    const props = phaseBadgeProps('paused' as StorySpawnPhase);
    expect(props.isLegacy).toBe(false);
    expect(props.label).toBe('PAUSED');
    expect(props.className).toBe('paused');
    expect(props.title).toBeUndefined();
  });
});
