// Insight I.4 (docs/forge/workstream-insights.md) — horizontal bar
// showing approxInputTokens / effectiveThreshold per role. Pulls
// from `GET /api/workstreams/{id}/memory`; rendered inside the
// IterKpiStrip header so context-window headroom is visible at a
// glance. Hidden entirely when memory is off for the crew (the
// endpoint returns no roles).

interface RoleMemory {
  agentId: string;
  backend: string;
  invokeMode: string;
  model: string | null;
  sessionId: string | null;
  hasMessagesHistory: boolean;
  turnCount: number | null;
  approxInputTokens: number;
  effectiveThreshold: number;
  lastIter: number;
  createdAt: string;
  updatedAt: string;
}

interface Props {
  roles: Record<string, RoleMemory | undefined>;
}

export function ContextUsageBar({ roles }: Props): JSX.Element | null {
  const entries: [string, RoleMemory][] = Object.entries(roles)
    .filter((entry): entry is [string, RoleMemory] => !!entry[1])
    // Render generator first, evaluator second, anything else after.
    .sort(([a], [b]) => roleOrder(a) - roleOrder(b));

  if (entries.length === 0) return null;

  return (
    <div
      role="region"
      aria-label="Context usage"
      style={{
        display: 'flex',
        gap: 12,
        padding: '6px 10px',
        borderBottom: '1px solid var(--border, #2a2f3a)',
        background: 'var(--bg-elevated, rgba(255,255,255,0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
        fontSize: 10,
      }}
    >
      {entries.map(([role, mem]) => (
        <BarRow key={role} role={role} mem={mem} />
      ))}
    </div>
  );
}

function BarRow({ role, mem }: { role: string; mem: RoleMemory }) {
  const used = mem.approxInputTokens;
  const cap = mem.effectiveThreshold > 0 ? mem.effectiveThreshold : 300_000;
  const pct = Math.min(100, Math.max(0, (used / cap) * 100));
  const tone = pct >= 90 ? 'var(--sc-red, #f87171)'
            : pct >= 70 ? 'var(--sc-amber, #ffb347)'
                        : 'var(--sc-green, #6ee7b7)';
  const label = `${formatTok(used)} / ${formatTok(cap)} (${pct.toFixed(0)}%)`;
  const title = `${role}${mem.model ? ` · ${mem.model}` : ''} — ${label}. Compactor folds older turns when usage crosses ${formatTok(cap)} (${roleThresholdSource(mem)}).`;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 180 }} title={title}>
      <span style={{ minWidth: 56, color: 'var(--text-secondary, #8d94a3)', letterSpacing: '0.04em' }}>
        {role}
      </span>
      <span
        aria-label={`${role} context usage ${label}`}
        style={{
          position: 'relative',
          flex: 1,
          height: 6,
          background: 'var(--bg-muted, rgba(255,255,255,0.06))',
          borderRadius: 3,
          overflow: 'hidden',
        }}
      >
        <span
          style={{
            display: 'block',
            width: `${pct}%`,
            height: '100%',
            background: tone,
            transition: 'width 200ms ease',
          }}
        />
      </span>
      <span style={{ minWidth: 110, textAlign: 'right', color: 'var(--text-primary, #e3e6eb)' }}>
        {label}
      </span>
    </div>
  );
}

function roleOrder(role: string): number {
  const lc = role.toLowerCase();
  if (lc === 'generator') return 0;
  if (lc === 'evaluator') return 1;
  return 2;
}

function roleThresholdSource(mem: RoleMemory): string {
  // We don't have direct access to the crew override on the client,
  // but we can hint at whether the threshold matches the global
  // default (300K) so the operator knows whether the row inherits
  // or overrides.
  if (mem.effectiveThreshold === 300_000) return 'global default';
  return 'crew override or model-window clamp';
}

function formatTok(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}K`;
  return `${n}`;
}
