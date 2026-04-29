// Phase 23.8 / 23.9 — pure-logic tests for the sync-status strip helpers.
// The visual JSX is thin; the rendering decisions live in pure helpers
// exported from RepoSyncStatusStrip so they can be locked in here without
// a DOM.

import { describe, expect, it } from 'vitest';
import { relativeTime, statusDotColor } from './RepoSyncStatusStrip';

describe('statusDotColor', () => {
  it('returns green for idle', () => {
    expect(statusDotColor('idle')).toBe('rgb(34, 197, 94)');
  });

  it('returns blue for running', () => {
    expect(statusDotColor('running')).toBe('rgb(20, 110, 245)');
  });

  it('returns red for failed', () => {
    expect(statusDotColor('failed')).toBe('rgb(220, 38, 38)');
  });

  it('falls back to tertiary text colour when status is undefined', () => {
    expect(statusDotColor(undefined)).toBe('var(--text-tertiary)');
  });
});

describe('relativeTime', () => {
  // Anchor "now" so the test is deterministic regardless of wall-clock.
  const now = Date.UTC(2026, 3, 29, 12, 0, 0); // 2026-04-29 12:00 UTC

  it('returns "just now" within the first minute', () => {
    expect(relativeTime(new Date(now - 30_000).toISOString(), now)).toBe('just now');
  });

  it('returns minutes for sub-hour intervals', () => {
    expect(relativeTime(new Date(now - 14 * 60_000).toISOString(), now)).toBe('14m ago');
  });

  it('returns hours for sub-day intervals', () => {
    expect(relativeTime(new Date(now - 3 * 60 * 60_000).toISOString(), now)).toBe('3h ago');
  });

  it('returns days for >24h intervals', () => {
    expect(relativeTime(new Date(now - 5 * 24 * 60 * 60_000).toISOString(), now)).toBe('5d ago');
  });

  it('returns "—" for null/undefined timestamps', () => {
    expect(relativeTime(null, now)).toBe('—');
    expect(relativeTime(undefined, now)).toBe('—');
    expect(relativeTime('not-a-date', now)).toBe('—');
  });
});
