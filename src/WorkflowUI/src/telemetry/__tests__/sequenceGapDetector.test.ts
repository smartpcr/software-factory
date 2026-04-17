import { describe, it, expect } from 'vitest';
import { detectSequenceGap, isDuplicate } from '../sequenceGapDetector';

describe('sequenceGapDetector', () => {
  it('returns null when no gap', () => {
    expect(detectSequenceGap(5, 6)).toBeNull();
  });

  it('detects gap between lastSeen and incoming', () => {
    const gap = detectSequenceGap(5, 10);
    expect(gap).toEqual({ gapStart: 6, gapEnd: 9 });
  });

  it('returns null for duplicate', () => {
    expect(detectSequenceGap(10, 10)).toBeNull();
    expect(detectSequenceGap(10, 5)).toBeNull();
  });

  it('detects single-event gap', () => {
    const gap = detectSequenceGap(5, 7);
    expect(gap).toEqual({ gapStart: 6, gapEnd: 6 });
  });
});

describe('isDuplicate', () => {
  it('returns true for same seq', () => {
    expect(isDuplicate(5, 5)).toBe(true);
  });

  it('returns true for older seq', () => {
    expect(isDuplicate(10, 3)).toBe(true);
  });

  it('returns false for newer seq', () => {
    expect(isDuplicate(5, 6)).toBe(false);
  });
});
