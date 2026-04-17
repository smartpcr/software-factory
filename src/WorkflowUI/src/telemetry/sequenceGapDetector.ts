/**
 * Detects gaps in a monotonically increasing sequence of RunEvents.
 * Used after reconnect to identify missing events for replay.
 */
export function detectSequenceGap(
  lastSeenSeq: number,
  incomingSeq: number,
): { gapStart: number; gapEnd: number } | null {
  if (incomingSeq <= lastSeenSeq) {
    return null; // duplicate or stale
  }
  if (incomingSeq === lastSeenSeq + 1) {
    return null; // no gap
  }
  return {
    gapStart: lastSeenSeq + 1,
    gapEnd: incomingSeq - 1,
  };
}

/**
 * Returns true if the event is a duplicate (already seen).
 */
export function isDuplicate(lastSeenSeq: number, incomingSeq: number): boolean {
  return incomingSeq <= lastSeenSeq;
}
