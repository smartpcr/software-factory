export const HISTORY_EXCLUDED_KEYS = new Set([
  'viewport',
  'selectedNodeIds',
  'executionStatuses',
  'sidecarMeta',
]);

export function isHistoryExcluded(key: string): boolean {
  return HISTORY_EXCLUDED_KEYS.has(key);
}