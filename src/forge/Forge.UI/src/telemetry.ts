// Phase 7.4 + 7.5 — minimal client-side telemetry. Two surfaces:
//   * logAction(name, attrs)  — structured console + ring buffer
//   * recentActions()         — last 50 actions, used by the report-problem
//                               capture so we can attach context to bug reports
// We deliberately don't ship a vendor SDK here (OTel is a Phase R thing);
// console.* + ring buffer give us the same debugging value with zero deps.

const MAX = 50;
const ring: ActionEntry[] = [];

export interface ActionEntry {
  at: string;
  name: string;
  attrs: Record<string, unknown>;
}

export function logAction(name: string, attrs: Record<string, unknown> = {}): void {
  const entry: ActionEntry = { at: new Date().toISOString(), name, attrs };
  ring.push(entry);
  if (ring.length > MAX) ring.shift();
  // Structured single-line log so devtools / parsers can filter.
  if (import.meta.env.DEV) {
    // eslint-disable-next-line no-console
    console.debug(`[forge] ${name}`, attrs);
  }
}

export function logView(view: string): void {
  logAction('forge.view', { view });
}

export function recentActions(): ActionEntry[] {
  return ring.slice();
}

export function correlationId(): string {
  return `forge-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}
