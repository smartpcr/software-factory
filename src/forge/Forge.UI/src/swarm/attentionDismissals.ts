/**
 * Dismissed NEEDS ATTENTION items, per story, in this browser.
 *
 * Each dismissal records the item's signature (status + stall reason). When the
 * workstream's situation changes — blocked → stalled, a new stall reason — the
 * signature no longer matches and the item shows again, so dismissing "blocked on
 * upstream" noise can never hide a later real problem.
 */
export interface AttentionItem { id: string; status: string; stallReason?: string | null }

export type Dismissals = Record<string, string>;

const key = (storyId: string) => `forge.attention.dismissed.${storyId}`;

export function attentionSignature(item: AttentionItem): string {
  return `${item.status}|${item.stallReason ?? ''}`;
}

export function loadDismissals(storyId: string): Dismissals {
  if (typeof window === 'undefined' || !storyId) return {};
  try {
    const raw = window.localStorage.getItem(key(storyId));
    const parsed = raw ? JSON.parse(raw) as unknown : {};
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Dismissals : {};
  } catch { return {}; }
}

export function saveDismissals(storyId: string, dismissals: Dismissals): void {
  if (typeof window === 'undefined' || !storyId) return;
  try {
    if (Object.keys(dismissals).length === 0) window.localStorage.removeItem(key(storyId));
    else window.localStorage.setItem(key(storyId), JSON.stringify(dismissals));
  } catch { /* quota / insecure context — dismissals simply don't persist */ }
}

export function isDismissed(item: AttentionItem, dismissals: Dismissals): boolean {
  return dismissals[item.id] === attentionSignature(item);
}

/** Dismiss the given items; entries for items no longer needing attention are dropped. */
export function dismiss(items: AttentionItem[], current: AttentionItem[], dismissals: Dismissals): Dismissals {
  const live = new Set(current.map(i => i.id));
  const next: Dismissals = {};
  for (const [id, sig] of Object.entries(dismissals)) if (live.has(id)) next[id] = sig;
  for (const item of items) next[item.id] = attentionSignature(item);
  return next;
}
